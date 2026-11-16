# Bundled FFmpeg

Windows x86-64 static executable: `Windows/x86_64/ffmpeg.exe`.
Version: FFmpeg 8.0 essentials, built by Gyan Doshi / gyan.dev.
Includes libx264 and the native AAC encoder. No external DLLs or PATH setup required.
Only the FFmpeg executable is included; ffplay and ffprobe are omitted.

Upstream distribution: https://www.gyan.dev/ffmpeg/builds/
Upstream FFmpeg source revision: https://github.com/FFmpeg/FFmpeg/commit/140fd653ae
Build configuration and upstream notices: `Windows/x86_64/UPSTREAM-README.txt`.
License: GNU GPL v3, reproduced in `Windows/x86_64/LICENSE.txt`.
The bundled FFmpeg executable retains its own license separately from the preview package.

SHA256 of ffmpeg.exe:
`1FCB0CB525085CC5BE3AD4BE16799D60786B65CC36951622A018D00F78C0FB5D`

The `~` suffix keeps this third-party tool outside Unity's asset/plugin importer.
It is used only by the editor and is not included in avatar builds.
Keep this directory when distributing or packaging the UPM package.
Other editor platforms, or installations missing this executable, disable recording
and compression without disabling motion, camera, audio, speed, or AudioLink preview.
