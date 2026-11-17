# MMD Play Mode Preview

[简体中文](README-ZH-rCN.md)

## Introduction

**MMD Play Mode Preview no longer provides VMD motion solving or conversion; it only previews converted AnimationClips.** For solving and conversion, use these separate repositories:

- Converter: [VMD Converter Window](https://github.com/Vera-Firefly/VMD-Converter-Window)
- Offline conversion: [VMD Converter Core](https://github.com/Vera-Firefly/VMD-Converter-Core)

Preview VRChat avatar motion, camera animation, and audio in Unity Play Mode. Current version: **3.0.0**.

## Dependencies

- **Unity 2022.3** and the **VRChat SDK for Avatars**.
- **Gesture Manager 3.9.8**, declared as a package dependency. The preview panel configures it for the selected avatar.
- Existing motion and camera **AnimationClips** and an optional **AudioClip**. Convert VMD files beforehand with a separate converter; VMD Converter Window and VMD Converter Core are not required by this package.
- Video recording and compression use the bundled **Windows x64 FFmpeg 8.0**, including libx264. No separate installation or PATH configuration is required. Recording currently supports the **Built-in Render Pipeline**. If FFmpeg is missing or the editor platform is unsupported, recording and compression are disabled; other preview features remain available.
- **AudioLink is optional**. Without it, AudioLink Integration is disabled and other features remain available. To enable integration for a session, the hierarchy must contain an active, enabled AudioLink component **before entering Play Mode**. AudioLink-aware materials must have their effects enabled; for lilToon, disable **AudioLink As Local**.

## Uses and Features

- Play, pause, end, reset, seek, and loop existing AnimationClips with synchronized audio and camera animation.
- Adjust playback speed from **0.1× to 3×**, with **0.1×, 0.2×, 0.5×, 1×, and 2×** presets. Audio pitch changes with playback speed.
- Use camera animation or free camera controls with a video, image, or solid-color background. Video segments follow preview time.
- Record **60 fps MP4** at the current camera aspect ratio and native image size, up to 1080p, with synchronized audio. Pauses are excluded from the video.
- Compress recordings in the background while keeping the original file. Open the last output location from the panel.
- Optionally feed preview audio to AudioLink for compatible material effects, including during recording.
- Use the panel in Simplified Chinese, English, or Japanese.

### Usage

1. Install the package and its required dependencies. Add the included **MMD Play Mode Preview** prefab, or use **VeraFirefly > MMD Play Mode Preview > Create MMD Play Mode Preview**.
2. Select an active scene avatar with a VRC Avatar Descriptor. Assign motion, audio, and camera clips as needed, then select the clip that determines playback duration. Configure the background if desired.
3. Click **Enter Play Mode Preview** in the component panel. Use this button to start the preview session; the panel remains disabled in Play sessions started elsewhere.
4. Adjust Playback Settings and use the playback controls to play, pause, reset, seek, or end. While recording, speed is locked, including during pauses; loop, reset, and seeking are disabled.

For free camera control, hold the right mouse button to rotate; use **W/A/S/D** to move, **Q/E** for down/up, and the wheel to adjust movement speed. Hold the middle mouse button to rotate, and use the wheel to adjust distance. Enable **Free Camera Control** to switch away from camera animation.

For a video background, import a video into Unity, enable **Preview Background**, and assign its **VideoClip**. Set **Timeline Start** and **Clip In / Out** in seconds; Clip Out 0 uses the video end. For example, Timeline Start 5, Clip In 10, and Clip Out 20 shows video seconds 10–20 at preview seconds 5–15. Outside this interval, the image or color appears. The video follows pause, seek, loop, and speed changes; recording waits for the corresponding video frame. Video audio is muted: assign music in the MMD audio field. Background settings are locked during recording. Use a format supported by Unity on your platform, such as H.264 MP4.

To record, end playback first, enable **Record Video**, and choose an output folder with the folder icon. **Play** starts recording, **Pause** suspends it, and **Play** resumes the same file. **End** or playback completion saves the MP4. Enable **Compress After Recording**, or click **Compress Last Output (FFmpeg)** afterward. **Last Output** is read-only; its folder icon opens the output location.

Recording advances frame by frame and may run slower than real time in complex scenes. Audio monitoring is muted during capture; the output includes the synchronized clip audio. Set the AudioClip Load Type to **Decompress On Load** and wait for loading to finish. The recorded audio excludes microphones, other scene sources, and AudioMixer effects. Compression creates a separate file and may offer little reduction for already compressed video.

During recording, scene time and avatar Animator/FX controllers advance once per output frame at the recording speed. They freeze while waiting for background video decoding and during recording pauses. Time and Animator update settings are restored afterward. Custom scripts, shaders, or animation systems that use independent clocks are not guaranteed to synchronize.

To use AudioLink, place and activate its component before entering Play Mode, then enable **AudioLink Integration** in Playback Settings. The switch defaults to off: the detected AudioLink object is inactive while off, and activates with the preview audio source when enabled. Leaving preview restores its original activation and audio settings. AudioLink added or activated after Play begins is not eligible until the next session. Keep one active AudioLink analyzer in the scene; if several exist, the preview uses the first detected component.
