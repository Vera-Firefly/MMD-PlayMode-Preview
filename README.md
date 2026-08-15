# MMD Play Mode Preview

[简体中文](README-ZH-rCN.md)

MMD Play Mode Preview is a Unity Editor package for previewing MMD motion on VRChat avatars and converting MMD motion, camera, and pose data into Unity `AnimationClip` assets.

The package provides a managed C# PMX/VMD/VPD pipeline, MMD bone evaluation and IK solving, Humanoid animation baking, target-avatar calibration, morph conversion, camera conversion, and PMX parse caching. Conversion runs inside the Unity Editor without launching an external converter.

## Requirements

- Unity 2022.3
- VRChat SDK - Avatars
- Gesture Manager 3.9.8 (declared as package dependency)

## Features

### Play Mode preview

- Preview an avatar with motion, facial morphs, audio, and camera animation.
- Select motion, audio, or camera as the playback-duration source.
- Looping and playback controls in Play Mode.
- Optional image or color background.
- Animated MMD camera or free-camera controls.
- Embedded Gesture Manager integration.
- Simplified Chinese, English, and Japanese Editor UI.

### VMD motion conversion

- Batch conversion of multiple `.vmd` files to Unity `.anim` assets.
- Combines motion data and optional facial-expression data.
- Outputs standard Humanoid or target-calibrated Humanoid curves.
- Evaluates PMX bone inheritance, MMD IK, leg/toe IK, and morph tracks.
- Supports a built-in standard MMD reference PMX or a user-selected PMX model.
- Curve reduction and PMX parse caching for repeated conversions.

### VMD camera conversion

- Converts MMD camera tracks into Unity camera animation clips.
- Preserves camera position, rotation, distance, field of view, and perspective state.

### VPD pose conversion

- Converts `.vpd` pose files into Unity `.anim` assets.
- Supports standard Humanoid and target-calibrated Humanoid output.
- Uses the same PMX retargeting and calibration path as VMD motion conversion.

### Package self-check

- Verifies package contents against a complete package manifest.
- Reports unexpected or missing files immediately in the Unity Editor.
- Unexpected files can be moved safely to a recoverable quarantine under `Assets/MMDPlayModePreview_Quarantine`.

## Quick start

1. Open `VeraFirefly > MMD Play Mode Preview > Create MMD Play Mode Preview`.
2. Select the created GameObject and choose an active VRChat avatar in its Inspector.
3. Open the converter from the Inspector, or use `VeraFirefly > MMD Play Mode Preview > VMD Converter`.
4. Add VMD/VPD files, select the output type and target Avatar when needed, then click **Convert**.
5. Assign the generated motion and camera clips, plus optional audio and background.
6. Click **Enter Play Mode Preview**.

Free-camera controls: hold the right mouse button to look around, use `W/A/S/D` to move, `Q/E` to move vertically, and the mouse wheel to adjust movement speed. Hold the middle mouse button to rotate around the avatar and use the wheel to adjust distance.

## Installation

Install the package through the VRChat Creator Companion or Unity Package Manager according to the distribution method of the repository. When installing from a Git repository, use Unity Package Manager's **Add package from git URL** option and the repository URL or package subdirectory URL provided by the release.

Do not merge unrelated files into the package directory. The package self-check treats files not present in its manifest as unexpected content.

## Notes

- The managed converter accepts PMX reference models. PMD is not supported by the built-in C# parser.
- Target-calibrated Humanoid output performs an additional `HumanPoseHandler` calibration against the selected target Avatar. It is normally slower than standard Humanoid output but better matches the target rig.
- Conversion accuracy depends on the source motion, PMX skeleton, Humanoid mapping, rest pose, and target Avatar configuration.
- Use only MMD models, motions, poses, audio, and other assets whose licenses permit your intended use.

## Bundled reference rig

The package includes a mesh-free standard MMD Humanoid reference rig at `Editor/MotionConverter/Model/_model.pmx`. It contains the bone, IK, and morph definitions required by the conversion pipeline and no character mesh or textures.

The generated reference PMX and its generator are licensed under the Apache License 2.0. Their license and notices are located in `Editor/MotionConverter/Model/`.

## License

MMD Play Mode Preview is licensed under the [Apache License 2.0](LICENSE).

This license applies to the package itself. MMD models, motions, poses, audio, images, and other content imported or converted by users retain their respective licenses and are not relicensed by this project.

## Author

VeraFirefly
