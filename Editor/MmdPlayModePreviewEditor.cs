using System;
using System.Collections.Generic;
using System.Reflection;
using Unity.Profiling;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using AvatarDescriptor = VRC.SDKBase.VRC_AvatarDescriptor;

[CustomEditor(typeof(MmdPlayModePreview))]
public sealed class MmdPlayModePreviewEditor : Editor
{
    private const string PackageRoot = "Packages/com.verafirefly.mmd-playmode-preview";
    private const string PreviewScriptPath = PackageRoot + "/Runtime/MmdPlayModePreview.cs";
    private const string PreviewIconPath = PackageRoot + "/Editor/Icon/icon.png";
    private const string AuthorName = "VeraFirefly";
    private const string LanguagePreference = "MmdPlayModePreview.Language";
    private readonly List<AvatarDescriptor> avatars = new List<AvatarDescriptor>();
    private int avatarIndex = -1;
    private string[] avatarNames;
    private bool avatarListDirty;
    private VisualElement inspectorRoot;
    private IMGUIContainer previewInspector;
    private IVisualElementScheduledItem inspectorRefresh;
    private VisualElement gestureManagerHost;
    private Editor gestureManagerEditor;
    private Component gestureManagerTarget;
    private Component requestedGestureManagerTarget;
    private bool gestureManagerSyncScheduled;
    private Component configuredGestureManager;
    private GameObject configuredGestureAvatar;
    private static readonly ProfilerMarker InspectorMarker = new ProfilerMarker("MMDPreview.Inspector");
    private static readonly ProfilerMarker BuildCurvesMarker = new ProfilerMarker("MMDPreview.BuildTransformCurves");
    private static string cachedPackageVersion;
    private static EditorLanguage? cachedLanguage;
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
        get => cachedLanguage ?? (cachedLanguage = (EditorLanguage)EditorPrefs.GetInt(
            LanguagePreference,
            (int)EditorLanguage.SimplifiedChinese)).Value;
        set
        {
            cachedLanguage = value;
            EditorPrefs.SetInt(LanguagePreference, (int)value);
        }
    }

    public override VisualElement CreateInspectorGUI()
    {
        inspectorRefresh?.Pause();
        inspectorRoot = new VisualElement();
        previewInspector = new IMGUIContainer(OnInspectorGUI);
        inspectorRoot.Add(previewInspector);
        // Refresh only our controls at 10 Hz. Repaint() from OnInspectorGUI
        // continuously invalidated the entire Inspector, including Gesture Manager.
        // UI Toolkit suspends this schedule while the Inspector is detached.
        inspectorRefresh = previewInspector.schedule.Execute(() =>
        {
            if (Application.isPlaying) previewInspector.MarkDirtyRepaint();
        }).Every(100);
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
        EditorApplication.hierarchyChanged += OnHierarchyChanged;
    }

    private void OnDisable()
    {
        EditorApplication.hierarchyChanged -= OnHierarchyChanged;
        DisposeEmbeddedGestureManager();
    }

    private void OnHierarchyChanged()
    {
        // The runtime locks the selected avatar throughout a preview session.
        if (Application.isPlaying) return;
        avatarListDirty = true;
        Repaint();
    }

    public override void OnInspectorGUI()
    {
        using (InspectorMarker.Auto()) DrawInspector();
    }

    private void DrawInspector()
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

        if (avatarListDirty && !Application.isPlaying) ScanAvatars();
        serializedObject.Update();

        BeginColoredSection(L("模型预览", "Model Preview", "モデルプレビュー"), new Color(0.16f, 0.38f, 0.72f));
        using (new EditorGUI.DisabledScope(Application.isPlaying))
        {
            DrawAvatarSelector(preview);
        }
        using (new EditorGUI.DisabledScope(preview.IsRecording))
        {
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
                DrawVideoBackgroundSettings();
                EditorGUI.indentLevel--;
            }
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
        SerializedProperty recordVideo = serializedObject.FindProperty("recordVideo");
        if (!preview.IsRecording && !MmdPreviewVideoRecorder.IsEncoderAvailable) recordVideo.boolValue = false;
        SerializedProperty loopPlayback = serializedObject.FindProperty("loopPlayback");
        bool obsRecording = serializedObject.FindProperty("enableSpoutOutput").boolValue &&
            serializedObject.FindProperty("syncObsRecording").boolValue;
        if (recordVideo.boolValue || obsRecording) loopPlayback.boolValue = false;
        using (new EditorGUI.DisabledScope(recordVideo.boolValue || obsRecording))
            EditorGUILayout.PropertyField(loopPlayback,
                new GUIContent(L("循环播放", "Loop Playback", "ループ再生")));

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
        if (Application.isPlaying)
        {
            DrawVideoOutput(preview);
            DrawPlaybackSettings(preview);
        }
        serializedObject.ApplyModifiedProperties();

        if (Application.isPlaying && preview.HasPlaybackContent)
        {
            BeginColoredSection(L("播放控制", "Playback Controls", "再生コントロール"), new Color(0.76f, 0.4f, 0.1f));
            DrawPlaybackControls(preview);
            EndColoredSection();
        }
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
        if (cachedPackageVersion != null) return cachedPackageVersion;
        UnityEditor.PackageManager.PackageInfo package =
            UnityEditor.PackageManager.PackageInfo.FindForAssetPath(PackageRoot);
        cachedPackageVersion = string.IsNullOrWhiteSpace(package?.version) ? "Unknown" : package.version;
        return cachedPackageVersion;
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

    private void DrawVideoBackgroundSettings()
    {
        SerializedProperty video = serializedObject.FindProperty("backgroundVideo");
        EditorGUILayout.PropertyField(video, new GUIContent(L("视频背景", "Video Background", "動画背景")));
        var clip = video.objectReferenceValue as UnityEngine.Video.VideoClip;
        if (clip == null) return;
        SerializedProperty timeline = serializedObject.FindProperty("backgroundVideoTimelineStart");
        SerializedProperty start = serializedObject.FindProperty("backgroundVideoClipStart");
        SerializedProperty end = serializedObject.FindProperty("backgroundVideoClipEnd");
        timeline.floatValue = Mathf.Max(0f, EditorGUILayout.FloatField(
            L("预览中开始时间 (秒)", "Timeline Start (s)", "開始タイムライン (秒)"), timeline.floatValue));
        start.floatValue = Mathf.Max(0f, EditorGUILayout.FloatField(
            L("视频片段起点 (秒)", "Clip In (s)", "動画の開始 (秒)"), start.floatValue));
        end.floatValue = Mathf.Max(0f, EditorGUILayout.FloatField(
            L("视频片段终点 (秒)", "Clip Out (s)", "動画の終了 (秒)"), end.floatValue));
        double effectiveEnd = end.floatValue > 0 ? System.Math.Min(clip.length, end.floatValue) : clip.length;
        if (start.floatValue >= effectiveEnd)
            EditorGUILayout.HelpBox(L("片段起点必须小于终点。", "Clip In must precede Clip Out.", "開始は終了より前に設定してください。"), MessageType.Warning);
        EditorGUILayout.HelpBox(L(
            "终点为 0 时使用视频结尾。片段按预览时间同步播放，区间外显示图片/纯色背景；视频原声静音，音轨使用 MMD 音频。录制期间锁定背景设置。",
            "Clip Out 0 uses the video end. The segment follows preview time; outside it the image/color is shown. Video audio is muted; use the MMD audio clip. Background settings are locked during recording.",
            "終了が 0 の場合は動画末尾まで。区間外は画像/単色を表示します。動画は無音で、MMD の音声を使用します。録画中は背景設定を固定します。"), MessageType.Info);
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
            using (new EditorGUI.DisabledScope(MmdPreviewObsRecording.IsBusy || (preview.IsObsRecordingLinked && preview.IsPlaybackPlaying)))
            if (GUILayout.Button(playPauseIcon, GUILayout.Width(36f), GUILayout.Height(26f)))
            {
                if (preview.IsPlaybackPlaying) preview.PausePreview();
                else preview.PlayPreview();
            }
            using (new EditorGUI.DisabledScope(preview.IsRecording || preview.IsObsRecordingLinked || MmdPreviewObsRecording.HasSession))
                if (GUILayout.Button(
                        GetIconContent("Animation.FirstKey", "↶", L("重置到开头", "Reset to Start", "先頭に戻す")),
                        GUILayout.Width(36f),
                        GUILayout.Height(26f)))
                    preview.ResetPreview();
            using (new EditorGUI.DisabledScope(preview.IsPlaybackEnded && !MmdPreviewObsRecording.HasSession))
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
            using (new EditorGUI.DisabledScope(preview.IsRecording || preview.IsObsRecordingLinked || MmdPreviewObsRecording.HasSession))
            {
                EditorGUI.BeginChangeCheck();
                float time = EditorGUILayout.Slider(preview.PlaybackTime, 0f, duration);
                if (EditorGUI.EndChangeCheck()) preview.SeekPreview(time);
            }

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

    private void DrawVideoOutput(MmdPlayModePreview preview)
    {
        BeginColoredSection(L("OBS 画面输出", "OBS Video Output", "OBS 映像出力"), new Color(0.38f, 0.35f, 0.63f));
        bool available = MmdPreviewSpoutOutput.IsAvailable;
        SerializedProperty enabled = serializedObject.FindProperty("enableSpoutOutput");
        using (new EditorGUI.DisabledScope(!available || preview.IsRecording))
            EditorGUILayout.PropertyField(enabled, new GUIContent(L("Spout2 输出", "Spout2 Output", "Spout2 出力")));
        if (!available)
            EditorGUILayout.HelpBox(L("Spout2 输出需要 Windows x64、Direct3D 11/12、Built-in Render Pipeline 及内置原生插件；其它预览功能不受影响。",
                "Spout2 requires Windows x64, Direct3D 11/12, Built-in RP and the bundled native plugin. Other preview features remain available.",
                "Spout2 は Windows x64、Direct3D 11/12、Built-in RP と同梱プラグインが必要です。他の機能は利用できます。"), MessageType.Info);
        using (new EditorGUI.DisabledScope(enabled.boolValue || preview.IsRecording || !available))
        {
            EditorGUILayout.PropertyField(serializedObject.FindProperty("spoutSenderName"),
                new GUIContent(L("输出名称", "Sender Name", "送信名"), L("使用英文、数字及 ASCII 符号；重名时自动添加编号。", "Use printable ASCII; duplicate names receive a suffix.", "半角英数・ASCII 記号を使用。重複時は番号を追加します。")));
            SerializedProperty size = serializedObject.FindProperty("spoutOutputSize");
            size.vector2IntValue = EditorGUILayout.Vector2IntField(L("输出尺寸", "Output Size", "出力サイズ"), size.vector2IntValue);
        }
        if (enabled.boolValue)
        {
            serializedObject.FindProperty("recordVideo").boolValue = false;
            string name = MmdPreviewSpoutOutput.GetSenderName(preview);
            if (!string.IsNullOrEmpty(name))
                using (new EditorGUI.DisabledScope(true))
                    EditorGUILayout.TextField(L("当前输出", "Active Sender", "送信中"), name);
            EditorGUILayout.HelpBox(L("在 OBS 添加“Spout2 Capture”并选择此输出。仅发送预览摄像机画面；声音使用 OBS“应用程序音频采集”选择 Unity。直播输出与逐帧视频录制互斥，请在 OBS 中录制。",
                "In OBS, add Spout2 Capture and select this sender. Camera video only; capture Unity with Application Audio Capture for sound. Live output excludes offline recording; record in OBS.",
                "OBS で Spout2 Capture を追加し送信名を選択。映像のみ送信するため、音声はアプリケーション音声キャプチャで Unity を選択。フレーム録画と併用できません。録画は OBS で行ってください。"), MessageType.Info);
        }
        string error = MmdPreviewSpoutOutput.GetError(preview);
        if (!string.IsNullOrEmpty(error)) EditorGUILayout.HelpBox(error, MessageType.Error);
        using (new EditorGUI.DisabledScope(!enabled.boolValue || preview.IsRecording || MmdPreviewObsRecording.IsBusy))
            EditorGUILayout.PropertyField(serializedObject.FindProperty("syncObsRecording"),
                new GUIContent(L("同步 OBS 录制", "Sync OBS Recording", "OBS 録画を同期")));
        if (serializedObject.FindProperty("syncObsRecording").boolValue)
        {
            if (enabled.boolValue) serializedObject.FindProperty("loopPlayback").boolValue = false;
            using (new EditorGUI.DisabledScope(MmdPreviewObsRecording.IsConnected || MmdPreviewObsRecording.IsBusy))
            {
                string endpoint = EditorGUILayout.TextField(L("WebSocket 地址", "WebSocket URL", "WebSocket URL"), MmdPreviewObsRecording.Endpoint);
                if (endpoint != MmdPreviewObsRecording.Endpoint) MmdPreviewObsRecording.Endpoint = endpoint;
                MmdPreviewObsRecording.Password = EditorGUILayout.PasswordField(L("密码", "Password", "パスワード"), MmdPreviewObsRecording.Password);
            }
            using (new EditorGUI.DisabledScope(MmdPreviewObsRecording.IsBusy))
            {
                if (GUILayout.Button(MmdPreviewObsRecording.IsConnected
                    ? L("断开 OBS", "Disconnect OBS", "OBS を切断") : L("连接 OBS", "Connect OBS", "OBS に接続")))
                {
                    if (MmdPreviewObsRecording.IsConnected) MmdPreviewObsRecording.Disconnect();
                    else MmdPreviewObsRecording.Connect();
                }
            }
            EditorGUILayout.HelpBox(MmdPreviewObsRecording.Status, MessageType.Info);
            EditorGUILayout.HelpBox(L("在 OBS“工具 → WebSocket 服务器设置”启用服务器，填写地址和密码后连接。播放启动 OBS 录制，结束或播放完成后停止保存。联动时禁用循环、暂停、重置和进度拖动，仅允许播放和结束。密码仅保存在本次编辑器会话中。",
                "Enable the OBS WebSocket server under Tools, then enter its URL and password and connect. Play starts recording; End/completion saves it. Loop, Pause, Reset and seeking are disabled while linked; use Play and End only. The password is held only in memory.",
                "OBS のツールで WebSocket サーバーを有効にし、URL とパスワードで接続。再生で録画開始、終了で保存。連動中はループ・一時停止・リセット・シークを無効にし、再生と終了のみ使用できます。パスワードはメモリのみ保持します。"), MessageType.Info);
        }
        EndColoredSection();
    }

    private void DrawPlaybackSettings(MmdPlayModePreview preview)
    {
        BeginColoredSection(L("播放设置", "Playback Settings", "再生設定"), new Color(0.2f, 0.48f, 0.65f));
        SerializedProperty speed = serializedObject.FindProperty("playbackSpeed");
        using (new EditorGUI.DisabledScope(preview.IsRecording))
        {
            speed.floatValue = EditorGUILayout.Slider(
                L("播放倍速", "Playback Speed", "再生速度"), speed.floatValue, 0.1f, 3f);
            using (new EditorGUILayout.HorizontalScope())
            {
                foreach (float preset in new[] { 0.1f, 0.2f, 0.5f, 1f, 2f })
                    if (GUILayout.Button(preset.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "x", EditorStyles.miniButton))
                        speed.floatValue = preset;
            }
        }

        SerializedProperty recording = serializedObject.FindProperty("recordVideo");
        SerializedProperty audioLink = serializedObject.FindProperty("enableAudioLink");
        bool canConnectAudioLink = MmdPreviewAudioLink.IsAvailable && preview.audioLinkSessionTarget != null;
        if (!canConnectAudioLink) audioLink.boolValue = false;
        using (new EditorGUI.DisabledScope(!canConnectAudioLink))
        {
            EditorGUILayout.PropertyField(audioLink,
                new GUIContent(L("AudioLink接入", "AudioLink Integration", "AudioLink 接続"),
                    L("勾选以激活 AudioLink 并接入预览音频；关闭时停用 AudioLink 对象。", "Activate AudioLink and feed preview audio; deactivate its GameObject when off.", "AudioLink を有効にしてプレビュー音声を接続し、オフ時はオブジェクトを無効にします。")));
        }
        if (!MmdPreviewAudioLink.IsAvailable)
            EditorGUILayout.HelpBox(L("未安装 AudioLink，接入功能不可用。", "AudioLink is not installed; integration is unavailable.", "AudioLink が未導入のため接続は無効です。"), MessageType.Info);
        else if (!canConnectAudioLink)
            EditorGUILayout.HelpBox(L("进入 Play 前未检测到激活且启用的 AudioLink 组件。", "No active, enabled AudioLink was found before Play Mode.", "Play 前に有効な AudioLink が検出されませんでした。"), MessageType.Info);
        bool encoderAvailable = MmdPreviewVideoRecorder.IsEncoderAvailable;
        if (!encoderAvailable && !preview.IsRecording) recording.boolValue = false;
        using (new EditorGUI.DisabledScope(!encoderAvailable || preview.enableSpoutOutput ||
            serializedObject.FindProperty("enableSpoutOutput").boolValue || !preview.IsPlaybackEnded || preview.IsRecording))
            EditorGUILayout.PropertyField(recording,
                new GUIContent(L("视频录制", "Record Video", "動画を録画")));
        if (!encoderAvailable)
            EditorGUILayout.HelpBox(L("内置 FFmpeg 不可用（随包提供 Windows x64 版本），录制和压缩已禁用，其它预览功能可正常使用。",
                "Bundled FFmpeg is unavailable (Windows x64 included). Recording and compression are disabled; other preview features remain available.",
                "内蔵 FFmpeg が利用できません（Windows x64 版を同梱）。録画と圧縮は無効ですが、他のプレビュー機能は利用できます。"), MessageType.Info);
        if (recording.boolValue)
        {
            serializedObject.FindProperty("loopPlayback").boolValue = false;
            using (new EditorGUI.DisabledScope(preview.IsRecording))
                EditorGUILayout.PropertyField(serializedObject.FindProperty("compressRecordedVideo"),
                    new GUIContent(L("录制后压缩", "Compress After Recording", "録画後に圧縮"),
                        L("后台压缩为较小 MP4，保留原文件。", "Compress to a smaller MP4 in the background; keep the original.", "元の動画を残し、バックグラウンドで小さい MP4 に圧縮します。")));
            SerializedProperty output = serializedObject.FindProperty("recordingOutputDirectory");
            if (string.IsNullOrEmpty(output.stringValue))
                output.stringValue = EditorPrefs.GetString(MmdPreviewVideoRecorder.OutputDirectoryPreference, "");
            using (new EditorGUI.DisabledScope(preview.IsRecording))
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.PrefixLabel(L("输出文件夹", "Output Folder", "出力フォルダー"));
                EditorGUILayout.SelectableLabel(output.stringValue, EditorStyles.textField,
                    GUILayout.Height(EditorGUIUtility.singleLineHeight));
                if (GUILayout.Button(GetIconContent("Folder Icon", "▣",
                        L("选择输出文件夹", "Select Output Folder", "出力フォルダーを選択")),
                        GUILayout.Width(28f), GUILayout.Height(EditorGUIUtility.singleLineHeight)))
                {
                    string selected = EditorUtility.OpenFolderPanel(
                        L("视频输出文件夹", "Video Output Folder", "動画の出力フォルダー"), output.stringValue, "");
                    if (!string.IsNullOrEmpty(selected))
                    {
                        output.stringValue = selected;
                        EditorPrefs.SetString(MmdPreviewVideoRecorder.OutputDirectoryPreference, selected);
                    }
                }
            }
            EditorGUILayout.HelpBox(preview.IsRecording
                ? (preview.IsPlaybackPlaying
                    ? L("正在逐帧录制（试听静音，成片含音轨）：暂停可暂存进度，结束后编码保存 MP4。", "Frame capture (monitor muted, audio included in output): Pause to suspend, End to encode MP4.", "フレーム録画中（試聴はミュート、動画には音声を保存）：一時停止で中断、終了で MP4 を保存。")
                    : L("录制已暂停，点击播放继续写入同一个视频。", "Recording paused. Play resumes the same video.", "録画を一時停止中。再生で同じ動画に続けて録画します。"))
                : L("MP4 · 60 fps · 当前画面尺寸（最高 1080p）。逐帧录制，试听静音，成片保留同步音轨。需 FFmpeg。播放开始，暂停不计入视频，结束后保存。录制期间锁定倍速、循环、重置和进度拖动。",
                    "MP4 · 60 fps · Native size (up to 1080p). Frame capture; monitoring is muted, output includes synchronized audio. Requires FFmpeg. Play records, Pause omits idle time, End saves. Speed, loop, reset and seeking are locked.",
                    "MP4・60 fps・現在の画面サイズ（最大 1080p）。フレーム録画、試聴はミュート、動画には同期音声を保存。FFmpeg が必要です。再生で録画、一時停止時間は除外、終了で保存。速度・ループ・リセット・シークは固定。"), MessageType.Info);
        }
        if (!string.IsNullOrEmpty(MmdPreviewVideoRecorder.LastError))
            EditorGUILayout.HelpBox(MmdPreviewVideoRecorder.LastError, MessageType.Error);
        if (!string.IsNullOrEmpty(MmdPreviewVideoRecorder.LastOutputPath))
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(true))
                    EditorGUILayout.TextField(L("上次输出", "Last Output", "前回の出力"),
                        MmdPreviewVideoRecorder.LastOutputPath);
                if (GUILayout.Button(GetIconContent("Folder Icon", "▣",
                        L("打开所在位置", "Show in Folder", "保存先を開く")),
                        GUILayout.Width(28f), GUILayout.Height(EditorGUIUtility.singleLineHeight)))
                    EditorUtility.RevealInFinder(MmdPreviewVideoRecorder.LastOutputPath);
            }
            using (new EditorGUI.DisabledScope(!encoderAvailable || preview.IsRecording || MmdPreviewVideoRecorder.IsCompressing))
                if (GUILayout.Button(L("压缩上次输出 (FFmpeg)", "Compress Last Output (FFmpeg)", "前回の動画を圧縮 (FFmpeg)")))
                    MmdPreviewVideoRecorder.CompressLastOutput();
        }
        if (MmdPreviewVideoRecorder.IsCompressing)
            EditorGUILayout.HelpBox(L("正在后台压缩视频，完成后更新“上次输出”。原文件保留。",
                "Compressing video in the background. Last Output updates on completion; the original is kept.",
                "動画をバックグラウンドで圧縮中。完了後に出力先を更新します。元の動画は保存されます。"), MessageType.Info);
        EndColoredSection();
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
            using (BuildCurvesMarker.Auto())
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

        if (configuredGestureManager != manager || configuredGestureAvatar != preview.avatar)
        {
            ConfigureGestureManager(manager, preview.avatar);
            configuredGestureManager = manager;
            configuredGestureAvatar = preview.avatar;
        }
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
        inspectorRefresh?.Pause();
        inspectorRefresh = null;
        requestedGestureManagerTarget = null;
        gestureManagerTarget = null;
        gestureManagerSyncScheduled = false;
        if (gestureManagerEditor != null) DestroyImmediate(gestureManagerEditor);
        gestureManagerEditor = null;
        gestureManagerHost = null;
        inspectorRoot = null;
        previewInspector = null;
        configuredGestureManager = null;
        configuredGestureAvatar = null;
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
        string[] labels = avatars.Count == 0 ? BuildAvatarNames() : avatarNames;
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
        avatarListDirty = false;
        var preview = target as MmdPlayModePreview;
        avatars.Clear();
        foreach (AvatarDescriptor descriptor in Resources.FindObjectsOfTypeAll<AvatarDescriptor>())
        {
            if (descriptor == null || !descriptor.gameObject.activeInHierarchy || !descriptor.gameObject.scene.IsValid()) continue;
            avatars.Add(descriptor);
        }
        avatars.Sort((left, right) => string.Compare(GetHierarchyPath(left.transform), GetHierarchyPath(right.transform), StringComparison.OrdinalIgnoreCase));
        avatarNames = BuildAvatarNames();
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
        if (state == PlayModeStateChange.ExitingEditMode)
        {
            Behaviour audioLink = MmdPreviewAudioLink.FindActiveSceneAudioLink();
            foreach (MmdPlayModePreview preview in FindScenePreviews())
            {
                preview.audioLinkSessionTarget = audioLink;
                preview.enableAudioLink = false;
            }
        }
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
            preview.audioLinkSessionTarget = null;
            preview.enableAudioLink = false;
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
