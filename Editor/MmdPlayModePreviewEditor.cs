using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using AvatarDescriptor = VRC.SDKBase.VRC_AvatarDescriptor;

[CustomEditor(typeof(MmdPlayModePreview))]
public sealed class MmdPlayModePreviewEditor : Editor
{
    private const string PackageRoot = "Packages/com.vrchat.mmd-playmode-preview";
    private const string PreviewScriptPath = PackageRoot + "/Runtime/MmdPlayModePreview.cs";
    private const string PreviewIconPath = PackageRoot + "/Editor/Icon/icon.png";
    private const string AuthorName = "VeraFirefly";
    private const string LanguagePreference = "MmdPlayModePreview.Language";
    private readonly List<AvatarDescriptor> avatars = new List<AvatarDescriptor>();
    private int avatarIndex = -1;
    private VisualElement inspectorRoot;
    private VisualElement gestureManagerHost;
    private Editor gestureManagerEditor;
    private Component gestureManagerTarget;
    private Component requestedGestureManagerTarget;
    private bool gestureManagerSyncScheduled;
    private static GUIStyle clearButtonStyle;
    private static GUIStyle titleStyle;
    private static GUIStyle versionStyle;
    private static GUIStyle authorStyle;
    private static AnimationClip cachedMotionTypeClip;
    private static PreviewMotionClipType cachedMotionClipType;
    private static AnimationClip sampledTransformClip;
    private static GameObject sampledTransformAvatar;
    private static TransformCurveSet[] sampledTransformCurves;

    private enum PreviewMotionClipType
    {
        None,
        Humanoid,
        TargetFk,
        Transform,
        AnimationClip
    }

    private sealed class TransformCurveSet
    {
        internal string Path;
        internal Transform Target;
        internal AnimationCurve PositionX;
        internal AnimationCurve PositionY;
        internal AnimationCurve PositionZ;
        internal AnimationCurve RotationX;
        internal AnimationCurve RotationY;
        internal AnimationCurve RotationZ;
        internal AnimationCurve RotationW;

        internal bool HasPosition =>
            PositionX != null || PositionY != null || PositionZ != null;
        internal bool HasRotation =>
            RotationX != null || RotationY != null || RotationZ != null || RotationW != null;
    }

    private enum EditorLanguage
    {
        SimplifiedChinese,
        English,
        Japanese
    }

    private static EditorLanguage Language
    {
        get => (EditorLanguage)EditorPrefs.GetInt(
            LanguagePreference,
            (int)EditorLanguage.SimplifiedChinese);
        set => EditorPrefs.SetInt(LanguagePreference, (int)value);
    }

    public override VisualElement CreateInspectorGUI()
    {
        inspectorRoot = new VisualElement();
        inspectorRoot.Add(new IMGUIContainer(OnInspectorGUI));
        gestureManagerHost = new VisualElement();
        inspectorRoot.Add(gestureManagerHost);
        return inspectorRoot;
    }

    [MenuItem("VeraFirefly/MMD Play Mode Preview/Create MMD Play Mode Preview")]
    private static void CreatePreviewController()
    {
        var controller = new GameObject("MMD Play Mode Preview");
        controller.AddComponent<MmdPlayModePreview>();
        Selection.activeGameObject = controller;
        Undo.RegisterCreatedObjectUndo(controller, "Create MMD Play Mode Preview");
    }

    private void OnEnable()
    {
        ForceEnabled();
        ApplyScriptIcon();
        ScanAvatars();
    }

    private void OnDisable()
    {
        DisposeEmbeddedGestureManager();
    }

    public override void OnInspectorGUI()
    {
        var preview = (MmdPlayModePreview)target;
        ForceEnabled();
        DrawPluginHeader();

        if (Application.isPlaying && !preview.IsPreviewSessionActive)
        {
            ClearEmbeddedGestureManager();
            EditorGUILayout.HelpBox(
                L("当前 Play Mode 不是由此组件启动，MMD 预览的全部功能已禁用。",
                    "This Play Mode session was not started by this component. MMD preview is disabled.",
                    "この Play Mode はこのコンポーネントから開始されていないため、MMD プレビューは無効です。"),
                MessageType.Info);
            return;
        }

        ScanAvatars();
        serializedObject.Update();

        BeginColoredSection(L("模型预览", "Model Preview", "モデルプレビュー"), new Color(0.16f, 0.38f, 0.72f));
        using (new EditorGUI.DisabledScope(Application.isPlaying))
        {
            DrawAvatarSelector(preview);
        }
        SerializedProperty enableBackgroundProperty = serializedObject.FindProperty("enablePreviewBackground");
        EditorGUILayout.PropertyField(enableBackgroundProperty, new GUIContent(
            L("预览背景", "Preview Background", "プレビュー背景")));
        if (enableBackgroundProperty.boolValue)
        {
            EditorGUI.indentLevel++;
            EditorGUILayout.PropertyField(
                serializedObject.FindProperty("backgroundImage"),
                new GUIContent(L("图片 (PNG)", "Image (PNG)", "画像 (PNG)")));
            EditorGUILayout.PropertyField(
                serializedObject.FindProperty("backgroundColor"),
                new GUIContent(L("背景颜色", "Background Color", "背景色")));
            EditorGUI.indentLevel--;
        }
        EndColoredSection();

        SerializedProperty freeCameraProperty = serializedObject.FindProperty("freeCameraControl");
        SerializedProperty cameraAnimationProperty = serializedObject.FindProperty("cameraAnimationClip");

        BeginColoredSection("MMD", new Color(0.52f, 0.26f, 0.68f));
        using (new EditorGUI.DisabledScope(Application.isPlaying && !preview.IsPlaybackEnded))
        {
            DrawClearableObjectField(
                serializedObject.FindProperty("animationClip"),
                new GUIContent(L("动作 (AnimationClip)", "Motion (AnimationClip)", "モーション (AnimationClip)")),
                L("清除动作", "Clear motion", "モーションをクリア"));
            DrawClearableObjectField(
                serializedObject.FindProperty("audioClip"),
                new GUIContent(L("音频 (AudioClip)", "Audio (AudioClip)", "音声 (AudioClip)")),
                L("清除音频", "Clear audio", "音声をクリア"));
        }
        bool disableCameraAnimation = Application.isPlaying && !freeCameraProperty.boolValue;
        using (new EditorGUI.DisabledScope(disableCameraAnimation))
        {
            DrawClearableObjectField(
                cameraAnimationProperty,
                new GUIContent(L("镜头动画 (AnimationClip)", "Camera Animation (AnimationClip)", "カメラアニメーション (AnimationClip)")),
                L("清除镜头动画", "Clear camera animation", "カメラアニメーションをクリア"));
        }
        using (new EditorGUI.DisabledScope(Application.isPlaying && !preview.IsPlaybackEnded))
        {
            SerializedProperty durationSource = serializedObject.FindProperty("playbackDurationSource");
            durationSource.enumValueIndex = EditorGUILayout.Popup(
                L("播放时长采样", "Playback Duration Source", "再生時間の基準"),
                durationSource.enumValueIndex,
                new[]
                {
                    L("动作", "Motion", "モーション"),
                    L("音频", "Audio", "音声"),
                    L("镜头", "Camera", "カメラ")
                });
        }
        EditorGUILayout.PropertyField(
            serializedObject.FindProperty("loopPlayback"),
            new GUIContent(L("循环播放", "Loop Playback", "ループ再生")));

        if (!Application.isPlaying && GUILayout.Button(
                L("打开 VMD Converter", "Open VMD Converter", "VMD Converter を開く")))
            MainConverterWindow.OpenFor(preview);
        EndColoredSection();

        BeginColoredSection("Camera", new Color(0.12f, 0.56f, 0.42f));
        if (Application.isPlaying && !preview.IsPlaybackEnded)
            EditorGUILayout.PropertyField(
                freeCameraProperty,
                new GUIContent(L("自由视角控制", "Free Camera Control", "フリーカメラ操作")));

        bool cameraAnimationMode = cameraAnimationProperty.objectReferenceValue != null &&
                                   !freeCameraProperty.boolValue;
        using (new EditorGUI.DisabledScope(Application.isPlaying && cameraAnimationMode))
        {
            EditorGUILayout.PropertyField(
                serializedObject.FindProperty("cameraPosition"),
                new GUIContent("Position", L("当前摄像机的世界坐标", "Current camera world position", "現在のカメラのワールド座標")));
            EditorGUILayout.PropertyField(
                serializedObject.FindProperty("cameraRotation"),
                new GUIContent("Rotation", L("当前摄像机的世界旋转", "Current camera world rotation", "現在のカメラのワールド回転")));
            EditorGUILayout.PropertyField(
                serializedObject.FindProperty("cameraMoveSpeed"),
                new GUIContent(L("移动速度", "Move Speed", "移動速度")));
        }
        EndColoredSection();
        serializedObject.ApplyModifiedProperties();

        if (Application.isPlaying && preview.HasPlaybackContent)
        {
            BeginColoredSection(L("播放控制", "Playback Controls", "再生コントロール"), new Color(0.76f, 0.4f, 0.1f));
            DrawPlaybackControls(preview);
            EndColoredSection();
        }
        if (Application.isPlaying) Repaint();

        if (Application.isPlaying) UpdateEmbeddedGestureManager(preview);
        else ClearEmbeddedGestureManager();

        if (!Application.isPlaying)
        {
            EditorGUILayout.Space(8f);
            using (new EditorGUI.DisabledScope(preview.avatar == null))
            {
                if (GUILayout.Button(
                        L("进入 Play Mode 预览", "Enter Play Mode Preview", "Play Mode プレビューを開始"),
                        GUILayout.Height(30f)))
                {
                    if (EnsureGestureManager(preview)) EditorApplication.isPlaying = true;
                }
            }
        }
        if (Application.isPlaying)
        {
            if (preview.PlaybackDuration <= 0f)
                EditorGUILayout.HelpBox(
                    L("当前“播放时长采样”对应的资源为空或长度为 0，无法开始播放。",
                        "The selected playback duration source is empty or has zero length.",
                        "選択した再生時間の基準が空、または長さが 0 のため再生できません。"),
                    MessageType.Warning);
            EditorGUILayout.HelpBox(
                preview.IsCameraAnimationMode
                    ? L("当前由镜头动画完全控制摄像机。勾选“自由视角控制”可立即切换为自由视角。",
                        "The camera animation currently controls the camera. Enable Free Camera Control to switch immediately.",
                        "現在はカメラアニメーションがカメラを制御しています。「フリーカメラ操作」を有効にすると切り替えられます。")
                    : L("自由视角：按住右键拖动旋转，W/A/S/D 移动，Q/E 向下/向上，滚轮调整移速；按住中键仅旋转，滚轮调整距离。",
                        "Free camera: hold right mouse to rotate; W/A/S/D to move; Q/E down/up; wheel adjusts speed. Hold middle mouse to rotate; wheel adjusts distance.",
                        "フリーカメラ：右ドラッグで回転、W/A/S/D で移動、Q/E で上下、ホイールで速度調整。中ドラッグ中は回転し、ホイールで距離を調整します。"),
                MessageType.Info);
        }
        else if (preview.avatar == null)
            EditorGUILayout.HelpBox(
                L("请选择一个场景中激活的 VRC Avatar Descriptor。",
                    "Select an active VRC Avatar Descriptor in the scene.",
                    "シーン内の有効な VRC Avatar Descriptor を選択してください。"),
                MessageType.Info);
    }

    [InitializeOnLoadMethod]
    private static void ScheduleScriptIcon()
    {
        MmdPlayModePreview.EditorTransformClipSampler = SampleTransformClip;
        EditorApplication.delayCall += ApplyScriptIcon;
    }

    private static void DrawPluginHeader()
    {
        using (new EditorGUILayout.HorizontalScope())
        {
            GUILayout.FlexibleSpace();
            GUILayout.Label(
                "MMD Play Mode Preview",
                GetTitleStyle(),
                GUILayout.Height(24f));
            GUILayout.FlexibleSpace();
        }

        DrawCenteredLabel(
            L("版本号: ", "Version: ", "バージョン: ") + GetPackageVersion(),
            GetVersionStyle());
        DrawCenteredLabel(
            L("作者: ", "Author: ", "作者: ") + AuthorName,
            GetAuthorStyle());

        EditorLanguage nextLanguage = (EditorLanguage)EditorGUILayout.Popup(
            L("语言", "Language", "言語"),
            (int)Language,
            new[] { "简体中文", "English", "日本語" });
        if (nextLanguage != Language)
        {
            Language = nextLanguage;
            GUI.changed = true;
        }
        EditorGUILayout.Space(8f);
    }

    private static void DrawCenteredLabel(string text, GUIStyle style)
    {
        using (new EditorGUILayout.HorizontalScope())
        {
            GUILayout.FlexibleSpace();
            GUILayout.Label(text, style);
            GUILayout.FlexibleSpace();
        }
    }

    private static string GetPackageVersion()
    {
        UnityEditor.PackageManager.PackageInfo package =
            UnityEditor.PackageManager.PackageInfo.FindForAssetPath(PackageRoot);
        return string.IsNullOrWhiteSpace(package?.version) ? "Unknown" : package.version;
    }

    private static string L(string chinese, string english, string japanese)
    {
        switch (Language)
        {
            case EditorLanguage.English:
                return english;
            case EditorLanguage.Japanese:
                return japanese;
            default:
                return chinese;
        }
    }

    private static Texture GetPreviewIcon()
    {
        return AssetDatabase.LoadAssetAtPath<Texture2D>(PreviewIconPath);
    }

    private static void ApplyScriptIcon()
    {
        MonoScript script = AssetDatabase.LoadAssetAtPath<MonoScript>(PreviewScriptPath);
        Texture2D icon = GetPreviewIcon() as Texture2D;
        if (script != null && icon != null)
            EditorGUIUtility.SetIconForObject(script, icon);
    }

    private void ForceEnabled()
    {
        var preview = target as MmdPlayModePreview;
        if (preview == null || preview.enabled) return;

        preview.enabled = true;
        EditorUtility.SetDirty(preview);
    }

    private static GUIStyle GetTitleStyle()
    {
        return titleStyle ?? (titleStyle = new GUIStyle(EditorStyles.boldLabel)
        {
            fontSize = 17,
            alignment = TextAnchor.MiddleCenter
        });
    }

    private static GUIStyle GetVersionStyle()
    {
        return versionStyle ?? (versionStyle = new GUIStyle(EditorStyles.miniLabel)
        {
            fontSize = 9,
            alignment = TextAnchor.MiddleCenter
        });
    }

    private static GUIStyle GetAuthorStyle()
    {
        return authorStyle ?? (authorStyle = new GUIStyle(EditorStyles.label)
        {
            fontSize = 11,
            alignment = TextAnchor.MiddleCenter
        });
    }

    private static void BeginColoredSection(string title, Color color)
    {
        Rect sectionRect = EditorGUILayout.BeginVertical(EditorStyles.helpBox);
        if (Event.current.type == EventType.Repaint)
        {
            color.a = EditorGUIUtility.isProSkin ? 0.24f : 0.12f;
            EditorGUI.DrawRect(sectionRect, color);
        }
        EditorGUILayout.LabelField(title, EditorStyles.boldLabel);
    }

    private static void EndColoredSection()
    {
        EditorGUILayout.EndVertical();
        EditorGUILayout.Space(4f);
    }

    private static void DrawClearableObjectField(
        SerializedProperty property,
        GUIContent label,
        string clearTooltip)
    {
        using (new EditorGUILayout.HorizontalScope())
        {
            EditorGUILayout.PropertyField(property, label, GUILayout.ExpandWidth(true));
            using (new EditorGUI.DisabledScope(property.objectReferenceValue == null))
            {
                if (GUILayout.Button(
                        new GUIContent("X", clearTooltip),
                        GetClearButtonStyle(),
                        GUILayout.Width(20f),
                        GUILayout.Height(EditorGUIUtility.singleLineHeight)))
                {
                    property.objectReferenceValue = null;
                }
            }
        }
    }

    private static GUIStyle GetClearButtonStyle()
    {
        if (clearButtonStyle != null)
            return clearButtonStyle;

        clearButtonStyle = new GUIStyle(EditorStyles.miniButton)
        {
            alignment = TextAnchor.MiddleCenter,
            fontSize = 12,
            fontStyle = FontStyle.Bold,
            padding = new RectOffset(0, 0, 0, 0)
        };
        return clearButtonStyle;
    }

    private static void DrawPlaybackControls(MmdPlayModePreview preview)
    {
        using (new EditorGUILayout.HorizontalScope())
        {
            GUILayout.FlexibleSpace();
            GUIContent playPauseIcon = preview.IsPlaybackPlaying
                ? GetIconContent("PauseButton", "Ⅱ", L("暂停", "Pause", "一時停止"))
                : GetIconContent("PlayButton", "▶", L("播放", "Play", "再生"));
            if (GUILayout.Button(playPauseIcon, GUILayout.Width(36f), GUILayout.Height(26f)))
            {
                if (preview.IsPlaybackPlaying) preview.PausePreview();
                else preview.PlayPreview();
            }
            if (GUILayout.Button(
                    GetIconContent("Animation.FirstKey", "↶", L("重置到开头", "Reset to Start", "先頭に戻す")),
                    GUILayout.Width(36f),
                    GUILayout.Height(26f)))
                preview.ResetPreview();
            using (new EditorGUI.DisabledScope(preview.IsPlaybackEnded))
                if (GUILayout.Button(
                        GetIconContent("winbtn_win_close", "■", L("结束动作", "End Motion", "モーション終了")),
                        GUILayout.Width(36f),
                        GUILayout.Height(26f)))
                    preview.EndPreview();
            GUILayout.FlexibleSpace();
        }

        if (!preview.IsPlaybackEnded)
        {
            float duration = Mathf.Max(0f, preview.PlaybackDuration);
            EditorGUI.BeginChangeCheck();
            float time = EditorGUILayout.Slider(preview.PlaybackTime, 0f, duration);
            if (EditorGUI.EndChangeCheck()) preview.SeekPreview(time);

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                GUILayout.Label($"{FormatPlaybackTime(preview.PlaybackTime)} / {FormatPlaybackTime(duration)}", EditorStyles.centeredGreyMiniLabel, GUILayout.Width(130f));
                GUILayout.FlexibleSpace();
            }

            if (preview.animationClip != null)
            {
                string typeLabel = GetMotionClipTypeLabel(
                    DetectMotionClipType(preview.animationClip));
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.FlexibleSpace();
                    GUILayout.Label(
                        L("当前播放类型：", "Current playback type: ", "現在の再生タイプ：") + typeLabel,
                        EditorStyles.centeredGreyMiniLabel);
                    GUILayout.FlexibleSpace();
                }
            }
        }
    }

    private static PreviewMotionClipType DetectMotionClipType(AnimationClip clip)
    {
        if (clip == null) return PreviewMotionClipType.None;
        if (clip == cachedMotionTypeClip) return cachedMotionClipType;

        PreviewMotionClipType result;
        if (clip.humanMotion)
        {
            result = PreviewMotionClipType.Humanoid;
        }
        else
        {
            EditorCurveBinding[] bindings = AnimationUtility.GetCurveBindings(clip);
            var rotationPaths = new HashSet<string>(StringComparer.Ordinal);
            bool hasHumanoidRootOrMuscle = false;
            bool hasTransformCurves = false;
            for (int index = 0; index < bindings.Length; index++)
            {
                EditorCurveBinding binding = bindings[index];
                if (binding.type == typeof(Animator) &&
                    (binding.propertyName.StartsWith("RootT.", StringComparison.Ordinal) ||
                     binding.propertyName.StartsWith("RootQ.", StringComparison.Ordinal) ||
                     binding.propertyName.StartsWith("Muscle.", StringComparison.Ordinal)))
                {
                    hasHumanoidRootOrMuscle = true;
                    break;
                }

                if (binding.type != typeof(Transform)) continue;
                hasTransformCurves = true;
                if (binding.propertyName.StartsWith("m_LocalRotation.", StringComparison.Ordinal))
                    rotationPaths.Add(binding.path ?? string.Empty);
            }

            if (hasHumanoidRootOrMuscle)
                result = PreviewMotionClipType.Humanoid;
            else if (rotationPaths.Count >= 6)
                result = PreviewMotionClipType.TargetFk;
            else if (hasTransformCurves)
                result = PreviewMotionClipType.Transform;
            else
                result = PreviewMotionClipType.AnimationClip;
        }

        cachedMotionTypeClip = clip;
        cachedMotionClipType = result;
        return result;
    }

    private static bool SampleTransformClip(AnimationClip clip, GameObject avatar, float time)
    {
        if (clip == null || avatar == null ||
            DetectMotionClipType(clip) != PreviewMotionClipType.TargetFk)
            return false;

        if (clip != sampledTransformClip || avatar != sampledTransformAvatar ||
            sampledTransformCurves == null)
        {
            sampledTransformClip = clip;
            sampledTransformAvatar = avatar;
            sampledTransformCurves = BuildTransformCurveSets(clip, avatar.transform);
        }

        for (int index = 0; index < sampledTransformCurves.Length; index++)
        {
            TransformCurveSet curves = sampledTransformCurves[index];
            Transform target = curves.Target;
            if (target == null) continue;

            if (curves.HasPosition)
            {
                Vector3 position = target.localPosition;
                if (curves.PositionX != null) position.x = curves.PositionX.Evaluate(time);
                if (curves.PositionY != null) position.y = curves.PositionY.Evaluate(time);
                if (curves.PositionZ != null) position.z = curves.PositionZ.Evaluate(time);
                target.localPosition = position;
            }

            if (!curves.HasRotation) continue;
            Quaternion rotation = target.localRotation;
            if (curves.RotationX != null) rotation.x = curves.RotationX.Evaluate(time);
            if (curves.RotationY != null) rotation.y = curves.RotationY.Evaluate(time);
            if (curves.RotationZ != null) rotation.z = curves.RotationZ.Evaluate(time);
            if (curves.RotationW != null) rotation.w = curves.RotationW.Evaluate(time);
            float lengthSquared = rotation.x * rotation.x + rotation.y * rotation.y +
                                  rotation.z * rotation.z + rotation.w * rotation.w;
            if (lengthSquared > 1e-12f)
            {
                float inverseLength = 1f / Mathf.Sqrt(lengthSquared);
                target.localRotation = new Quaternion(
                    rotation.x * inverseLength,
                    rotation.y * inverseLength,
                    rotation.z * inverseLength,
                    rotation.w * inverseLength);
            }
        }
        return true;
    }

    private static TransformCurveSet[] BuildTransformCurveSets(
        AnimationClip clip, Transform avatarRoot)
    {
        var byPath = new Dictionary<string, TransformCurveSet>(StringComparer.Ordinal);
        EditorCurveBinding[] bindings = AnimationUtility.GetCurveBindings(clip);
        for (int index = 0; index < bindings.Length; index++)
        {
            EditorCurveBinding binding = bindings[index];
            if (binding.type != typeof(Transform)) continue;
            bool position = binding.propertyName.StartsWith(
                "m_LocalPosition.", StringComparison.Ordinal);
            bool rotation = binding.propertyName.StartsWith(
                "m_LocalRotation.", StringComparison.Ordinal);
            if (!position && !rotation) continue;

            string path = binding.path ?? string.Empty;
            if (!byPath.TryGetValue(path, out TransformCurveSet curves))
            {
                curves = new TransformCurveSet
                {
                    Path = path,
                    Target = string.IsNullOrEmpty(path)
                        ? avatarRoot
                        : avatarRoot.Find(path)
                };
                byPath.Add(path, curves);
            }

            AnimationCurve curve = AnimationUtility.GetEditorCurve(clip, binding);
            switch (binding.propertyName)
            {
                case "m_LocalPosition.x": curves.PositionX = curve; break;
                case "m_LocalPosition.y": curves.PositionY = curve; break;
                case "m_LocalPosition.z": curves.PositionZ = curve; break;
                case "m_LocalRotation.x": curves.RotationX = curve; break;
                case "m_LocalRotation.y": curves.RotationY = curve; break;
                case "m_LocalRotation.z": curves.RotationZ = curve; break;
                case "m_LocalRotation.w": curves.RotationW = curve; break;
            }
        }
        return new List<TransformCurveSet>(byPath.Values).ToArray();
    }

    private static string GetMotionClipTypeLabel(PreviewMotionClipType type)
    {
        switch (type)
        {
            case PreviewMotionClipType.Humanoid:
                return L("Humanoid（Root/Muscle）", "Humanoid (Root/Muscle)",
                    "Humanoid（Root/Muscle）");
            case PreviewMotionClipType.TargetFk:
                return L("目标 FK（Transform 曲线）", "Target FK (Transform curves)",
                    "対象 FK（Transform カーブ）");
            case PreviewMotionClipType.Transform:
                return L("Transform 动画", "Transform animation", "Transform アニメーション");
            case PreviewMotionClipType.AnimationClip:
                return "AnimationClip";
            default:
                return L("无", "None", "なし");
        }
    }

    private static string FormatPlaybackTime(float time)
    {
        time = Mathf.Max(0f, time);
        int minutes = Mathf.FloorToInt(time / 60f);
        float seconds = time - minutes * 60f;
        return $"{minutes:00}:{seconds:00.00}";
    }

    private void UpdateEmbeddedGestureManager(MmdPlayModePreview preview)
    {
        Component manager = GetGestureManager(preview);
        if (manager == null || preview.avatar == null || gestureManagerHost == null)
        {
            RequestEmbeddedGestureManager(null);
            return;
        }

        ConfigureGestureManager(manager, preview.avatar);
        RequestEmbeddedGestureManager(manager);
    }

    private void RequestEmbeddedGestureManager(Component manager)
    {
        requestedGestureManagerTarget = manager;
        if (gestureManagerHost == null || gestureManagerSyncScheduled) return;
        if (gestureManagerTarget == manager && (manager == null || gestureManagerEditor != null)) return;

        gestureManagerSyncScheduled = true;
        gestureManagerHost.schedule.Execute(ApplyEmbeddedGestureManager);
    }

    private void ApplyEmbeddedGestureManager()
    {
        gestureManagerSyncScheduled = false;
        if (gestureManagerHost == null) return;

        Component manager = requestedGestureManagerTarget;
        if (gestureManagerTarget == manager && (manager == null || gestureManagerEditor != null)) return;

        gestureManagerHost.Clear();
        if (gestureManagerEditor != null) DestroyImmediate(gestureManagerEditor);
        gestureManagerEditor = null;
        gestureManagerTarget = null;
        if (manager == null) return;

        Editor.CreateCachedEditor(manager, null, ref gestureManagerEditor);
        gestureManagerTarget = manager;
        VisualElement nativeInspector = gestureManagerEditor != null ? gestureManagerEditor.CreateInspectorGUI() : null;
        if (nativeInspector != null) gestureManagerHost.Add(nativeInspector);
    }

    private void ClearEmbeddedGestureManager()
    {
        RequestEmbeddedGestureManager(null);
    }

    private void DisposeEmbeddedGestureManager()
    {
        requestedGestureManagerTarget = null;
        gestureManagerTarget = null;
        gestureManagerSyncScheduled = false;
        if (gestureManagerEditor != null) DestroyImmediate(gestureManagerEditor);
        gestureManagerEditor = null;
        gestureManagerHost = null;
        inspectorRoot = null;
    }

    private static Component GetGestureManager(MmdPlayModePreview preview)
    {
        return preview.gestureManager != null ? preview.gestureManager : preview.GetComponent("BlackStartX.GestureManager.GestureManager");
    }

    private static void ConfigureGestureManager(Component manager, GameObject avatar)
    {
        object settings = GetMemberValue(manager, "settings");
        if (settings == null)
        {
            Type settingsType = FindGestureManagerType("BlackStartX.GestureManager.Modules.ModuleSettings");
            if (settingsType == null) return;
            settings = Activator.CreateInstance(settingsType);
            SetMemberValue(manager, "settings", settings);
        }
        SetMemberValue(settings, "favourite", avatar.GetComponent<AvatarDescriptor>());
    }

    private static Type FindGestureManagerType(string fullName)
    {
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type type = assembly.GetType(fullName, false);
            if (type != null) return type;
        }
        return null;
    }

    private static object GetMemberValue(object target, string memberName)
    {
        Type type = target.GetType();
        FieldInfo field = type.GetField(memberName, BindingFlags.Instance | BindingFlags.Public);
        if (field != null) return field.GetValue(target);
        return type.GetProperty(memberName, BindingFlags.Instance | BindingFlags.Public)?.GetValue(target);
    }

    private static void SetMemberValue(object target, string memberName, object value)
    {
        Type type = target.GetType();
        FieldInfo field = type.GetField(memberName, BindingFlags.Instance | BindingFlags.Public);
        if (field != null) field.SetValue(target, value);
        else type.GetProperty(memberName, BindingFlags.Instance | BindingFlags.Public)?.SetValue(target, value);
    }

    private static bool EnsureGestureManager(MmdPlayModePreview preview)
    {
        MmdPlayModePreviewSession.PrepareFor(preview);
        Component manager = GetGestureManager(preview);
        if (manager == null)
        {
            Type managerType = FindGestureManagerType("BlackStartX.GestureManager.GestureManager");
            if (managerType == null)
            {
                Debug.LogError("Gesture Manager package was not found. Please install vrchat.blackstartx.gesture-manager.");
                return false;
            }
            manager = Undo.AddComponent(preview.gameObject, managerType);
            preview.gestureManagerAutoAdded = true;
        }
        preview.gestureManager = manager;
        preview.previewSessionAuthorized = true;
        ConfigureGestureManager(manager, preview.avatar);
        EditorUtility.SetDirty(preview);
        return true;
    }

    private void DrawAvatarSelector(MmdPlayModePreview preview)
    {
        EditorGUILayout.BeginHorizontal();
        string[] labels = BuildAvatarNames();
        using (new EditorGUI.DisabledScope(labels.Length == 0))
        {
            int nextIndex = EditorGUILayout.Popup(
                L("模型", "Model", "モデル"),
                Mathf.Max(0, avatarIndex),
                labels);
            if (nextIndex >= 0 && nextIndex < avatars.Count && nextIndex != avatarIndex)
            {
                Undo.RecordObject(preview, "Select MMD Preview Avatar");
                preview.avatar = avatars[nextIndex].gameObject;
                EditorUtility.SetDirty(preview);
                avatarIndex = nextIndex;
            }
        }
        if (GUILayout.Button(
                GetIconContent("Refresh", "↻", L("刷新模型列表", "Refresh Model List", "モデル一覧を更新")),
                EditorStyles.miniButton,
                GUILayout.Width(28f),
                GUILayout.Height(18f)))
            ScanAvatars();
        EditorGUILayout.EndHorizontal();
    }

    private static GUIContent GetIconContent(string iconName, string fallback, string tooltip)
    {
        string themedName = EditorGUIUtility.isProSkin ? "d_" + iconName : iconName;
        GUIContent icon = EditorGUIUtility.IconContent(themedName);
        if (icon == null || icon.image == null) icon = EditorGUIUtility.IconContent(iconName);
        return icon != null && icon.image != null
            ? new GUIContent(icon.image, tooltip)
            : new GUIContent(fallback, tooltip);
    }

    private void ScanAvatars()
    {
        var preview = target as MmdPlayModePreview;
        avatars.Clear();
        foreach (AvatarDescriptor descriptor in Resources.FindObjectsOfTypeAll<AvatarDescriptor>())
        {
            if (descriptor == null || !descriptor.gameObject.activeInHierarchy || !descriptor.gameObject.scene.IsValid()) continue;
            avatars.Add(descriptor);
        }
        avatars.Sort((left, right) => string.Compare(GetHierarchyPath(left.transform), GetHierarchyPath(right.transform), StringComparison.OrdinalIgnoreCase));
        avatarIndex = avatars.FindIndex(descriptor => descriptor != null && preview != null && descriptor.gameObject == preview.avatar);
        if (avatarIndex < 0 && avatars.Count > 0)
        {
            avatarIndex = 0;
            if (preview != null)
            {
                Undo.RecordObject(preview, "Select Default MMD Preview Avatar");
                preview.avatar = avatars[0].gameObject;
                EditorUtility.SetDirty(preview);
            }
        }
    }

    private string[] BuildAvatarNames()
    {
        if (avatars.Count == 0)
            return new[]
            {
                L("未找到激活的 VRC Avatar Descriptor",
                    "No active VRC Avatar Descriptor found",
                    "有効な VRC Avatar Descriptor が見つかりません")
            };
        string[] names = new string[avatars.Count];
        for (int index = 0; index < avatars.Count; index++) names[index] = GetHierarchyPath(avatars[index].transform);
        return names;
    }

    private static string GetHierarchyPath(Transform transform)
    {
        var names = new List<string>();
        for (Transform current = transform; current != null; current = current.parent) names.Add(current.name);
        names.Reverse();
        return string.Join("/", names);
    }
}

[InitializeOnLoad]
internal static class MmdPlayModePreviewSession
{
    static MmdPlayModePreviewSession()
    {
        EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        if (!EditorApplication.isPlayingOrWillChangePlaymode)
            EditorApplication.delayCall += CleanupAll;
    }

    internal static void PrepareFor(MmdPlayModePreview selectedPreview)
    {
        foreach (MmdPlayModePreview preview in FindScenePreviews())
        {
            if (preview == selectedPreview) continue;
            bool changed = preview.previewSessionAuthorized || preview.gestureManagerAutoAdded;
            preview.previewSessionAuthorized = false;
            RemoveAutoAddedGestureManager(preview);
            if (changed) EditorUtility.SetDirty(preview);
        }
    }

    private static void OnPlayModeStateChanged(PlayModeStateChange state)
    {
        if (state == PlayModeStateChange.ExitingEditMode || state == PlayModeStateChange.EnteredPlayMode)
            ApplySessionAuthorization();
        else if (state == PlayModeStateChange.EnteredEditMode)
            CleanupAll();
    }

    private static void ApplySessionAuthorization()
    {
        foreach (MmdPlayModePreview preview in FindScenePreviews())
            preview.SetPreviewSessionActive(preview.previewSessionAuthorized);
    }

    private static void CleanupAll()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) return;
        foreach (MmdPlayModePreview preview in FindScenePreviews())
        {
            bool changed = preview.previewSessionAuthorized || preview.gestureManagerAutoAdded;
            preview.SetPreviewSessionActive(false);
            preview.previewSessionAuthorized = false;
            RemoveAutoAddedGestureManager(preview);
            if (changed) EditorUtility.SetDirty(preview);
        }
    }

    private static IEnumerable<MmdPlayModePreview> FindScenePreviews()
    {
        foreach (MmdPlayModePreview preview in Resources.FindObjectsOfTypeAll<MmdPlayModePreview>())
        {
            if (preview != null && preview.gameObject.scene.IsValid()) yield return preview;
        }
    }

    private static void RemoveAutoAddedGestureManager(MmdPlayModePreview preview)
    {
        if (!preview.gestureManagerAutoAdded) return;

        Component manager = preview.gestureManager != null
            ? preview.gestureManager
            : preview.GetComponent("BlackStartX.GestureManager.GestureManager");
        preview.gestureManager = null;
        preview.gestureManagerAutoAdded = false;
        if (manager != null) Undo.DestroyObjectImmediate(manager);
    }
}
