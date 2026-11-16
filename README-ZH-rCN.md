# MMD Play Mode Preview

[English](README.md)

在 Unity Play Mode 中预览 VRChat 虚拟形象的动作、镜头动画和音频。当前版本：**3.0.0-pre**。

## 功能

- 播放、暂停、结束、重置、拖动进度及循环播放现有 AnimationClip，同步播放音频和镜头动画。
- 提供 **0.1×–3×** 倍速滑条，以及 **0.1×、0.2×、0.5×、1×、2×** 快捷按钮；音频音调随倍速变化。
- 支持镜头动画和自由视角控制，可设置图片背景或纯色背景。
- 按当前摄像机宽高比和原生画面尺寸录制 **60 fps MP4**，最高 1080p，保留同步音轨，暂停时间不计入视频。
- 后台压缩视频并保留原文件，可从面板打开上次输出所在位置。
- 可选接入 AudioLink，驱动兼容材质的音频效果，录制时同样可用。
- 面板支持简体中文、英文和日文。

## 依赖

- **Unity 2022.3** 和 **VRChat Avatars SDK**。
- **Gesture Manager 3.9.8**，已在包依赖中声明；预览面板会为选中的形象配置 Gesture Manager。
- 已有的动作、镜头 **AnimationClip** 及可选的 **AudioClip**。VMD 文件需先通过独立转换工具转换；本包不依赖 VMD Converter Window 或 VMD Converter Core。
- 录制和压缩使用内置 **Windows x64 FFmpeg 8.0**，包含 libx264，无需另外安装或配置 PATH。目前录制支持 **Built-in Render Pipeline**。FFmpeg 缺失或编辑器平台不支持时，仅禁用录制和压缩，其它预览功能仍可使用。
- **AudioLink 为可选依赖**。未安装时禁用“AudioLink接入”，不影响其它功能。需要在**进入 Play 前**，场景层级中存在激活且启用的 AudioLink 组件，本次会话才可接入。材质需自行启用 AudioLink 效果；lilToon 材质还需关闭 **AudioLink As Local**。

预览包使用 [Apache-2.0 许可证](LICENSE)。内置 FFmpeg 使用独立的 [GPL v3 许可证](Editor/Tools/FFmpeg~/Windows/x86_64/LICENSE.txt)，并附带[上游说明](Editor/Tools/FFmpeg~/README.md)。分发包时请保留内置工具目录及许可说明。

## 使用方式

1. 安装本包和必要依赖。将附带的 **MMD Play Mode Preview** 预制体放入场景，或通过 **VeraFirefly > MMD Play Mode Preview > Create MMD Play Mode Preview** 创建组件。
2. 选择场景中激活且具有 VRC Avatar Descriptor 的形象，按需设置动作、音频、镜头动画，并选择用于决定播放时长的资源；需要时配置预览背景。
3. 点击组件面板中的**进入 Play Mode 预览**。请通过此按钮启动预览会话；从其它入口进入 Play 时，该面板的预览功能会禁用。
4. 在“播放设置”调整倍速，通过“播放控制”播放、暂停、重置、拖动进度或结束。录制期间（包括暂停）锁定倍速，并禁用循环、重置和进度拖动。

自由视角操作：按住鼠标右键拖动旋转，**W/A/S/D** 移动，**Q/E** 向下／向上，滚轮调整移速；按住中键旋转，滚轮调整距离。使用镜头动画时，可勾选**自由视角控制**切换为手动控制。

录制视频前，先结束播放，再勾选**视频录制**，通过文件夹图标选择输出位置。点击**播放**开始录制，**暂停**暂存进度，再次**播放**继续同一个文件；点击**结束**或播放完成后保存 MP4。可提前勾选**录制后压缩**，也可录制后点击**压缩上次输出 (FFmpeg)**。**上次输出**为只读输入框，右侧文件夹图标可打开所在位置。

录制采用逐帧方式，复杂场景可能慢于实时。录制期间试听静音，成片保留同步的音频片段。AudioClip 的 Load Type 须设为 **Decompress On Load** 并完成加载；音轨不包含麦克风、其它场景音源或 AudioMixer 效果。压缩会另存文件，已高度压缩的视频可能没有明显体积收益。

接入 AudioLink 时，在进入 Play 前放置并激活 AudioLink 组件，进入后在“播放设置”勾选 **AudioLink接入**。该开关默认关闭：关闭时停用检测到的 AudioLink 对象，勾选后激活并接入预览音源；退出预览后恢复原激活状态和音源设置。进入 Play 后才新建或激活的 AudioLink 需等下次会话才能接入。建议场景只保留一个激活的 AudioLink 分析器；存在多个时使用检测到的第一个。
