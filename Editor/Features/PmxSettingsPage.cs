using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using VRChat.MmdPreview.Pmx2Fbx.Editor;

[Serializable]
internal sealed class PmxSettingsPage : ConverterFeaturePage
{
    [SerializeField] private Vector2 pageScroll;
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

    internal void OnEnable(ConverterWindowContext context)
    {
        Attach(context);
        NormalizeModelParser();
    }

    internal void Draw(ConverterWindowContext context)
    {
        Attach(context);
        pageScroll = EditorGUILayout.BeginScrollView(pageScroll);
        try
        {
            DrawPmxSettings();
        }
        finally
        {
            EditorGUILayout.EndScrollView();
        }
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
        targetUpperLimbInverseSolve = EditorGUILayout.Toggle(
            new GUIContent(
                L("目标上肢反解", "Target Upper-limb Inverse Solve", "対象上肢の逆解析"),
                L("仅影响目标校准 Humanoid。启用后，上肢执行目标 FK 重建并由 HumanPose 反解；默认关闭。",
                    "Only affects target-calibrated Humanoid. Reconstructs target upper-limb FK and extracts it through HumanPose; disabled by default.",
                    "対象校正 Humanoid のみに適用します。対象上肢 FK を再構築して HumanPose から逆解析します。既定では無効です。")),
            targetUpperLimbInverseSolve);
        if (targetUpperLimbInverseSolve)
        {
            EditorGUILayout.HelpBox(
                L("调试模式：特殊 Avatar 可能受益，但极端手臂/前臂扭转可能发生分支翻转或跳变。",
                    "Debug mode: unusual Avatars may benefit, but extreme arm/forearm twist can flip branches or jump.",
                    "デバッグモード：特殊な Avatar では改善する場合がありますが、極端な腕・前腕のツイストでは分岐反転やジャンプが発生する可能性があります。"),
                MessageType.Warning);
        }
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
                CurveReductionError = curveReductionError,
                TargetUpperLimbInverseSolve = targetUpperLimbInverseSolve
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
}
