using System;
using UnityEngine;
using UnityEngine.Video;

// Owns a muted decoder; the preview timeline remains the only playback clock.
internal sealed class MmdPreviewVideoBackground : IDisposable
{
    private readonly MmdPlayModePreview owner;
    private GameObject decoderObject;
    private VideoPlayer player;
    private VideoClip clip;
    private RenderTexture target;
    private bool failed;
    private bool pending;
    private bool visible;
    private bool exactFrame;
    private long requestedFrame = -1;
    private long decodedFrame = -1;
    private double operationStarted;
    public bool IsFrameReady { get; private set; } = true;
    public Texture Texture => visible && decodedFrame >= 0 ? target : null;

    public MmdPreviewVideoBackground(MmdPlayModePreview owner) { this.owner = owner; }

    public void Update(float timeline, bool playing, bool recording)
    {
        exactFrame = recording || !playing;
        if (clip != owner.backgroundVideo)
        {
            Dispose();
            clip = owner.backgroundVideo;
            failed = false;
            if (clip != null) CreatePlayer();
        }
        IsFrameReady = true;
        visible = false;
        if (player == null || failed) return;

        double start = Math.Max(0, owner.backgroundVideoClipStart);
        double end = owner.backgroundVideoClipEnd > 0 ? Math.Min(clip.length, owner.backgroundVideoClipEnd) : clip.length;
        double sourceTime = timeline - Math.Max(0, owner.backgroundVideoTimelineStart) + start;
        if (start >= end || timeline < Math.Max(0, owner.backgroundVideoTimelineStart) || sourceTime >= end)
        {
            if (player.isPrepared && player.isPlaying && !pending) player.Pause();
            return;
        }
        if (!player.isPrepared)
        {
            IsFrameReady = false;
            CheckTimeout();
            return;
        }

        visible = true;
        double rate = clip.frameRate > 0 ? clip.frameRate : 30;
        long frame = Math.Min((long)clip.frameCount - 1, Math.Max(0, (long)Math.Floor(sourceTime * rate + 0.00001)));
        if (exactFrame)
        {
            if (player.isPlaying && !pending) player.Pause();
            IsFrameReady = !pending && decodedFrame == frame;
            if (!IsFrameReady) RequestFrame(frame);
        }
        else
        {
            // Continuous decoding keeps ordinary preview fast. Correct drift after
            // seeks, loops and speed changes without seeking on every render frame.
            player.playbackSpeed = owner.PlaybackSpeed;
            if (decodedFrame < 0 || Math.Abs(player.time - sourceTime) > 0.12)
                RequestFrame(frame);
            if (!pending && !player.isPlaying) player.Play();
        }
        CheckTimeout();
    }

    private void CreatePlayer()
    {
        decoderObject = new GameObject("MMD Preview Video Decoder") { hideFlags = HideFlags.HideAndDontSave };
        player = decoderObject.AddComponent<VideoPlayer>();
        player.playOnAwake = false;
        player.source = VideoSource.VideoClip;
        player.clip = clip;
        player.audioOutputMode = VideoAudioOutputMode.None;
        player.renderMode = VideoRenderMode.RenderTexture;
        player.waitForFirstFrame = true;
        player.skipOnDrop = false;
        player.sendFrameReadyEvents = true;
        target = new RenderTexture(Math.Max(1, (int)clip.width), Math.Max(1, (int)clip.height), 0,
            RenderTextureFormat.ARGB32) { name = "MMD Video Background", hideFlags = HideFlags.HideAndDontSave };
        target.Create();
        player.targetTexture = target;
        player.frameReady += OnFrameReady;
        player.errorReceived += OnError;
        operationStarted = Time.realtimeSinceStartupAsDouble;
        player.Prepare();
    }

    private void RequestFrame(long frame)
    {
        if (pending) return;
        pending = true;
        requestedFrame = frame;
        operationStarted = Time.realtimeSinceStartupAsDouble;
        player.frame = frame;
        player.Play();
    }

    private void OnFrameReady(VideoPlayer source, long frame)
    {
        decodedFrame = frame;
        if (pending && frame == requestedFrame)
        {
            pending = false;
            if (exactFrame) source.Pause();
        }
    }

    private void CheckTimeout()
    {
        if ((!player.isPrepared || pending) && Time.realtimeSinceStartupAsDouble - operationStarted > 15)
            OnError(player, "Timed out preparing/seeking the background video (requested " + requestedFrame +
                ", decoded " + decodedFrame + ", player " + player.frame + ").");
    }

    private void OnError(VideoPlayer source, string message)
    {
        failed = true;
        visible = false;
        IsFrameReady = true;
        source.Stop();
        Debug.LogError("MMD 视频背景不可用，已回退到图片/纯色背景。Video background unavailable: " + message, owner);
        // Do not silently produce a recording with a missing or stale background.
        if (owner.IsRecording) owner.EndPreview();
    }

    public void Dispose()
    {
        if (player != null)
        {
            player.frameReady -= OnFrameReady;
            player.errorReceived -= OnError;
            player.Stop();
            player.targetTexture = null;
        }
        if (decoderObject != null) UnityEngine.Object.Destroy(decoderObject);
        if (target != null)
        {
            target.Release();
            UnityEngine.Object.Destroy(target);
        }
        player = null;
        decoderObject = null;
        target = null;
        clip = null;
        pending = visible = false;
        requestedFrame = decodedFrame = -1;
        IsFrameReady = true;
    }
}
