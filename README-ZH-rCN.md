# MMD Play Mode Preview

[English](README.md)

MMD Play Mode Preview 是一个 Unity Editor 工具包，用于在 VRChat Avatar 上预览 MMD 动作，并将 MMD 动作、镜头和姿势数据转换为 Unity `AnimationClip` 资源。

本包提供完全托管的 C# PMX/VMD/VPD 处理流程，包括 MMD 骨骼计算与 IK 解算、Humanoid 动画烘焙、目标 Avatar 校准、Morph 转换、镜头转换和 PMX 解析缓存。转换过程直接在 Unity Editor 内完成，无需启动外部转换程序。

## 环境要求

- Unity 2022.3
- VRChat SDK - Avatars
- Gesture Manager 3.9.8（已声明为包依赖）

## 主要功能

### Play Mode 预览

- 同步预览 Avatar 动作、面部 Morph、音频和镜头动画。
- 可选择动作、音频或镜头作为播放时长基准。
- 提供循环播放和 Play Mode 播放控制。
- 支持图片背景与纯色背景。
- 支持 MMD 镜头动画及自由视角控制。
- 集成 Gesture Manager。
- Editor UI 支持简体中文、English 和日本語。

### VMD 动作转换

- 批量将多个 `.vmd` 文件转换为 Unity `.anim` 资源。
- 支持动作数据与可选表情数据合成。
- 支持标准 Humanoid 与目标校准 Humanoid 输出。
- 计算 PMX 骨骼继承、MMD IK、腿部/脚尖 IK 和 Morph 轨道。
- 可使用内置标准 MMD 参考 PMX，也可指定其他 PMX 模型。
- 提供曲线精简及 PMX 解析缓存，提升重复转换效率。

### VMD 镜头转换

- 将 MMD 镜头轨道转换为 Unity 镜头动画。
- 保留镜头位置、旋转、距离、视野角和透视状态。

### VPD 姿势转换

- 将 `.vpd` 姿势转换为 Unity `.anim` 资源。
- 支持标准 Humanoid 与目标校准 Humanoid 输出。
- 与 VMD 动作转换共用 PMX 重定向和目标校准流程。

### 包内容自检

- 使用完整包文件清单验证安装内容。
- 在 Unity Editor 中立即报告额外文件或缺失文件。
- 可将额外文件安全移动至 `Assets/MMDPlayModePreview_Quarantine` 下的可恢复隔离目录。

## 快速开始

1. 打开 `VeraFirefly > MMD Play Mode Preview > Create MMD Play Mode Preview`。
2. 选中创建的 GameObject，在 Inspector 中选择场景内激活的 VRChat Avatar。
3. 从 Inspector 打开转换器，或使用 `VeraFirefly > MMD Play Mode Preview > VMD Converter`。
4. 添加 VMD/VPD 文件，按需选择输出类型和目标 Avatar，然后点击“转换”。
5. 将生成的动作、镜头动画以及可选音频和背景分配到预览组件。
6. 点击“进入 Play Mode 预览”。

自由视角操作：按住鼠标右键旋转视角，使用 `W/A/S/D` 移动，使用 `Q/E` 上下移动，滚轮调整移动速度；按住鼠标中键可围绕 Avatar 旋转，滚轮调整距离。

## 安装

根据仓库的发布方式，通过 VRChat Creator Companion 或 Unity Package Manager 安装。若从 Git 仓库安装，可使用 Unity Package Manager 的 **Add package from git URL**，并填写发布页提供的仓库 URL 或包子目录 URL。

请勿将无关文件合并到包目录。包自检会将清单外的文件识别为额外内容。

## 使用说明

- 托管转换器使用 PMX 参考模型；内置 C# 解析器不支持 PMD。
- “目标校准 Humanoid”会额外通过 `HumanPoseHandler` 针对目标 Avatar 进行校准，因此通常比标准 Humanoid 输出更慢，但能更好地适配目标骨架。
- 转换精度会受到源动作、PMX 骨架、Humanoid 映射、初始姿势和目标 Avatar 配置影响。
- 请确保使用的 MMD 模型、动作、姿势、音频及其他资源允许用于你的目标用途。

## 内置参考骨架

包内包含无角色网格的标准 MMD Humanoid 参考骨架：`Editor/MotionConverter/Model/_model.pmx`。该文件仅保留转换流程所需的骨骼、IK 与 Morph 定义，不包含角色网格或贴图。

生成的参考 PMX 及其生成器采用 Apache License 2.0。对应许可证与声明位于 `Editor/MotionConverter/Model/`。

## 开源协议

MMD Play Mode Preview 采用 [Apache License 2.0](LICENSE) 开源协议。

该协议适用于工具包本身。用户导入或转换的 MMD 模型、动作、姿势、音频、图片及其他内容仍遵循其各自的原始授权，本项目不会改变这些资源的许可证。

## 作者

VeraFirefly
