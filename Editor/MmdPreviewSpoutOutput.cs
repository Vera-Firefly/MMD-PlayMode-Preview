using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

/// <summary>Editor preview camera feed, independent of the offline recording clock.</summary>
[InitializeOnLoad]
internal static class MmdPreviewSpoutOutput
{
    private static readonly Dictionary<MmdPlayModePreview, Output> Outputs = new Dictionary<MmdPlayModePreview, Output>();
    private static readonly Dictionary<MmdPlayModePreview, string> Errors = new Dictionary<MmdPlayModePreview, string>();
    private static readonly List<RetiredOutput> Retired = new List<RetiredOutput>();
    private const string ShaderName = "Hidden/MMDPreview/SpoutOutput";
    private static string nativePath;

    internal static bool IsAvailable => Application.platform == RuntimePlatform.WindowsEditor &&
        IntPtr.Size == 8 && SystemInfo.supportsAsyncGPUReadback && GraphicsSettings.currentRenderPipeline == null &&
        (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Direct3D11 ||
         SystemInfo.graphicsDeviceType == GraphicsDeviceType.Direct3D12) && File.Exists(NativePath);

    private static string NativePath
    {
        get
        {
            if (nativePath != null) return nativePath;
            var package = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(MmdPreviewSpoutOutput).Assembly);
            string root = package?.resolvedPath ?? Path.GetFullPath("Packages/com.verafirefly.mmd-playmode-preview");
            return nativePath = Path.Combine(root, "Editor", "Plugins", "x86_64", "MmdPreviewSpout.dll");
        }
    }

    static MmdPreviewSpoutOutput()
    {
        MmdPlayModePreview.EditorUpdateVideoOutput = Update;
        MmdPlayModePreview.EditorReleaseVideoOutput = Stop;
        Camera.onPostRender += OnCameraRendered;
        EditorApplication.update += CollectRetired;
        AssemblyReloadEvents.beforeAssemblyReload += StopAll;
        EditorApplication.quitting += StopAll;
        EditorApplication.playModeStateChanged += state =>
        {
            if (state == PlayModeStateChange.ExitingPlayMode) StopAll();
        };
    }

    internal static string GetError(MmdPlayModePreview preview) => Errors.TryGetValue(preview, out string error) ? error : null;
    internal static string GetSenderName(MmdPlayModePreview preview) => Outputs.TryGetValue(preview, out Output output) ? output.Name : null;

    private static void Update(MmdPlayModePreview preview)
    {
        if (!preview.enableSpoutOutput || !preview.isActiveAndEnabled || !preview.IsPreviewSessionActive)
        {
            Stop(preview);
            return;
        }
        // Scripts may change settings outside the Inspector while recording.
        if (preview.IsRecording) { preview.enableSpoutOutput = false; Stop(preview); return; }
        preview.recordVideo = false;
        var size = preview.spoutOutputSize;
        string name = (preview.spoutSenderName ?? "").Trim();
        if (Outputs.TryGetValue(preview, out Output current) && current.Camera == preview.PreviewCamera &&
            current.RequestedName == name && current.Size == size) return;
        try
        {
            if (!IsAvailable) throw new NotSupportedException("Spout 输出不可用：需要 Windows x64、Direct3D 11/12、Built-in Render Pipeline 和随包原生插件。Spout requires Windows x64, D3D11/12, Built-in RP and the bundled native plugin.");
            if (size.x < 16 || size.y < 16 || size.x > 4096 || size.y > 4096)
                throw new ArgumentException("输出宽高须为 16–4096。Output dimensions must be 16–4096.");
            if (name.Length == 0 || name.Length > 180 || Array.Exists(name.ToCharArray(), c => c < 32 || c > 126))
                throw new ArgumentException("输出名称须为 1–180 个英文、数字或 ASCII 符号。Use 1–180 printable ASCII characters for the sender name.");
            if (preview.PreviewCamera == null) return;
            Stop(preview);
            if (Outputs.Count != 0) throw new InvalidOperationException("已有预览正在输出。Another preview is already sending video.");
            Outputs.Add(preview, new Output(preview.PreviewCamera, name, size));
            Errors.Remove(preview);
        }
        catch (Exception exception) { Fail(preview, exception); }
    }

    private static void OnCameraRendered(Camera camera)
    {
        // Rendering runs on the main thread. Defer removal until enumeration ends.
        MmdPlayModePreview failed = null;
        Exception error = null;
        foreach (var pair in Outputs)
        {
            if (pair.Value.Camera != camera) continue;
            try { pair.Value.Send(); }
            catch (Exception exception) { failed = pair.Key; error = exception; }
            break;
        }
        if (failed != null) Fail(failed, error);
    }

    private static void Fail(MmdPlayModePreview preview, Exception exception)
    {
        Stop(preview);
        preview.enableSpoutOutput = false;
        Errors[preview] = exception.Message;
        Debug.LogWarning("MMD Preview Spout: " + exception.Message, preview);
    }

    private static void Stop(MmdPlayModePreview preview)
    {
        if (!Outputs.TryGetValue(preview, out Output output)) return;
        Outputs.Remove(preview);
        output.Dispose();
    }

    private static void StopAll()
    {
        foreach (var output in Outputs.Values) output.Dispose();
        Outputs.Clear();
        // A render-thread close event still references the pinned interop block
        // and texture. A one-pixel readback fence retires them after that event.
        foreach (var retired in Retired) retired.WaitAndDispose();
        Retired.Clear();
        Errors.Clear();
    }

    private static void CollectRetired()
    {
        for (int i = Retired.Count - 1; i >= 0; i--)
        {
            if (!Retired[i].Fence.done) continue;
            Retired[i].Dispose();
            Retired.RemoveAt(i);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EventData { public IntPtr sender, texture; }

    private static class Native
    {
        [DllImport("MmdPreviewSpout")] internal static extern IntPtr GetRenderEventCallback();
        [DllImport("MmdPreviewSpout", CharSet = CharSet.Ansi)] internal static extern IntPtr CreateSender(string name, int width, int height);
        [DllImport("MmdPreviewSpout")] internal static extern void GetSenderNames(
            [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] out IntPtr[] names, out int count);
    }

    private static string UniqueName(string requested)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        Native.GetSenderNames(out IntPtr[] names, out int count);
        for (int i = 0; i < count; i++)
        {
            try { used.Add(Marshal.PtrToStringAnsi(names[i])); }
            finally { Marshal.FreeCoTaskMem(names[i]); }
        }
        foreach (var output in Outputs.Values) used.Add(output.Name);
        string name = requested;
        for (int suffix = 2; used.Contains(name); suffix++) name = requested + " (" + suffix + ")";
        return name;
    }

    private sealed class Output : IDisposable
    {
        internal readonly Camera Camera;
        internal readonly string RequestedName, Name;
        internal readonly Vector2Int Size;
        private readonly RenderTexture previousTarget;
        private readonly Rect previousRect;
        private readonly float previousAspect;
        private readonly CameraClearFlags previousClearFlags;
        private readonly bool previousRunInBackground;
        private RenderTexture cameraTarget, sharedBuffer;
        private Material material;
        private CommandBuffer commands;
        private IntPtr sender, callback;
        private GCHandle data;
        private bool applied;

        internal Output(Camera camera, string requestedName, Vector2Int size)
        {
            Camera = camera; RequestedName = requestedName; Size = size;
            previousTarget = camera.targetTexture; previousRect = camera.rect;
            previousAspect = camera.aspect; previousClearFlags = camera.clearFlags;
            previousRunInBackground = Application.runInBackground;
            try
            {
                Name = UniqueName(requestedName);
                var shader = Shader.Find(ShaderName);
                if (shader == null || !shader.isSupported) throw new NotSupportedException("Spout output shader is unavailable.");
                material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
                cameraTarget = new RenderTexture(size.x, size.y, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB)
                { name = "MMD OBS Camera", hideFlags = HideFlags.HideAndDontSave, antiAliasing = 1 };
                sharedBuffer = new RenderTexture(new RenderTextureDescriptor(size.x, size.y,
                    QualitySettings.activeColorSpace == ColorSpace.Linear ? GraphicsFormat.R8G8B8A8_SRGB : GraphicsFormat.R8G8B8A8_UNorm, 0))
                { name = "MMD Spout Shared Frame", hideFlags = HideFlags.HideAndDontSave };
                if (!cameraTarget.Create() || !sharedBuffer.Create()) throw new IOException("Could not allocate Spout output textures.");
                callback = Native.GetRenderEventCallback();
                if (callback == IntPtr.Zero) throw new NotSupportedException("Spout native render callback is unavailable.");
                commands = new CommandBuffer { name = "MMD Preview Spout" };
                sender = Native.CreateSender(Name, size.x, size.y);
                if (sender == IntPtr.Zero) throw new IOException("Could not create Spout sender.");
                data = GCHandle.Alloc(new EventData { sender = sender, texture = sharedBuffer.GetNativeTexturePtr() }, GCHandleType.Pinned);
                applied = true;
                camera.targetTexture = cameraTarget;
                camera.rect = new Rect(0, 0, 1, 1);
                camera.aspect = (float)size.x / size.y;
                if (camera.clearFlags == CameraClearFlags.Depth || camera.clearFlags == CameraClearFlags.Nothing)
                    camera.clearFlags = CameraClearFlags.SolidColor;
                Application.runInBackground = true;
            }
            catch { Dispose(); throw; }
        }

        internal void Send()
        {
            if (Camera.targetTexture != cameraTarget) throw new InvalidOperationException("预览摄像机输出被其它组件修改，已停止 Spout。Another component changed the camera target; Spout stopped.");
            commands.Clear();
            commands.Blit(cameraTarget, sharedBuffer, material);
            commands.IssuePluginEventAndData(callback, 0, data.AddrOfPinnedObject());
            Graphics.ExecuteCommandBuffer(commands);
            // Keep Game View visible without rendering the avatar a second time.
            var active = RenderTexture.active;
            bool srgb = GL.sRGBWrite;
            try
            {
                GL.sRGBWrite = QualitySettings.activeColorSpace == ColorSpace.Linear;
                Graphics.Blit(cameraTarget, previousTarget);
            }
            finally { GL.sRGBWrite = srgb; RenderTexture.active = active; }
        }

        public void Dispose()
        {
            if (applied)
            {
                if (Camera != null && Camera.targetTexture == cameraTarget)
                {
                    Camera.targetTexture = previousTarget; Camera.rect = previousRect;
                    Camera.aspect = previousAspect; Camera.clearFlags = previousClearFlags;
                }
                Application.runInBackground = previousRunInBackground;
                applied = false;
            }
            if (sender != IntPtr.Zero && data.IsAllocated)
            {
                commands.Clear();
                commands.IssuePluginEventAndData(callback, 2, data.AddrOfPinnedObject());
                Graphics.ExecuteCommandBuffer(commands);
                Retired.Add(new RetiredOutput(data, cameraTarget, sharedBuffer, material));
                sender = IntPtr.Zero; data = default;
                cameraTarget = sharedBuffer = null; material = null;
            }
            if (data.IsAllocated) data.Free();
            if (cameraTarget != null) UnityEngine.Object.DestroyImmediate(cameraTarget);
            if (sharedBuffer != null) UnityEngine.Object.DestroyImmediate(sharedBuffer);
            if (material != null) UnityEngine.Object.DestroyImmediate(material);
            commands?.Dispose(); commands = null;
        }
    }

    private sealed class RetiredOutput : IDisposable
    {
        internal readonly AsyncGPUReadbackRequest Fence;
        private GCHandle data;
        private readonly RenderTexture cameraTarget, sharedBuffer;
        private readonly Material material;
        internal RetiredOutput(GCHandle data, RenderTexture cameraTarget, RenderTexture sharedBuffer, Material material)
        {
            this.data = data; this.cameraTarget = cameraTarget; this.sharedBuffer = sharedBuffer; this.material = material;
            Fence = AsyncGPUReadback.Request(sharedBuffer, 0, 0, 1, 0, 1, 0, 1);
        }
        internal void WaitAndDispose() { Fence.WaitForCompletion(); Dispose(); }
        public void Dispose()
        {
            if (data.IsAllocated) data.Free();
            UnityEngine.Object.DestroyImmediate(cameraTarget);
            UnityEngine.Object.DestroyImmediate(sharedBuffer);
            UnityEngine.Object.DestroyImmediate(material);
        }
    }
}
