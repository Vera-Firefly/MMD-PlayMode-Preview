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
    private bool resyncRequired;
    private bool retrying;
    private bool seekFinished;
    private double seekFinishedAt;
    private double lastFrameAt;
    private bool correctedSeek;
    private bool requiresFrames;
    private bool waitingForPlay;
    private bool wasPlaying;
    private double lastTimeline = double.NaN;
    private bool previousExactFrame = true;
    private long requestedFrame = -1;
    private long decodedFrame = -1;
    private double operationStarted;
    public bool IsFrameReady { get; private set; } = true;
    public Texture Texture => visible && decodedFrame >= 0 ? target : null;

    public MmdPreviewVideoBackground(MmdPlayModePreview owner) { this.owner = owner; }

    public void Update(float timeline, bool playing, bool recording)
    {
        bool startingPlayback = playing && !wasPlaying;
        wasPlaying = playing;
        requiresFrames = playing || recording;
        bool exactFrame = recording || !playing;
        bool discontinuity = !double.IsNaN(lastTimeline) &&
            (timeline < lastTimeline || Math.Abs(timeline - lastTimeline) > Math.Max(.25, Time.unscaledDeltaTime * owner.PlaybackSpeed * 3));
        bool resumed = previousExactFrame && !exactFrame;
        lastTimeline = timeline;
        previousExactFrame = exactFrame;
        if (clip != owner.backgroundVideo)
        {
            Dispose();
            wasPlaying = playing;
            clip = owner.backgroundVideo;
            failed = false;
            if (clip != null) CreatePlayer();
        }
        if (player != null && playing && (waitingForPlay || (failed && startingPlayback)))
        {
            // Idle preparation is speculative. Play always gets a fresh attempt,
            // even if that preparation timed out or the native decoder failed.
            ReleasePlayer();
            failed = waitingForPlay = pending = retrying = seekFinished = false;
            decodedFrame = -1;
            resyncRequired = true;
            CreatePlayer();
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
        if (waitingForPlay)
        {
            visible = decodedFrame >= 0;
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
        // Remember jumps made while an earlier asynchronous seek is decoding.
        // Never change its clock or speed until the requested frame arrives.
        if (discontinuity || (resumed && Math.Abs(player.time - sourceTime) > .12))
            resyncRequired = true;
        if (pending)
        {
            IsFrameReady = false;
            SettleSeek();
            CheckTimeout();
            return;
        }
        if (exactFrame)
        {
            if (player.isPlaying) player.Pause();
            IsFrameReady = decodedFrame == frame;
            if (!IsFrameReady) RequestFrame(frame);
            else resyncRequired = false;
        }
        else
        {
            // Use the same game clock as the preview. Switching to ExternalTime
            // after a seek can asynchronously restore its previous clock value,
            // undoing a completed backward seek on platform video decoders.
            double driftLimit = Math.Max(.25, Time.deltaTime * owner.PlaybackSpeed * 3);
            if (decodedFrame < 0 || resyncRequired || Math.Abs(player.time - sourceTime) > driftLimit)
            {
                RequestFrame(frame);
            }
            else
            {
                player.timeUpdateMode = VideoTimeUpdateMode.GameTime;
                player.skipOnDrop = true;
                player.playbackSpeed = owner.PlaybackSpeed;
                if (!player.isPlaying) player.Play();
            }
        }
        CheckTimeout();
    }

    private void CreatePlayer()
    {
        decoderObject = new GameObject("MMD Preview Video Decoder") { hideFlags = HideFlags.HideAndDontSave };
        player = decoderObject.AddComponent<VideoPlayer>();
        player.playOnAwake = false;
        // Decoding must continue while the recording simulation is frozen.
        player.timeUpdateMode = VideoTimeUpdateMode.UnscaledGameTime;
        player.source = VideoSource.VideoClip;
        player.clip = clip;
        player.audioOutputMode = VideoAudioOutputMode.None;
        player.controlledAudioTrackCount = (ushort)Math.Min((int)clip.audioTrackCount, 64);
        for (ushort track = 0; track < player.controlledAudioTrackCount; track++)
            player.EnableAudioTrack(track, false);
        player.timeReference = VideoTimeReference.Freerun;
        player.renderMode = VideoRenderMode.RenderTexture;
        player.waitForFirstFrame = true;
        player.skipOnDrop = false;
        player.sendFrameReadyEvents = true;
        // A background is bounded by the preview timeline, not decoder looping.
        player.isLooping = false;
        if (target == null)
        {
            target = new RenderTexture(Math.Max(1, (int)clip.width), Math.Max(1, (int)clip.height), 0,
                RenderTextureFormat.ARGB32) { name = "MMD Video Background", hideFlags = HideFlags.HideAndDontSave };
            target.Create();
        }
        player.targetTexture = target;
        player.frameReady += OnFrameReady;
        player.seekCompleted += OnSeekCompleted;
        player.errorReceived += OnError;
        operationStarted = Time.realtimeSinceStartupAsDouble;
        player.Prepare();
    }

    private void RequestFrame(long frame)
    {
        if (pending) return;
        pending = true;
        seekFinished = correctedSeek = false;
        requestedFrame = frame;
        resyncRequired = false;
        IsFrameReady = false;
        operationStarted = Time.realtimeSinceStartupAsDouble;
        // ExternalTime can pull a seek straight back to the old playback clock,
        // and skipOnDrop can omit the exact frame we are waiting for.
        player.Pause();
        player.timeReference = VideoTimeReference.Freerun;
        player.timeUpdateMode = VideoTimeUpdateMode.UnscaledGameTime;
        player.playbackSpeed = 1f;
        player.skipOnDrop = false;
        // Frame boundaries can round down to the previous frame in native
        // decoders. Seek inside the frame rather than exactly on its boundary.
        double rate = clip.frameRate > 0 ? clip.frameRate : 30;
        player.time = frame == 0 ? 0 : (frame + .5) / rate;
        // Seek while paused. Playing just to obtain a frame lets the decoder
        // run away if the platform omits that exact frameReady callback.
        player.Pause();
    }

    private void OnFrameReady(VideoPlayer source, long frame)
    {
        if (source != player || failed) return;
        lastFrameAt = Time.realtimeSinceStartupAsDouble;
        decodedFrame = frame;
        if (pending && frame == requestedFrame)
        {
            CompleteSeek(source);
        }
    }

    private void OnSeekCompleted(VideoPlayer source)
    {
        if (source == player && !failed && pending)
        {
            // seekCompleted can precede presentation and still report the OLD
            // frame. Never step or complete from that transient value.
            seekFinished = true;
            seekFinishedAt = Time.realtimeSinceStartupAsDouble;
        }
    }

    private void SettleSeek()
    {
        double now = Time.realtimeSinceStartupAsDouble;
        if (!seekFinished || now - Math.Max(seekFinishedAt, lastFrameAt) < .15) return;
        long frame = player.frame;
        if (frame == requestedFrame)
        {
            // A backend may omit frameReady for an already presented frame.
            decodedFrame = frame;
            CompleteSeek(player);
        }
        else if (player.canStep && frame >= 0 && frame < requestedFrame && requestedFrame - frame <= 2)
        {
            seekFinishedAt = now;
            player.StepForward();
        }
        else if (!correctedSeek)
        {
            // Correct a settled overshoot once, without accepting the wrong
            // frame or relying on the same timestamp rounding a second time.
            correctedSeek = true;
            seekFinished = false;
            double rate = clip.frameRate > 0 ? clip.frameRate : 30;
            player.time = requestedFrame / rate;
        }
    }

    private void CompleteSeek(VideoPlayer source)
    {
        pending = false;
        seekFinished = false;
        retrying = false;
        source.Pause();
    }

    private void CheckTimeout()
    {
        if ((!player.isPrepared || pending) && Time.realtimeSinceStartupAsDouble - operationStarted > (pending ? 5 : 15))
        {
            if (!requiresFrames)
            {
                // A stopped preview does not need an exact frame immediately.
                // Keep its last image and defer another attempt until Play.
                waitingForPlay = true;
                pending = seekFinished = false;
                IsFrameReady = true;
                player.Pause();
                return;
            }
            if (!retrying)
            {
                // Some platform decoders stop returning frames after an end or
                // backward seek. Re-prepare once, then seek the current timeline.
                retrying = true;
                pending = false;
                seekFinished = false;
                decodedFrame = -1;
                resyncRequired = true;
                ReleasePlayer();
                CreatePlayer();
                return;
            }
            OnError(player, "Timed out preparing/seeking the background video (requested " + requestedFrame +
                ", decoded " + decodedFrame + ", player " + player.frame + ").");
        }
    }

    private void OnError(VideoPlayer source, string message)
    {
        if (source != player) return;
        if (!requiresFrames)
        {
            waitingForPlay = true;
            pending = seekFinished = false;
            IsFrameReady = true;
            source.Pause();
            return;
        }
        failed = true;
        visible = false;
        IsFrameReady = true;
        source.Stop();
        Debug.LogError("MMD 视频背景不可用，已回退到图片/纯色背景。Video background unavailable: " + message, owner);
        // Do not silently produce a recording with a missing or stale background.
        if (owner.IsRecording) owner.EndPreview();
    }

    private void ReleasePlayer()
    {
        if (player != null)
        {
            player.frameReady -= OnFrameReady;
            player.seekCompleted -= OnSeekCompleted;
            player.errorReceived -= OnError;
            player.Stop();
            player.targetTexture = null;
        }
        if (decoderObject != null) UnityEngine.Object.Destroy(decoderObject);
        player = null;
        decoderObject = null;
    }

    public void Dispose()
    {
        ReleasePlayer();
        if (target != null)
        {
            target.Release();
            UnityEngine.Object.Destroy(target);
        }
        target = null;
        clip = null;
        pending = visible = false;
        resyncRequired = retrying = false;
        seekFinished = correctedSeek = false;
        waitingForPlay = wasPlaying = false;
        lastFrameAt = seekFinishedAt = 0;
        requestedFrame = decodedFrame = -1;
        IsFrameReady = true;
        lastTimeline = double.NaN;
        previousExactFrame = true;
    }
}
