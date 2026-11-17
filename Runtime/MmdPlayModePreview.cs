using System.Collections.Generic;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Video;
using AvatarDescriptor = VRC.SDKBase.VRC_AvatarDescriptor;
#if UNITY_EDITOR
using System;
#endif

public enum MmdPreviewDurationSource
{
    Animation,
    AudioClip,
    Camera
}

/// <summary>
/// Attach this component to a scene GameObject, configure it in the Inspector, then
/// enter Play Mode to preview an avatar animation, audio, background, and camera.
/// </summary>
[DisallowMultipleComponent]
[DefaultExecutionOrder(32000)]
public sealed class MmdPlayModePreview : MonoBehaviour
{
    private static readonly ProfilerMarker SampleMarker = new ProfilerMarker("MMDPreview.SampleAnimation");
    private static readonly ProfilerMarker TransformSampleMarker = new ProfilerMarker("MMDPreview.SampleTransformCurves");
    private static readonly ProfilerMarker CameraSampleMarker = new ProfilerMarker("MMDPreview.SampleCamera");
    private static readonly ProfilerMarker AudioSyncMarker = new ProfilerMarker("MMDPreview.SyncAudio");
#if UNITY_EDITOR
    /// <summary>
    /// Editor-only fallback for FK/Transform clips on Humanoid Animators. Unity's
    /// AnimationClip.SampleAnimation can apply blend shapes while ignoring raw
    /// Transform bindings on this combination, so the editor installs a cached
    /// direct-curve sampler during Play Mode.
    /// </summary>
    public static Func<AnimationClip, GameObject, float, bool> EditorTransformClipSampler;
    internal static Func<MmdPlayModePreview, bool> EditorStartRecording;
    internal static Func<bool> EditorIsRecordingAvailable;
    internal static Action<MmdPlayModePreview, float, float> EditorCaptureRecordingFrame;
    internal static Action<MmdPlayModePreview> EditorStopRecording;
    internal static Action<MmdPlayModePreview> EditorUpdateAudioLink;
    internal static Action<MmdPlayModePreview> EditorReleaseAudioLink;
#endif

    [Header("Preview")]
    public GameObject avatar;
    public bool enablePreviewBackground;
    public Texture2D backgroundImage;
    public VideoClip backgroundVideo;
    [Min(0f)] public float backgroundVideoTimelineStart;
    [Min(0f)] public float backgroundVideoClipStart;
    [Min(0f)] public float backgroundVideoClipEnd;
    public Color backgroundColor = new Color(0.11f, 0.12f, 0.14f);

    public AnimationClip animationClip;
    public AnimationClip cameraAnimationClip;
    public AudioClip audioClip;
    public bool loopPlayback = true;
    [Range(0.1f, 3f)] public float playbackSpeed = 1f;
    public bool recordVideo;
    public bool compressRecordedVideo;
    public string recordingOutputDirectory = "";
    public MmdPreviewDurationSource playbackDurationSource = MmdPreviewDurationSource.Animation;

    [HideInInspector] public Component gestureManager;
    [HideInInspector] public bool previewSessionAuthorized;
    [HideInInspector] public bool gestureManagerAutoAdded;
    [HideInInspector] public Behaviour audioLinkSessionTarget;
    [HideInInspector] public bool enableAudioLink;

    public bool freeCameraControl;
    public Vector3 cameraPosition = Vector3.zero;
    public Vector3 cameraRotation = new Vector3(8f, -25f, 0f);
    [Min(0.01f)] public float cameraMoveSpeed = 1f;

    private Camera previewCamera;
    private GameObject createdCameraObject;
    private bool usesExistingCamera;
    private Vector3 originalCameraPosition;
    private Quaternion originalCameraRotation;
    private float originalCameraFieldOfView;
    private float originalCameraNearClipPlane;
    private float originalCameraFarClipPlane;
    private float originalCameraDepth;
    private CameraClearFlags originalCameraClearFlags;
    private Color originalCameraBackgroundColor;
    private AudioSource previewAudio;
    private GameObject backgroundPlane;
    private Material backgroundMaterial;
    private MmdPreviewVideoBackground videoBackground;
    private readonly List<GameObject> disabledAvatars = new List<GameObject>();
    private Vector2 dragStart;
    private int cameraDragButton = -1;
    private float animationTime;
    private bool playbackPlaying;
    private bool playbackEnded;
    private bool recordingActive;
    private bool endingPreview;
    private float recordingSpeed;
    private float recordingStartTime;
    private long recordingFrameIndex;
    private int lastRecordedUnityFrame = -1;
    private bool recordingTargetWarmedUp;
    private bool recordingFramePending;
    private float pendingRecordingTime;
    public const int RecordingFrameRate = 60;
    private bool previewSessionActive;
    private Vector3 cameraFocus;
    private float cameraExtent = 1f;
    private GameObject lockedAvatar;
    private bool previewBackgroundApplied;
    private bool previousCameraAnimationMode;
    private Vector3 lastSynchronizedCameraPosition;
    private Vector3 lastSynchronizedCameraRotation;
    private readonly List<BlendShapeSnapshot> blendShapeSnapshots = new List<BlendShapeSnapshot>();
    private bool blendShapeStateCaptured;
    private bool playbackCameraStateCaptured;
    private Vector3 playbackCameraPosition;
    private Quaternion playbackCameraRotation;
    private float playbackCameraFieldOfView;
    private bool playbackCameraOrthographic;
    private float playbackCameraOrthographicSize;

    private sealed class BlendShapeSnapshot
    {
        public readonly SkinnedMeshRenderer renderer;
        public readonly Mesh mesh;
        public readonly float[] weights;

        public BlendShapeSnapshot(SkinnedMeshRenderer renderer, Mesh mesh, float[] weights)
        {
            this.renderer = renderer;
            this.mesh = mesh;
            this.weights = weights;
        }
    }

    private void Start()
    {
        previewSessionActive = previewSessionAuthorized;
        if (!previewSessionActive) return;

        lockedAvatar = avatar;
        gestureManager = GetComponent("BlackStartX.GestureManager.GestureManager");
        if (avatar == null)
        {
            Debug.LogError("MMD Play Mode Preview requires an Avatar.", this);
            enabled = false;
            return;
        }

        DisableUnselectedAvatars();

        CreateCamera();
        InitializeCameraFocus();
        InitializeCameraSettings();
        UpdatePreviewBackground();
        RefreshRecordingAvailability();
        if (recordVideo) loopPlayback = false;
        playbackSpeed = Mathf.Clamp(playbackSpeed, 0.1f, 3f);
        previewAudio = gameObject.AddComponent<AudioSource>();
        previewAudio.playOnAwake = false;
        previewAudio.loop = loopPlayback;
        previewAudio.clip = audioClip;
        previewAudio.pitch = PlaybackSpeed;
        playbackPlaying = false;
        playbackEnded = true;
        freeCameraControl = true;
        previousCameraAnimationMode = IsCameraAnimationMode;
        Camera.onPostRender += OnPreviewCameraRendered;
    }

    private void Update()
    {
        if (!previewSessionActive) return;

        // The selected avatar defines the active preview session and must not change mid-play.
        if (avatar != lockedAvatar) avatar = lockedAvatar;
        if (avatar == null || previewCamera == null) return;

        FlushRenderedRecordingFrame();

        RefreshRecordingAvailability();
        if (recordVideo) loopPlayback = false;
        UpdatePreviewBackground();

        bool cameraAnimationMode = IsCameraAnimationMode;
        if (cameraAnimationMode != previousCameraAnimationMode)
        {
            if (cameraAnimationMode)
                SampleCameraAnimation(animationTime);
            else
                SynchronizeCameraSettingsFromTransform();
            previousCameraAnimationMode = cameraAnimationMode;
        }

        if (cameraAnimationMode)
        {
            if (playbackEnded) SampleCameraAnimation(animationTime);
        }
        else
        {
            UpdateFreeCamera();
        }
        if (recordingActive && playbackPlaying)
        {
            animationTime = (float)(recordingStartTime +
                (double)recordingFrameIndex * recordingSpeed / RecordingFrameRate);
        }
        UpdateBackgroundPlane();
    }

    private void LateUpdate()
    {
#if UNITY_EDITOR
        if (previewSessionActive) EditorUpdateAudioLink?.Invoke(this);
#endif
        if (!previewSessionActive || avatar == null || playbackEnded || !HasPlaybackContent) return;

        if (previewAudio != null)
        {
            previewAudio.loop = loopPlayback && !recordVideo;
            previewAudio.pitch = PlaybackSpeed;
        }
        if (recordingActive)
        {
            if (playbackPlaying && animationTime >= PlaybackDuration) EndPreview();
            else
            {
                // Animator/Gesture Manager run after Update. Apply the final pose
                // here, just as normal preview does, before camera rendering.
                SamplePreview(animationTime);
                UpdateBackgroundPlane();
            }
            return;
        }
        if (playbackPlaying)
        {
            animationTime += Time.deltaTime * PlaybackSpeed;

            float duration = PlaybackDuration;
            if (duration > 0f && animationTime >= duration)
            {
                if (loopPlayback && !recordVideo)
                {
                    animationTime %= duration;
                    SyncAudio(true);
                }
                else
                {
                    animationTime = duration;
                    SamplePreview(animationTime);
                    UpdateBackgroundPlane();
                    EndPreview();
                    return;
                }
            }
        }

        SamplePreview(animationTime);
        UpdateBackgroundPlane();
    }

    public bool IsPlaybackPlaying => playbackPlaying;

    public bool IsPlaybackEnded => playbackEnded;
    public bool IsRecording => recordingActive;
    public float PlaybackSpeed => recordingActive ? recordingSpeed : Mathf.Clamp(playbackSpeed, 0.1f, 3f);
    public Camera PreviewCamera => previewCamera;
    internal AudioSource PreviewAudioSource => previewAudio;

    public bool IsPreviewSessionActive => previewSessionActive || previewSessionAuthorized;

    public bool HasPlaybackContent => animationClip != null || cameraAnimationClip != null || audioClip != null;

    public bool IsCameraAnimationMode => cameraAnimationClip != null && !freeCameraControl;

    public void SetPreviewSessionActive(bool active)
    {
        if (!active)
        {
#if UNITY_EDITOR
            EditorReleaseAudioLink?.Invoke(this);
#endif
            StopRecording();
            videoBackground?.Dispose();
            videoBackground = null;
            RestorePlaybackState();
        }
        previewSessionActive = active;
    }

    public float PlaybackTime => animationTime;

    public float PlaybackDuration
    {
        get
        {
            switch (playbackDurationSource)
            {
                case MmdPreviewDurationSource.AudioClip:
                    return audioClip != null ? audioClip.length : 0f;
                case MmdPreviewDurationSource.Camera:
                    return cameraAnimationClip != null ? cameraAnimationClip.length : 0f;
                default:
                    return animationClip != null ? animationClip.length : 0f;
            }
        }
    }

    private void RefreshRecordingAvailability()
    {
#if UNITY_EDITOR
        if (!recordingActive && (EditorIsRecordingAvailable == null || !EditorIsRecordingAvailable()))
            recordVideo = false;
#endif
    }

    public void PlayPreview()
    {
        if (!previewSessionActive || !HasPlaybackContent || PlaybackDuration <= 0f) return;
        if (playbackPlaying) return;
        RefreshRecordingAvailability();
        if (recordVideo)
        {
            loopPlayback = false;
            if (!recordingActive)
            {
#if UNITY_EDITOR
                if (EditorStartRecording == null || !EditorStartRecording(this)) return;
                recordingSpeed = Mathf.Clamp(playbackSpeed, 0.1f, 3f);
                recordingStartTime = playbackEnded ? 0f : animationTime;
                recordingFrameIndex = 0;
                // Switching the target during Inspector input may occur after
                // culling. Let that Unity frame finish before accepting captures.
                lastRecordedUnityFrame = Time.frameCount;
                recordingTargetWarmedUp = false;
                recordingFramePending = false;
                recordingActive = true;
#else
                return;
#endif
            }
        }
        if (playbackEnded)
        {
            CapturePlaybackState();
            playbackEnded = false;
            animationTime = 0f;
            if (cameraAnimationClip != null) freeCameraControl = false;
            SamplePreview(0f);
        }
        if (!loopPlayback && animationTime >= PlaybackDuration) animationTime = 0f;
        ApplyAudioClip();
        playbackPlaying = true;
        // Offline capture cannot provide continuous real-time audio monitoring.
        if (recordingActive)
        {
            if (previewAudio != null) previewAudio.Stop();
        }
        else SyncAudio(true);
    }

    public void PausePreview()
    {
        if (!previewSessionActive) return;
        FlushRenderedRecordingFrame();
        playbackPlaying = false;
        if (previewAudio != null) previewAudio.Pause();
        if (recordingActive) Time.timeScale = 0f;
    }

    public void ResetPreview()
    {
        if (!previewSessionActive || recordingActive) return;
        CapturePlaybackState();
        playbackPlaying = false;
        playbackEnded = false;
        animationTime = 0f;
        if (cameraAnimationClip != null) freeCameraControl = false;
        if (previewAudio != null)
        {
            previewAudio.Stop();
            previewAudio.time = 0f;
        }
        SamplePreview(0f);
        UpdateBackgroundPlane();
    }

    public void SeekPreview(float time)
    {
        if (!previewSessionActive || recordingActive) return;
        CapturePlaybackState();
        playbackEnded = false;
        animationTime = Mathf.Clamp(time, 0f, PlaybackDuration);
        SamplePreview(animationTime);
        UpdateBackgroundPlane();
        SyncAudio(playbackPlaying);
    }

    public void EndPreview()
    {
        if (!previewSessionActive || endingPreview) return;
        endingPreview = true;
        FlushRenderedRecordingFrame();
        if (previewAudio != null) previewAudio.Stop();
        StopRecording();
        playbackPlaying = false;
        playbackEnded = true;
        animationTime = 0f;
        freeCameraControl = true;
        cameraDragButton = -1;
        if (previewAudio != null) previewAudio.Stop();
        RestorePlaybackState();
        if (previewCamera != null)
        {
            SynchronizeCameraSettingsFromTransform();
            previousCameraAnimationMode = false;
            UpdateBackgroundPlane();
        }
        endingPreview = false;
    }

    private void OnPreviewCameraRendered(Camera camera)
    {
#if UNITY_EDITOR
        if (camera != previewCamera || !recordingActive || !playbackPlaying ||
            lastRecordedUnityFrame == Time.frameCount || animationTime >= PlaybackDuration ||
            (videoBackground != null && !videoBackground.IsFrameReady)) return;
        lastRecordedUnityFrame = Time.frameCount;
        if (!recordingTargetWarmedUp)
        {
            recordingTargetWarmedUp = true;
            return;
        }
        // OnPostRender can run before the target resolve has completed. Read it
        // at the next Update, before the camera can overwrite the finished frame.
        recordingFramePending = true;
        pendingRecordingTime = animationTime;
        // Permit exactly one simulation step for the next accepted output frame.
        // Decoder-only frames and pauses must not advance avatar controllers.
        Time.timeScale = 1f;
#endif
    }

    private void FlushRenderedRecordingFrame()
    {
#if UNITY_EDITOR
        if (!recordingActive || !recordingFramePending) return;
        recordingFramePending = false;
        EditorCaptureRecordingFrame?.Invoke(this, pendingRecordingTime, recordingSpeed);
        if (recordingActive) recordingFrameIndex++;
        if (recordingActive) Time.timeScale = 0f;
#endif
    }

    private void StopRecording()
    {
        if (!recordingActive) return;
        recordingActive = false;
#if UNITY_EDITOR
        EditorStopRecording?.Invoke(this);
#endif
    }

    private void SamplePreview(float time)
    {
        if (animationClip != null && avatar != null)
        {
            // Keep Unity sampling for Humanoid curves and blend shapes. The
            // editor fallback then reapplies raw FK Transform curves after it,
            // so an active Humanoid Animator cannot discard the bone pose.
            using (SampleMarker.Auto()) animationClip.SampleAnimation(avatar, time);
#if UNITY_EDITOR
            using (TransformSampleMarker.Auto())
                EditorTransformClipSampler?.Invoke(animationClip, avatar, time);
#endif
        }
        if (IsCameraAnimationMode) SampleCameraAnimation(time);
    }

    private void SampleCameraAnimation(float time)
    {
        if (cameraAnimationClip == null || previewCamera == null) return;
        using (CameraSampleMarker.Auto())
            cameraAnimationClip.SampleAnimation(previewCamera.gameObject, time);
        SynchronizeCameraSettingsFromTransform();
    }

    private void SyncAudio(bool play)
    {
        using (AudioSyncMarker.Auto()) SyncAudioInternal(play);
    }

    private void SyncAudioInternal(bool play)
    {
        if (previewAudio == null || previewAudio.clip == null) return;

        bool loop = loopPlayback && !recordVideo;
        previewAudio.loop = loop;
        previewAudio.pitch = PlaybackSpeed;
        float audioLength = previewAudio.clip.length;
        bool shouldPlay = play && (loop || animationTime < audioLength);
        if (shouldPlay && !previewAudio.isPlaying) previewAudio.Play();

        if (audioLength > 0f && previewAudio.clip.samples > 0)
        {
            float targetTime = loop ? animationTime % audioLength : Mathf.Min(animationTime, Mathf.Max(0f, audioLength - 0.001f));
            int targetSample = Mathf.Clamp(Mathf.RoundToInt(targetTime * previewAudio.clip.frequency), 0, previewAudio.clip.samples - 1);
            previewAudio.timeSamples = targetSample;
        }

        if (!shouldPlay) previewAudio.Pause();
    }

    private void ApplyAudioClip()
    {
        if (previewAudio == null || previewAudio.clip == audioClip) return;
        previewAudio.Stop();
        previewAudio.clip = audioClip;
    }

    private void CaptureBlendShapeState()
    {
        if (blendShapeStateCaptured || avatar == null) return;

        blendShapeSnapshots.Clear();
        foreach (SkinnedMeshRenderer renderer in avatar.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            Mesh mesh = renderer.sharedMesh;
            if (mesh == null || mesh.blendShapeCount == 0) continue;

            var weights = new float[mesh.blendShapeCount];
            for (int index = 0; index < weights.Length; index++)
                weights[index] = renderer.GetBlendShapeWeight(index);
            blendShapeSnapshots.Add(new BlendShapeSnapshot(renderer, mesh, weights));
        }
        blendShapeStateCaptured = true;
    }

    private void CapturePlaybackCameraState()
    {
        if (playbackCameraStateCaptured || previewCamera == null) return;

        playbackCameraPosition = previewCamera.transform.position;
        playbackCameraRotation = previewCamera.transform.rotation;
        playbackCameraFieldOfView = previewCamera.fieldOfView;
        playbackCameraOrthographic = previewCamera.orthographic;
        playbackCameraOrthographicSize = previewCamera.orthographicSize;
        playbackCameraStateCaptured = true;
    }

    private void CapturePlaybackState()
    {
        CaptureBlendShapeState();
        CapturePlaybackCameraState();
    }

    private void RestoreBlendShapeState()
    {
        if (!blendShapeStateCaptured) return;

        foreach (BlendShapeSnapshot snapshot in blendShapeSnapshots)
        {
            if (snapshot.renderer == null || snapshot.renderer.sharedMesh != snapshot.mesh) continue;

            int count = Mathf.Min(snapshot.weights.Length, snapshot.mesh.blendShapeCount);
            for (int index = 0; index < count; index++)
                snapshot.renderer.SetBlendShapeWeight(index, snapshot.weights[index]);
        }
        blendShapeSnapshots.Clear();
        blendShapeStateCaptured = false;
    }

    private void RestorePlaybackCameraState()
    {
        if (!playbackCameraStateCaptured) return;

        if (previewCamera != null)
        {
            previewCamera.transform.position = playbackCameraPosition;
            previewCamera.transform.rotation = playbackCameraRotation;
            previewCamera.fieldOfView = playbackCameraFieldOfView;
            previewCamera.orthographic = playbackCameraOrthographic;
            previewCamera.orthographicSize = playbackCameraOrthographicSize;
            SynchronizeCameraSettingsFromTransform();
        }
        playbackCameraStateCaptured = false;
    }

    private void RestorePlaybackState()
    {
        RestoreBlendShapeState();
        RestorePlaybackCameraState();
    }

    private void OnDestroy()
    {
#if UNITY_EDITOR
        EditorReleaseAudioLink?.Invoke(this);
#endif
        Camera.onPostRender -= OnPreviewCameraRendered;
        FlushRenderedRecordingFrame();
        StopRecording();
        RestorePlaybackState();
        if (!previewSessionActive) return;

        foreach (GameObject otherAvatar in disabledAvatars)
        {
            if (otherAvatar != null) otherAvatar.SetActive(true);
        }
        RestoreCamera();
        DestroyBackgroundPlane();
    }

    private void OnDisable()
    {
#if UNITY_EDITOR
        EditorReleaseAudioLink?.Invoke(this);
#endif
        if (recordingActive) EndPreview();
        videoBackground?.Dispose();
        videoBackground = null;
    }

    private void DisableUnselectedAvatars()
    {
        foreach (AvatarDescriptor descriptor in Resources.FindObjectsOfTypeAll<AvatarDescriptor>())
        {
            if (descriptor == null || descriptor.gameObject == avatar || !descriptor.gameObject.activeInHierarchy || !descriptor.gameObject.scene.IsValid()) continue;
            descriptor.gameObject.SetActive(false);
            disabledAvatars.Add(descriptor.gameObject);
        }
    }

    private void CreateCamera()
    {
        previewCamera = FindUsableCamera();
        usesExistingCamera = previewCamera != null;
        if (usesExistingCamera)
        {
            originalCameraPosition = previewCamera.transform.position;
            originalCameraRotation = previewCamera.transform.rotation;
            originalCameraFieldOfView = previewCamera.fieldOfView;
            originalCameraNearClipPlane = previewCamera.nearClipPlane;
            originalCameraFarClipPlane = previewCamera.farClipPlane;
            originalCameraDepth = previewCamera.depth;
            originalCameraClearFlags = previewCamera.clearFlags;
            originalCameraBackgroundColor = previewCamera.backgroundColor;
        }
        else
        {
            createdCameraObject = new GameObject("MMD Preview Camera");
            previewCamera = createdCameraObject.AddComponent<Camera>();
            if (FindObjectOfType<AudioListener>() == null) createdCameraObject.AddComponent<AudioListener>();
        }

        if (!usesExistingCamera)
        {
            originalCameraClearFlags = previewCamera.clearFlags;
            originalCameraBackgroundColor = previewCamera.backgroundColor;
        }
        previewCamera.fieldOfView = 30f;
        previewCamera.nearClipPlane = 0.01f;
        previewCamera.depth = 100f;
    }

    private Camera FindUsableCamera()
    {
        if (IsUsableCamera(Camera.main)) return Camera.main;
        foreach (Camera camera in Resources.FindObjectsOfTypeAll<Camera>())
        {
            if (IsUsableCamera(camera)) return camera;
        }
        return null;
    }

    private bool IsUsableCamera(Camera camera)
    {
        return camera != null && camera.enabled && camera.gameObject.activeInHierarchy && camera.gameObject.scene.IsValid() &&
               (avatar == null || !camera.transform.IsChildOf(avatar.transform));
    }

    private void RestoreCamera()
    {
        if (usesExistingCamera && previewCamera != null)
        {
            previewCamera.transform.position = originalCameraPosition;
            previewCamera.transform.rotation = originalCameraRotation;
            previewCamera.fieldOfView = originalCameraFieldOfView;
            previewCamera.nearClipPlane = originalCameraNearClipPlane;
            previewCamera.farClipPlane = originalCameraFarClipPlane;
            previewCamera.depth = originalCameraDepth;
            previewCamera.clearFlags = originalCameraClearFlags;
            previewCamera.backgroundColor = originalCameraBackgroundColor;
        }
        else if (createdCameraObject != null)
        {
            Destroy(createdCameraObject);
        }
        previewCamera = null;
        createdCameraObject = null;
    }

    private void CreateBackgroundPlane()
    {
        if (!enablePreviewBackground || (backgroundImage == null && backgroundVideo == null)) return;

        backgroundPlane = GameObject.CreatePrimitive(PrimitiveType.Quad);
        backgroundPlane.name = "MMD Preview Background";
        Destroy(backgroundPlane.GetComponent<Collider>());
        backgroundMaterial = new Material(Shader.Find("Unlit/Texture"));
        backgroundMaterial.mainTexture = backgroundImage;
        backgroundPlane.GetComponent<MeshRenderer>().sharedMaterial = backgroundMaterial;
    }

    private void EnsureBackgroundPlane()
    {
        if (backgroundPlane == null) CreateBackgroundPlane();
    }

    private void UpdatePreviewBackground()
    {
        if (previewCamera == null) return;

        if (!enablePreviewBackground)
        {
            if (previewBackgroundApplied)
            {
                previewCamera.clearFlags = originalCameraClearFlags;
                previewCamera.backgroundColor = originalCameraBackgroundColor;
                previewBackgroundApplied = false;
            }
            DestroyBackgroundPlane();
            return;
        }

        previewCamera.clearFlags = CameraClearFlags.SolidColor;
        previewCamera.backgroundColor = backgroundColor;
        previewBackgroundApplied = true;
        EnsureBackgroundPlane();
    }

    private void DestroyBackgroundPlane()
    {
        videoBackground?.Dispose();
        videoBackground = null;
        if (backgroundPlane != null) Destroy(backgroundPlane);
        if (backgroundMaterial != null) Destroy(backgroundMaterial);
        backgroundPlane = null;
        backgroundMaterial = null;
    }

    private void UpdateFreeCamera()
    {
        if (previewCamera == null) return;

        // Inspector edits are applied back to the real camera before processing input.
        if (!Approximately(cameraPosition, lastSynchronizedCameraPosition))
            previewCamera.transform.position = cameraPosition;
        if (!Approximately(cameraRotation, lastSynchronizedCameraRotation))
            previewCamera.transform.rotation = Quaternion.Euler(cameraRotation);

        if (!freeCameraControl)
        {
            cameraDragButton = -1;
            previewCamera.farClipPlane = cameraExtent * 30f;
            SynchronizeCameraSettingsFromTransform();
            return;
        }

        if (Input.GetMouseButtonDown(1) || Input.GetMouseButtonDown(2))
        {
            cameraDragButton = Input.GetMouseButtonDown(1) ? 1 : 2;
            dragStart = Input.mousePosition;
        }

        if (cameraDragButton >= 0 && Input.GetMouseButton(cameraDragButton))
        {
            Vector2 delta = (Vector2)Input.mousePosition - dragStart;
            Vector3 euler = ToSignedEuler(previewCamera.transform.eulerAngles);
            euler.x = Mathf.Clamp(euler.x - delta.y * 0.25f, -89.9f, 89.9f);
            euler.y += delta.x * 0.25f;
            previewCamera.transform.rotation = Quaternion.Euler(euler);
            dragStart = Input.mousePosition;

            float scroll = Input.mouseScrollDelta.y;
            if (cameraDragButton == 1)
            {
                if (Mathf.Abs(scroll) > 0.001f)
                    cameraMoveSpeed = Mathf.Clamp(cameraMoveSpeed * Mathf.Pow(1.25f, scroll), 0.01f, 100f);

                Vector3 movement = Vector3.zero;
                if (Input.GetKey(KeyCode.W)) movement += previewCamera.transform.forward;
                if (Input.GetKey(KeyCode.S)) movement -= previewCamera.transform.forward;
                if (Input.GetKey(KeyCode.D)) movement += previewCamera.transform.right;
                if (Input.GetKey(KeyCode.A)) movement -= previewCamera.transform.right;
                if (Input.GetKey(KeyCode.E)) movement += Vector3.up;
                if (Input.GetKey(KeyCode.Q)) movement -= Vector3.up;
                if (movement.sqrMagnitude > 0f)
                    previewCamera.transform.position += movement.normalized * cameraMoveSpeed * Time.deltaTime;
            }
            else if (Mathf.Abs(scroll) > 0.001f)
            {
                previewCamera.transform.position +=
                    previewCamera.transform.forward * scroll * cameraMoveSpeed * 0.25f;
            }
        }

        if (cameraDragButton >= 0 && Input.GetMouseButtonUp(cameraDragButton))
            cameraDragButton = -1;

        previewCamera.farClipPlane = cameraExtent * 30f;
        SynchronizeCameraSettingsFromTransform();
    }

    private void InitializeCameraFocus()
    {
        Bounds bounds = CalculateBounds(avatar);
        cameraFocus = bounds.center;
        cameraExtent = Mathf.Max(0.01f, Mathf.Max(bounds.extents.x, Mathf.Max(bounds.extents.y, bounds.extents.z)));
    }

    private void InitializeCameraSettings()
    {
        if (previewCamera == null) return;

        Quaternion configuredRotation = Quaternion.Euler(cameraRotation);
        if (cameraPosition == Vector3.zero)
            cameraPosition = cameraFocus - configuredRotation * Vector3.forward * cameraExtent * 3.3f;

        previewCamera.transform.position = cameraPosition;
        previewCamera.transform.rotation = configuredRotation;
        cameraMoveSpeed = Mathf.Max(0.01f, cameraMoveSpeed);
        previewCamera.farClipPlane = cameraExtent * 30f;
        SynchronizeCameraSettingsFromTransform();
    }

    private void SynchronizeCameraSettingsFromTransform()
    {
        if (previewCamera == null) return;
        cameraPosition = previewCamera.transform.position;
        cameraRotation = ToSignedEuler(previewCamera.transform.eulerAngles);
        lastSynchronizedCameraPosition = cameraPosition;
        lastSynchronizedCameraRotation = cameraRotation;
    }

    private static Vector3 ToSignedEuler(Vector3 euler)
    {
        return new Vector3(ToSignedAngle(euler.x), ToSignedAngle(euler.y), ToSignedAngle(euler.z));
    }

    private static float ToSignedAngle(float angle)
    {
        angle %= 360f;
        return angle > 180f ? angle - 360f : angle;
    }

    private static bool Approximately(Vector3 left, Vector3 right)
    {
        return (left - right).sqrMagnitude < 0.00000001f;
    }

    private void UpdateBackgroundPlane()
    {
        if (backgroundPlane == null || previewCamera == null) return;
        if (videoBackground == null) videoBackground = new MmdPreviewVideoBackground(this);
        videoBackground.Update(animationTime, playbackPlaying && !playbackEnded, recordingActive);
        Texture texture = videoBackground.Texture != null ? videoBackground.Texture : backgroundImage;
        backgroundMaterial.mainTexture = texture;
        backgroundPlane.GetComponent<MeshRenderer>().enabled = texture != null;
        float distance = previewCamera.farClipPlane * 0.5f;
        float height = previewCamera.orthographic ? 2f * previewCamera.orthographicSize :
            2f * distance * Mathf.Tan(previewCamera.fieldOfView * 0.5f * Mathf.Deg2Rad);
        backgroundPlane.transform.position = previewCamera.transform.position + previewCamera.transform.forward * distance;
        backgroundPlane.transform.rotation = previewCamera.transform.rotation;
        backgroundPlane.transform.localScale = new Vector3(height * previewCamera.aspect, height, 1f);
    }

    private static Bounds CalculateBounds(GameObject root)
    {
        Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
        if (renderers.Length == 0) return new Bounds(root.transform.position, Vector3.one);
        Bounds result = renderers[0].bounds;
        for (int index = 1; index < renderers.Length; index++) result.Encapsulate(renderers[index].bounds);
        return result;
    }
}
