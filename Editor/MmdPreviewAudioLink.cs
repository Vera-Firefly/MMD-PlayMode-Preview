using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;

/// <summary>Optional editor bridge; no AudioLink assembly is required by this package.</summary>
[InitializeOnLoad]
internal static class MmdPreviewAudioLink
{
    private const string AudioLinkTypeName = "AudioLink.AudioLink";
    private static readonly Type AudioLinkType = FindAudioLinkType();
    internal static bool IsAvailable => AudioLinkType != null;
    private static readonly Dictionary<MmdPlayModePreview, Binding> Bindings = new Dictionary<MmdPlayModePreview, Binding>();

    static MmdPreviewAudioLink()
    {
        MmdPlayModePreview.EditorUpdateAudioLink = Update;
        MmdPlayModePreview.EditorReleaseAudioLink = Release;
        AssemblyReloadEvents.beforeAssemblyReload += ReleaseAll;
        EditorApplication.quitting += ReleaseAll;
        EditorApplication.playModeStateChanged += state =>
        {
            if (state == PlayModeStateChange.ExitingPlayMode) ReleaseAll();
        };
    }

    private static Type FindAudioLinkType()
    {
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type type = assembly.GetType(AudioLinkTypeName, false);
            if (type != null && typeof(Behaviour).IsAssignableFrom(type)) return type;
        }
        return null;
    }

    internal static Behaviour FindActiveSceneAudioLink()
    {
        if (!IsAvailable) return null;
        foreach (UnityEngine.Object candidate in Resources.FindObjectsOfTypeAll(AudioLinkType))
        {
            var link = candidate as Behaviour;
            if (link != null && link.isActiveAndEnabled && link.gameObject.scene.IsValid() &&
                link.gameObject.scene.isLoaded && !EditorUtility.IsPersistent(link)) return link;
        }
        return null;
    }

    private static void Update(MmdPlayModePreview preview)
    {
        Behaviour link = preview.audioLinkSessionTarget;
        if (!IsAvailable || link == null || preview.PreviewAudioSource == null)
        {
            preview.enableAudioLink = false;
            Release(preview);
            return;
        }
        try
        {
            if (!Bindings.TryGetValue(preview, out Binding binding))
            {
                binding = new Binding(link);
                Bindings.Add(preview, binding);
            }
            binding.Update(preview);
        }
        catch (Exception exception)
        {
            Release(preview);
            preview.enableAudioLink = false;
            Debug.LogWarning("MMD Preview AudioLink: " + exception.Message, preview);
        }
    }

    private static void Release(MmdPlayModePreview preview)
    {
        if (!Bindings.TryGetValue(preview, out Binding binding)) return;
        Bindings.Remove(preview);
        binding.Restore();
    }

    private static void ReleaseAll()
    {
        foreach (MmdPlayModePreview preview in new List<MmdPlayModePreview>(Bindings.Keys)) Release(preview);
    }

    private sealed class Binding
    {
        private const int BlockSize = 1023;
        private const int SampleCount = BlockSize * 4;
        private readonly Behaviour link;
        private readonly FieldInfo sourceField;
        private readonly FieldInfo rightSourceField;
        private readonly FieldInfo autoMediaField;
        private readonly FieldInfo materialField;
        private readonly AudioSource previousSource;
        private readonly object previousRightSource;
        private readonly object previousAutoMedia;
        private readonly bool previousActive;
        private readonly float[][] left = new float[4][];
        private readonly float[][] right = new float[4][];
        private readonly int[] leftIds = new int[4];
        private readonly int[] rightIds = new int[4];
        private float[] pcm;
        private bool previousRecording;
        private float previousTime = -1f;

        internal Binding(Behaviour link)
        {
            this.link = link;
            previousActive = link.gameObject.activeSelf;
            Type type = link.GetType();
            sourceField = type.GetField("audioSource");
            rightSourceField = type.GetField("optionalRightAudioSource");
            autoMediaField = type.GetField("autoSetMediaState");
            materialField = type.GetField("audioMaterial");
            if (sourceField == null || materialField == null) throw new NotSupportedException("Unsupported AudioLink component.");
            previousSource = sourceField.GetValue(link) as AudioSource;
            previousRightSource = rightSourceField?.GetValue(link);
            previousAutoMedia = autoMediaField?.GetValue(link);
            for (int i = 0; i < 4; i++)
            {
                left[i] = new float[BlockSize];
                right[i] = new float[BlockSize];
                leftIds[i] = Shader.PropertyToID("_Samples" + i + "L");
                rightIds[i] = Shader.PropertyToID("_Samples" + i + "R");
            }
        }

        internal void Update(MmdPlayModePreview preview)
        {
            if (!preview.enableAudioLink)
            {
                RestoreAudio();
                previousRecording = false;
                previousTime = -1f;
                if (link.gameObject.activeSelf) link.gameObject.SetActive(false);
                return;
            }
            sourceField.SetValue(link, preview.PreviewAudioSource);
            rightSourceField?.SetValue(link, null);
            if (!link.gameObject.activeSelf) link.gameObject.SetActive(true);
            if (previousAutoMedia != null) autoMediaField.SetValue(link, !preview.IsRecording && (bool)previousAutoMedia);
            if (!preview.IsRecording)
            {
                previousRecording = false;
                previousTime = -1f;
                return;
            }

            // AudioLink's regular Update already schedules its CRT update. Supply
            // PCM afterwards, before rendering, because offline capture stops audio.
            Material material = materialField.GetValue(link) as Material;
            if (material == null) return;
            if (previousRecording && previousTime == preview.PlaybackTime && !preview.IsPlaybackEnded)
            {
                material.SetFloat("_MediaPlaying", preview.IsPlaybackPlaying ? 1f : 2f);
                return;
            }
            previousRecording = true;
            previousTime = preview.PlaybackTime;
            AudioClip clip = preview.audioClip;
            bool hasSignal = clip != null && clip.samples > 0 && clip.channels > 0 && !preview.IsPlaybackEnded;
            int first = 0;
            int neededFrames = 0;
            double step = 0;
            if (hasSignal)
            {
                int outputRate = Mathf.Max(1, AudioSettings.outputSampleRate);
                step = (double)clip.frequency * preview.PlaybackSpeed / outputRate;
                first = (int)Math.Floor((double)preview.PlaybackTime * clip.frequency - SampleCount * step);
                neededFrames = (int)Math.Ceiling(SampleCount * step) + 2;
                int length = checked(neededFrames * clip.channels);
                if (pcm == null || pcm.Length != length) pcm = new float[length];
                int offset = ((first % clip.samples) + clip.samples) % clip.samples;
                if (!clip.GetData(pcm, offset)) throw new InvalidOperationException(
                    "录制 AudioLink 需要已加载的 Decompress On Load 音频。Offline AudioLink requires decompressed audio.");
            }
            for (int block = 0; block < 4; block++)
            {
                for (int sample = 0; sample < BlockSize; sample++)
                {
                    double position = (block * BlockSize + sample) * step;
                    int index = (int)position;
                    float fraction = (float)(position - index);
                    bool valid = hasSignal && first + index >= 0 && first + index + 1 < clip.samples;
                    left[block][sample] = valid ? Mathf.Lerp(pcm[index * clip.channels], pcm[(index + 1) * clip.channels], fraction) : 0f;
                    int channel = hasSignal ? Math.Min(1, clip.channels - 1) : 0;
                    right[block][sample] = valid ? Mathf.Lerp(pcm[index * clip.channels + channel], pcm[(index + 1) * clip.channels + channel], fraction) : 0f;
                }
                material.SetFloatArray(leftIds[block], left[block]);
                material.SetFloatArray(rightIds[block], right[block]);
            }
            material.SetFloat("_SourceVolume", 1f);
            material.SetFloat("_SourceSpatialBlend", 0f);
            material.SetFloat("_MediaVolume", 1f);
            material.SetFloat("_MediaTime", clip != null && clip.length > 0f ? preview.PlaybackTime / clip.length : 0f);
            material.SetFloat("_MediaPlaying", preview.IsPlaybackEnded ? 3f : preview.IsPlaybackPlaying ? 1f : 2f);
            material.SetFloat("_MediaLoop", 0f);
        }

        internal void Restore()
        {
            if (link == null) return;
            RestoreAudio();
            if (link.gameObject.activeSelf != previousActive) link.gameObject.SetActive(previousActive);
        }

        private void RestoreAudio()
        {
            sourceField.SetValue(link, previousSource);
            rightSourceField?.SetValue(link, previousRightSource);
            if (previousAutoMedia != null) autoMediaField.SetValue(link, previousAutoMedia);
        }
    }
}
