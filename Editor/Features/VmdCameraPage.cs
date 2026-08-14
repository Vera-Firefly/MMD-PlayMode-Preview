using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

[Serializable]
internal sealed class VmdCameraPage : ConverterFeaturePage
{
    [SerializeField] private List<DefaultAsset> cameraVmdAssets = new List<DefaultAsset>();
    [SerializeField] private Vector2 cameraVmdListScroll;
    [SerializeField] private Vector2 pageScroll;
    [SerializeField] private MotionStorageLocation cameraOutputStorageLocation;
    [SerializeField] private string customCameraOutputStorageFolder = DefaultMotionStorageFolder;
    [SerializeField] private float unitScale = 0.08f;

    private struct CameraFrame
    {
        public uint Frame;
        public float Distance;
        public Vector3 Center;
        public Vector3 RotationRadians;
        public byte[] Interpolation;
        public float FieldOfView;
        public byte Perspective;

        public CameraFrame CloneAt(uint frame)
        {
            return new CameraFrame
            {
                Frame = frame,
                Distance = Distance,
                Center = Center,
                RotationRadians = RotationRadians,
                Interpolation = (byte[])Interpolation.Clone(),
                FieldOfView = FieldOfView,
                Perspective = Perspective
            };
        }
    }

    internal void OnEnable(ConverterWindowContext context, DefaultAsset selected, bool acceptSelection)
    {
        Attach(context);
        if (cameraVmdAssets == null) cameraVmdAssets = new List<DefaultAsset>();
        if (acceptSelection && IsVmdAsset(selected))
            AddVmdAsset(cameraVmdAssets, selected);
    }

    internal void Draw(ConverterWindowContext context)
    {
        Attach(context);
        if (cameraVmdAssets == null) cameraVmdAssets = new List<DefaultAsset>();
        cameraVmdAssets.RemoveAll(asset => asset == null);
        if (targetPreview == null) targetPreview = null;
        pageScroll = EditorGUILayout.BeginScrollView(pageScroll);
        try
        {
            DrawCameraConverter();
        }
        finally
        {
            EditorGUILayout.EndScrollView();
        }
    }

    internal void DrawAction(ConverterWindowContext context)
    {
        Attach(context);
        EditorGUILayout.Space(8f);
        List<string> sourcePaths = GetVmdAssetPaths(cameraVmdAssets);
        using (new EditorGUI.DisabledScope(
                   sourcePaths.Count == 0 || !TryGetCameraOutputStorageFolder(out _)))
        {
            if (GUILayout.Button(L("转换", "Convert", "変換"), GUILayout.Height(30f)))
                ConvertCameras(sourcePaths);
        }
    }

    private void DrawCameraConverter()
    {
        ConverterWindowGui.DrawSectionTitle(L("MMD镜头转换", "MMD Camera Conversion", "MMDカメラ変換"));
        DrawVmdFileList(cameraVmdAssets, ref cameraVmdListScroll);

        ConverterWindowGui.DrawOutlinedSetting(() => DrawStorageLocationSetting(
            L("输出位置", "Output Location", "出力場所"),
            ref cameraOutputStorageLocation,
            ref customCameraOutputStorageFolder,
            L("输出存储位置", "Output Storage Location", "出力保存場所")));

        unitScale = Mathf.Max(0.000001f, EditorGUILayout.FloatField(
            new GUIContent(
                L("单位缩放", "Unit Scale", "単位スケール"),
                L("VRChat 常用 0.08；保留 MMD 原始单位时填写 1。",
                    "Use 0.08 for VRChat, or 1 for original MMD units.",
                    "VRChat は通常 0.08、MMD の元単位は 1。")),
            unitScale));
        targetPreview = (MmdPlayModePreview)EditorGUILayout.ObjectField(
            L("应用到预览器", "Apply to Preview", "プレビューに適用"),
            targetPreview,
            typeof(MmdPlayModePreview),
            true);

        int cameraFileCount = GetVmdAssetPaths(cameraVmdAssets).Count;
        EditorGUILayout.LabelField(
            L("输出", "Output", "出力"),
            L($"将生成 {cameraFileCount} 个镜头动画",
                $"{cameraFileCount} camera animations will be created",
                $"{cameraFileCount} 個のカメラアニメーションを生成します"));

    }

    private bool DrawVmdFileList(List<DefaultAsset> assets, ref Vector2 scroll)
    {
        bool changed = false;
        if (assets.Count > 0)
        {
            const float maxListHeight = 220f;
            float rowHeight = EditorGUIUtility.singleLineHeight + EditorGUIUtility.standardVerticalSpacing;
            bool useScrollView = assets.Count * rowHeight > maxListHeight;
            if (useScrollView)
            {
                scroll = EditorGUILayout.BeginScrollView(
                    scroll,
                    false,
                    false,
                    GUILayout.Height(maxListHeight));
            }

            try
            {
                for (int index = 0; index < assets.Count; index++)
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        DefaultAsset next = (DefaultAsset)EditorGUILayout.ObjectField(
                            assets[index],
                            typeof(DefaultAsset),
                            false);
                        if (next != assets[index])
                        {
                            if (next == null || IsVmdAsset(next))
                            {
                                string nextPath = GetVmdAssetPath(next);
                                bool duplicate = !string.IsNullOrEmpty(nextPath) && assets
                                    .Where((_, otherIndex) => otherIndex != index)
                                    .Any(other => string.Equals(
                                        GetVmdAssetPath(other),
                                        nextPath,
                                        StringComparison.OrdinalIgnoreCase));
                                if (!duplicate)
                                {
                                    assets[index] = next;
                                    changed = true;
                                }
                            }
                            else
                            {
                                ShowDialog(
                                    L("VMD文件", "VMD Files", "VMDファイル"),
                                    L("请选择Assets目录中的.vmd文件。",
                                        "Select .vmd files inside Assets.",
                                        "Assets内の.vmdファイルを選択してください。"));
                            }
                        }

                        if (GUILayout.Button("−", GUILayout.Width(24f)))
                        {
                            assets.RemoveAt(index);
                            changed = true;
                            break;
                        }
                    }
                }
            }
            finally
            {
                if (useScrollView)
                    EditorGUILayout.EndScrollView();
            }
        }

        Rect dropRect = GUILayoutUtility.GetRect(0f, 42f, GUILayout.ExpandWidth(true));
        var dropStyle = new GUIStyle(GUI.skin.box) { alignment = TextAnchor.MiddleCenter };
        GUI.Box(
            dropRect,
            L("拖入一个或多个VMD文件", "Drop one or more VMD files", "1つ以上のVMDファイルをドロップ"),
            dropStyle);
        Event currentEvent = Event.current;
        if (dropRect.Contains(currentEvent.mousePosition) &&
            (currentEvent.type == EventType.DragUpdated || currentEvent.type == EventType.DragPerform))
        {
            bool hasVmd = DragAndDrop.objectReferences
                .OfType<DefaultAsset>()
                .Any(IsVmdAsset);
            DragAndDrop.visualMode = hasVmd ? DragAndDropVisualMode.Copy : DragAndDropVisualMode.Rejected;
            if (currentEvent.type == EventType.DragPerform && hasVmd)
            {
                DragAndDrop.AcceptDrag();
                foreach (DefaultAsset asset in DragAndDrop.objectReferences.OfType<DefaultAsset>())
                    changed |= AddVmdAsset(assets, asset);
            }
            currentEvent.Use();
        }

        return changed;
    }

    private void ConvertCameras(IReadOnlyList<string> sourcePaths)
    {
        if (!TryGetCameraOutputStorageFolder(out string outputStorageFolder))
            throw new InvalidOperationException("The camera output storage folder must be inside Assets.");
        int converted = 0;
        int skipped = 0;
        bool cancelled = false;
        var errors = new List<string>();
        AnimationClip lastClip = null;
        string lastOutputPath = string.Empty;
        var writtenPaths = new List<string>(sourcePaths.Count);

        for (int fileIndex = 0; fileIndex < sourcePaths.Count; fileIndex++)
        {
            string sourcePath = sourcePaths[fileIndex];
            string outputPath = GetCameraOutputPath(sourcePath, outputStorageFolder);
            try
            {
                if (AssetDatabase.LoadAssetAtPath<AnimationClip>(outputPath) != null &&
                    !EditorUtility.DisplayDialog(
                        L("覆盖镜头动画", "Overwrite Camera Animation", "カメラアニメーションを上書き"),
                        L("目标文件已经存在：\n", "The target already exists:\n", "対象ファイルは既に存在します:\n") +
                        outputPath,
                        L("覆盖", "Overwrite", "上書き"),
                        L("取消", "Cancel", "キャンセル")))
                {
                    skipped++;
                    continue;
                }

                double lastProgressUpdate = double.NegativeInfinity;
                string lastProgressStage = string.Empty;
                Action<float, string> progress = (value, message) =>
                {
                    double now = EditorApplication.timeSinceStartup;
                    string stage = GetProgressStage(message);
                    bool stageChanged = !string.Equals(stage, lastProgressStage, StringComparison.Ordinal);
                    if (value < 1f && !stageChanged && now - lastProgressUpdate < 0.08d) return;
                    lastProgressUpdate = now;
                    lastProgressStage = stage;
                    float overall = (fileIndex + Mathf.Clamp01(value)) /
                                    Mathf.Max(1f, sourcePaths.Count);
                    bool cancelRequested = EditorUtility.DisplayCancelableProgressBar(
                        L("MMD镜头转换", "MMD Camera Conversion", "MMDカメラ変換"),
                        $"[{fileIndex + 1}/{sourcePaths.Count}] {Path.GetFileName(sourcePath)} — {message}",
                        overall);
                    if (cancelRequested && value < 0.96f)
                        throw new OperationCanceledException();
                };

                lastClip = ConvertVmdCamera(
                    sourcePath, outputPath, unitScale, progress, true);
                lastOutputPath = outputPath;
                writtenPaths.Add(outputPath);
                converted++;
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
                break;
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                errors.Add(Path.GetFileName(sourcePath) + ": " + exception.Message);
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        if (writtenPaths.Count > 0)
        {
            try
            {
                EditorUtility.DisplayProgressBar(
                    L("MMD镜头转换", "MMD Camera Conversion", "MMDカメラ変換"),
                    L("正在保存批量镜头动画", "Saving camera animation batch", "カメラアニメーションを保存中"),
                    0.99f);
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                AnimationClip imported = AssetDatabase.LoadAssetAtPath<AnimationClip>(lastOutputPath);
                if (imported != null) lastClip = imported;
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                errors.Add("Batch asset write failed: " + exception.Message);
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        if (lastClip != null)
        {
            Selection.activeObject = lastClip;
            EditorGUIUtility.PingObject(lastClip);
            if (targetPreview != null)
            {
                Undo.RecordObject(targetPreview, "Assign MMD Camera Animation");
                targetPreview.cameraAnimationClip = lastClip;
                EditorUtility.SetDirty(targetPreview);
            }
        }
        ShowBatchResult(
            L("MMD镜头转换", "MMD Camera Conversion", "MMDカメラ変換"),
            converted,
            skipped,
            errors,
            cancelled);
    }

    internal static AnimationClip ConvertVmdCamera(
        string sourceAssetPath,
        string outputAssetPath,
        float scale,
        Action<float, string> progress = null,
        bool deferAssetImport = false)
    {
        if (string.IsNullOrEmpty(sourceAssetPath) || !sourceAssetPath.StartsWith("Assets/", StringComparison.Ordinal))
            throw new ArgumentException("VMD 必须位于当前 Unity 项目的 Assets 目录中。", nameof(sourceAssetPath));
        if (!sourceAssetPath.EndsWith(".vmd", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("源文件不是 .vmd 文件。", nameof(sourceAssetPath));
        if (scale <= 0f)
            throw new ArgumentOutOfRangeException(nameof(scale), "单位缩放必须大于 0。");
        if (string.IsNullOrEmpty(outputAssetPath) ||
            !outputAssetPath.StartsWith("Assets/", StringComparison.Ordinal) ||
            !outputAssetPath.EndsWith(".anim", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("输出动画必须位于 Assets 目录中。", nameof(outputAssetPath));

        string fullSourcePath = Path.GetFullPath(sourceAssetPath);
        progress?.Invoke(0.02f, "Reading VMD");
        List<CameraFrame> sourceFrames = ReadCameraFrames(fullSourcePath);
        List<CameraFrame> frames = SortAndDeduplicateCameraFrames(sourceFrames);

        if (frames.Count == 0)
            throw new InvalidDataException("该 VMD 中没有摄像机关键帧。");
        if (frames[frames.Count - 1].Frame > 1000000)
            throw new InvalidDataException("摄像机动画长度异常，拒绝生成超过 1,000,000 帧的动画。");

        AnimationClip clip = BuildAnimationClip(
            frames,
            scale,
            Path.GetFileNameWithoutExtension(outputAssetPath),
            progress);
        AnimationClipSettings settings = AnimationUtility.GetAnimationClipSettings(clip);
        settings.loopTime = false;
        AnimationUtility.SetAnimationClipSettings(clip, settings);

        EditorCurveBinding[] bindings = AnimationUtility.GetCurveBindings(clip);
        if (bindings.Length != 9)
            throw new InvalidDataException($"生成结果校验失败：应有 9 条曲线，实际为 {bindings.Length} 条。");

        progress?.Invoke(0.96f, "Writing AnimationClip");
        VmdMotionConverter.EnsureAssetFolderExists(
            Path.GetDirectoryName(outputAssetPath)?.Replace('\\', '/'));
        AssetDatabase.DeleteAsset(outputAssetPath);
        AssetDatabase.CreateAsset(clip, outputAssetPath);
        AnimationClip result = clip;
        if (!deferAssetImport)
        {
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(outputAssetPath, ImportAssetOptions.ForceSynchronousImport);
            result = AssetDatabase.LoadAssetAtPath<AnimationClip>(outputAssetPath);
            if (result == null)
                throw new InvalidDataException("AnimationClip 已写入，但 Unity 无法重新加载该资源。");
        }

        progress?.Invoke(1f, "Camera conversion complete");

        Debug.Log(
            $"MMD Camera VMD converted: {sourceAssetPath} -> {outputAssetPath}, " +
            $"{frames.Count} source keys, {frames[frames.Count - 1].Frame + 1} baked frames, scale {scale:0.######}");
        return result;
    }

    private static List<CameraFrame> SortAndDeduplicateCameraFrames(List<CameraFrame> sourceFrames)
    {
        sourceFrames.Sort((left, right) => left.Frame.CompareTo(right.Frame));
        var frames = new List<CameraFrame>(sourceFrames.Count);
        for (int index = 0; index < sourceFrames.Count; index++)
        {
            CameraFrame frame = sourceFrames[index];
            if (frames.Count > 0 && frames[frames.Count - 1].Frame == frame.Frame)
                frames[frames.Count - 1] = frame;
            else
                frames.Add(frame);
        }
        return frames;
    }

    private static AnimationClip BuildAnimationClip(
        IReadOnlyList<CameraFrame> sourceFrames,
        float scale,
        string clipName,
        Action<float, string> progress)
    {
        uint lastFrame = sourceFrames[sourceFrames.Count - 1].Frame;
        int bakedCount = checked((int)lastFrame + 1);
        HashSet<int> cameraCuts = FindCameraCuts(sourceFrames);
        var positionX = new Keyframe[bakedCount];
        var positionY = new Keyframe[bakedCount];
        var positionZ = new Keyframe[bakedCount];
        var rotationX = new Keyframe[bakedCount];
        var rotationY = new Keyframe[bakedCount];
        var rotationZ = new Keyframe[bakedCount];
        var rotationW = new Keyframe[bakedCount];
        var fieldOfView = new Keyframe[bakedCount];
        var orthographic = new Keyframe[bakedCount];

        int rightIndex = sourceFrames.Count > 1 ? 1 : 0;
        for (int frameIndex = 0; frameIndex < bakedCount; frameIndex++)
        {
            uint frame = (uint)frameIndex;
            while (rightIndex < sourceFrames.Count - 1 && frame > sourceFrames[rightIndex].Frame)
                rightIndex++;

            CameraFrame sampled = SampleFrame(sourceFrames, rightIndex, frame);
            float time = frame / VmdFrameRate;
            // VMD camera angles rotate the camera rig in the opposite direction
            // from Unity's Transform Euler angles on all three axes.
            Quaternion mmdRotation =
                Quaternion.Euler(-sampled.RotationRadians * Mathf.Rad2Deg);

            // VMD stores the camera's orbit center, not its final world position.
            // First place the camera at local Z = Distance around that center, then
            // rotate the complete rig by 180 degrees on Y into the Unity avatar space.
            Quaternion rotation = MmdToUnityRotation * mmdRotation;
            Vector3 mmdCameraPosition =
                sampled.Center + mmdRotation * (Vector3.forward * sampled.Distance);
            Vector3 cameraPosition = MmdToUnityRotation * mmdCameraPosition * scale;

            positionX[frameIndex] = new Keyframe(time, cameraPosition.x);
            positionY[frameIndex] = new Keyframe(time, cameraPosition.y);
            positionZ[frameIndex] = new Keyframe(time, cameraPosition.z);
            rotationX[frameIndex] = new Keyframe(time, rotation.x);
            rotationY[frameIndex] = new Keyframe(time, rotation.y);
            rotationZ[frameIndex] = new Keyframe(time, rotation.z);
            rotationW[frameIndex] = new Keyframe(time, rotation.w);
            fieldOfView[frameIndex] = new Keyframe(time, sampled.FieldOfView);
            orthographic[frameIndex] = new Keyframe(time, sampled.Perspective == 0 ? 0f : 1f);

            if ((frame & 31u) == 0u)
            {
                float value = lastFrame == 0 ? 0.9f : Mathf.Lerp(0.08f, 0.9f, frame / (float)lastFrame);
                progress?.Invoke(value, $"Baking camera frame {frame}/{lastFrame}");
            }
        }

        var clip = new AnimationClip
        {
            frameRate = VmdFrameRate,
            name = clipName
        };
        SetLinearCurve(clip, typeof(Transform), "m_LocalPosition.x", positionX);
        SetLinearCurve(clip, typeof(Transform), "m_LocalPosition.y", positionY);
        SetLinearCurve(clip, typeof(Transform), "m_LocalPosition.z", positionZ);
        SetLinearCurve(clip, typeof(Transform), "m_LocalRotation.x", rotationX);
        SetLinearCurve(clip, typeof(Transform), "m_LocalRotation.y", rotationY);
        SetLinearCurve(clip, typeof(Transform), "m_LocalRotation.z", rotationZ);
        SetLinearCurve(clip, typeof(Transform), "m_LocalRotation.w", rotationW);
        SetLinearCurve(clip, typeof(Camera), "field of view", fieldOfView);
        SetConstantCurve(clip, typeof(Camera), "orthographic", orthographic);
        clip.EnsureQuaternionContinuity();
        ApplyCameraCutTangents(clip, cameraCuts);
        return clip;
    }

    private static HashSet<int> FindCameraCuts(IReadOnlyList<CameraFrame> sourceFrames)
    {
        var cuts = new HashSet<int>();
        for (int index = 1; index < sourceFrames.Count; index++)
        {
            uint leftFrame = sourceFrames[index - 1].Frame;
            uint rightFrame = sourceFrames[index].Frame;

            // MMD camera cuts are conventionally authored as two different source
            // poses on adjacent frames. MMD displays those discrete frames as a cut,
            // while Unity would otherwise interpolate an unwanted in-between pose.
            if (rightFrame == leftFrame + 1u)
                cuts.Add(checked((int)leftFrame));
        }
        return cuts;
    }

    private static void ApplyCameraCutTangents(AnimationClip clip, HashSet<int> cameraCuts)
    {
        if (cameraCuts.Count == 0) return;

        foreach (EditorCurveBinding binding in AnimationUtility.GetCurveBindings(clip))
        {
            if (binding.type == typeof(Camera) && binding.propertyName == "orthographic")
                continue;

            AnimationCurve curve = AnimationUtility.GetEditorCurve(clip, binding);
            if (curve == null) continue;

            foreach (int leftIndex in cameraCuts)
            {
                int rightIndex = leftIndex + 1;
                if (leftIndex < 0 || rightIndex >= curve.length) continue;

                AnimationUtility.SetKeyBroken(curve, leftIndex, true);
                AnimationUtility.SetKeyRightTangentMode(
                    curve,
                    leftIndex,
                    AnimationUtility.TangentMode.Constant);

                AnimationUtility.SetKeyBroken(curve, rightIndex, true);
                AnimationUtility.SetKeyLeftTangentMode(
                    curve,
                    rightIndex,
                    AnimationUtility.TangentMode.Constant);
            }

            AnimationUtility.SetEditorCurve(clip, binding, curve);
        }
    }

    private static CameraFrame SampleFrame(IReadOnlyList<CameraFrame> frames, int rightIndex, uint frame)
    {
        if (frames.Count == 1 || frame <= frames[0].Frame)
            return frames[0].CloneAt(frame);

        CameraFrame right = frames[rightIndex];
        if (frame >= right.Frame && rightIndex == frames.Count - 1)
            return right.CloneAt(frame);

        CameraFrame left = frames[Mathf.Max(0, rightIndex - 1)];
        if (frame == right.Frame)
            return right.CloneAt(frame);

        float span = right.Frame - left.Frame;
        float normalizedTime = span <= 0f ? 1f : (frame - left.Frame) / span;
        float tx = EvaluateBezier(right.Interpolation, 0, normalizedTime);
        float ty = EvaluateBezier(right.Interpolation, 4, normalizedTime);
        float tz = EvaluateBezier(right.Interpolation, 8, normalizedTime);
        float tr = EvaluateBezier(right.Interpolation, 12, normalizedTime);
        float td = EvaluateBezier(right.Interpolation, 16, normalizedTime);
        float tf = EvaluateBezier(right.Interpolation, 20, normalizedTime);

        return new CameraFrame
        {
            Frame = frame,
            Center = new Vector3(
                Mathf.LerpUnclamped(left.Center.x, right.Center.x, tx),
                Mathf.LerpUnclamped(left.Center.y, right.Center.y, ty),
                Mathf.LerpUnclamped(left.Center.z, right.Center.z, tz)),
            RotationRadians = Vector3.LerpUnclamped(left.RotationRadians, right.RotationRadians, tr),
            Distance = Mathf.LerpUnclamped(left.Distance, right.Distance, td),
            FieldOfView = Mathf.LerpUnclamped(left.FieldOfView, right.FieldOfView, tf),
            Perspective = left.Perspective,
            Interpolation = right.Interpolation
        };
    }

    private static float EvaluateBezier(byte[] interpolation, int offset, float x)
    {
        x = Mathf.Clamp01(x);
        if (interpolation == null || interpolation.Length < offset + 4)
            return x;

        float x1 = Mathf.Clamp01(interpolation[offset] / 127f);
        float x2 = Mathf.Clamp01(interpolation[offset + 1] / 127f);
        float y1 = Mathf.Clamp01(interpolation[offset + 2] / 127f);
        float y2 = Mathf.Clamp01(interpolation[offset + 3] / 127f);

        float low = 0f;
        float high = 1f;
        float parameter = x;
        for (int iteration = 0; iteration < 15; iteration++)
        {
            float sampledX = CubicBezier(parameter, x1, x2);
            float difference = sampledX - x;
            if (Mathf.Abs(difference) <= 0.000001f) break;
            if (difference < 0f) low = parameter;
            else high = parameter;

            float derivative = CubicBezierDerivative(parameter, x1, x2);
            float next = Mathf.Abs(derivative) > 0.000001f
                ? parameter - difference / derivative
                : float.NaN;
            if (float.IsNaN(next) || next <= low || next >= high)
                next = (low + high) * 0.5f;
            parameter = next;
        }
        return CubicBezier(parameter, y1, y2);
    }

    private static float CubicBezier(float t, float control1, float control2)
    {
        float inverse = 1f - t;
        return 3f * inverse * inverse * t * control1 +
               3f * inverse * t * t * control2 +
               t * t * t;
    }

    private static float CubicBezierDerivative(float t, float control1, float control2)
    {
        float inverse = 1f - t;
        return 3f * inverse * inverse * control1 +
               6f * inverse * t * (control2 - control1) +
               3f * t * t * (1f - control2);
    }

    private static void SetLinearCurve(AnimationClip clip, Type type, string propertyName, Keyframe[] keys)
    {
        for (int index = 0; index < keys.Length; index++)
        {
            Keyframe key = keys[index];
            key.inTangent = index > 0 ? GetKeySlope(keys[index - 1], key) : 0f;
            key.outTangent = index + 1 < keys.Length ? GetKeySlope(key, keys[index + 1]) : 0f;
            key.weightedMode = WeightedMode.None;
            keys[index] = key;
        }
        AnimationUtility.SetEditorCurve(
            clip,
            EditorCurveBinding.FloatCurve(string.Empty, type, propertyName),
            new AnimationCurve(keys));
    }

    private static void SetConstantCurve(AnimationClip clip, Type type, string propertyName, Keyframe[] keys)
    {
        for (int index = 0; index < keys.Length; index++)
        {
            Keyframe key = keys[index];
            key.inTangent = float.PositiveInfinity;
            key.outTangent = float.PositiveInfinity;
            key.weightedMode = WeightedMode.None;
            keys[index] = key;
        }
        AnimationUtility.SetEditorCurve(
            clip,
            EditorCurveBinding.FloatCurve(string.Empty, type, propertyName),
            new AnimationCurve(keys));
    }

    private static float GetKeySlope(Keyframe left, Keyframe right)
    {
        float duration = right.time - left.time;
        return Mathf.Abs(duration) > 0.0000001f ? (right.value - left.value) / duration : 0f;
    }

    private static List<CameraFrame> ReadCameraFrames(string fullPath)
    {
        using (FileStream stream = File.OpenRead(fullPath))
        using (var reader = new BinaryReader(stream))
        {
            string header = System.Text.Encoding.ASCII.GetString(reader.ReadBytes(30)).TrimEnd('\0');
            if (!header.StartsWith("Vocaloid Motion Data", StringComparison.Ordinal))
                throw new InvalidDataException("文件头不是有效的 VMD 格式。");
            if (!header.Contains("0002"))
                throw new InvalidDataException("目前仅支持 Vocaloid Motion Data 0002 格式。");

            EnsureRemaining(stream, 20, "模型名称");
            reader.ReadBytes(20);

            uint boneCount = ReadCount(reader, stream, "骨骼");
            SkipRecords(stream, boneCount, 111, "骨骼");

            uint morphCount = ReadCount(reader, stream, "表情");
            SkipRecords(stream, morphCount, 23, "表情");

            uint cameraCount = ReadCount(reader, stream, "摄像机");
            if (cameraCount > 1000000)
                throw new InvalidDataException("摄像机关键帧数量异常。");

            var frames = new List<CameraFrame>(checked((int)cameraCount));
            for (uint index = 0; index < cameraCount; index++)
            {
                EnsureRemaining(stream, 61, "摄像机");
                frames.Add(new CameraFrame
                {
                    Frame = reader.ReadUInt32(),
                    Distance = reader.ReadSingle(),
                    Center = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()),
                    RotationRadians = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()),
                    Interpolation = reader.ReadBytes(24),
                    FieldOfView = reader.ReadUInt32(),
                    Perspective = reader.ReadByte()
                });
            }
            return frames;
        }
    }

    private static uint ReadCount(BinaryReader reader, Stream stream, string section)
    {
        EnsureRemaining(stream, 4, section);
        return reader.ReadUInt32();
    }

    private static void SkipRecords(Stream stream, uint count, int recordSize, string section)
    {
        long bytes = checked((long)count * recordSize);
        EnsureRemaining(stream, bytes, section);
        stream.Position += bytes;
    }

    private static void EnsureRemaining(Stream stream, long bytes, string section)
    {
        if (bytes < 0 || stream.Length - stream.Position < bytes)
            throw new InvalidDataException($"VMD 的{section}数据不完整。");
    }

    private static string GetCameraOutputPath(string sourcePath, string outputFolder)
    {
        string fileName = Path.GetFileNameWithoutExtension(sourcePath) + "_Camera.anim";
        if (string.IsNullOrEmpty(outputFolder))
        {
            string sourceFolder = Path.GetDirectoryName(sourcePath)?.Replace('\\', '/');
            return string.IsNullOrEmpty(sourceFolder) ? fileName : sourceFolder + "/" + fileName;
        }
        return outputFolder.TrimEnd('/') + "/" + fileName;
    }

    private bool TryGetCameraOutputStorageFolder(out string folder)
    {
        return TryResolveStorageFolder(
            cameraOutputStorageLocation, customCameraOutputStorageFolder, out folder);
    }
}
