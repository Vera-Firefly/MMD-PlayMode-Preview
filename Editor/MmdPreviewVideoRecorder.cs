using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Debug = UnityEngine.Debug;

/// <summary>One rendered Unity frame per output frame, with encoding off the main thread.</summary>
[InitializeOnLoad]
internal static class MmdPreviewVideoRecorder
{
    internal const string OutputDirectoryPreference = "MmdPlayModePreview.VideoOutputDirectory";
    internal static string LastError { get; private set; }
    internal static string LastOutputPath { get; private set; }
    internal static bool IsCompressing => compressionTask != null;
    internal static bool IsEncoderAvailable => File.Exists(BundledEncoderPath);
    private static string bundledEncoderPath;
    internal static string BundledEncoderPath
    {
        get
        {
            if (Application.platform != RuntimePlatform.WindowsEditor || !Environment.Is64BitOperatingSystem)
                return null;
            if (bundledEncoderPath != null) return bundledEncoderPath;
            var package = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(MmdPreviewVideoRecorder).Assembly);
            string root = package?.resolvedPath ?? Path.GetFullPath("Packages/com.verafirefly.mmd-playmode-preview");
            return bundledEncoderPath = Path.Combine(root, "Editor", "Tools", "FFmpeg~", "Windows", "x86_64", "ffmpeg.exe");
        }
    }
    private static Task<string> compressionTask;
    private static readonly Dictionary<MmdPlayModePreview, Recording> Recordings =
        new Dictionary<MmdPlayModePreview, Recording>();

    static MmdPreviewVideoRecorder()
    {
        MmdPlayModePreview.EditorStartRecording = Start;
        MmdPlayModePreview.EditorIsRecordingAvailable = () => IsEncoderAvailable;
        MmdPlayModePreview.EditorCaptureRecordingFrame = Capture;
        MmdPlayModePreview.EditorStopRecording = Stop;
        AssemblyReloadEvents.beforeAssemblyReload += StopAll;
        EditorApplication.quitting += StopAll;
        EditorApplication.update += UpdateCompression;
        EditorApplication.playModeStateChanged += state =>
        {
            if (state == PlayModeStateChange.ExitingPlayMode) StopAll();
        };
    }

    private static bool Start(MmdPlayModePreview preview)
    {
        LastError = null;
        try
        {
            if (preview.enableSpoutOutput) throw new InvalidOperationException("请先关闭 OBS 画面输出再录制。Disable OBS output before offline recording.");
            if (Recordings.Count != 0) throw new InvalidOperationException("另一个预览正在录制。Another preview is recording.");
            if (string.IsNullOrWhiteSpace(preview.recordingOutputDirectory) ||
                !Path.IsPathRooted(preview.recordingOutputDirectory) || !Directory.Exists(preview.recordingOutputDirectory))
                throw new IOException("请先选择有效的输出文件夹。Select an output folder first.");
            if (preview.PreviewCamera == null) throw new InvalidOperationException("Preview camera is unavailable.");
            if (UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline != null)
                throw new NotSupportedException("录制目前仅支持 Built-in Render Pipeline。Recording requires the Built-in Render Pipeline.");
            Recordings.Add(preview, new Recording(preview));
            return true;
        }
        catch (Exception exception) { ReportError(preview, exception); return false; }
    }

    private static void Capture(MmdPlayModePreview preview, float time, float speed)
    {
        if (!Recordings.TryGetValue(preview, out Recording recording)) return;
        try { recording.Capture(); }
        catch (Exception exception)
        {
            ReportError(preview, exception);
            preview.EndPreview();
        }
    }

    private static void Stop(MmdPlayModePreview preview)
    {
        if (!Recordings.TryGetValue(preview, out Recording recording)) return;
        Recordings.Remove(preview);
        try
        {
            recording.Dispose();
            if (recording.FrameCount > 0)
            {
                LastOutputPath = recording.OutputPath;
                Debug.Log("MMD Preview video saved: " + LastOutputPath, preview);
                if (preview != null && preview.compressRecordedVideo) CompressLastOutput();
            }
        }
        catch (Exception exception) { ReportError(preview, exception); }
    }

    private static void StopAll()
    {
        foreach (MmdPlayModePreview preview in new List<MmdPlayModePreview>(Recordings.Keys))
        {
            if (preview != null) preview.EndPreview();
            Stop(preview);
        }
    }

    private static void ReportError(MmdPlayModePreview preview, Exception exception)
    {
        LastError = "视频录制失败 / Video recording failed: " + exception.Message;
        Debug.LogError(LastError, preview);
    }

    internal static void CompressLastOutput()
    {
        if (IsCompressing || string.IsNullOrEmpty(LastOutputPath)) return;
        try
        {
            string executable = FindEncoder();
            string input = LastOutputPath;
            if (!File.Exists(input)) throw new FileNotFoundException("视频文件不存在。Video file no longer exists.", input);
            string output = Path.Combine(Path.GetDirectoryName(input), Path.GetFileNameWithoutExtension(input) +
                "_compressed_" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".mp4");
            LastError = null;
            compressionTask = Task.Run(() =>
            {
                using (var process = new Process
                {
                    StartInfo = new ProcessStartInfo(executable,
                        "-hide_banner -loglevel error -nostdin -n -i " + Quote(input) +
                        " -map 0:v:0 -map 0:a? -c:v libx264 -preset fast -crf 20 -threads 2 -pix_fmt yuv420p" +
                        " -c:a aac -b:a 192k -movflags +faststart " + Quote(output))
                    {
                        UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true
                    }
                })
                {
                    process.Start();
                    string error = process.StandardError.ReadToEnd();
                    process.WaitForExit();
                    if (process.ExitCode != 0) throw new IOException(error);
                }
                return output;
            });
        }
        catch (Exception exception) { ReportError(null, exception); }
    }

    private static void UpdateCompression()
    {
        if (compressionTask == null || !compressionTask.IsCompleted) return;
        try
        {
            LastOutputPath = compressionTask.GetAwaiter().GetResult();
            Debug.Log("MMD Preview compressed video saved: " + LastOutputPath);
        }
        catch (Exception exception) { ReportError(null, exception); }
        finally { compressionTask = null; }
    }

    private static string Quote(string path) => "\"" + path.Replace("\"", "") + "\"";

    private static string FindEncoder()
    {
        if (IsEncoderAvailable) return BundledEncoderPath;
        throw new FileNotFoundException("内置 FFmpeg 不可用，视频录制和压缩已禁用，其它预览功能可正常使用。Bundled FFmpeg is unavailable; video recording and compression are disabled.");
    }

    private sealed class Recording : IDisposable
    {
        private const int SampleRate = 48000;
        private const int Channels = 2;
        private const int SamplesPerFrame = SampleRate / MmdPlayModePreview.RecordingFrameRate;
        internal readonly string OutputPath;
        internal int FrameCount { get; private set; }
        private readonly string encoderPath;
        private readonly string videoPath;
        private readonly string audioPath;
        private readonly float speed;
        private readonly float startTime;
        private readonly Camera camera;
        private readonly RenderTexture previousTarget;
        private readonly CameraClearFlags previousClearFlags;
        private readonly Rect previousRect;
        private readonly float previousAspect;
        private readonly float previousCaptureDelta;
        private readonly float previousFixedDelta;
        private readonly float previousTimeScale;
        private readonly List<AnimatorClock> animatorClocks = new List<AnimatorClock>();
        private sealed class AnimatorClock
        {
            internal Animator animator;
            internal AnimatorUpdateMode mode;
            internal UnityEngine.Playables.PlayableGraph graph;
            internal UnityEngine.Playables.DirectorUpdateMode graphMode;
        }
        private readonly bool previousRunInBackground;
        private readonly int previousVSyncCount;
        private readonly int previousTargetFrameRate;
        private readonly int previousRenderFrameInterval;
        private readonly BlockingCollection<byte[]> frames = new BlockingCollection<byte[]>(3);
        private readonly ConcurrentBag<byte[]> bufferPool = new ConcurrentBag<byte[]>();
        private readonly Queue<ReadbackFrame> readbacks = new Queue<ReadbackFrame>();
        private sealed class ReadbackFrame
        {
            internal AsyncGPUReadbackRequest request;
            internal byte[] bytes;
            internal bool ready;
            internal bool failed;
        }
        private readonly StringBuilder encoderErrors = new StringBuilder();
        private Process encoder;
        private Task writer;
        private RenderTexture target;
        private Texture2D pixels;
        private float[] sourceAudio;
        private int sourceChannels;
        private int sourceFrequency;
        private int sourceSamples;
        private bool stateApplied;
        private bool disposed;

        internal Recording(MmdPlayModePreview preview)
        {
            encoderPath = FindEncoder();
            OutputPath = Path.Combine(Path.GetFullPath(preview.recordingOutputDirectory),
                "MMD_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".mp4");
            videoPath = OutputPath + ".video.mp4";
            audioPath = OutputPath + ".audio.wav";
            speed = preview.PlaybackSpeed;
            startTime = preview.IsPlaybackEnded ? 0f : preview.PlaybackTime;
            camera = preview.PreviewCamera;
            previousTarget = camera.targetTexture;
            previousClearFlags = camera.clearFlags;
            previousRect = camera.rect;
            previousAspect = camera.aspect;
            previousCaptureDelta = Time.captureDeltaTime;
            previousFixedDelta = Time.fixedDeltaTime;
            previousTimeScale = Time.timeScale;
            previousRunInBackground = Application.runInBackground;
            previousVSyncCount = QualitySettings.vSyncCount;
            previousTargetFrameRate = Application.targetFrameRate;
            previousRenderFrameInterval = OnDemandRendering.renderFrameInterval;
            try
            {
                AudioClip clip = preview.audioClip;
                if (clip != null)
                {
                    sourceChannels = clip.channels;
                    sourceFrequency = clip.frequency;
                    sourceSamples = clip.samples;
                    sourceAudio = new float[checked(sourceSamples * sourceChannels)];
                    if (!clip.GetData(sourceAudio, 0)) throw new InvalidOperationException(
                        "请将 AudioClip 的 Load Type 设为 Decompress On Load 并等待加载。Audio must be decompressed and loaded.");
                }
                // Use native detail, capped at 1080p. A small Game View must not
                // silently multiply the avatar's rendering cost.
                int height = Math.Max(2, Math.Min(1080, camera.pixelHeight));
                height = (height + 1) / 2 * 2;
                int width = Math.Max(2, (int)Math.Round(height * (double)previousAspect / 2) * 2);
                target = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB)
                {
                    name = "MMD Full Resolution Recording",
                    hideFlags = HideFlags.HideAndDontSave,
                    antiAliasing = 1,
                    useMipMap = false,
                    autoGenerateMips = false
                };
                if (!target.Create()) throw new IOException("Could not allocate recording render target.");
                pixels = new Texture2D(width, height, TextureFormat.RGBA32, false, false) { hideFlags = HideFlags.HideAndDontSave };
                encoder = StartProcess("-hide_banner -loglevel error -nostdin -y -f rawvideo -pixel_format rgba -video_size " +
                    width + "x" + height + " -framerate " + MmdPlayModePreview.RecordingFrameRate +
                    " -i pipe:0 -an -vf vflip -c:v libx264 -preset ultrafast -crf 16 -g 60 -bf 0 -pix_fmt yuv420p " + Quote(videoPath), true);
                encoder.ErrorDataReceived += (_, args) =>
                {
                    if (args.Data != null) lock (encoderErrors) encoderErrors.AppendLine(args.Data);
                };
                encoder.BeginErrorReadLine();
                writer = Task.Run(() =>
                {
                    try
                    {
                        foreach (byte[] frame in frames.GetConsumingEnumerable())
                        {
                            try { encoder.StandardInput.BaseStream.Write(frame, 0, frame.Length); }
                            finally { bufferPool.Add(frame); }
                        }
                    }
                    catch { frames.CompleteAdding(); throw; }
                    finally { encoder.StandardInput.Close(); }
                });
                stateApplied = true;
                camera.targetTexture = target;
                camera.rect = new Rect(0f, 0f, 1f, 1f);
                camera.aspect = previousAspect;
                // A persistent target must not retain the previous frame's pixels.
                if (camera.clearFlags == CameraClearFlags.Depth || camera.clearFlags == CameraClearFlags.Nothing)
                    camera.clearFlags = CameraClearFlags.SolidColor;
                Time.captureDeltaTime = speed / MmdPlayModePreview.RecordingFrameRate;
                Time.fixedDeltaTime = Time.captureDeltaTime;
                // A decoder may need several Unity frames for one output frame.
                // Only accepted captures grant the next simulation step.
                Time.timeScale = 0f;
                foreach (Animator animator in preview.avatar.GetComponentsInChildren<Animator>(true))
                {
                    var clock = new AnimatorClock { animator = animator, mode = animator.updateMode, graph = animator.playableGraph };
                    animator.updateMode = AnimatorUpdateMode.Normal;
                    if (clock.graph.IsValid())
                    {
                        clock.graphMode = clock.graph.GetTimeUpdateMode();
                        if (clock.graphMode == UnityEngine.Playables.DirectorUpdateMode.UnscaledGameTime)
                            clock.graph.SetTimeUpdateMode(UnityEngine.Playables.DirectorUpdateMode.GameTime);
                    }
                    animatorClocks.Add(clock);
                }
                Application.runInBackground = true;
                QualitySettings.vSyncCount = 0;
                Application.targetFrameRate = -1;
                OnDemandRendering.renderFrameInterval = 1;
            }
            catch
            {
                Cleanup();
                throw;
            }
        }

        internal void Capture()
        {
            if (writer.IsFaulted) throw new IOException("FFmpeg frame write failed. " + encoderErrors, writer.Exception);
            DrainReadbacks(false);
            if (readbacks.Count >= 3) DrainReadbacks(true);
            RenderTexture previousActive = RenderTexture.active;
            try
            {
                if (SystemInfo.supportsAsyncGPUReadback)
                {
                    var frame = new ReadbackFrame();
                    readbacks.Enqueue(frame);
                    frame.request = AsyncGPUReadback.Request(target, 0, TextureFormat.RGBA32, request =>
                    {
                        frame.failed = request.hasError;
                        if (!frame.failed)
                        {
                            var data = request.GetData<byte>();
                            frame.bytes = RentBuffer(data.Length);
                            data.CopyTo(frame.bytes);
                        }
                        frame.ready = true;
                    });
                }
                else
                {
                    RenderTexture.active = target;
                    pixels.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0, false);
                    var data = pixels.GetRawTextureData<byte>();
                    byte[] bytes = RentBuffer(data.Length);
                    data.CopyTo(bytes);
                    Enqueue(bytes);
                }
                FrameCount++;
                // Keep the same rendered image visible in the Game View.
                Graphics.Blit(target, previousTarget);
            }
            finally { RenderTexture.active = previousActive; }
        }

        private byte[] RentBuffer(int size)
        {
            return bufferPool.TryTake(out byte[] bytes) && bytes.Length == size ? bytes : new byte[size];
        }

        private void Enqueue(byte[] bytes)
        {
            while (!frames.TryAdd(bytes, 100))
                if (writer.IsFaulted) throw new IOException("FFmpeg stopped receiving frames. " + encoderErrors, writer.Exception);
        }

        private void DrainReadbacks(bool wait)
        {
            while (readbacks.Count > 0)
            {
                ReadbackFrame frame = readbacks.Peek();
                if (!frame.ready && wait) frame.request.WaitForCompletion();
                if (!frame.ready) break;
                readbacks.Dequeue();
                if (frame.failed) throw new IOException("GPU video readback failed.");
                Enqueue(frame.bytes);
            }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            try
            {
                DrainReadbacks(true);
                frames.CompleteAdding();
                writer?.GetAwaiter().GetResult();
                if (encoder != null)
                {
                    if (!encoder.WaitForExit(60000)) throw new IOException("FFmpeg did not finish encoding.");
                    encoder.WaitForExit();
                    if (encoder.ExitCode != 0) throw new IOException("FFmpeg: " + encoderErrors);
                }
                if (FrameCount == 0) return;
                if (sourceAudio == null) File.Move(videoPath, OutputPath);
                else
                {
                    WriteAudio();
                    using (Process mux = StartProcess("-hide_banner -loglevel error -nostdin -y -i " + Quote(videoPath) +
                        " -i " + Quote(audioPath) + " -map 0:v:0 -map 1:a:0 -c:v copy -c:a aac -b:a 320k -movflags +faststart " + Quote(OutputPath), false))
                    {
                        string error = mux.StandardError.ReadToEnd();
                        mux.WaitForExit();
                        if (mux.ExitCode != 0) throw new IOException("FFmpeg audio mux: " + error);
                    }
                }
                if (File.Exists(videoPath)) File.Delete(videoPath);
                if (File.Exists(audioPath)) File.Delete(audioPath);
            }
            finally { Cleanup(); }
        }

        private void WriteAudio()
        {
            long sampleCount = (long)FrameCount * SamplesPerFrame;
            long byteCount = sampleCount * Channels * sizeof(short);
            if (byteCount > uint.MaxValue - 36) throw new IOException("Recording exceeds WAV size limit.");
            using (var output = new BinaryWriter(File.Create(audioPath)))
            {
                output.Write(Encoding.ASCII.GetBytes("RIFF")); output.Write((uint)(36 + byteCount));
                output.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); output.Write(16);
                output.Write((short)1); output.Write((short)Channels); output.Write(SampleRate);
                output.Write(SampleRate * Channels * sizeof(short)); output.Write((short)(Channels * sizeof(short)));
                output.Write((short)16); output.Write(Encoding.ASCII.GetBytes("data")); output.Write((uint)byteCount);
                for (long sample = 0; sample < sampleCount; sample++)
                {
                    double position = (startTime + (double)sample * speed / SampleRate) * sourceFrequency;
                    long left = (long)Math.Floor(position);
                    double fraction = position - left;
                    for (int channel = 0; channel < Channels; channel++)
                    {
                        double value = 0;
                        if (left >= 0 && left < sourceSamples)
                        {
                            int input = Math.Min(channel, sourceChannels - 1);
                            long right = Math.Min(left + 1, sourceSamples - 1);
                            value = sourceAudio[left * sourceChannels + input] * (1 - fraction) +
                                sourceAudio[right * sourceChannels + input] * fraction;
                        }
                        output.Write((short)Math.Round(Math.Max(-1d, Math.Min(1d, value)) * short.MaxValue));
                    }
                }
            }
        }

        private Process StartProcess(string arguments, bool input)
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo(encoderPath, arguments)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = input,
                    RedirectStandardError = true
                }
            };
            process.Start();
            return process;
        }

        private void Cleanup()
        {
            foreach (ReadbackFrame frame in readbacks)
                if (!frame.ready) frame.request.WaitForCompletion();
            readbacks.Clear();
            if (stateApplied)
            {
                if (camera != null)
                {
                    camera.targetTexture = previousTarget;
                    camera.clearFlags = previousClearFlags;
                    camera.rect = previousRect;
                    camera.aspect = previousAspect;
                }
                Time.captureDeltaTime = previousCaptureDelta;
                Time.fixedDeltaTime = previousFixedDelta;
                Time.timeScale = previousTimeScale;
                foreach (AnimatorClock clock in animatorClocks)
                {
                    if (clock.animator != null) clock.animator.updateMode = clock.mode;
                    if (clock.graph.IsValid()) clock.graph.SetTimeUpdateMode(clock.graphMode);
                }
                animatorClocks.Clear();
                Application.runInBackground = previousRunInBackground;
                QualitySettings.vSyncCount = previousVSyncCount;
                Application.targetFrameRate = previousTargetFrameRate;
                OnDemandRendering.renderFrameInterval = previousRenderFrameInterval;
                stateApplied = false;
            }
            if (encoder != null)
            {
                if (!frames.IsAddingCompleted) frames.CompleteAdding();
                if (!encoder.HasExited) encoder.Kill();
                try { writer?.Wait(5000); } catch (AggregateException) { }
                encoder.Dispose();
                encoder = null;
            }
            frames.Dispose();
            if (target != null) { target.Release(); UnityEngine.Object.DestroyImmediate(target); target = null; }
            if (pixels != null) UnityEngine.Object.DestroyImmediate(pixels);
            pixels = null;
            sourceAudio = null;
            if (FrameCount == 0 && File.Exists(videoPath)) File.Delete(videoPath);
        }
    }
}
