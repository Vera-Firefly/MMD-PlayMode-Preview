using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using VRChat.MmdPreview.Pmx2Fbx;
using VRChat.MmdPreview.Pmx2Fbx.Editor;

public sealed class MainConverterWindow : EditorWindow
{
    private sealed class ManagedPreparationJob
    {
        internal int Ordinal;
        internal string AssetPath;
        internal string PhysicalPath;
        internal string ExpressionAssetPath;
        internal string ExpressionPhysicalPath;
        internal volatile int CompletedSamples;
        internal volatile int TotalSamples;
        internal Task<VmdMotionConverter.ManagedPreparedConversion> Work;
    }

    private const float VmdFrameRate = 30f;
    private const string MenuPath = "VeraFirefly/MMD Play Mode Preview/VMD Converter";
    private const string PackageRoot = "Packages/com.vrchat.mmd-playmode-preview";
    private const string IconPath = PackageRoot + "/Editor/Icon/icon.png";
    private const string AuthorName = "VeraFirefly";
    private const string LanguagePreference = "MmdPlayModePreview.Language";
    private const string DefaultMotionStorageFolder =
        "Assets/VeraFirefly/MMD Play Mode Converter OutPut";
    private static readonly Quaternion MmdToUnityRotation = Quaternion.Euler(0f, 180f, 0f);
    private static readonly Color[] DarkMotionEntryColors =
    {
        new Color(0.38f, 0.78f, 1f),
        new Color(1f, 0.62f, 0.38f),
        new Color(0.55f, 0.88f, 0.48f),
        new Color(0.85f, 0.58f, 1f),
        new Color(1f, 0.78f, 0.30f),
        new Color(0.40f, 0.88f, 0.82f)
    };
    private static readonly Color[] LightMotionEntryColors =
    {
        new Color(0.05f, 0.42f, 0.72f),
        new Color(0.72f, 0.25f, 0.05f),
        new Color(0.16f, 0.52f, 0.12f),
        new Color(0.52f, 0.20f, 0.68f),
        new Color(0.65f, 0.42f, 0.02f),
        new Color(0.02f, 0.48f, 0.43f)
    };

    [SerializeField] private List<DefaultAsset> motionVmdAssets = new List<DefaultAsset>();
    [SerializeField] private List<DefaultAsset> motionExpressionVmdAssets = new List<DefaultAsset>();
    [SerializeField] private List<bool> motionVmdFileFoldouts = new List<bool>();
    [SerializeField] private Vector2 motionVmdListScroll;
    [SerializeField] private Vector2 motionPageScroll;
    [SerializeField] private DefaultAsset motionModelAsset;
    [SerializeField] private VmdMotionConversionScheme motionConversionScheme =
        VmdMotionConversionScheme.ExternalHumanoidClip;
    [SerializeField] private bool motionConversionSettingsExpanded = true;
    [SerializeField] private MotionStorageLocation motionAssetStorageLocation;
    [SerializeField] private string customMotionAssetStorageFolder = DefaultMotionStorageFolder;
    [SerializeField] private MotionStorageLocation motionOutputStorageLocation;
    [SerializeField] private string customMotionOutputStorageFolder = DefaultMotionStorageFolder;
    [SerializeField] private bool retargetModel;
    [SerializeField] private int selectedModelIndex;

    [SerializeField] private List<DefaultAsset> cameraVmdAssets = new List<DefaultAsset>();
    [SerializeField] private Vector2 cameraVmdListScroll;
    [SerializeField] private Vector2 cameraPageScroll;
    [SerializeField] private MotionStorageLocation cameraOutputStorageLocation;
    [SerializeField] private string customCameraOutputStorageFolder = DefaultMotionStorageFolder;
    [SerializeField] private List<DefaultAsset> vpdAssets = new List<DefaultAsset>();
    [SerializeField] private List<bool> vpdFileFoldouts = new List<bool>();
    [SerializeField] private Vector2 vpdListScroll;
    [SerializeField] private Vector2 vpdPageScroll;
    [SerializeField] private bool vpdConversionSettingsExpanded = true;
    [SerializeField] private bool vpdRetargetModel;
    [SerializeField] private DefaultAsset vpdModelAsset;
    [SerializeField] private GameObject vpdTargetAvatar;
    [SerializeField] private MotionOutputType vpdOutputType;
    [SerializeField] private MotionStorageLocation vpdOutputStorageLocation;
    [SerializeField] private string customVpdOutputStorageFolder = DefaultMotionStorageFolder;
    [SerializeField] private MmdPlayModePreview targetPreview;
    [SerializeField] private float unitScale = 0.08f;
    [SerializeField] private int selectedTab;
    [SerializeField] private Vector2 pmxPageScroll;
    [SerializeField] private VmdMotionConverter.ModelParserMethod modelParserMethod;
    [SerializeField] private DefaultAsset debugPmxAsset;
    [SerializeField] private DefaultAsset debugVmdAsset;
    [SerializeField] private GameObject debugSkeletonRoot;
    [SerializeField] private GameObject debugMorphTargetRoot;
    [SerializeField] private BuiltInDebugOutput debugOutput = BuiltInDebugOutput.Humanoid;
    [SerializeField] private float debugSampleRate = 30f;
    [SerializeField] private float debugPositionScale = 1f;
    [SerializeField] private PmxCoordinateConversion debugCoordinateConversion =
        PmxCoordinateConversion.RotateY180;
    [SerializeField] private bool debugBakeMorphs = true;
    [SerializeField] private float curveReductionError = 0.0001f;
    [SerializeField] private MotionOutputType motionOutputType;
    [SerializeField] private float fkRotationErrorDegrees = 0.01f;
    [SerializeField] private float fkPositionError = 0.00005f;

    private readonly List<GameObject> pipelineModels = new List<GameObject>();
    [NonSerialized] private List<ConvertedMotionEntry> convertedMotionEntries;
    [NonSerialized] private bool convertedMotionResourcesDirty = true;
    [NonSerialized] private Dictionary<string, bool> mappingFoldouts;
    [NonSerialized] private Dictionary<string, int> mappingPageIndices;
    [NonSerialized] private GameObject cachedMappingModel;
    [NonSerialized] private List<VmdMotionConverter.BlendShapeTarget> cachedMappingTargets;
    [NonSerialized] private string[] cachedMappingTargetLabels;
    [NonSerialized] private Dictionary<string, int> cachedMappingTargetIndices;
    [NonSerialized] private Dictionary<string, VmdMotionConverter.BlendShapeTarget> cachedSameNameTargets;
    [NonSerialized] private Dictionary<string, VmdMotionConversionData> transientMotionData;
    [NonSerialized] private EditorLanguage cachedMappingLanguage;
    [NonSerialized] private ConverterWindowContext windowContext;
    [NonSerialized] private VmdConverterPage vmdConverterPage;
    [NonSerialized] private VmdCameraPage vmdCameraPage;
    [NonSerialized] private VpdConversionPage vpdConversionPage;
    [NonSerialized] private PmxSettingsPage pmxSettingsPage;

    private enum EditorLanguage
    {
        SimplifiedChinese,
        English,
        Japanese
    }

    private enum BuiltInDebugOutput
    {
        Humanoid,
        GenericTransform,
        TargetModelFkFidelity,
        TargetCalibratedHumanoid
    }

    private enum MotionOutputType
    {
        Humanoid,
        TargetCalibratedHumanoid
    }

    private bool IsTargetCalibratedHumanoid =>
        motionOutputType == MotionOutputType.TargetCalibratedHumanoid;

    private enum MotionStorageLocation
    {
        FollowSource,
        Default,
        Custom
    }

    private EditorLanguage Language
    {
        get => (EditorLanguage)EditorPrefs.GetInt(
            LanguagePreference,
            (int)EditorLanguage.SimplifiedChinese);
        set => EditorPrefs.SetInt(LanguagePreference, (int)value);
    }

    [MenuItem(MenuPath)]
    private static void Open()
    {
        MainConverterWindow window = GetWindow<MainConverterWindow>("VMD Converter");
        window.Show();
    }

    internal static void OpenFor(MmdPlayModePreview preview)
    {
        MainConverterWindow window = GetWindow<MainConverterWindow>("VMD Converter");
        window.targetPreview = preview;
        window.selectedTab = 1;
        window.Show();
        window.Focus();
    }

    private void OnEnable()
    {
        EnsureFeaturePages();
        titleContent = new GUIContent("VMD Converter", AssetDatabase.LoadAssetAtPath<Texture2D>(IconPath));
        minSize = new Vector2(430f, 420f);
        EnsureVmdLists();
        if (motionConversionScheme != VmdMotionConversionScheme.EmbeddedHumanoidClip &&
            motionConversionScheme != VmdMotionConversionScheme.DirectExport &&
            motionConversionScheme != VmdMotionConversionScheme.ExternalHumanoidClip)
            motionConversionScheme = VmdMotionConversionScheme.ExternalHumanoidClip;
        if (motionOutputType != MotionOutputType.Humanoid &&
            motionOutputType != MotionOutputType.TargetCalibratedHumanoid)
            motionOutputType = MotionOutputType.Humanoid;
        convertedMotionResourcesDirty = true;
        ScanPipelineModels();
        NormalizeModelParser();

        DefaultAsset selected = Selection.activeObject as DefaultAsset;
        if (IsVmdAsset(selected))
        {
            if (selectedTab == 0)
            {
                AddMotionVmdAsset(selected);
                convertedMotionResourcesDirty = true;
            }
            else if (selectedTab == 1)
            {
                AddVmdAsset(cameraVmdAssets, selected);
            }
        }
        else if (IsVpdAsset(selected) && selectedTab == 2)
        {
            AddVpdAsset(vpdAssets, selected);
        }
    }

    private void OnHierarchyChange()
    {
        InvalidateMappingTargetCache();
        ScanPipelineModels();
        Repaint();
    }

    private void OnDisable()
    {
        DestroyTransientMotionData();
    }

    private void OnProjectChange()
    {
        InvalidateMappingTargetCache();
        convertedMotionResourcesDirty = true;
        Repaint();
    }

    private void OnGUI()
    {
        EnsureFeaturePages();
        DrawHeader();
        selectedTab = GUILayout.Toolbar(
            selectedTab,
            new[]
            {
                L("VMD动作转换", "VMD Motion", "VMDモーション変換"),
                L("VMD镜头转换", "VMD Camera", "VMDカメラ変換"),
                L("VPD转换", "VPD Conversion", "VPD変換"),
                L("PMX转换设置", "PMX Settings", "PMX変換設定")
            });
        EditorGUILayout.Space(8f);

        using (new EditorGUILayout.VerticalScope(GUILayout.ExpandHeight(true)))
        {
            if (selectedTab == 0)
            {
                motionPageScroll = EditorGUILayout.BeginScrollView(motionPageScroll);
                vmdConverterPage.Draw(windowContext);
                EditorGUILayout.EndScrollView();
            }
            else if (selectedTab == 1)
            {
                cameraPageScroll = EditorGUILayout.BeginScrollView(cameraPageScroll);
                vmdCameraPage.Draw(windowContext);
                EditorGUILayout.EndScrollView();
            }
            else if (selectedTab == 2)
            {
                vpdPageScroll = EditorGUILayout.BeginScrollView(vpdPageScroll);
                vpdConversionPage.Draw(windowContext);
                EditorGUILayout.EndScrollView();
            }
            else
            {
                pmxPageScroll = EditorGUILayout.BeginScrollView(pmxPageScroll);
                pmxSettingsPage.Draw(windowContext);
                EditorGUILayout.EndScrollView();
            }
        }

        if (selectedTab == 0) vmdConverterPage.DrawAction(windowContext);
        else if (selectedTab == 1) vmdCameraPage.DrawAction(windowContext);
        else if (selectedTab == 2) vpdConversionPage.DrawAction(windowContext);
        EditorGUILayout.Space(4f);
    }

    private void EnsureFeaturePages()
    {
        if (windowContext == null) windowContext = new ConverterWindowContext();
        windowContext.Attach(this);
        if (vmdConverterPage == null) vmdConverterPage = new VmdConverterPage();
        if (vmdCameraPage == null) vmdCameraPage = new VmdCameraPage();
        if (vpdConversionPage == null) vpdConversionPage = new VpdConversionPage();
        if (pmxSettingsPage == null) pmxSettingsPage = new PmxSettingsPage();
    }

    internal void DrawVmdConverterPage() => DrawMotionConverter();
    internal void DrawVmdCameraPage() => DrawCameraConverter();
    internal void DrawVpdConversionPage() => DrawVpdConverter();
    internal void DrawPmxSettingsPage() => DrawPmxSettings();
    internal void DrawCurrentPageAction() => DrawBottomAction();

    private void DrawHeader()
    {
        using (new EditorGUILayout.HorizontalScope())
        {
            GUILayout.FlexibleSpace();
            Texture2D icon = AssetDatabase.LoadAssetAtPath<Texture2D>(IconPath);
            if (icon != null)
                GUILayout.Label(icon, GUILayout.Width(56f), GUILayout.Height(56f));
            GUILayout.Label("Play Mode Preview", new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 19,
                alignment = TextAnchor.MiddleCenter
            }, GUILayout.Height(56f));
            GUILayout.FlexibleSpace();
        }

        ConverterWindowGui.DrawCenteredLabel(
            L("版本号: ", "Version: ", "バージョン: ") + GetPackageVersion(),
            EditorStyles.centeredGreyMiniLabel);
        ConverterWindowGui.DrawCenteredLabel(
            L("作者: ", "Author: ", "作者: ") + AuthorName,
            EditorStyles.centeredGreyMiniLabel);

        EditorLanguage nextLanguage = (EditorLanguage)EditorGUILayout.Popup(
            L("语言", "Language", "言語"),
            (int)Language,
            new[] { "简体中文", "English", "日本語" });
        if (nextLanguage != Language)
        {
            Language = nextLanguage;
            InvalidateMappingTargetCache();
            Repaint();
        }
        EditorGUILayout.Space(6f);
    }

    private void DrawMotionConverter()
    {
        ConverterWindowGui.DrawSectionTitle(L("MMD动作转换", "MMD Motion Conversion", "MMDモーション変換"));

        if (DrawMotionVmdFileList())
            convertedMotionResourcesDirty = true;

        DrawMotionConversionSettings();
        EnsureConvertedMotionResourcesLoaded();

        if (convertedMotionEntries.Count == 0) return;

        EditorGUILayout.Space(8f);
        ConverterWindowGui.DrawSectionTitle(L("模型形态键映射", "Model BlendShape Mapping", "モデルモーフマッピング"));
        DrawModelMappings();
    }

    private void DrawMotionConversionSettings()
    {
        ConverterWindowGui.DrawOutlinedSetting(() =>
        {
            motionConversionSettingsExpanded = EditorGUILayout.Foldout(
                motionConversionSettingsExpanded,
                L("转换设置", "Conversion Settings", "変換設定"),
                true);
            if (!motionConversionSettingsExpanded) return;

            EditorGUILayout.Space(5f);
            DrawMotionOutputTypeSettings();
            EditorGUILayout.Space(5f);
            DrawMotionConversionScheme();
            EditorGUILayout.Space(5f);
            DrawStorageLocationSetting(
                L("Asset存储位置", "Asset Storage Location", "Asset保存場所"),
                ref motionAssetStorageLocation,
                ref customMotionAssetStorageFolder,
                L("Asset存储位置", "Asset Storage Location", "Asset保存場所"));
            EditorGUILayout.Space(5f);
            DrawStorageLocationSetting(
                L("输出位置", "Output Location", "出力場所"),
                ref motionOutputStorageLocation,
                ref customMotionOutputStorageFolder,
                L("输出存储位置", "Output Storage Location", "出力保存場所"));
            EditorGUILayout.Space(5f);
            DrawMotionConversionModelSettings();
        });
    }

    private void DrawStorageLocationSetting(
        string label,
        ref MotionStorageLocation location,
        ref string customFolder,
        string customLabel)
    {
        string[] labels =
        {
            L("跟随文件: 文件所在位置", "Follow File: File Location", "ファイルに従う: ファイルの場所"),
            L("默认: " + EscapePopupPath(DefaultMotionStorageFolder + "/"),
                "Default: " + EscapePopupPath(DefaultMotionStorageFolder + "/"),
                "既定: " + EscapePopupPath(DefaultMotionStorageFolder + "/")),
            L("自定义", "Custom", "カスタム")
        };
        EditorGUI.BeginChangeCheck();
        location = (MotionStorageLocation)EditorGUILayout.Popup(label, (int)location, labels);
        if (EditorGUI.EndChangeCheck()) convertedMotionResourcesDirty = true;
        if (location != MotionStorageLocation.Custom) return;

        using (new EditorGUILayout.HorizontalScope())
        {
            EditorGUI.BeginChangeCheck();
            customFolder = EditorGUILayout.TextField(customLabel, customFolder ?? string.Empty);
            if (EditorGUI.EndChangeCheck()) convertedMotionResourcesDirty = true;
            GUIContent folderIcon = EditorGUIUtility.IconContent("Folder Icon");
            folderIcon.tooltip = L("选择Assets内的文件夹", "Select a folder inside Assets", "Assets内のフォルダーを選択");
            if (GUILayout.Button(folderIcon, GUILayout.Width(28f), GUILayout.Height(EditorGUIUtility.singleLineHeight)))
            {
                string selectedFolder = EditorUtility.OpenFolderPanel(
                    customLabel,
                    AssetFolderToPhysicalPath(customFolder),
                    string.Empty);
                if (!string.IsNullOrEmpty(selectedFolder))
                {
                    if (TryPhysicalFolderToAssetPath(selectedFolder, out string assetFolder))
                    {
                        customFolder = assetFolder;
                        convertedMotionResourcesDirty = true;
                        GUI.FocusControl(null);
                    }
                    else
                    {
                        ShowDialog(
                            customLabel,
                            L("请选择项目Assets目录内的文件夹。",
                                "Select a folder inside the project's Assets directory.",
                                "プロジェクトのAssetsフォルダー内を選択してください。"));
                    }
                }
            }
        }

        if (!TryNormalizeAssetFolder(customFolder, out _))
            EditorGUILayout.LabelField(
                L("路径必须位于Assets目录内。",
                    "The path must be inside Assets.",
                    "パスはAssets内である必要があります。"),
                EditorStyles.miniLabel);
    }

    private void DrawMotionConversionScheme()
    {
        if (IsTargetCalibratedHumanoid &&
            motionConversionScheme != VmdMotionConversionScheme.DirectExport)
            motionConversionScheme = VmdMotionConversionScheme.DirectExport;
        VmdMotionConversionScheme[] values =
        {
            VmdMotionConversionScheme.EmbeddedHumanoidClip,
            VmdMotionConversionScheme.DirectExport,
            VmdMotionConversionScheme.ExternalHumanoidClip
        };
        string[] labels =
        {
            L("方案一：内嵌完整 Humanoid AnimationClip",
                "Scheme 1: Embedded Humanoid AnimationClip",
                "方式1：Humanoid AnimationClipを内蔵"),
            L("方案二：直接导出动画（不生成.asset）",
                "Scheme 2: Direct Export (No .asset)",
                "方式2：直接書き出し（.assetなし）"),
            L("方案三：元数据与 AnimationClip 分离",
                "Scheme 3: Separate Metadata and AnimationClip",
                "方式3：メタデータとAnimationClipを分離")
        };
        int current = Array.IndexOf(values, motionConversionScheme);
        if (current < 0) current = 2;
        int next;
        using (new EditorGUI.DisabledScope(IsTargetCalibratedHumanoid))
            next = EditorGUILayout.Popup(L("转换方案", "Conversion Scheme", "変換方式"), current, labels);
        if (next == current) return;
        motionConversionScheme = values[next];
        DestroyTransientMotionData();
        convertedMotionResourcesDirty = true;
    }

    private void DrawMotionOutputTypeSettings()
    {
        EditorGUI.BeginChangeCheck();
        MotionOutputType next = (MotionOutputType)EditorGUILayout.Popup(
            L("输出类型", "Output Type", "出力タイプ"),
            (int)motionOutputType,
            new[]
            {
                "Humanoid",
                L("目标校准 Humanoid", "Target-calibrated Humanoid", "対象校正Humanoid")
            });
        if (EditorGUI.EndChangeCheck())
        {
            motionOutputType = next;
            DestroyTransientMotionData();
            convertedMotionResourcesDirty = true;
            if (IsTargetCalibratedHumanoid)
            {
                motionConversionScheme = VmdMotionConversionScheme.DirectExport;
                modelParserMethod = VmdMotionConverter.ModelParserMethod.BuiltInManaged;
            }
        }
        if (!IsTargetCalibratedHumanoid) return;

        EditorGUILayout.HelpBox(
            L("在目标Avatar骨架上校准后输出标准HumanoidRoot/Muscle曲线, 该方案会额外经过目标 Avatar 的 HumanPoseHandler 校准, 因此转换通常更慢",
                "Outputs standard Humanoid Root/Muscle curves after calibration on the target Avatar skeleton. This adds a target Avatar HumanPoseHandler calibration pass, so conversion is usually slower.",
                "対象Avatar骨格で校正後、標準Humanoid Root/Muscleカーブを出力します。対象AvatarのHumanPoseHandler校正処理が追加されるため、通常は変換が遅くなります。"),
            MessageType.Info);
    }

    private void DrawMotionConversionModelSettings()
    {
        retargetModel = EditorGUILayout.Toggle(
            L("重定向模型", "Retarget Model", "モデルを置換"),
            retargetModel);
        if (!retargetModel) return;

        EditorGUI.BeginChangeCheck();
        DefaultAsset nextModel = (DefaultAsset)EditorGUILayout.ObjectField(
            L("MMD模型", "MMD Model", "MMDモデル"),
            motionModelAsset,
            typeof(DefaultAsset),
            false);
        if (!EditorGUI.EndChangeCheck()) return;

        string modelPath = nextModel != null ? AssetDatabase.GetAssetPath(nextModel) : string.Empty;
        if (nextModel != null && !VmdMotionConverter.IsModelPath(modelPath, modelParserMethod))
        {
            ShowDialog(
                L("MMD模型", "MMD Model", "MMDモデル"),
                modelParserMethod == VmdMotionConverter.ModelParserMethod.BuiltInManaged
                    ? L("内置解析方案仅支持.pmx模型。",
                        "The built-in parser supports .pmx models only.",
                        "内蔵解析方式は.pmxモデルのみ対応しています。")
                    : L("请选择.pmx或.pmd模型。",
                        "Select a .pmx or .pmd model.",
                        ".pmxまたは.pmdモデルを選択してください。"));
            return;
        }
        motionModelAsset = nextModel;
    }

    private void DrawModelMappings()
    {
        if (pipelineModels.Count == 0)
        {
            EditorGUILayout.LabelField(
                L("模型选择", "Model", "モデル"),
                L("未找到 Pipeline Manager 模型",
                    "No Pipeline Manager model found",
                    "Pipeline Manager モデルが見つかりません"));
            return;
        }

        selectedModelIndex = Mathf.Clamp(selectedModelIndex, 0, pipelineModels.Count - 1);
        string[] modelNames = pipelineModels.Select(GetHierarchyPath).ToArray();
        selectedModelIndex = EditorGUILayout.Popup(
            L("模型选择", "Model", "モデル"),
            selectedModelIndex,
            modelNames);

        GameObject model = pipelineModels[selectedModelIndex];
        GetMappingTargetCache(
            model,
            out List<VmdMotionConverter.BlendShapeTarget> targets,
            out string[] targetLabels,
            out Dictionary<string, int> targetIndices,
            out Dictionary<string, VmdMotionConverter.BlendShapeTarget> sameNameTargets);
        string modelHierarchyPath = GetHierarchyPath(model);
        bool createdMapping = false;
        for (int entryIndex = 0; entryIndex < convertedMotionEntries.Count; entryIndex++)
        {
            createdMapping |= DrawMotionMappingEntry(
                convertedMotionEntries[entryIndex],
                entryIndex,
                model,
                modelHierarchyPath,
                targets,
                targetLabels,
                targetIndices,
                sameNameTargets);
        }
        if (createdMapping) AssetDatabase.SaveAssets();
    }

    private bool DrawMotionMappingEntry(
        ConvertedMotionEntry entry,
        int entryIndex,
        GameObject model,
        string modelHierarchyPath,
        List<VmdMotionConverter.BlendShapeTarget> targets,
        string[] targetLabels,
        Dictionary<string, int> targetIndices,
        IReadOnlyDictionary<string, VmdMotionConverter.BlendShapeTarget> sameNameTargets)
    {
        VmdMotionConversionData.ModelMapping modelMapping;
        bool mappingCreated;
        try
        {
            modelMapping = VmdMotionConverter.GetOrCreateMapping(
                entry.Data,
                model,
                targets,
                out mappingCreated,
                (value, message) => EditorUtility.DisplayProgressBar(
                    L("自动匹配同名形态键", "Matching Same-name Morphs", "同名モーフを自動照合"),
                    $"[{entryIndex + 1}/{convertedMotionEntries.Count}] {entry.DisplayName} — {message}",
                    (entryIndex + Mathf.Clamp01(value)) /
                    Mathf.Max(1f, convertedMotionEntries.Count)),
                sameNameTargets,
                true);
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }

        Rect mappingBorder = EditorGUILayout.BeginVertical();
        EditorGUILayout.Space(4f);
        using (new EditorGUILayout.HorizontalScope())
        {
            GUILayout.Space(6f);
            using (new EditorGUILayout.VerticalScope())
            {
                EditorGUI.BeginChangeCheck();
                float angle = EditorGUILayout.FloatField(
                    L("姿势旋转角度", "Pose Rotation Angle", "ポーズ回転角度"),
                    entry.Data.poseRotationAngle);
                if (EditorGUI.EndChangeCheck())
                {
                    entry.Data.poseRotationAngle = angle;
                    if (!entry.IsTransient) EditorUtility.SetDirty(entry.Data);
                }

                string foldoutKey = entry.Path + "|" + modelHierarchyPath;
                bool expanded = GetMappingFoldout(
                    foldoutKey,
                    convertedMotionEntries.Count == 1 || entryIndex == 0);
                expanded = EditorGUILayout.Foldout(
                    expanded,
                    entry.DisplayName + L("-映射列表", "-Mapping List", "-マッピング一覧"),
                    true);
                SetMappingFoldout(foldoutKey, expanded);
                if (expanded)
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        GUILayout.Label(L("源形态键", "Source Morph", "ソースモーフ"), EditorStyles.boldLabel);
                        GUILayout.Label(L("模型形态键", "Model BlendShape", "モデルモーフ"), EditorStyles.boldLabel);
                    }

                    const int pageSize = 32;
                    int mappingCount = modelMapping.mappings.Count;
                    int pageCount = Mathf.Max(1, Mathf.CeilToInt(mappingCount / (float)pageSize));
                    int pageIndex = Mathf.Clamp(GetMappingPageIndex(foldoutKey), 0, pageCount - 1);
                    if (pageCount > 1)
                    {
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            using (new EditorGUI.DisabledScope(pageIndex <= 0))
                                if (GUILayout.Button("‹", GUILayout.Width(28f))) pageIndex--;
                            GUILayout.FlexibleSpace();
                            GUILayout.Label(
                                $"{pageIndex + 1} / {pageCount}  ({mappingCount})",
                                EditorStyles.centeredGreyMiniLabel);
                            GUILayout.FlexibleSpace();
                            using (new EditorGUI.DisabledScope(pageIndex >= pageCount - 1))
                                if (GUILayout.Button("›", GUILayout.Width(28f))) pageIndex++;
                        }
                    }
                    SetMappingPageIndex(foldoutKey, pageIndex);
                    int firstVisible = pageIndex * pageSize;
                    int lastVisible = Mathf.Min(mappingCount, firstVisible + pageSize);
                    EditorGUI.BeginChangeCheck();
                    for (int mappingIndex = firstVisible; mappingIndex < lastVisible; mappingIndex++)
                    {
                        VmdMotionConversionData.MorphMapping mapping = modelMapping.mappings[mappingIndex];
                        string targetKey = GetBlendShapeTargetKey(mapping.target, mapping.targetPath);
                        int currentIndex = targetIndices.TryGetValue(targetKey, out int foundIndex)
                            ? foundIndex
                            : 0;
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            EditorGUILayout.LabelField(mapping.source, GUILayout.MinWidth(120f));
                            int nextIndex = EditorGUILayout.Popup(currentIndex, targetLabels);
                            if (nextIndex == currentIndex) continue;
                            if (nextIndex <= 0)
                            {
                                mapping.target = string.Empty;
                                mapping.targetPath = string.Empty;
                            }
                            else
                            {
                                VmdMotionConverter.BlendShapeTarget target = targets[nextIndex - 1];
                                mapping.target = target.Name;
                                mapping.targetPath = target.Path;
                            }
                        }
                    }
                    if (EditorGUI.EndChangeCheck())
                        if (!entry.IsTransient) EditorUtility.SetDirty(entry.Data);
                }
            }
            GUILayout.Space(6f);
        }
        EditorGUILayout.Space(4f);
        EditorGUILayout.EndVertical();
        ConverterWindowGui.DrawOutline(mappingBorder);
        EditorGUILayout.Space(6f);
        return mappingCreated && !entry.IsTransient;
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

            if (useScrollView)
                EditorGUILayout.EndScrollView();
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

    private bool DrawMotionVmdFileList()
    {
        EnsureMotionVmdEntryState();
        bool changed = false;
        if (motionVmdAssets.Count > 0)
        {
            const float maxListHeight = 220f;
            float rowHeight = EditorGUIUtility.singleLineHeight +
                              EditorGUIUtility.standardVerticalSpacing;
            float contentHeight = 0f;
            for (int index = 0; index < motionVmdAssets.Count; index++)
                contentHeight += rowHeight * (motionVmdFileFoldouts[index] ? 3f : 1f) +
                                 (index > 0 ? 5f : 0f);
            bool useScrollView = contentHeight > maxListHeight;
            if (useScrollView)
            {
                motionVmdListScroll = EditorGUILayout.BeginScrollView(
                    motionVmdListScroll,
                    false,
                    false,
                    GUILayout.Height(maxListHeight));
            }

            for (int index = 0; index < motionVmdAssets.Count; index++)
            {
                if (index > 0) ConverterWindowGui.DrawEntrySeparator();
                DefaultAsset displayAsset = motionVmdAssets[index] != null
                    ? motionVmdAssets[index]
                    : motionExpressionVmdAssets[index];
                Color entryColor = GetMotionEntryColor(index);
                bool removeEntry = false;
                Color previousContentColor = GUI.contentColor;
                GUI.contentColor = entryColor;
                using (new EditorGUILayout.HorizontalScope())
                {
                    string arrow = motionVmdFileFoldouts[index] ? "▼ " : "▶ ";
                    if (GUILayout.Button(
                            arrow + GetVmdDisplayName(displayAsset),
                            EditorStyles.boldLabel,
                            GUILayout.ExpandWidth(true)))
                        motionVmdFileFoldouts[index] = !motionVmdFileFoldouts[index];
                    removeEntry = GUILayout.Button(
                        "×", EditorStyles.miniButton, GUILayout.Width(24f));
                }
                GUI.contentColor = previousContentColor;
                if (removeEntry)
                {
                    motionVmdAssets.RemoveAt(index);
                    motionExpressionVmdAssets.RemoveAt(index);
                    motionVmdFileFoldouts.RemoveAt(index);
                    changed = true;
                    break;
                }
                if (!motionVmdFileFoldouts[index]) continue;

                EditorGUI.indentLevel++;
                if (DrawMotionVmdDataRow(
                        L("动作数据", "Motion Data", "モーションデータ"),
                        motionVmdAssets[index],
                        out DefaultAsset nextMotion))
                {
                    string nextPath = GetVmdAssetPath(nextMotion);
                    bool duplicate = !string.IsNullOrEmpty(nextPath) && motionVmdAssets
                        .Where((_, otherIndex) => otherIndex != index)
                        .Any(other => string.Equals(
                            GetVmdAssetPath(other),
                            nextPath,
                            StringComparison.OrdinalIgnoreCase));
                    if (duplicate)
                    {
                        ShowDialog(
                            L("VMD文件", "VMD Files", "VMDファイル"),
                            L("该动作VMD已在列表中。",
                                "That motion VMD is already in the list.",
                                "そのモーションVMDはすでにリストにあります。"));
                    }
                    else
                    {
                        motionVmdAssets[index] = nextMotion;
                        changed = true;
                    }
                }

                if (DrawMotionVmdDataRow(
                        L("表情数据", "Expression Data", "表情データ"),
                        motionExpressionVmdAssets[index],
                        out DefaultAsset nextExpression))
                {
                    motionExpressionVmdAssets[index] = nextExpression;
                    changed = true;
                }
                EditorGUI.indentLevel--;

                if (motionVmdAssets[index] != null || motionExpressionVmdAssets[index] != null)
                    continue;
                motionVmdAssets.RemoveAt(index);
                motionExpressionVmdAssets.RemoveAt(index);
                motionVmdFileFoldouts.RemoveAt(index);
                changed = true;
                break;
            }

            if (useScrollView)
                EditorGUILayout.EndScrollView();
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
            DragAndDrop.visualMode = hasVmd
                ? DragAndDropVisualMode.Copy
                : DragAndDropVisualMode.Rejected;
            if (currentEvent.type == EventType.DragPerform && hasVmd)
            {
                DragAndDrop.AcceptDrag();
                foreach (DefaultAsset asset in DragAndDrop.objectReferences.OfType<DefaultAsset>())
                    changed |= AddMotionVmdAsset(asset);
            }
            currentEvent.Use();
        }

        return changed;
    }

    private static Color GetMotionEntryColor(int index)
    {
        Color[] palette = EditorGUIUtility.isProSkin
            ? DarkMotionEntryColors
            : LightMotionEntryColors;
        return palette[Mathf.Abs(index) % palette.Length];
    }

    private bool DrawMotionVmdDataRow(string label, DefaultAsset current, out DefaultAsset next)
    {
        next = current;
        using (new EditorGUILayout.HorizontalScope())
        {
            DefaultAsset selected = (DefaultAsset)EditorGUILayout.ObjectField(
                label,
                current,
                typeof(DefaultAsset),
                false);
            if (selected != current)
            {
                if (selected == null || IsVmdAsset(selected))
                {
                    next = selected;
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
            if (GUILayout.Button("×", GUILayout.Width(24f))) next = null;
        }
        return next != current;
    }

    private void DrawVpdConverter()
    {
        ConverterWindowGui.DrawSectionTitle(L("VPD姿势转换", "VPD Pose Conversion", "VPDポーズ変換"));
        DrawVpdFileList();

        ConverterWindowGui.DrawOutlinedSetting(() =>
        {
            vpdConversionSettingsExpanded = EditorGUILayout.Foldout(
                vpdConversionSettingsExpanded,
                L("转换设置", "Conversion Settings", "変換設定"),
                true);
            if (!vpdConversionSettingsExpanded) return;

            EditorGUILayout.Space(5f);
            vpdOutputType = (MotionOutputType)EditorGUILayout.Popup(
                L("输出类型", "Output Type", "出力タイプ"),
                (int)vpdOutputType,
                new[]
                {
                    "Humanoid",
                    L("目标校准 Humanoid", "Target-calibrated Humanoid", "対象校正Humanoid")
                });
            if (vpdOutputType == MotionOutputType.TargetCalibratedHumanoid)
            {
                EditorGUILayout.HelpBox(
                    L("在目标Avatar骨架上校准后输出标准HumanoidRoot/Muscle曲线, 该方案会额外经过目标 Avatar 的 HumanPoseHandler 校准, 因此转换通常更慢",
                        "Outputs standard Humanoid Root/Muscle curves after calibration on the target Avatar skeleton. This adds a target Avatar HumanPoseHandler calibration pass, so conversion is usually slower.",
                        "対象Avatar骨格で校正後、標準Humanoid Root/Muscleカーブを出力します。対象AvatarのHumanPoseHandler校正処理が追加されるため、通常は変換が遅くなります。"),
                    MessageType.Info);
            }

            EditorGUILayout.Space(5f);
            vpdTargetAvatar = (GameObject)EditorGUILayout.ObjectField(
                vpdOutputType == MotionOutputType.TargetCalibratedHumanoid
                    ? L("目标 Avatar", "Target Avatar", "対象Avatar")
                    : L("Morph目标 Avatar（可选）", "Morph Target Avatar (Optional)", "Morph対象Avatar（任意）"),
                vpdTargetAvatar,
                typeof(GameObject),
                true);

            EditorGUILayout.Space(5f);
            DrawVpdConversionModelSettings();

            EditorGUILayout.Space(5f);
            DrawStorageLocationSetting(
                L("输出位置", "Output Location", "出力場所"),
                ref vpdOutputStorageLocation,
                ref customVpdOutputStorageFolder,
                L("输出存储位置", "Output Storage Location", "出力保存場所"));
            EditorGUILayout.Space(5f);
            EditorGUILayout.HelpBox(
                L("每个VPD固定生成一个.anim。姿势写入0与1/30秒两个相同关键帧，不进行密集采样。",
                    "Each VPD always produces one .anim. The pose is written as identical keys at 0 and 1/30 seconds without dense sampling.",
                    "各VPDから必ず1つの.animを生成します。ポーズは0秒と1/30秒の同一キーとして書き込み、密なサンプリングは行いません。"),
                MessageType.None);
        });
    }

    private void DrawVpdConversionModelSettings()
    {
        vpdRetargetModel = EditorGUILayout.Toggle(
            L("重定向模型", "Retarget Model", "モデルを置換"),
            vpdRetargetModel);
        if (!vpdRetargetModel) return;

        DefaultAsset nextModel = (DefaultAsset)EditorGUILayout.ObjectField(
            L("MMD模型", "MMD Model", "MMDモデル"),
            vpdModelAsset,
            typeof(DefaultAsset),
            false);
        if (nextModel == vpdModelAsset) return;

        string modelPath = nextModel != null
            ? AssetDatabase.GetAssetPath(nextModel)
            : string.Empty;
        if (nextModel != null && !VmdMotionConverter.IsModelPath(
                modelPath, VmdMotionConverter.ModelParserMethod.BuiltInManaged))
        {
            ShowDialog(
                L("MMD模型", "MMD Model", "MMDモデル"),
                L("内置解析方案仅支持.pmx模型。",
                    "The built-in parser supports .pmx models only.",
                    "内蔵解析方式は.pmxモデルのみ対応しています。"));
            return;
        }
        vpdModelAsset = nextModel;
    }

    private void DrawVpdFileList()
    {
        EnsureVpdEntryState();
        if (vpdAssets.Count > 0)
        {
            const float maxListHeight = 220f;
            float rowHeight = EditorGUIUtility.singleLineHeight +
                              EditorGUIUtility.standardVerticalSpacing;
            float contentHeight = 0f;
            for (int index = 0; index < vpdAssets.Count; index++)
                contentHeight += rowHeight * (vpdFileFoldouts[index] ? 2f : 1f) +
                                 (index > 0 ? 5f : 0f);
            bool useScrollView = contentHeight > maxListHeight;
            if (useScrollView)
            {
                vpdListScroll = EditorGUILayout.BeginScrollView(
                    vpdListScroll, false, false, GUILayout.Height(maxListHeight));
            }

            for (int index = 0; index < vpdAssets.Count; index++)
            {
                if (index > 0) ConverterWindowGui.DrawEntrySeparator();
                Color entryColor = GetMotionEntryColor(index);
                bool removeEntry;
                Color previousContentColor = GUI.contentColor;
                GUI.contentColor = entryColor;
                using (new EditorGUILayout.HorizontalScope())
                {
                    string arrow = vpdFileFoldouts[index] ? "▼ " : "▶ ";
                    if (GUILayout.Button(
                            arrow + GetVpdDisplayName(vpdAssets[index]),
                            EditorStyles.boldLabel,
                            GUILayout.ExpandWidth(true)))
                        vpdFileFoldouts[index] = !vpdFileFoldouts[index];
                    removeEntry = GUILayout.Button(
                        "×", EditorStyles.miniButton, GUILayout.Width(24f));
                }
                GUI.contentColor = previousContentColor;
                if (removeEntry)
                {
                    vpdAssets.RemoveAt(index);
                    vpdFileFoldouts.RemoveAt(index);
                    break;
                }
                if (!vpdFileFoldouts[index]) continue;

                EditorGUI.indentLevel++;
                if (DrawVpdDataRow(
                        L("姿势数据", "Pose Data", "ポーズデータ"),
                        vpdAssets[index],
                        out DefaultAsset next))
                {
                    if (next == null)
                    {
                        vpdAssets[index] = null;
                    }
                    else
                    {
                        string nextPath = GetVpdAssetPath(next);
                        bool duplicate = vpdAssets
                            .Where((_, otherIndex) => otherIndex != index)
                            .Any(other => string.Equals(
                                GetVpdAssetPath(other), nextPath,
                                StringComparison.OrdinalIgnoreCase));
                        if (duplicate)
                        {
                            ShowDialog(
                                L("VPD文件", "VPD Files", "VPDファイル"),
                                L("该VPD已在列表中。",
                                    "That VPD is already in the list.",
                                    "そのVPDはすでにリストにあります。"));
                        }
                        else
                        {
                            vpdAssets[index] = next;
                        }
                    }
                }
                EditorGUI.indentLevel--;
                if (vpdAssets[index] != null) continue;
                vpdAssets.RemoveAt(index);
                vpdFileFoldouts.RemoveAt(index);
                break;
            }
            if (useScrollView) EditorGUILayout.EndScrollView();
        }

        Rect dropRect = GUILayoutUtility.GetRect(0f, 42f, GUILayout.ExpandWidth(true));
        var dropStyle = new GUIStyle(GUI.skin.box) { alignment = TextAnchor.MiddleCenter };
        GUI.Box(
            dropRect,
            L("拖入一个或多个VPD文件",
                "Drop one or more VPD files",
                "1つ以上のVPDファイルをドロップ"),
            dropStyle);
        Event currentEvent = Event.current;
        if (!dropRect.Contains(currentEvent.mousePosition) ||
            (currentEvent.type != EventType.DragUpdated &&
             currentEvent.type != EventType.DragPerform))
            return;

        bool hasVpd = DragAndDrop.objectReferences.OfType<DefaultAsset>().Any(IsVpdAsset);
        DragAndDrop.visualMode = hasVpd
            ? DragAndDropVisualMode.Copy
            : DragAndDropVisualMode.Rejected;
        if (currentEvent.type == EventType.DragPerform && hasVpd)
        {
            DragAndDrop.AcceptDrag();
            foreach (DefaultAsset asset in DragAndDrop.objectReferences.OfType<DefaultAsset>())
                AddVpdAsset(vpdAssets, asset);
            EnsureVpdEntryState();
        }
        currentEvent.Use();
    }

    private bool DrawVpdDataRow(string label, DefaultAsset current, out DefaultAsset next)
    {
        next = current;
        using (new EditorGUILayout.HorizontalScope())
        {
            DefaultAsset selected = (DefaultAsset)EditorGUILayout.ObjectField(
                label, current, typeof(DefaultAsset), false);
            if (selected != current)
            {
                if (selected == null || IsVpdAsset(selected))
                {
                    next = selected;
                }
                else
                {
                    ShowDialog(
                        L("VPD文件", "VPD Files", "VPDファイル"),
                        L("请选择Assets目录中的.vpd文件。",
                            "Select .vpd files inside Assets.",
                            "Assets内の.vpdファイルを選択してください。"));
                }
            }
            if (GUILayout.Button("×", GUILayout.Width(24f))) next = null;
        }
        return next != current;
    }

    private void DrawPmxSettings()
    {
        NormalizeModelParser();
        ConverterWindowGui.DrawSectionTitle(L("PMX转换设置", "PMX Conversion Settings", "PMX変換設定"));

        IReadOnlyList<VmdMotionConverter.ModelParserMethod> parsers =
            VmdMotionConverter.GetAvailableModelParsers();
        string[] parserLabels = parsers.Select(GetModelParserLabel).ToArray();
        int currentIndex = Mathf.Max(0, parsers.ToList().IndexOf(modelParserMethod));
        int nextIndex;
        using (new EditorGUI.DisabledScope(IsTargetCalibratedHumanoid))
            nextIndex = EditorGUILayout.Popup(
                L("模型解析方式", "Model Parser", "モデル解析方式"),
                currentIndex,
                parserLabels);
        modelParserMethod = parsers[Mathf.Clamp(nextIndex, 0, parsers.Count - 1)];

        if (modelParserMethod != VmdMotionConverter.ModelParserMethod.BuiltInManaged)
            return;

        EditorGUILayout.Space(8f);
        DrawBuiltInDebugSettings();
        EditorGUILayout.Space(14f);
        DrawPmxCacheManagement();
    }

    private void DrawBuiltInDebugSettings()
    {
        ConverterWindowGui.DrawSectionTitle(L("内置解析方案调试", "Built-in Parser Debug", "内蔵解析方式のデバッグ"));
        debugPmxAsset = (DefaultAsset)EditorGUILayout.ObjectField(
            L("PMX文件", "PMX File", "PMXファイル"),
            debugPmxAsset,
            typeof(DefaultAsset),
            false);
        debugVmdAsset = (DefaultAsset)EditorGUILayout.ObjectField(
            L("VMD文件", "VMD File", "VMDファイル"),
            debugVmdAsset,
            typeof(DefaultAsset),
            false);

        debugOutput = (BuiltInDebugOutput)EditorGUILayout.Popup(
            L("输出类型", "Output", "出力タイプ"),
            (int)debugOutput,
            new[]
            {
                "Humanoid",
                L("通用骨骼", "Generic Transform", "汎用ボーン"),
                L("目标模型 FK 保真模式", "Target-model FK Fidelity", "対象モデルFK忠実モード"),
                L("目标校准 Humanoid", "Target-calibrated Humanoid", "対象校正Humanoid")
            });

        if (debugOutput == BuiltInDebugOutput.Humanoid)
        {
            EditorGUILayout.LabelField(
                L("源骨架", "Source Skeleton", "ソーススケルトン"),
                L("从 PMX 自动构建", "Built automatically from PMX", "PMXから自動構築"));
            debugMorphTargetRoot = (GameObject)EditorGUILayout.ObjectField(
                L("形态键目标根（可选）", "Morph Target Root (Optional)", "モーフ対象ルート（任意）"),
                debugMorphTargetRoot,
                typeof(GameObject),
                true);
        }
        else
        {
            debugSkeletonRoot = (GameObject)EditorGUILayout.ObjectField(
                debugOutput == BuiltInDebugOutput.TargetModelFkFidelity ||
                debugOutput == BuiltInDebugOutput.TargetCalibratedHumanoid
                    ? L("目标 Humanoid 模型根", "Target Humanoid Model Root", "対象Humanoidモデルルート")
                    : L("现有PMX骨架根", "Existing PMX Skeleton Root", "既存PMXスケルトンルート"),
                debugSkeletonRoot,
                typeof(GameObject),
                true);
        }

        debugSampleRate = EditorGUILayout.Slider(
            L("采样率", "Sample Rate", "サンプルレート"),
            debugSampleRate,
            1f,
            120f);
        debugPositionScale = EditorGUILayout.FloatField(
            L("位置缩放", "Position Scale", "位置スケール"),
            debugPositionScale);
        debugCoordinateConversion = (PmxCoordinateConversion)EditorGUILayout.Popup(
            L("坐标转换", "Coordinates", "座標変換"),
            (int)debugCoordinateConversion,
            new[]
            {
                "MMD → Unity",
                "MMD → Unity + Y 180°",
                "Y 180°",
                L("无", "None", "なし")
            });
        debugBakeMorphs = EditorGUILayout.Toggle(
            L("烘焙同名形态键", "Bake Same-name Morphs", "同名モーフをベイク"),
            debugBakeMorphs);
        curveReductionError = EditorGUILayout.Slider(
            new GUIContent(
                "Curve Error",
                L("此项同时应用于VMD动作转换。0表示保留全部采样点。",
                    "This also applies to VMD Motion conversion. Zero keeps every sampled key.",
                    "この設定はVMDモーション変換にも適用されます。0では全サンプルキーを保持します。")),
            curveReductionError,
            0f,
            0.001f);
        if (debugOutput == BuiltInDebugOutput.TargetModelFkFidelity)
        {
            fkRotationErrorDegrees = EditorGUILayout.Slider(
                L("FK旋转角误差 (度)", "FK Rotation Error (deg)", "FK回転角誤差（度）"),
                fkRotationErrorDegrees,
                0f,
                0.1f);
            fkPositionError = EditorGUILayout.Slider(
                L("FK根位置误差", "FK Root Position Error", "FKルート位置誤差"),
                fkPositionError,
                0f,
                0.001f);
        }

        string pmxPath = AssetDatabase.GetAssetPath(debugPmxAsset);
        string vmdPath = AssetDatabase.GetAssetPath(debugVmdAsset);
        bool canConvert = IsAssetExtension(pmxPath, ".pmx") &&
                          IsAssetExtension(vmdPath, ".vmd") &&
                          (debugOutput == BuiltInDebugOutput.Humanoid || debugSkeletonRoot != null);
        using (new EditorGUI.DisabledScope(!canConvert))
        {
            if (GUILayout.Button(L("转换", "Convert", "変換"), GUILayout.Height(28f)))
                ConvertBuiltInDebug(pmxPath, vmdPath);
        }
    }

    private void DrawPmxCacheManagement()
    {
        ConverterWindowGui.DrawSectionTitle(L("PMX解析缓存管理", "PMX Parse Cache", "PMX解析キャッシュ管理"));
        PmxModelCache.GetDiskCacheStats(out int cacheFiles, out long cacheBytes);
        EditorGUILayout.LabelField(
            L("PMX解析缓存", "PMX Parse Cache", "PMX解析キャッシュ"),
            L($"{cacheFiles} 个文件，{FormatBytes(cacheBytes)}",
                $"{cacheFiles} files, {FormatBytes(cacheBytes)}",
                $"{cacheFiles} ファイル、{FormatBytes(cacheBytes)}"));
        using (new EditorGUI.DisabledScope(true))
            EditorGUILayout.TextField(
                L("缓存位置", "Cache Location", "キャッシュ場所"),
                PmxModelCache.DiskCacheDirectory);

        if (!GUILayout.Button(
                L("清理 PMX 解析缓存", "Clear PMX Parse Cache", "PMX解析キャッシュを削除"),
                GUILayout.Height(28f)))
            return;

        if (!EditorUtility.DisplayDialog(
                L("清理 PMX 解析缓存", "Clear PMX Parse Cache", "PMX解析キャッシュを削除"),
                L("将清除内存与磁盘中的 PMX 解析缓存。下次转换会重新解析 PMX。",
                    "This clears the PMX parse cache in memory and on disk. The next conversion will parse the PMX again.",
                    "メモリとディスク上の PMX 解析キャッシュを削除します。次回の変換時に PMX を再解析します。"),
                L("清理", "Clear", "削除"),
                L("取消", "Cancel", "キャンセル")))
            return;

        try
        {
            PmxModelCache.ClearAll();
            ShowDialog(
                L("PMX解析缓存", "PMX Parse Cache", "PMX解析キャッシュ"),
                L("缓存已清理。", "Cache cleared.", "キャッシュを削除しました。"));
        }
        catch (Exception exception)
        {
            Debug.LogException(exception);
            ShowDialog(
                L("缓存清理失败", "Cache Cleanup Failed", "キャッシュ削除に失敗"),
                exception.Message);
        }
    }

    private void ConvertBuiltInDebug(string pmxPath, string vmdPath)
    {
        string defaultName = Path.GetFileNameWithoutExtension(vmdPath) + "_CSharp.anim";
        string outputPath = EditorUtility.SaveFilePanelInProject(
            L("保存MMD动画", "Save MMD Animation", "MMDアニメーションを保存"),
            defaultName,
            "anim",
            L("选择输出AnimationClip路径。",
                "Choose the output AnimationClip path.",
                "AnimationClipの出力先を選択してください。"));
        if (string.IsNullOrEmpty(outputPath)) return;

        try
        {
            var options = new PmxAnimationClipBakerOptions
            {
                SampleRate = debugSampleRate,
                PositionScale = debugPositionScale,
                CoordinateConversion = debugCoordinateConversion,
                BakeMorphs = debugBakeMorphs,
                CurveReductionError = curveReductionError
            };

            double lastProgressUpdate = double.NegativeInfinity;
            string lastStage = string.Empty;
            Action<float, string> progress = (value, message) =>
            {
                double now = EditorApplication.timeSinceStartup;
                string stage = GetProgressStage(message);
                bool stageChanged = !string.Equals(stage, lastStage, StringComparison.Ordinal);
                if (value < 1f && !stageChanged && now - lastProgressUpdate < 0.1d) return;
                lastProgressUpdate = now;
                lastStage = stage;
                if (EditorUtility.DisplayCancelableProgressBar(
                        L("内置解析方案", "Built-in Parser", "内蔵解析方式"),
                        message,
                        value))
                    throw new OperationCanceledException();
            };

            PmxAnimationBakeReport report;
            if (debugOutput == BuiltInDebugOutput.Humanoid)
            {
                report = PmxHumanoidAnimationClipBaker.BakeToAsset(
                    pmxPath,
                    vmdPath,
                    debugMorphTargetRoot != null ? debugMorphTargetRoot.transform : null,
                    outputPath,
                    options,
                    progress);
            }
            else if (debugOutput == BuiltInDebugOutput.TargetCalibratedHumanoid)
            {
                report = PmxTargetHumanoidAnimationClipBaker.BakeToAsset(
                    pmxPath,
                    vmdPath,
                    debugSkeletonRoot,
                    outputPath,
                    options,
                    progress);
            }
            else if (debugOutput == BuiltInDebugOutput.TargetModelFkFidelity)
            {
                report = PmxTargetFkAnimationClipBaker.BakeToAsset(
                    pmxPath,
                    vmdPath,
                    debugSkeletonRoot,
                    outputPath,
                    options,
                    CreateFkBakeOptions(),
                    progress);
            }
            else
            {
                report = PmxAnimationClipBaker.BakeToAsset(
                    pmxPath,
                    vmdPath,
                    debugSkeletonRoot.transform,
                    outputPath,
                    options,
                    progress);
            }

            Selection.activeObject = report.Clip;
            EditorGUIUtility.PingObject(report.Clip);
            Debug.Log(
                $"[MMD Play Mode Preview] Baked {report.SampleCount} samples as " +
                $"{debugOutput}, {report.BoundBoneCount} mapped bones and " +
                $"{report.BoundMorphCount} morph curves.",
                report.Clip);
            ShowDialog(
                L("内置解析方案", "Built-in Parser", "内蔵解析方式"),
                L("转换完成：\n", "Conversion completed:\n", "変換が完了しました：\n") + outputPath);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            Debug.LogException(exception);
            ShowDialog(
                L("转换失败", "Conversion Failed", "変換に失敗"),
                exception.Message);
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }
    }

    private PmxTargetFkBakeOptions CreateFkBakeOptions()
    {
        return new PmxTargetFkBakeOptions
        {
            RotationErrorDegrees = Mathf.Clamp(fkRotationErrorDegrees, 0f, 0.1f),
            PositionError = Mathf.Clamp(fkPositionError, 0f, 0.001f),
            BakeRootTranslation = true
        };
    }

    private static bool IsAssetExtension(string path, string extension)
    {
        return !string.IsNullOrEmpty(path) &&
               path.StartsWith("Assets/", StringComparison.Ordinal) &&
               path.EndsWith(extension, StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasValidHumanoidAnimator(GameObject model)
    {
        if (model == null) return false;
        Animator animator = model.GetComponent<Animator>() ?? model.GetComponentInChildren<Animator>(true);
        return animator != null && animator.avatar != null &&
               animator.avatar.isValid && animator.avatar.isHuman;
    }

    private static string GetProgressStage(string message)
    {
        if (string.IsNullOrEmpty(message)) return string.Empty;
        int separator = message.IndexOf(' ');
        return separator > 0 ? message.Substring(0, separator) : message;
    }

    private void DrawBottomAction()
    {
        EditorGUILayout.Space(8f);
        if (selectedTab == 0)
        {
            EnsureConvertedMotionResourcesLoaded();
            List<string> sourcePaths = GetVmdAssetPaths(motionVmdAssets);
            if (motionConversionScheme == VmdMotionConversionScheme.DirectExport)
            {
                bool outputStorageValid = TryGetMotionOutputStorageFolder(out _);
                string directModelPath = retargetModel
                    ? AssetDatabase.GetAssetPath(motionModelAsset)
                    : VmdMotionConverter.DefaultModelPath;
                GameObject directTargetModel = pipelineModels.Count > 0
                    ? pipelineModels[Mathf.Clamp(selectedModelIndex, 0, pipelineModels.Count - 1)]
                    : null;
                bool canDirectConvert = sourcePaths.Count > 0 &&
                                        directTargetModel != null &&
                                        outputStorageValid &&
                                        (!IsTargetCalibratedHumanoid ||
                                         (modelParserMethod == VmdMotionConverter.ModelParserMethod.BuiltInManaged &&
                                          HasValidHumanoidAnimator(directTargetModel))) &&
                                        (!retargetModel || VmdMotionConverter.IsModelPath(
                                            directModelPath, modelParserMethod));
                using (new EditorGUI.DisabledScope(!canDirectConvert))
                {
                    if (GUILayout.Button(L("转换", "Convert", "変換"), GUILayout.Height(30f)))
                    {
                        ConvertDirectMotions(
                            sourcePaths, directModelPath, directTargetModel);
                    }
                }
                return;
            }

            var convertedPaths = new HashSet<string>(
                convertedMotionEntries.Select(entry => entry.Path),
                StringComparer.OrdinalIgnoreCase);
            List<string> pendingPaths = sourcePaths
                .Where(path => !convertedPaths.Contains(path))
                .ToList();
            if (pendingPaths.Count > 0 || sourcePaths.Count == 0)
            {
                bool assetStorageValid = TryGetMotionAssetStorageFolder(out _);
                string modelPath = retargetModel
                    ? AssetDatabase.GetAssetPath(motionModelAsset)
                    : VmdMotionConverter.DefaultModelPath;
                bool canConvert = pendingPaths.Count > 0 &&
                                  assetStorageValid &&
                                  (!retargetModel || VmdMotionConverter.IsModelPath(modelPath, modelParserMethod));
                using (new EditorGUI.DisabledScope(!canConvert))
                {
                    if (GUILayout.Button(L("转换", "Convert", "変換"), GUILayout.Height(30f)))
                        ConvertMotions(pendingPaths, modelPath);
                }
                return;
            }

            bool canExport = pipelineModels.Count > 0 && sourcePaths.Count > 0 &&
                             TryGetMotionOutputStorageFolder(out _);
            using (new EditorGUI.DisabledScope(!canExport))
            {
                if (!GUILayout.Button(
                        L("转换", "Convert", "変換"),
                        GUILayout.Height(30f)))
                    return;

                selectedModelIndex = Mathf.Clamp(selectedModelIndex, 0, pipelineModels.Count - 1);
                GameObject model = pipelineModels[selectedModelIndex];
                ExportMotions(model, sourcePaths);
            }
            return;
        }

        if (selectedTab == 1)
        {
            List<string> cameraSourcePaths = GetVmdAssetPaths(cameraVmdAssets);
            using (new EditorGUI.DisabledScope(
                       cameraSourcePaths.Count == 0 || !TryGetCameraOutputStorageFolder(out _)))
            {
                if (GUILayout.Button(L("转换", "Convert", "変換"), GUILayout.Height(30f)))
                    ConvertCameras(cameraSourcePaths);
            }
            return;
        }

        List<string> vpdSourcePaths = GetVpdAssetPaths(vpdAssets);
        string vpdModelPath = vpdRetargetModel
            ? AssetDatabase.GetAssetPath(vpdModelAsset)
            : VmdMotionConverter.DefaultModelPath;
        bool needsTarget = vpdOutputType == MotionOutputType.TargetCalibratedHumanoid;
        bool canConvertVpd = vpdSourcePaths.Count > 0 &&
                             VmdMotionConverter.IsModelPath(
                                 vpdModelPath,
                                 VmdMotionConverter.ModelParserMethod.BuiltInManaged) &&
                             TryGetVpdOutputStorageFolder(out _) &&
                             (!needsTarget || HasValidHumanoidAnimator(vpdTargetAvatar));
        using (new EditorGUI.DisabledScope(!canConvertVpd))
        {
            if (GUILayout.Button(L("转换", "Convert", "変換"),
                    GUILayout.Height(30f)))
                ConvertVpds(vpdSourcePaths, vpdModelPath, vpdTargetAvatar);
        }
    }

    private void ConvertMotions(IReadOnlyList<string> sourcePaths, string modelPath)
    {
        if (!TryGetMotionAssetStorageFolder(out string assetStorageFolder))
            throw new InvalidOperationException("The Asset storage folder must be inside Assets.");
        if (modelParserMethod == VmdMotionConverter.ModelParserMethod.BuiltInManaged)
        {
            ConvertManagedMotionsParallel(sourcePaths, modelPath);
            return;
        }

        int converted = 0;
        int skipped = 0;
        bool cancelled = false;
        var errors = new List<string>();
        VmdMotionConversionData lastData = null;
        PmxHumanoidAnimationClipBaker.BatchSession batchSession = null;
        bool assetEditing = false;
        if (sourcePaths.Count > 1 &&
            modelParserMethod == VmdMotionConverter.ModelParserMethod.BuiltInManaged)
        {
            try
            {
                batchSession = VmdMotionConverter.CreateManagedBatchSession(modelPath, curveReductionError);
                AssetDatabase.StartAssetEditing();
                assetEditing = true;
            }
            catch (Exception exception)
            {
                if (batchSession != null) batchSession.Dispose();
                batchSession = null;
                Debug.LogException(exception);
                errors.Add("Batch initialization failed: " + exception.Message);
            }
        }
        for (int fileIndex = 0; fileIndex < sourcePaths.Count; fileIndex++)
        {
            string sourcePath = sourcePaths[fileIndex];
            try
            {
                EditorUtility.DisplayProgressBar(
                    L("VMD动作转换", "VMD Motion Conversion", "VMDモーション変換"),
                    $"[{fileIndex + 1}/{sourcePaths.Count}] {Path.GetFileName(sourcePath)}",
                    fileIndex / (float)Mathf.Max(1, sourcePaths.Count));
                VmdMotionConversionData data = VmdMotionConverter.Convert(
                    sourcePath,
                    modelPath,
                    modelParserMethod,
                    curveReductionError,
                    ConfirmOverwrite,
                    motionConversionScheme,
                    batchSession,
                    assetEditing,
                    assetStorageFolder,
                    GetExpressionVmdPath(sourcePath));
                if (data == null)
                {
                    skipped++;
                    continue;
                }
                converted++;
                lastData = data;
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

        if (assetEditing)
        {
            try
            {
                AssetDatabase.StopAssetEditing();
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                errors.Add("Batch asset write failed: " + exception.Message);
            }
        }
        if (batchSession != null) batchSession.Dispose();

        convertedMotionResourcesDirty = true;
        EnsureConvertedMotionResourcesLoaded();
        if (lastData != null)
        {
            Selection.activeObject = lastData;
            EditorGUIUtility.PingObject(lastData);
        }
        ShowBatchResult(
            L("VMD动作转换", "VMD Motion Conversion", "VMDモーション変換"),
            converted,
            skipped,
            errors,
            cancelled);
    }

    private void ConvertDirectMotions(
        IReadOnlyList<string> sourcePaths, string modelPath, GameObject targetModel)
    {
        if (!TryGetMotionOutputStorageFolder(out string outputStorageFolder))
            throw new InvalidOperationException("The output storage folder must be inside Assets.");
        if (modelParserMethod == VmdMotionConverter.ModelParserMethod.BuiltInManaged)
        {
            ConvertManagedMotionsParallel(sourcePaths, modelPath, targetModel);
            return;
        }

        int converted = 0;
        int skipped = 0;
        bool cancelled = false;
        var errors = new List<string>();
        AnimationClip lastClip = null;
        GetMappingTargetCache(
            targetModel,
            out List<VmdMotionConverter.BlendShapeTarget> targets,
            out _,
            out _,
            out Dictionary<string, VmdMotionConverter.BlendShapeTarget> sameNameTargets);

        for (int fileIndex = 0; fileIndex < sourcePaths.Count; fileIndex++)
        {
            string sourcePath = sourcePaths[fileIndex];
            AnimationClip sourceClip = null;
            try
            {
                ConvertedMotionEntry entry = convertedMotionEntries.FirstOrDefault(item =>
                    string.Equals(item.Path, sourcePath, StringComparison.OrdinalIgnoreCase));
                if (entry == null)
                    throw new InvalidOperationException("The transient VMD mapping data is missing.");
                sourceClip = VmdMotionConverter.ConvertDirectSourceClip(
                    sourcePath,
                    GetExpressionVmdPath(sourcePath),
                    modelPath,
                    modelParserMethod,
                    curveReductionError,
                    out List<VmdMotionConversionData.MorphRecord> morphs);
                entry.Data.morphs = morphs;
                VmdMotionConversionData.ModelMapping mapping =
                    VmdMotionConverter.GetOrCreateMapping(
                        entry.Data,
                        targetModel,
                        targets,
                        out _,
                        null,
                        sameNameTargets,
                        true);

                double lastProgressUpdate = double.NegativeInfinity;
                Action<float, string> progress = (value, message) =>
                {
                    double now = EditorApplication.timeSinceStartup;
                    if (value < 1f && now - lastProgressUpdate < 0.08d) return;
                    lastProgressUpdate = now;
                    if (EditorUtility.DisplayCancelableProgressBar(
                            L("VMD动作转换", "VMD Motion Conversion", "VMDモーション変換"),
                            $"[{fileIndex + 1}/{sourcePaths.Count}] " +
                            Path.GetFileName(sourcePath) + " — " + message,
                            (fileIndex + Mathf.Clamp01(value)) / Mathf.Max(1f, sourcePaths.Count)))
                        throw new OperationCanceledException();
                };
                AnimationClip clip = VmdMotionConverter.Export(
                    entry.Data,
                    targetModel,
                    mapping,
                    sourcePath,
                    ConfirmOverwrite,
                    progress,
                    sourceClip,
                    outputStorageFolder);
                if (clip == null)
                {
                    skipped++;
                    continue;
                }
                converted++;
                lastClip = clip;
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
                if (sourceClip != null && !AssetDatabase.Contains(sourceClip))
                    UnityEngine.Object.DestroyImmediate(sourceClip);
                EditorUtility.ClearProgressBar();
            }
        }

        ApplyExportedClip(lastClip);
        ShowBatchResult(
            L("VMD动作转换", "VMD Motion Conversion", "VMDモーション変換"),
            converted,
            skipped,
            errors,
            cancelled);
    }

    private void ConvertManagedMotionsParallel(
        IReadOnlyList<string> sourcePaths, string modelPath, GameObject directTargetModel = null)
    {
        bool directExport = directTargetModel != null;
        bool targetCalibratedExport = directExport && IsTargetCalibratedHumanoid;
        string assetStorageFolder = null;
        string outputStorageFolder = null;
        if (directExport)
        {
            if (!TryGetMotionOutputStorageFolder(out outputStorageFolder))
                throw new InvalidOperationException("The output storage folder must be inside Assets.");
        }
        else if (!TryGetMotionAssetStorageFolder(out assetStorageFolder))
        {
            throw new InvalidOperationException("The Asset storage folder must be inside Assets.");
        }
        int converted = 0;
        int skipped = 0;
        bool cancelled = false;
        var errors = new List<string>();
        var pending = new List<ManagedPreparationJob>();
        VmdMotionConversionData lastData = null;
        AnimationClip lastClip = null;

        for (int index = 0; index < sourcePaths.Count; index++)
        {
            string sourcePath = sourcePaths[index];
            try
            {
                if (!directExport && !VmdMotionConverter.ConfirmManagedConversion(
                        sourcePath,
                        motionConversionScheme,
                        ConfirmOverwrite,
                        assetStorageFolder))
                {
                    skipped++;
                    continue;
                }
                pending.Add(new ManagedPreparationJob
                {
                    Ordinal = pending.Count,
                    AssetPath = sourcePath,
                    PhysicalPath = VmdMotionConverter.GetPhysicalVmdPath(sourcePath),
                    ExpressionAssetPath = GetExpressionVmdPath(sourcePath),
                    ExpressionPhysicalPath = GetExpressionPhysicalVmdPath(sourcePath)
                });
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                errors.Add(Path.GetFileName(sourcePath) + ": " + exception.Message);
            }
        }

        if (pending.Count == 0)
        {
            ShowBatchResult(
                L("VMD动作转换", "VMD Motion Conversion", "VMDモーション変換"),
                converted, skipped, errors, false);
            return;
        }

        PmxHumanoidAnimationClipBaker.BatchSession batchSession = null;
        var cancellation = new CancellationTokenSource();
        bool assetEditing = false;
        var active = new List<ManagedPreparationJob>();
        try
        {
            batchSession = VmdMotionConverter.CreateManagedBatchSession(
                modelPath, curveReductionError, !targetCalibratedExport);
            if (!directExport && pending.Count > 1)
            {
                AssetDatabase.StartAssetEditing();
                assetEditing = true;
            }

            int availableWorkers = PmxHumanoidAnimationClipBaker.DefaultParallelism;
            int fileParallelism = Math.Min(pending.Count, Math.Min(4, availableWorkers));
            int frameParallelism = Math.Max(
                1, (availableWorkers + fileParallelism - 1) / Math.Max(1, fileParallelism));
            int nextJob = 0;
            int completedPreparationJobs = 0;

            Action startNext = () =>
            {
                if (nextJob >= pending.Count || cancellation.IsCancellationRequested) return;
                ManagedPreparationJob job = pending[nextJob++];
                job.Work = Task.Run(
                    () => VmdMotionConverter.PrepareManagedConversion(
                        job.PhysicalPath,
                        job.ExpressionPhysicalPath,
                        batchSession,
                        frameParallelism,
                        (completedSamples, totalSamples) =>
                        {
                            job.TotalSamples = totalSamples;
                            job.CompletedSamples = completedSamples;
                        },
                        cancellation.Token),
                    cancellation.Token);
                active.Add(job);
            };

            for (int index = 0; index < fileParallelism; index++) startNext();

            while (active.Count > 0 && !cancelled)
            {
                int readyIndex = active.FindIndex(job => job.Work.IsCompleted);
                if (readyIndex < 0)
                {
                    float partial = completedPreparationJobs;
                    foreach (ManagedPreparationJob job in active)
                    {
                        int total = job.TotalSamples;
                        if (total > 0)
                            partial += Mathf.Clamp01(job.CompletedSamples / (float)total);
                    }
                    ManagedPreparationJob displayJob = active[0];
                    if (EditorUtility.DisplayCancelableProgressBar(
                            L("VMD动作转换", "VMD Motion Conversion", "VMDモーション変換"),
                            $"Preparing [{displayJob.Ordinal + 1}/{pending.Count}] " +
                            Path.GetFileName(displayJob.AssetPath),
                            partial / Mathf.Max(1f, pending.Count)))
                    {
                        cancelled = true;
                        cancellation.Cancel();
                        break;
                    }
                    Thread.Sleep(15);
                    continue;
                }

                ManagedPreparationJob completedJob = active[readyIndex];
                active.RemoveAt(readyIndex);
                VmdMotionConverter.ManagedPreparedConversion prepared;
                try
                {
                    prepared = completedJob.Work.GetAwaiter().GetResult();
                    completedPreparationJobs++;
                }
                catch (OperationCanceledException)
                {
                    cancelled = true;
                    cancellation.Cancel();
                    break;
                }
                catch (Exception exception)
                {
                    completedPreparationJobs++;
                    Debug.LogException(exception);
                    errors.Add(Path.GetFileName(completedJob.AssetPath) + ": " + exception.Message);
                    startNext();
                    continue;
                }

                // Refill the background queue before the main-thread Humanoid pass so both stages overlap.
                startNext();
                try
                {
                    double lastProgressUpdate = double.NegativeInfinity;
                    string lastStage = string.Empty;
                    Action<float, string> progress = (value, message) =>
                    {
                        double now = EditorApplication.timeSinceStartup;
                        string stage = GetProgressStage(message);
                        bool stageChanged = !string.Equals(stage, lastStage, StringComparison.Ordinal);
                        if (value < 1f && !stageChanged && now - lastProgressUpdate < 0.08d) return;
                        lastProgressUpdate = now;
                        lastStage = stage;
                        if (EditorUtility.DisplayCancelableProgressBar(
                                L("VMD动作转换", "VMD Motion Conversion", "VMDモーション変換"),
                                $"[{completedJob.Ordinal + 1}/{pending.Count}] " +
                                Path.GetFileName(completedJob.AssetPath) + " — " + message,
                                (completedJob.Ordinal + Mathf.Clamp01(value)) /
                                Mathf.Max(1f, pending.Count)))
                            throw new OperationCanceledException("VMD motion conversion was cancelled.");
                    };

                    if (directExport)
                    {
                        ConvertedMotionEntry entry = convertedMotionEntries.FirstOrDefault(
                            item => string.Equals(
                                item.Path, completedJob.AssetPath, StringComparison.OrdinalIgnoreCase));
                        if (entry == null)
                            throw new InvalidOperationException("The transient VMD mapping data is missing.");
                        entry.Data.morphs = prepared.Morphs;
                        GetMappingTargetCache(
                            directTargetModel,
                            out List<VmdMotionConverter.BlendShapeTarget> targets,
                            out _,
                            out _,
                            out Dictionary<string, VmdMotionConverter.BlendShapeTarget> sameNameTargets);
                        VmdMotionConversionData.ModelMapping mapping =
                            VmdMotionConverter.GetOrCreateMapping(
                                entry.Data,
                                directTargetModel,
                                targets,
                                out _,
                                null,
                                sameNameTargets,
                                true);
                        AnimationClip sourceClip = null;
                        try
                        {
                            sourceClip = targetCalibratedExport
                                ? VmdMotionConverter.BakeManagedTargetHumanoidSourceClip(
                                    prepared,
                                    batchSession,
                                    directTargetModel,
                                    (value, message) => progress(value * 0.72f, message))
                                : VmdMotionConverter.BakeManagedSourceClip(
                                    prepared,
                                    batchSession,
                                    (value, message) => progress(value * 0.72f, message));
                            AnimationClip clip = VmdMotionConverter.Export(
                                entry.Data,
                                directTargetModel,
                                mapping,
                                completedJob.AssetPath,
                                ConfirmOverwrite,
                                (value, message) => progress(0.72f + value * 0.28f, message),
                                sourceClip,
                                outputStorageFolder);
                            if (clip == null)
                            {
                                skipped++;
                                continue;
                            }
                            converted++;
                            lastClip = clip;
                        }
                        finally
                        {
                            if (sourceClip != null && !AssetDatabase.Contains(sourceClip))
                                UnityEngine.Object.DestroyImmediate(sourceClip);
                        }
                    }
                    else
                    {
                        VmdMotionConversionData data = VmdMotionConverter.CommitManagedConversion(
                            completedJob.AssetPath,
                            prepared,
                            batchSession,
                            motionConversionScheme,
                            assetEditing,
                            progress,
                            assetStorageFolder,
                            completedJob.ExpressionAssetPath);
                        converted++;
                        lastData = data;
                    }
                }
                catch (OperationCanceledException)
                {
                    cancelled = true;
                    cancellation.Cancel();
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception);
                    errors.Add(Path.GetFileName(completedJob.AssetPath) + ": " + exception.Message);
                }
                finally
                {
                    EditorUtility.ClearProgressBar();
                }
            }

            if (cancelled && active.Count > 0)
            {
                while (active.Any(job => !job.Work.IsCompleted))
                {
                    EditorUtility.DisplayProgressBar(
                        L("VMD动作转换", "VMD Motion Conversion", "VMDモーション変換"),
                        "Cancelling background conversion tasks...",
                        1f);
                    Thread.Sleep(15);
                }
                foreach (ManagedPreparationJob job in active)
                {
                    try { job.Work.GetAwaiter().GetResult(); }
                    catch (OperationCanceledException) { }
                    catch (Exception exception)
                    {
                        Debug.LogException(exception);
                        errors.Add(Path.GetFileName(job.AssetPath) + ": " + exception.Message);
                    }
                }
                active.Clear();
            }
        }
        catch (Exception exception)
        {
            Debug.LogException(exception);
            errors.Add("Parallel batch initialization failed: " + exception.Message);
        }
        finally
        {
            cancellation.Cancel();
            foreach (ManagedPreparationJob job in active)
            {
                if (job.Work == null) continue;
                try { job.Work.GetAwaiter().GetResult(); }
                catch (OperationCanceledException) { }
                catch (Exception exception)
                {
                    Debug.LogException(exception);
                    errors.Add(Path.GetFileName(job.AssetPath) + ": " + exception.Message);
                }
            }
            active.Clear();
            cancellation.Dispose();
            EditorUtility.ClearProgressBar();
            if (assetEditing)
            {
                try
                {
                    AssetDatabase.StopAssetEditing();
                    AssetDatabase.SaveAssets();
                    AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception);
                    errors.Add("Batch asset write failed: " + exception.Message);
                }
            }
            if (batchSession != null) batchSession.Dispose();
        }

        if (!directExport)
        {
            convertedMotionResourcesDirty = true;
            EnsureConvertedMotionResourcesLoaded();
        }
        if (lastData != null)
        {
            Selection.activeObject = lastData;
            EditorGUIUtility.PingObject(lastData);
        }
        else if (lastClip != null)
        {
            ApplyExportedClip(lastClip);
        }
        ShowBatchResult(
            L("VMD动作转换", "VMD Motion Conversion", "VMDモーション変換"),
            converted,
            skipped,
            errors,
            cancelled);
    }

    private void ExportMotions(GameObject model, IReadOnlyList<string> sourcePaths)
    {
        if (!TryGetMotionAssetStorageFolder(out string assetStorageFolder))
            throw new InvalidOperationException("The Asset storage folder must be inside Assets.");
        if (!TryGetMotionOutputStorageFolder(out string outputStorageFolder))
            throw new InvalidOperationException("The output storage folder must be inside Assets.");
        int exported = 0;
        int skipped = 0;
        bool cancelled = false;
        var errors = new List<string>();
        AnimationClip lastClip = null;
        GetMappingTargetCache(
            model,
            out List<VmdMotionConverter.BlendShapeTarget> targets,
            out _,
            out _,
            out Dictionary<string, VmdMotionConverter.BlendShapeTarget> sameNameTargets);
        bool unsavedMappings = false;

        for (int fileIndex = 0; fileIndex < sourcePaths.Count; fileIndex++)
        {
            string sourcePath = sourcePaths[fileIndex];
            try
            {
                VmdMotionConversionData data = VmdMotionConverter.LoadData(
                    sourcePath, assetStorageFolder);
                if (data == null)
                    throw new InvalidOperationException("The VMD conversion data is missing.");
                VmdMotionConversionData.ModelMapping modelMapping =
                    VmdMotionConverter.GetOrCreateMapping(
                        data,
                        model,
                        targets,
                        out bool mappingCreated,
                        (value, message) => EditorUtility.DisplayProgressBar(
                            L("自动匹配同名形态键", "Matching Same-name Morphs", "同名モーフを自動照合"),
                            $"[{fileIndex + 1}/{sourcePaths.Count}] {Path.GetFileName(sourcePath)} — {message}",
                            (fileIndex + Mathf.Clamp01(value)) /
                            Mathf.Max(1f, sourcePaths.Count)),
                        sameNameTargets,
                        true);
                unsavedMappings |= mappingCreated;

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
                        L("导出动画", "Export Animation", "アニメーションを書き出す"),
                        $"[{fileIndex + 1}/{sourcePaths.Count}] {Path.GetFileName(sourcePath)} — {message}",
                        overall);
                    if (cancelRequested && value < 0.9f)
                        throw new OperationCanceledException();
                };

                AnimationClip clip = VmdMotionConverter.Export(
                    data,
                    model,
                    modelMapping,
                    sourcePath,
                    ConfirmOverwrite,
                    progress,
                    null,
                    outputStorageFolder);
                if (clip == null)
                {
                    skipped++;
                    continue;
                }
                exported++;
                lastClip = clip;
                // Export saves every dirty asset, including mappings created earlier in this batch.
                unsavedMappings = false;
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

        if (unsavedMappings) AssetDatabase.SaveAssets();

        ApplyExportedClip(lastClip);
        ShowBatchResult(
            L("导出动画", "Export Animation", "アニメーションを書き出す"),
            exported,
            skipped,
            errors,
            cancelled);
    }

    private void ApplyExportedClip(AnimationClip clip)
    {
        if (clip == null) return;
        Selection.activeObject = clip;
        EditorGUIUtility.PingObject(clip);
        if (targetPreview == null) return;
        Undo.RecordObject(targetPreview, "Assign MMD Motion Animation");
        targetPreview.animationClip = clip;
        EditorUtility.SetDirty(targetPreview);
    }

    private bool ConfirmOverwrite(string title, string path, string confirm)
    {
        return EditorUtility.DisplayDialog(
            L("覆盖现有文件", title, "既存ファイルを上書き"),
            L("目标文件已经存在：\n", "The target already exists:\n", "対象ファイルは既に存在します:\n") +
            path,
            L("覆盖", confirm, "上書き"),
            L("取消", "Cancel", "キャンセル"));
    }

    private void EnsureConvertedMotionResourcesLoaded()
    {
        if (!convertedMotionResourcesDirty && convertedMotionEntries != null) return;
        convertedMotionResourcesDirty = false;
        if (convertedMotionEntries == null)
            convertedMotionEntries = new List<ConvertedMotionEntry>();
        else
            convertedMotionEntries.Clear();

        List<string> sourcePaths = GetVmdAssetPaths(motionVmdAssets);
        string assetStorageFolder = null;
        if (motionConversionScheme != VmdMotionConversionScheme.DirectExport &&
            !TryGetMotionAssetStorageFolder(out assetStorageFolder))
            return;
        try
        {
            if (motionConversionScheme == VmdMotionConversionScheme.DirectExport)
            {
                if (transientMotionData == null)
                    transientMotionData = new Dictionary<string, VmdMotionConversionData>(
                        StringComparer.OrdinalIgnoreCase);
                var activePaths = new HashSet<string>(sourcePaths, StringComparer.OrdinalIgnoreCase);
                foreach (string stalePath in transientMotionData.Keys
                             .Where(path => !activePaths.Contains(path)).ToArray())
                {
                    UnityEngine.Object.DestroyImmediate(transientMotionData[stalePath]);
                    transientMotionData.Remove(stalePath);
                }
            }

            for (int index = 0; index < sourcePaths.Count; index++)
            {
                string path = sourcePaths[index];
                string expressionPath = GetExpressionVmdPath(path);
                EditorUtility.DisplayProgressBar(
                    L("加载已转换资源", "Loading Converted Resources", "変換済みリソースを読み込み"),
                    $"[{index + 1}/{sourcePaths.Count}] {Path.GetFileName(path)}",
                    (index + 1f) / Mathf.Max(1, sourcePaths.Count));
                if (motionConversionScheme == VmdMotionConversionScheme.DirectExport)
                {
                    string dependencyHash = GetMotionEntryDependencyHash(path, expressionPath);
                    if (!transientMotionData.TryGetValue(path, out VmdMotionConversionData transient) ||
                        transient == null ||
                        !string.Equals(
                            transient.sourceVmdDependencyHash, dependencyHash, StringComparison.Ordinal))
                    {
                        if (transient != null) UnityEngine.Object.DestroyImmediate(transient);
                        transient = VmdMotionConverter.CreateTransientData(path, expressionPath);
                        transient.sourceVmdDependencyHash = dependencyHash;
                        transientMotionData[path] = transient;
                    }
                    convertedMotionEntries.Add(new ConvertedMotionEntry
                    {
                        Path = path,
                        DisplayName = Path.GetFileNameWithoutExtension(path),
                        Data = transient,
                        IsTransient = true
                    });
                    continue;
                }

                VmdMotionConversionData data = VmdMotionConverter.LoadData(
                    path, assetStorageFolder);
                if (!VmdMotionConverter.IsConvertedFor(
                        data, path, motionConversionScheme, expressionPath)) continue;
                convertedMotionEntries.Add(new ConvertedMotionEntry
                {
                    Path = path,
                    DisplayName = Path.GetFileNameWithoutExtension(path),
                    Data = data,
                    IsTransient = false
                });
            }
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }
    }

    private void DestroyTransientMotionData()
    {
        if (transientMotionData == null) return;
        foreach (VmdMotionConversionData data in transientMotionData.Values)
            if (data != null) UnityEngine.Object.DestroyImmediate(data);
        transientMotionData.Clear();
    }

    private string GetExpressionVmdPath(string motionPath)
    {
        if (string.IsNullOrEmpty(motionPath)) return null;
        EnsureMotionVmdEntryState();
        for (int index = 0; index < motionVmdAssets.Count; index++)
        {
            if (!string.Equals(GetVmdAssetPath(motionVmdAssets[index]), motionPath,
                    StringComparison.OrdinalIgnoreCase))
                continue;
            string expressionPath = GetVmdAssetPath(motionExpressionVmdAssets[index]);
            return string.Equals(expressionPath, motionPath, StringComparison.OrdinalIgnoreCase)
                ? null
                : expressionPath;
        }
        return null;
    }

    private string GetExpressionPhysicalVmdPath(string motionPath)
    {
        string expressionPath = GetExpressionVmdPath(motionPath);
        return string.IsNullOrEmpty(expressionPath)
            ? null
            : VmdMotionConverter.GetPhysicalVmdPath(expressionPath);
    }

    private static string GetMotionEntryDependencyHash(
        string motionPath, string expressionPath)
    {
        string motionHash = AssetDatabase.GetAssetDependencyHash(motionPath).ToString();
        if (string.IsNullOrEmpty(expressionPath)) return motionHash + "|";
        return motionHash + "|" + expressionPath + ":" +
               AssetDatabase.GetAssetDependencyHash(expressionPath);
    }

    private void ScanPipelineModels()
    {
        GameObject selectedModel = selectedModelIndex >= 0 && selectedModelIndex < pipelineModels.Count
            ? pipelineModels[selectedModelIndex]
            : null;
        pipelineModels.Clear();
        pipelineModels.AddRange(VmdMotionConverter.FindPipelineModels());
        selectedModelIndex = selectedModel != null ? pipelineModels.IndexOf(selectedModel) : 0;
        if (selectedModelIndex < 0) selectedModelIndex = 0;
    }

    private static string GetHierarchyPath(GameObject gameObject)
    {
        var names = new List<string>();
        for (Transform current = gameObject.transform; current != null; current = current.parent)
            names.Add(current.name);
        names.Reverse();
        return string.Join("/", names);
    }

    private void NormalizeModelParser()
    {
        IReadOnlyList<VmdMotionConverter.ModelParserMethod> parsers =
            VmdMotionConverter.GetAvailableModelParsers();
        if (!parsers.Contains(modelParserMethod))
            modelParserMethod = VmdMotionConverter.ModelParserMethod.BuiltInManaged;
    }

    private string GetModelParserLabel(VmdMotionConverter.ModelParserMethod parser)
    {
        switch (parser)
        {
            case VmdMotionConverter.ModelParserMethod.MoyuerAvatarToolkit:
                return "Moyuer Avatar Toolkit (PMX2FBX)";
            default:
                return L("内置解析方案", "Built-in Parser", "内蔵解析方式");
        }
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return bytes + " B";
        if (bytes < 1024L * 1024L) return (bytes / 1024d).ToString("0.##") + " KB";
        return (bytes / (1024d * 1024d)).ToString("0.##") + " MB";
    }

    private string L(string chinese, string english, string japanese)
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

    private static string GetPackageVersion()
    {
        UnityEditor.PackageManager.PackageInfo package =
            UnityEditor.PackageManager.PackageInfo.FindForAssetPath(PackageRoot);
        return string.IsNullOrWhiteSpace(package?.version) ? "Unknown" : package.version;
    }

    private void ShowDialog(string title, string message)
    {
        EditorUtility.DisplayDialog(title, message, L("确定", "OK", "OK"));
    }

    private void ShowBatchResult(
        string title,
        int succeeded,
        int skipped,
        IReadOnlyList<string> errors,
        bool cancelled)
    {
        string message = L(
            $"成功：{succeeded}\n跳过：{skipped}\n失败：{errors.Count}",
            $"Succeeded: {succeeded}\nSkipped: {skipped}\nFailed: {errors.Count}",
            $"成功：{succeeded}\nスキップ：{skipped}\n失敗：{errors.Count}");
        if (cancelled)
            message += "\n" + L("批处理已取消。", "Batch operation cancelled.", "バッチ処理をキャンセルしました。");
        if (errors.Count > 0)
        {
            message += "\n\n" + L("错误：", "Errors:", "エラー：") + "\n" +
                       string.Join("\n", errors.Take(6));
            if (errors.Count > 6)
                message += $"\n... (+{errors.Count - 6})";
        }
        ShowDialog(title, message);
    }

    private void EnsureVmdLists()
    {
        if (motionVmdAssets == null) motionVmdAssets = new List<DefaultAsset>();
        if (motionExpressionVmdAssets == null)
            motionExpressionVmdAssets = new List<DefaultAsset>();
        if (motionVmdFileFoldouts == null)
            motionVmdFileFoldouts = new List<bool>();
        EnsureMotionVmdEntryState();
        if (cameraVmdAssets == null) cameraVmdAssets = new List<DefaultAsset>();
        if (vpdAssets == null) vpdAssets = new List<DefaultAsset>();
        if (vpdFileFoldouts == null) vpdFileFoldouts = new List<bool>();
        EnsureVpdEntryState();
        if (convertedMotionEntries == null) convertedMotionEntries = new List<ConvertedMotionEntry>();
        if (mappingFoldouts == null) mappingFoldouts = new Dictionary<string, bool>();
        if (mappingPageIndices == null) mappingPageIndices = new Dictionary<string, int>();
    }

    private void EnsureMotionVmdEntryState()
    {
        if (motionExpressionVmdAssets == null)
            motionExpressionVmdAssets = new List<DefaultAsset>();
        if (motionVmdFileFoldouts == null)
            motionVmdFileFoldouts = new List<bool>();
        while (motionExpressionVmdAssets.Count < motionVmdAssets.Count)
            motionExpressionVmdAssets.Add(motionVmdAssets[motionExpressionVmdAssets.Count]);
        while (motionExpressionVmdAssets.Count > motionVmdAssets.Count)
            motionExpressionVmdAssets.RemoveAt(motionExpressionVmdAssets.Count - 1);
        while (motionVmdFileFoldouts.Count < motionVmdAssets.Count)
            motionVmdFileFoldouts.Add(false);
        while (motionVmdFileFoldouts.Count > motionVmdAssets.Count)
            motionVmdFileFoldouts.RemoveAt(motionVmdFileFoldouts.Count - 1);
    }

    private void EnsureVpdEntryState()
    {
        if (vpdAssets == null) vpdAssets = new List<DefaultAsset>();
        if (vpdFileFoldouts == null) vpdFileFoldouts = new List<bool>();
        while (vpdFileFoldouts.Count < vpdAssets.Count)
            vpdFileFoldouts.Add(false);
        while (vpdFileFoldouts.Count > vpdAssets.Count)
            vpdFileFoldouts.RemoveAt(vpdFileFoldouts.Count - 1);
    }

    private bool GetMappingFoldout(string key, bool defaultExpanded)
    {
        if (mappingFoldouts == null) mappingFoldouts = new Dictionary<string, bool>();
        return mappingFoldouts.TryGetValue(key, out bool expanded) ? expanded : defaultExpanded;
    }

    private void SetMappingFoldout(string key, bool expanded)
    {
        if (mappingFoldouts == null) mappingFoldouts = new Dictionary<string, bool>();
        mappingFoldouts[key] = expanded;
    }

    private int GetMappingPageIndex(string key)
    {
        if (mappingPageIndices == null)
            mappingPageIndices = new Dictionary<string, int>();
        return mappingPageIndices.TryGetValue(key, out int pageIndex)
            ? pageIndex
            : 0;
    }

    private void SetMappingPageIndex(string key, int pageIndex)
    {
        if (mappingPageIndices == null)
            mappingPageIndices = new Dictionary<string, int>();
        mappingPageIndices[key] = Mathf.Max(0, pageIndex);
    }

    private static string GetBlendShapeTargetKey(string name, string path)
    {
        return (name ?? string.Empty) + "\u001f" + (path ?? string.Empty);
    }

    private void GetMappingTargetCache(
        GameObject model,
        out List<VmdMotionConverter.BlendShapeTarget> targets,
        out string[] labels,
        out Dictionary<string, int> indices,
        out Dictionary<string, VmdMotionConverter.BlendShapeTarget> sameNameTargets)
    {
        if (cachedMappingModel != model ||
            cachedMappingTargets == null ||
            cachedMappingTargetLabels == null ||
            cachedMappingTargetIndices == null ||
            cachedSameNameTargets == null ||
            cachedMappingLanguage != Language)
        {
            cachedMappingModel = model;
            cachedMappingLanguage = Language;
            cachedMappingTargets = VmdMotionConverter.GetBlendShapeTargets(model);
            cachedSameNameTargets = VmdMotionConverter.BuildSameNameTargetLookup(cachedMappingTargets);
            cachedMappingTargetLabels = new[] { L("(未映射)", "(Unmapped)", "(未設定)") }
                .Concat(cachedMappingTargets.Select(target => target.DisplayName))
                .ToArray();
            cachedMappingTargetIndices = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int targetIndex = 0; targetIndex < cachedMappingTargets.Count; targetIndex++)
            {
                VmdMotionConverter.BlendShapeTarget target = cachedMappingTargets[targetIndex];
                string key = GetBlendShapeTargetKey(target.Name, target.Path);
                if (!cachedMappingTargetIndices.ContainsKey(key))
                    cachedMappingTargetIndices.Add(key, targetIndex + 1);
            }
        }

        targets = cachedMappingTargets;
        labels = cachedMappingTargetLabels;
        indices = cachedMappingTargetIndices;
        sameNameTargets = cachedSameNameTargets;
    }

    private void InvalidateMappingTargetCache()
    {
        cachedMappingModel = null;
        cachedMappingTargets = null;
        cachedMappingTargetLabels = null;
        cachedMappingTargetIndices = null;
        cachedSameNameTargets = null;
    }

    private static bool AddVmdAsset(List<DefaultAsset> assets, DefaultAsset asset)
    {
        string path = GetVmdAssetPath(asset);
        if (string.IsNullOrEmpty(path)) return false;
        if (assets.Any(existing => string.Equals(
                GetVmdAssetPath(existing),
                path,
                StringComparison.OrdinalIgnoreCase)))
            return false;
        assets.Add(asset);
        return true;
    }

    private bool AddMotionVmdAsset(DefaultAsset asset)
    {
        EnsureMotionVmdEntryState();
        if (!AddVmdAsset(motionVmdAssets, asset)) return false;
        motionExpressionVmdAssets.Add(asset);
        motionVmdFileFoldouts.Add(false);
        return true;
    }

    private static List<string> GetVmdAssetPaths(IEnumerable<DefaultAsset> assets)
    {
        return assets
            .Select(GetVmdAssetPath)
            .Where(path => !string.IsNullOrEmpty(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool AddVpdAsset(List<DefaultAsset> assets, DefaultAsset asset)
    {
        string path = GetVpdAssetPath(asset);
        if (string.IsNullOrEmpty(path)) return false;
        if (assets.Any(existing => string.Equals(
                GetVpdAssetPath(existing), path, StringComparison.OrdinalIgnoreCase)))
            return false;
        assets.Add(asset);
        return true;
    }

    private static List<string> GetVpdAssetPaths(IEnumerable<DefaultAsset> assets)
    {
        return assets
            .Select(GetVpdAssetPath)
            .Where(path => !string.IsNullOrEmpty(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private string GetVmdDisplayName(DefaultAsset asset)
    {
        string path = GetVmdAssetPath(asset);
        return string.IsNullOrEmpty(path)
            ? L("（空）", "(Empty)", "（空）")
            : Path.GetFileNameWithoutExtension(path);
    }

    private string GetVpdDisplayName(DefaultAsset asset)
    {
        string path = GetVpdAssetPath(asset);
        return string.IsNullOrEmpty(path)
            ? L("（空）", "(Empty)", "（空）")
            : Path.GetFileNameWithoutExtension(path);
    }

    private void ConvertVpds(
        IReadOnlyList<string> sourcePaths, string modelPath, GameObject targetAvatar)
    {
        if (!TryGetVpdOutputStorageFolder(out string outputStorageFolder))
            throw new InvalidOperationException("The VPD output storage folder must be inside Assets.");

        int converted = 0;
        int skipped = 0;
        bool cancelled = false;
        var errors = new List<string>();
        AnimationClip lastClip = null;
        PmxHumanoidAnimationClipBaker.BatchSession batchSession = null;
        try
        {
            PmxModel model = PmxModelCache.Load(modelPath);
            var options = new PmxAnimationClipBakerOptions
            {
                SampleRate = VmdFrameRate,
                PositionScale = 0.08f,
                CoordinateConversion = PmxCoordinateConversion.RotateY180,
                BakeMorphs = true,
                CurveReductionError = Mathf.Clamp(curveReductionError, 0f, 0.001f)
            };
            bool targetCalibrated = vpdOutputType == MotionOutputType.TargetCalibratedHumanoid;
            batchSession = new PmxHumanoidAnimationClipBaker.BatchSession(
                model, options, !targetCalibrated);

            for (int fileIndex = 0; fileIndex < sourcePaths.Count; fileIndex++)
            {
                string sourcePath = sourcePaths[fileIndex];
                string outputPath = GetVpdOutputPath(sourcePath, outputStorageFolder);
                try
                {
                    if (AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(outputPath) != null &&
                        !ConfirmOverwrite("Overwrite VPD animation", outputPath, "Overwrite"))
                    {
                        skipped++;
                        continue;
                    }

                    double lastProgressUpdate = double.NegativeInfinity;
                    Action<float, string> progress = (value, message) =>
                    {
                        double now = EditorApplication.timeSinceStartup;
                        if (value < 1f && now - lastProgressUpdate < 0.08d) return;
                        lastProgressUpdate = now;
                        if (EditorUtility.DisplayCancelableProgressBar(
                                L("VPD转换", "VPD Conversion", "VPD変換"),
                                $"[{fileIndex + 1}/{sourcePaths.Count}] " +
                                Path.GetFileName(sourcePath) + " — " + message,
                                (fileIndex + Mathf.Clamp01(value)) / Mathf.Max(1f, sourcePaths.Count)))
                            throw new OperationCanceledException("VPD conversion was cancelled.");
                    };

                    progress(0.02f, "Reading VPD pose");
                    VmdMotion motion = VpdParser.Parse(
                        PmxAnimationClipBaker.ToPhysicalPath(sourcePath));
                    PmxHumanoidAnimationClipBaker.PreparedMotion prepared =
                        batchSession.Prepare(motion, 1);
                    PmxAnimationBakeReport report;
                    if (targetCalibrated)
                    {
                        report = batchSession.BakeTargetHumanoid(
                            prepared, targetAvatar,
                            (value, message) => progress(0.12f + value * 0.78f, message));
                        if (targetAvatar != null)
                            report.BoundMorphCount = PmxAnimationClipBaker.BakeMorphCurves(
                                report.Clip, motion, targetAvatar.transform);
                    }
                    else
                    {
                        report = batchSession.Bake(
                            prepared,
                            targetAvatar != null ? targetAvatar.transform : null,
                            (value, message) => progress(0.12f + value * 0.78f, message));
                    }

                    progress(0.92f, "Writing AnimationClip asset");
                    report.Clip.name = Path.GetFileNameWithoutExtension(outputPath);
                    VmdMotionConverter.EnsureAssetFolderExists(
                        Path.GetDirectoryName(outputPath)?.Replace('\\', '/'));
                    AssetDatabase.DeleteAsset(outputPath);
                    AssetDatabase.CreateAsset(report.Clip, outputPath);
                    AssetDatabase.SaveAssets();
                    AssetDatabase.ImportAsset(
                        outputPath, ImportAssetOptions.ForceSynchronousImport);
                    lastClip = AssetDatabase.LoadAssetAtPath<AnimationClip>(outputPath);
                    converted++;
                    progress(1f, "Conversion complete");
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
        }
        catch (Exception exception)
        {
            Debug.LogException(exception);
            errors.Add("VPD conversion initialization failed: " + exception.Message);
        }
        finally
        {
            if (batchSession != null) batchSession.Dispose();
            EditorUtility.ClearProgressBar();
        }

        ApplyExportedClip(lastClip);
        ShowBatchResult(
            L("VPD转换", "VPD Conversion", "VPD変換"),
            converted, skipped, errors, cancelled);
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

    private static bool IsVmdAsset(DefaultAsset asset)
    {
        return !string.IsNullOrEmpty(GetVmdAssetPath(asset));
    }

    private static string GetVmdAssetPath(DefaultAsset asset)
    {
        if (asset == null) return string.Empty;
        string path = AssetDatabase.GetAssetPath(asset);
        return path.StartsWith("Assets/", StringComparison.Ordinal) &&
               path.EndsWith(".vmd", StringComparison.OrdinalIgnoreCase)
            ? path
            : string.Empty;
    }

    private static bool IsVpdAsset(DefaultAsset asset)
    {
        return !string.IsNullOrEmpty(GetVpdAssetPath(asset));
    }

    private static string GetVpdAssetPath(DefaultAsset asset)
    {
        if (asset == null) return string.Empty;
        string path = AssetDatabase.GetAssetPath(asset);
        return path.StartsWith("Assets/", StringComparison.Ordinal) &&
               path.EndsWith(".vpd", StringComparison.OrdinalIgnoreCase)
            ? path
            : string.Empty;
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

    private static string GetVpdOutputPath(string sourcePath, string outputFolder)
    {
        string fileName = Path.GetFileNameWithoutExtension(sourcePath) + "_VPD.anim";
        if (string.IsNullOrEmpty(outputFolder))
        {
            string sourceFolder = Path.GetDirectoryName(sourcePath)?.Replace('\\', '/');
            return string.IsNullOrEmpty(sourceFolder) ? fileName : sourceFolder + "/" + fileName;
        }
        return outputFolder.TrimEnd('/') + "/" + fileName;
    }

    private bool TryGetMotionAssetStorageFolder(out string folder)
    {
        return TryResolveStorageFolder(
            motionAssetStorageLocation, customMotionAssetStorageFolder, out folder);
    }

    private bool TryGetMotionOutputStorageFolder(out string folder)
    {
        return TryResolveStorageFolder(
            motionOutputStorageLocation, customMotionOutputStorageFolder, out folder);
    }

    private bool TryGetCameraOutputStorageFolder(out string folder)
    {
        return TryResolveStorageFolder(
            cameraOutputStorageLocation, customCameraOutputStorageFolder, out folder);
    }

    private bool TryGetVpdOutputStorageFolder(out string folder)
    {
        return TryResolveStorageFolder(
            vpdOutputStorageLocation, customVpdOutputStorageFolder, out folder);
    }

    private static bool TryResolveStorageFolder(
        MotionStorageLocation location, string customFolder, out string folder)
    {
        switch (location)
        {
            case MotionStorageLocation.FollowSource:
                folder = null;
                return true;
            case MotionStorageLocation.Default:
                folder = DefaultMotionStorageFolder;
                return true;
            case MotionStorageLocation.Custom:
                return TryNormalizeAssetFolder(customFolder, out folder);
            default:
                folder = null;
                return false;
        }
    }

    private static bool TryNormalizeAssetFolder(string folder, out string normalized)
    {
        normalized = (folder ?? string.Empty).Trim().Replace('\\', '/').TrimEnd('/');
        return string.Equals(normalized, "Assets", StringComparison.Ordinal) ||
               normalized.StartsWith("Assets/", StringComparison.Ordinal);
    }

    private static string EscapePopupPath(string path)
    {
        // Popup does not support escaping its '/' submenu separator. Use backslashes for the
        // displayed path only; stored Unity Asset paths continue to use forward slashes.
        return (path ?? string.Empty).Replace("/", "\\");
    }

    private static string AssetFolderToPhysicalPath(string assetFolder)
    {
        if (!TryNormalizeAssetFolder(assetFolder, out string normalized))
            return Application.dataPath;
        if (string.Equals(normalized, "Assets", StringComparison.Ordinal))
            return Application.dataPath;
        return Path.Combine(
            Application.dataPath,
            normalized.Substring("Assets/".Length).Replace('/', Path.DirectorySeparatorChar));
    }

    private static bool TryPhysicalFolderToAssetPath(string physicalFolder, out string assetFolder)
    {
        string assetsRoot = Path.GetFullPath(Application.dataPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string selected = Path.GetFullPath(physicalFolder)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(selected, assetsRoot, StringComparison.OrdinalIgnoreCase))
        {
            assetFolder = "Assets";
            return true;
        }

        string assetsPrefix = assetsRoot + Path.DirectorySeparatorChar;
        if (!selected.StartsWith(assetsPrefix, StringComparison.OrdinalIgnoreCase))
        {
            assetFolder = string.Empty;
            return false;
        }

        assetFolder = "Assets/" + selected.Substring(assetsPrefix.Length).Replace('\\', '/');
        return true;
    }

    private sealed class ConvertedMotionEntry
    {
        public string Path;
        public string DisplayName;
        public VmdMotionConversionData Data;
        public bool IsTransient;
    }

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
                Interpolation = Interpolation,
                FieldOfView = FieldOfView,
                Perspective = Perspective
            };
        }
    }
}
