using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace VRChat.MmdPreview.Pmx2Fbx.Editor
{
    public static class PmxHumanoidAnimationClipBaker
    {
        public static PmxAnimationBakeReport Bake(PmxModel model, VmdMotion motion,
            Transform morphTargetRoot = null, PmxAnimationClipBakerOptions options = null,
            Action<float, string> progress = null)
        {
            if (model == null) throw new ArgumentNullException(nameof(model));
            if (motion == null) throw new ArgumentNullException(nameof(motion));
            options = options ?? new PmxAnimationClipBakerOptions();
            using (var session = new BatchSession(model, options))
                return session.Bake(motion, morphTargetRoot, progress);
        }

        internal sealed class BatchSession : IDisposable
        {
            private readonly PmxModel _model;
            private readonly PmxAnimationClipBakerOptions _options;
            private PmxHumanoidSourceRig _rig;
            private HumanPoseHandler _poseHandler;
            private PmxTargetHumanoidSourceRig _targetHumanoidRig;
            private int _targetHumanoidRootId;
            private bool _disposed;

            internal BatchSession(PmxModel model, PmxAnimationClipBakerOptions options,
                bool createHumanoidRig = true)
            {
                _model = model ?? throw new ArgumentNullException(nameof(model));
                _options = options ?? new PmxAnimationClipBakerOptions();
                if (_options.SampleRate <= 0f || _options.SampleRate > 240f)
                    throw new ArgumentOutOfRangeException(nameof(options.SampleRate));
                if (!createHumanoidRig) return;

                try
                {
                    _rig = PmxHumanoidSourceRig.Create(_model, _options);
                    if (_rig.Avatar == null || !_rig.Avatar.isValid || !_rig.Avatar.isHuman)
                        throw new InvalidOperationException(
                            "Unity could not create a valid Humanoid Avatar from the PMX skeleton. " +
                            "Required MMD humanoid bones may be missing or the PMX rest pose may be invalid.");
                    _poseHandler = new HumanPoseHandler(_rig.Avatar, _rig.Root.transform);
                }
                catch
                {
                    if (_poseHandler != null) _poseHandler.Dispose();
                    if (_rig != null) _rig.Dispose();
                    _poseHandler = null;
                    _rig = null;
                    throw;
                }
            }

            internal PmxAnimationBakeReport Bake(VmdMotion motion, Transform morphTargetRoot = null,
                Action<float, string> progress = null)
            {
                if (motion == null) throw new ArgumentNullException(nameof(motion));
                if (_rig == null || _poseHandler == null)
                    throw new ObjectDisposedException(nameof(BatchSession));
                progress?.Invoke(0f, "Preparing MMD bone and IK samples");
                PreparedMotion prepared = Prepare(motion);
                return BakePrepared(_model, prepared, morphTargetRoot, _options, progress, _rig, _poseHandler);
            }

            internal PreparedMotion Prepare(VmdMotion motion, int maximumParallelism = 0,
                Action<int, int> threadSafeProgress = null, CancellationToken cancellationToken = default)
            {
                if (motion == null) throw new ArgumentNullException(nameof(motion));
                if (_disposed)
                    throw new ObjectDisposedException(nameof(BatchSession));
                return PrepareMotion(
                    _model, motion, _options, maximumParallelism, threadSafeProgress, cancellationToken);
            }

            internal PmxAnimationBakeReport Bake(PreparedMotion prepared, Transform morphTargetRoot = null,
                Action<float, string> progress = null)
            {
                if (prepared == null) throw new ArgumentNullException(nameof(prepared));
                if (_rig == null || _poseHandler == null)
                    throw new ObjectDisposedException(nameof(BatchSession));
                return BakePrepared(_model, prepared, morphTargetRoot, _options, progress, _rig, _poseHandler);
            }

            internal PmxAnimationBakeReport BakeTargetFk(PreparedMotion prepared, GameObject targetRoot,
                PmxTargetFkBakeOptions fkOptions, Action<float, string> progress = null)
            {
                if (prepared == null) throw new ArgumentNullException(nameof(prepared));
                if (targetRoot == null) throw new ArgumentNullException(nameof(targetRoot));
                if (_disposed)
                    throw new ObjectDisposedException(nameof(BatchSession));
                return PmxTargetFkAnimationClipBaker.Bake(
                    _model, prepared, targetRoot, _options, fkOptions, progress);
            }

            internal PmxAnimationBakeReport BakeTargetHumanoid(PreparedMotion prepared,
                GameObject targetRoot, Action<float, string> progress = null)
            {
                if (prepared == null) throw new ArgumentNullException(nameof(prepared));
                if (targetRoot == null) throw new ArgumentNullException(nameof(targetRoot));
                if (_disposed) throw new ObjectDisposedException(nameof(BatchSession));
                int targetId = targetRoot.GetInstanceID();
                if (_targetHumanoidRig == null || _targetHumanoidRootId != targetId)
                {
                    if (_targetHumanoidRig != null) _targetHumanoidRig.Dispose();
                    _targetHumanoidRig = null;
                    _targetHumanoidRootId = 0;
                    PmxTargetHumanoidSourceRig targetRig = PmxTargetHumanoidSourceRig.Create(
                        _model, targetRoot, _options);
                    _targetHumanoidRig = targetRig;
                    _targetHumanoidRootId = targetId;
                }
                return PmxTargetHumanoidAnimationClipBaker.Bake(
                    prepared, _options, _targetHumanoidRig, progress);
            }

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                try
                {
                    if (_poseHandler != null) _poseHandler.Dispose();
                }
                finally
                {
                    if (_targetHumanoidRig != null) _targetHumanoidRig.Dispose();
                    if (_rig != null) _rig.Dispose();
                    _poseHandler = null;
                    _rig = null;
                    _targetHumanoidRig = null;
                    _targetHumanoidRootId = 0;
                }
            }
        }

        internal sealed class PreparedMotion
        {
            internal readonly VmdMotion Motion;
            internal readonly float Duration;
            internal readonly PmxHumanoidPoseSample[] Samples;

            internal PreparedMotion(VmdMotion motion, float duration, PmxHumanoidPoseSample[] samples)
            {
                Motion = motion;
                Duration = duration;
                Samples = samples;
            }
        }

        private sealed class PoseEvaluationWorker
        {
            internal readonly PmxMotionEvaluator Evaluator;
            internal readonly PmxPose Pose;

            internal PoseEvaluationWorker(PmxModel model, VmdMotion motion, PmxEvaluationOptions options)
            {
                Evaluator = new PmxMotionEvaluator(model, motion, options);
                Pose = Evaluator.CreatePose();
            }
        }

        internal static int DefaultParallelism => Math.Max(1, Environment.ProcessorCount - 1);

        private static PreparedMotion PrepareMotion(PmxModel model, VmdMotion motion,
            PmxAnimationClipBakerOptions options, int maximumParallelism,
            Action<int, int> threadSafeProgress, CancellationToken cancellationToken)
        {
            model.ValidateHierarchy();
            model.GetRestLocalPositions();
            model.GetTransformOrder();
            motion.PrepareForConcurrentSampling();

            float duration = motion.MaximumFrame / 30f;
            int sampleCount = Math.Max(1, Mathf.CeilToInt(duration * options.SampleRate) + 1);
            var samples = new PmxHumanoidPoseSample[sampleCount];
            int completed = 0;
            var parallelOptions = new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = maximumParallelism > 0
                    ? maximumParallelism
                    : DefaultParallelism
            };

            Parallel.For(
                0,
                sampleCount,
                parallelOptions,
                () => new PoseEvaluationWorker(model, motion, options.Evaluation),
                (sample, _, worker) =>
                {
                    float time = sample == sampleCount - 1 ? duration : sample / options.SampleRate;
                    float vmdFrame = CalculateVmdFrame(
                        sample, sampleCount, options.SampleRate, motion.MaximumFrame);
                    PmxPose pose = worker.Evaluator.Evaluate(vmdFrame, worker.Pose);
                    samples[sample] = new PmxHumanoidPoseSample(pose);
                    int current = Interlocked.Increment(ref completed);
                    threadSafeProgress?.Invoke(current, sampleCount);
                    return worker;
                },
                _ => { });

            return new PreparedMotion(motion, duration, samples);
        }

        private static PmxAnimationBakeReport BakePrepared(PmxModel model, PreparedMotion prepared,
            Transform morphTargetRoot, PmxAnimationClipBakerOptions options,
            Action<float, string> progress, PmxHumanoidSourceRig rig, HumanPoseHandler poseHandler)
        {
            VmdMotion motion = prepared.Motion;
            var report = new PmxAnimationBakeReport
            {
                BoundBoneCount = rig.MappedHumanBoneCount
            };
            report.MissingBones.AddRange(rig.MissingRequiredBones);

            float duration = prepared.Duration;
            int sampleCount = prepared.Samples.Length;
            report.SampleCount = sampleCount;
            float[] sampleTimes = CreateSampleTimes(sampleCount, duration, options.SampleRate);

            var muscles = new FloatCurveAccumulator[HumanTrait.MuscleCount];
            for (int index = 0; index < muscles.Length; index++)
                muscles[index] = new FloatCurveAccumulator(sampleTimes);
            var rootTx = new FloatCurveAccumulator(sampleTimes);
            var rootTy = new FloatCurveAccumulator(sampleTimes);
            var rootTz = new FloatCurveAccumulator(sampleTimes);
            var rootQx = new FloatCurveAccumulator(sampleTimes);
            var rootQy = new FloatCurveAccumulator(sampleTimes);
            var rootQz = new FloatCurveAccumulator(sampleTimes);
            var rootQw = new FloatCurveAccumulator(sampleTimes);

            var humanPose = new HumanPose();
            Quaternion previousBodyRotation = Quaternion.identity;
            bool hasPreviousBodyRotation = false;

            for (int sample = 0; sample < sampleCount; sample++)
            {
                float vmdFrame = CalculateVmdFrame(
                    sample, sampleCount, options.SampleRate, motion.MaximumFrame);
                rig.Apply(prepared.Samples[sample], options.PositionScale);
                poseHandler.GetHumanPose(ref humanPose);

                ValidateHumanPose(humanPose, vmdFrame);
                Vector3 bodyPosition = ConvertHumanoidRootPosition(
                    humanPose.bodyPosition, options.CoordinateConversion);
                Quaternion bodyRotation = ConvertHumanoidRootRotation(
                    humanPose.bodyRotation, options.CoordinateConversion);
                if (hasPreviousBodyRotation && Quaternion.Dot(previousBodyRotation, bodyRotation) < 0f)
                    bodyRotation = new Quaternion(
                        -bodyRotation.x, -bodyRotation.y, -bodyRotation.z, -bodyRotation.w);
                previousBodyRotation = bodyRotation;
                hasPreviousBodyRotation = true;

                rootTx.Add(bodyPosition.x);
                rootTy.Add(bodyPosition.y);
                rootTz.Add(bodyPosition.z);
                rootQx.Add(bodyRotation.x);
                rootQy.Add(bodyRotation.y);
                rootQz.Add(bodyRotation.z);
                rootQw.Add(bodyRotation.w);
                for (int muscle = 0; muscle < muscles.Length; muscle++)
                    muscles[muscle].Add(humanPose.muscles[muscle]);

                if ((sample & 15) == 0 || sample + 1 == sampleCount)
                    progress?.Invoke(0.8f * (sample + 1f) / sampleCount,
                        $"Baking Humanoid frame {sample + 1}/{sampleCount}");
            }

            var clip = new AnimationClip
            {
                name = string.IsNullOrEmpty(motion.ModelName) ? "MMD Humanoid Motion" : motion.ModelName,
                frameRate = options.SampleRate,
                legacy = false
            };
            progress?.Invoke(0.81f, "Optimizing Humanoid root curves");
            var accumulators = new FloatCurveAccumulator[7 + muscles.Length];
            accumulators[0] = rootTx;
            accumulators[1] = rootTy;
            accumulators[2] = rootTz;
            accumulators[3] = rootQx;
            accumulators[4] = rootQy;
            accumulators[5] = rootQz;
            accumulators[6] = rootQw;
            Array.Copy(muscles, 0, accumulators, 7, muscles.Length);
            var optimizedKeys = new Keyframe[accumulators.Length][];
            Parallel.For(0, accumulators.Length, index =>
            {
                bool keepConstant = index < 7;
                FloatCurveAccumulator accumulator = accumulators[index];
                if (!keepConstant && accumulator.IsApproximatelyZero()) return;
                optimizedKeys[index] = PmxAnimationClipBaker.CreateLinearKeys(
                    accumulator.ToArray(), options.CurveReductionError);
            });

            var properties = new string[optimizedKeys.Length];
            properties[0] = "RootT.x";
            properties[1] = "RootT.y";
            properties[2] = "RootT.z";
            properties[3] = "RootQ.x";
            properties[4] = "RootQ.y";
            properties[5] = "RootQ.z";
            properties[6] = "RootQ.w";
            for (int muscle = 0; muscle < muscles.Length; muscle++)
            {
                properties[7 + muscle] = GetAnimatorMusclePropertyName(muscle);
                progress?.Invoke(0.82f + 0.12f * (muscle + 1f) / muscles.Length,
                    $"Optimizing Humanoid curves {muscle + 1}/{muscles.Length}");
            }
            WriteAnimatorCurves(clip, properties, optimizedKeys);

            if (options.BakeMorphs && morphTargetRoot != null)
                report.BoundMorphCount = PmxAnimationClipBaker.BakeMorphCurves(
                    clip, motion, morphTargetRoot);

            clip.EnsureQuaternionContinuity();
            progress?.Invoke(0.95f, "Finalizing Humanoid animation");
            report.Clip = clip;
            return report;
        }

        public static PmxAnimationBakeReport BakeToAsset(string pmxPath, string vmdPath,
            Transform morphTargetRoot, string outputAssetPath,
            PmxAnimationClipBakerOptions options = null, Action<float, string> progress = null)
        {
            PmxAnimationClipBaker.ValidateAssetPath(outputAssetPath);
            PmxModel model = PmxModelCache.Load(pmxPath);
            VmdMotion motion = VmdParser.Parse(PmxAnimationClipBaker.ToPhysicalPath(vmdPath));
            PmxAnimationBakeReport report = Bake(model, motion, morphTargetRoot, options, progress);
            report.Clip.name = Path.GetFileNameWithoutExtension(outputAssetPath);
            progress?.Invoke(0.96f, "Writing AnimationClip asset");
            AssetDatabase.DeleteAsset(outputAssetPath);
            AssetDatabase.CreateAsset(report.Clip, outputAssetPath);
            AssetDatabase.SaveAssets();
            progress?.Invoke(0.99f, "Importing AnimationClip asset");
            AssetDatabase.ImportAsset(outputAssetPath, ImportAssetOptions.ForceSynchronousImport);
            report.Clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(outputAssetPath);
            progress?.Invoke(1f, "Conversion complete");
            return report;
        }

        internal static void WriteAnimatorCurves(AnimationClip clip,
            IReadOnlyList<string> properties, IReadOnlyList<Keyframe[]> keys)
        {
            if (clip == null) throw new ArgumentNullException(nameof(clip));
            if (properties == null) throw new ArgumentNullException(nameof(properties));
            if (keys == null || properties.Count != keys.Count)
                throw new ArgumentException("Animator curve properties and keys must have equal lengths.");
            var bindings = new List<EditorCurveBinding>(properties.Count);
            var curves = new List<AnimationCurve>(properties.Count);
            for (int index = 0; index < properties.Count; index++)
            {
                if (string.IsNullOrEmpty(properties[index]) || keys[index] == null) continue;
                bindings.Add(EditorCurveBinding.FloatCurve(
                    string.Empty, typeof(Animator), properties[index]));
                curves.Add(new AnimationCurve(keys[index]));
            }
            if (bindings.Count > 0)
                AnimationUtility.SetEditorCurves(clip, bindings.ToArray(), curves.ToArray());
        }

        internal static string GetAnimatorMusclePropertyName(int muscleIndex)
        {
            string muscleName = HumanTrait.MuscleName[muscleIndex];
            if (string.IsNullOrEmpty(muscleName)) return muscleName;

            // HumanTrait exposes finger labels such as "Left Thumb 1 Stretched",
            // but imported Humanoid clips bind them through Animator's canonical
            // hand namespace: "LeftHand.Thumb.1 Stretched". The display label is
            // not a valid substitute for that serialized binding name.
            string hand;
            string remainder;
            if (muscleName.StartsWith("Left ", StringComparison.Ordinal))
            {
                hand = "LeftHand.";
                remainder = muscleName.Substring(5);
            }
            else if (muscleName.StartsWith("Right ", StringComparison.Ordinal))
            {
                hand = "RightHand.";
                remainder = muscleName.Substring(6);
            }
            else
            {
                return muscleName;
            }

            string[] fingers = { "Thumb", "Index", "Middle", "Ring", "Little" };
            foreach (string finger in fingers)
            {
                if (!remainder.StartsWith(finger + " ", StringComparison.Ordinal)) continue;
                string channel = remainder.Substring(finger.Length + 1);
                return hand + finger + "." + channel;
            }
            return muscleName;
        }

        internal static void ValidateHumanPose(HumanPose pose, float frame)
        {
            if (!IsFinite(pose.bodyPosition.x) || !IsFinite(pose.bodyPosition.y) ||
                !IsFinite(pose.bodyPosition.z) || !IsFinite(pose.bodyRotation.x) ||
                !IsFinite(pose.bodyRotation.y) || !IsFinite(pose.bodyRotation.z) ||
                !IsFinite(pose.bodyRotation.w))
                throw new InvalidOperationException($"Humanoid root pose became non-finite at VMD frame {frame}.");
            if (pose.muscles == null || pose.muscles.Length != HumanTrait.MuscleCount)
                throw new InvalidOperationException("Unity returned an invalid Humanoid muscle pose.");
            for (int index = 0; index < pose.muscles.Length; index++)
                if (!IsFinite(pose.muscles[index]))
                    throw new InvalidOperationException(
                        $"Humanoid muscle {index} became non-finite at VMD frame {frame}.");
        }

        internal static float CalculateVmdFrame(int sampleIndex, int sampleCount,
            float sampleRate, uint maximumFrame)
        {
            if (sampleIndex == sampleCount - 1) return maximumFrame;
            if (Mathf.Abs(sampleRate - 30f) <= 1e-6f)
                return Mathf.Min(maximumFrame, sampleIndex);
            float time = sampleIndex / sampleRate;
            return Mathf.Min(maximumFrame, time * 30f);
        }

        internal static float[] CreateSampleTimes(int sampleCount, float duration, float sampleRate)
        {
            var result = new float[sampleCount];
            for (int sample = 0; sample < sampleCount; sample++)
                result[sample] = sample == sampleCount - 1 ? duration : sample / sampleRate;
            return result;
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        internal static Vector3 ConvertHumanoidRootPosition(Vector3 value, PmxCoordinateConversion target)
        {
            // The Humanoid proxy always uses a 180-degree Y source basis so semantic left/right
            // and forward match Unity. Convert its root back to the requested clip coordinate basis.
            switch (target)
            {
                case PmxCoordinateConversion.MmdToUnity:
                    return new Vector3(-value.x, value.y, value.z);
                case PmxCoordinateConversion.MmdToUnityAndRotateY180:
                    return new Vector3(value.x, value.y, -value.z);
                case PmxCoordinateConversion.None:
                    return new Vector3(-value.x, value.y, -value.z);
                default:
                    return value;
            }
        }

        internal static Quaternion ConvertHumanoidRootRotation(Quaternion value,
            PmxCoordinateConversion target)
        {
            switch (target)
            {
                case PmxCoordinateConversion.MmdToUnity:
                    return PmxAnimationClipBaker.NormalizeSafe(
                        new Quaternion(value.x, -value.y, -value.z, value.w));
                case PmxCoordinateConversion.MmdToUnityAndRotateY180:
                    return PmxAnimationClipBaker.NormalizeSafe(
                        new Quaternion(-value.x, -value.y, value.z, value.w));
                case PmxCoordinateConversion.None:
                    return PmxAnimationClipBaker.NormalizeSafe(
                        new Quaternion(-value.x, value.y, -value.z, value.w));
                default:
                    return PmxAnimationClipBaker.NormalizeSafe(value);
            }
        }

        internal sealed class FloatCurveAccumulator
        {
            private readonly float[] _times;
            private readonly float[] _values;
            private int _count;
            private float _maximumAbsoluteValue;

            internal FloatCurveAccumulator(float[] sharedTimes)
            {
                _times = sharedTimes ?? throw new ArgumentNullException(nameof(sharedTimes));
                _values = new float[sharedTimes.Length];
            }

            internal void Add(float value)
            {
                if (_count >= _values.Length)
                    throw new InvalidOperationException("Humanoid curve sample capacity was exceeded.");
                _values[_count] = value;
                _count++;
                _maximumAbsoluteValue = Mathf.Max(_maximumAbsoluteValue, Mathf.Abs(value));
            }

            internal bool IsApproximatelyZero()
            {
                return _maximumAbsoluteValue < 1e-6f;
            }

            internal Keyframe[] ToArray()
            {
                var keys = new Keyframe[_count];
                for (int index = 0; index < _count; index++)
                    keys[index] = new Keyframe(_times[index], _values[index]);
                return keys;
            }
        }
    }

    internal sealed class PmxHumanoidPoseSample
    {
        internal readonly Vector3[] WorldPositions;
        internal readonly Quaternion[] WorldRotations;
        internal readonly Quaternion[] LocalRotations;

        internal PmxHumanoidPoseSample(PmxPose pose)
        {
            int count = pose.BoneCount;
            WorldPositions = new Vector3[count];
            WorldRotations = new Quaternion[count];
            LocalRotations = new Quaternion[count];
            Array.Copy(pose.WorldPositions, WorldPositions, count);
            Array.Copy(pose.WorldRotations, WorldRotations, count);
            Array.Copy(pose.LocalRotations, LocalRotations, count);
        }
    }

    internal sealed class PmxHumanoidSourceRig : IDisposable
    {
        internal sealed class HumanBoneRule
        {
            internal readonly HumanBodyBones HumanBone;
            internal readonly string[] Candidates;

            internal HumanBoneRule(HumanBodyBones humanBone, params string[] candidates)
            {
                HumanBone = humanBone;
                Candidates = candidates;
            }
        }

        internal static readonly HumanBoneRule[] HumanBoneRules =
        {
            // Standard MMD waist is the common ancestor of upper body and lower body/legs.
            // Mapping 下半身 directly would put Spine outside the Humanoid Hips hierarchy.
            Rule(HumanBodyBones.Hips, "下半身", "lower body", "LowerBody", "腰", "waist", "Waist", "hips", "Hips"),
            Rule(HumanBodyBones.Spine, "上半身", "upper body", "UpperBody", "spine", "Spine"),
            Rule(HumanBodyBones.Chest, "上半身2", "上半身２", "upper body2", "UpperBody2", "chest", "Chest"),
            Rule(HumanBodyBones.UpperChest, "上半身3", "上半身３", "upper body3", "UpperBody3", "upper chest", "UpperChest"),
            Rule(HumanBodyBones.Neck, "首", "neck", "Neck"),
            Rule(HumanBodyBones.Head, "頭", "head", "Head"),
            Rule(HumanBodyBones.LeftEye, "左目", "left eye", "LeftEye"),
            Rule(HumanBodyBones.RightEye, "右目", "right eye", "RightEye"),
            Rule(HumanBodyBones.Jaw, "あご", "顎", "jaw", "Jaw"),
            Rule(HumanBodyBones.LeftShoulder, "左肩", "left shoulder", "LeftShoulder"),
            Rule(HumanBodyBones.LeftUpperArm, "左腕", "left arm", "LeftArm", "LeftUpperArm"),
            Rule(HumanBodyBones.LeftLowerArm, "左ひじ", "左肘", "left elbow", "LeftElbow", "LeftLowerArm"),
            Rule(HumanBodyBones.LeftHand, "左手首", "left wrist", "LeftWrist", "LeftHand"),
            Rule(HumanBodyBones.RightShoulder, "右肩", "right shoulder", "RightShoulder"),
            Rule(HumanBodyBones.RightUpperArm, "右腕", "right arm", "RightArm", "RightUpperArm"),
            Rule(HumanBodyBones.RightLowerArm, "右ひじ", "右肘", "right elbow", "RightElbow", "RightLowerArm"),
            Rule(HumanBodyBones.RightHand, "右手首", "right wrist", "RightWrist", "RightHand"),
            Rule(HumanBodyBones.LeftUpperLeg, "左足D", "左足Ｄ", "左足", "left leg", "LeftLeg", "LeftUpperLeg"),
            Rule(HumanBodyBones.LeftLowerLeg, "左ひざD", "左膝D", "左ひざＤ", "左ひざ", "左膝", "left knee", "LeftKnee", "LeftLowerLeg"),
            Rule(HumanBodyBones.LeftFoot, "左足首D", "左足首Ｄ", "左足首", "left ankle", "LeftAnkle", "LeftFoot"),
            Rule(HumanBodyBones.LeftToes, "左つま先EX", "左つま先", "left toe", "LeftToe", "LeftToes"),
            Rule(HumanBodyBones.RightUpperLeg, "右足D", "右足Ｄ", "右足", "right leg", "RightLeg", "RightUpperLeg"),
            Rule(HumanBodyBones.RightLowerLeg, "右ひざD", "右膝D", "右ひざＤ", "右ひざ", "右膝", "right knee", "RightKnee", "RightLowerLeg"),
            Rule(HumanBodyBones.RightFoot, "右足首D", "右足首Ｄ", "右足首", "right ankle", "RightAnkle", "RightFoot"),
            Rule(HumanBodyBones.RightToes, "右つま先EX", "右つま先", "right toe", "RightToe", "RightToes"),
            Rule(HumanBodyBones.LeftThumbIntermediate, "左親指1", "左親指１", "left thumb1", "LeftThumb1"),
            Rule(HumanBodyBones.LeftThumbDistal, "左親指2", "左親指２", "left thumb2", "LeftThumb2"),
            Rule(HumanBodyBones.LeftIndexProximal, "左人指1", "左人指１", "left index1", "LeftIndex1"),
            Rule(HumanBodyBones.LeftIndexIntermediate, "左人指2", "左人指２", "left index2", "LeftIndex2"),
            Rule(HumanBodyBones.LeftIndexDistal, "左人指3", "左人指３", "left index3", "LeftIndex3"),
            Rule(HumanBodyBones.LeftMiddleProximal, "左中指1", "左中指１", "left middle1", "LeftMiddle1"),
            Rule(HumanBodyBones.LeftMiddleIntermediate, "左中指2", "左中指２", "left middle2", "LeftMiddle2"),
            Rule(HumanBodyBones.LeftMiddleDistal, "左中指3", "左中指３", "left middle3", "LeftMiddle3"),
            Rule(HumanBodyBones.LeftRingProximal, "左薬指1", "左薬指１", "left ring1", "LeftRing1"),
            Rule(HumanBodyBones.LeftRingIntermediate, "左薬指2", "左薬指２", "left ring2", "LeftRing2"),
            Rule(HumanBodyBones.LeftRingDistal, "左薬指3", "左薬指３", "left ring3", "LeftRing3"),
            Rule(HumanBodyBones.LeftLittleProximal, "左小指1", "左小指１", "left little1", "LeftLittle1"),
            Rule(HumanBodyBones.LeftLittleIntermediate, "左小指2", "左小指２", "left little2", "LeftLittle2"),
            Rule(HumanBodyBones.LeftLittleDistal, "左小指3", "左小指３", "left little3", "LeftLittle3"),
            Rule(HumanBodyBones.RightThumbIntermediate, "右親指1", "右親指１", "right thumb1", "RightThumb1"),
            Rule(HumanBodyBones.RightThumbDistal, "右親指2", "右親指２", "right thumb2", "RightThumb2"),
            Rule(HumanBodyBones.RightIndexProximal, "右人指1", "右人指１", "right index1", "RightIndex1"),
            Rule(HumanBodyBones.RightIndexIntermediate, "右人指2", "右人指２", "right index2", "RightIndex2"),
            Rule(HumanBodyBones.RightIndexDistal, "右人指3", "右人指３", "right index3", "RightIndex3"),
            Rule(HumanBodyBones.RightMiddleProximal, "右中指1", "右中指１", "right middle1", "RightMiddle1"),
            Rule(HumanBodyBones.RightMiddleIntermediate, "右中指2", "右中指２", "right middle2", "RightMiddle2"),
            Rule(HumanBodyBones.RightMiddleDistal, "右中指3", "右中指３", "right middle3", "RightMiddle3"),
            Rule(HumanBodyBones.RightRingProximal, "右薬指1", "右薬指１", "right ring1", "RightRing1"),
            Rule(HumanBodyBones.RightRingIntermediate, "右薬指2", "右薬指２", "right ring2", "RightRing2"),
            Rule(HumanBodyBones.RightRingDistal, "右薬指3", "右薬指３", "right ring3", "RightRing3"),
            Rule(HumanBodyBones.RightLittleProximal, "右小指1", "右小指１", "right little1", "RightLittle1"),
            Rule(HumanBodyBones.RightLittleIntermediate, "右小指2", "右小指２", "right little2", "RightLittle2"),
            Rule(HumanBodyBones.RightLittleDistal, "右小指3", "右小指３", "right little3", "RightLittle3")
        };

        internal GameObject Root { get; private set; }
        internal Avatar Avatar { get; private set; }
        internal int MappedHumanBoneCount { get; private set; }
        internal readonly List<string> MissingRequiredBones = new List<string>();

        private PmxModel _model;
        private Transform[] _bones;
        private Vector3[] _restLocalPositions;
        private readonly Dictionary<HumanBodyBones, int> _humanBoneIndices =
            new Dictionary<HumanBodyBones, int>();
        private readonly Dictionary<HumanBodyBones, Transform> _humanBoneTransforms =
            new Dictionary<HumanBodyBones, Transform>();
        private readonly List<int> _humanBoneWorldOrder = new List<int>();
        private readonly HashSet<int> _thumbLocalMotionBoneIndices = new HashSet<int>();
        private readonly Dictionary<int, int> _thumbMotionParentIndices = new Dictionary<int, int>();

        internal static PmxHumanoidSourceRig Create(PmxModel model, PmxAnimationClipBakerOptions options)
        {
            var rig = new PmxHumanoidSourceRig();
            try
            {
                rig.Build(model, options);
                return rig;
            }
            catch
            {
                rig.Dispose();
                throw;
            }
        }

        internal void Apply(PmxHumanoidPoseSample pose, float positionScale)
        {
            var sourceCoordinates = new PmxAnimationClipBakerOptions
            {
                CoordinateConversion = PmxCoordinateConversion.RotateY180
            };
            foreach (int index in _humanBoneWorldOrder)
            {
                if (_thumbLocalMotionBoneIndices.Contains(index)) continue;
                _bones[index].position =
                    PmxAnimationClipBaker.ConvertPosition(pose.WorldPositions[index], sourceCoordinates) * positionScale;
                _bones[index].rotation =
                    PmxAnimationClipBaker.ConvertRotation(pose.WorldRotations[index], sourceCoordinates);
            }

            // Native PMX2FBX inserts a static Thumb0M proxy between the wrist and
            // the original Thumb1/Thumb2 bones. Those two bones therefore retain
            // their own local VMD rotations and must not inherit the animated PMX
            // Thumb0 world rotation.
            foreach (int index in _humanBoneWorldOrder)
            {
                if (!_thumbLocalMotionBoneIndices.Contains(index)) continue;
                Quaternion localRotation = pose.LocalRotations[index];
                if (_thumbMotionParentIndices.TryGetValue(index, out int originalThumbRoot))
                {
                    // Thumb1 used to be a child of the animated PMX Thumb0. Once
                    // Thumb0 is replaced by the static Mecanim proxy, preserve the
                    // original world motion by folding Thumb0 into Thumb1.
                    localRotation = pose.LocalRotations[originalThumbRoot] * localRotation;
                }
                _bones[index].localRotation =
                    PmxAnimationClipBaker.ConvertRotation(localRotation, sourceCoordinates);
            }
        }

        public void Dispose()
        {
            if (Avatar != null) UnityEngine.Object.DestroyImmediate(Avatar);
            if (Root != null) UnityEngine.Object.DestroyImmediate(Root);
            Avatar = null;
            Root = null;
        }

        private void Build(PmxModel model, PmxAnimationClipBakerOptions options)
        {
            _model = model ?? throw new ArgumentNullException(nameof(model));
            _restLocalPositions = model.GetRestLocalPositions();
            Root = new GameObject("__PMX_Humanoid_Source__")
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            _bones = new Transform[model.Bones.Count];
            var sourceCoordinates = new PmxAnimationClipBakerOptions
            {
                CoordinateConversion = PmxCoordinateConversion.RotateY180
            };
            var usedNames = new HashSet<string>(StringComparer.Ordinal);
            for (int index = 0; index < model.Bones.Count; index++)
            {
                PmxBone bone = model.Bones[index];
                string name = string.IsNullOrEmpty(bone.NameJapanese) ? bone.NameEnglish : bone.NameJapanese;
                if (string.IsNullOrEmpty(name)) name = "PMX Bone " + index;
                string uniqueName = name;
                for (int suffix = 1; !usedNames.Add(uniqueName); suffix++)
                    uniqueName = name + "__" + suffix;
                var gameObject = new GameObject(uniqueName) { hideFlags = HideFlags.HideAndDontSave };
                _bones[index] = gameObject.transform;
            }
            for (int index = 0; index < model.Bones.Count; index++)
            {
                int parent = model.Bones[index].ParentIndex;
                _bones[index].SetParent(parent >= 0 ? _bones[parent] : Root.transform, false);
                _bones[index].localPosition =
                    PmxAnimationClipBaker.ConvertPosition(_restLocalPositions[index], sourceCoordinates) * options.PositionScale;
                _bones[index].localRotation = Quaternion.identity;
                _bones[index].localScale = Vector3.one;
            }

            HumanBone[] human = BuildHumanBones();
            if (MissingRequiredBones.Count > 0)
                throw new InvalidOperationException(
                    "PMX is missing required Humanoid mappings: " + string.Join(", ", MissingRequiredBones));
            BuildHumanoidProxyHierarchy();
            SkeletonBone[] referenceSkeleton = null;
            if (options.EnforceHumanoidTPose)
                TryBuildUnityHumanoidReferenceSkeleton(human, out referenceSkeleton);
            if (referenceSkeleton == null)
            {
                if (options.EnforceHumanoidTPose)
                    ForceHumanoidReferencePose(PmxCoordinateConversion.RotateY180);
                referenceSkeleton = BuildSkeletonBones(Root.transform);
            }

            var description = new HumanDescription
            {
                human = human,
                skeleton = referenceSkeleton,
                upperArmTwist = 0.5f,
                lowerArmTwist = 0.5f,
                upperLegTwist = 0.5f,
                lowerLegTwist = 0.5f,
                armStretch = 0.05f,
                legStretch = 0.05f,
                feetSpacing = 0f,
                hasTranslationDoF = false
            };
            Avatar = AvatarBuilder.BuildHumanAvatar(Root, description);
            Avatar.name = string.IsNullOrEmpty(model.NameJapanese)
                ? "PMX Humanoid Source Avatar"
                : model.NameJapanese + " Humanoid Source Avatar";
        }

        private HumanBone[] BuildHumanBones()
        {
            var result = new List<HumanBone>();
            var mappedPmxBones = new HashSet<int>();
            foreach (HumanBoneRule rule in HumanBoneRules)
            {
                int boneIndex = FindCandidate(rule.Candidates);
                if (boneIndex < 0 || !mappedPmxBones.Add(boneIndex))
                {
                    int humanIndex = (int)rule.HumanBone;
                    if (humanIndex >= 0 && humanIndex < HumanTrait.BoneCount && HumanTrait.RequiredBone(humanIndex))
                        MissingRequiredBones.Add(HumanTrait.BoneName[humanIndex]);
                    continue;
                }

                result.Add(new HumanBone
                {
                    boneName = _bones[boneIndex].name,
                    humanName = HumanTrait.BoneName[(int)rule.HumanBone],
                    limit = new HumanLimit { useDefaultValues = true }
                });
                _humanBoneIndices[rule.HumanBone] = boneIndex;
                _humanBoneTransforms[rule.HumanBone] = _bones[boneIndex];
            }

            AddSyntheticThumbProximal(result, true);
            AddSyntheticThumbProximal(result, false);
            MappedHumanBoneCount = result.Count;
            return result.ToArray();
        }

        private void AddSyntheticThumbProximal(List<HumanBone> humanBones, bool left)
        {
            HumanBodyBones handBone = left ? HumanBodyBones.LeftHand : HumanBodyBones.RightHand;
            HumanBodyBones proximalBone = left
                ? HumanBodyBones.LeftThumbProximal
                : HumanBodyBones.RightThumbProximal;
            HumanBodyBones intermediateBone = left
                ? HumanBodyBones.LeftThumbIntermediate
                : HumanBodyBones.RightThumbIntermediate;
            HumanBodyBones distalBone = left
                ? HumanBodyBones.LeftThumbDistal
                : HumanBodyBones.RightThumbDistal;
            if (!_humanBoneTransforms.TryGetValue(handBone, out Transform hand) ||
                !_humanBoneTransforms.TryGetValue(intermediateBone, out Transform intermediate) ||
                !_humanBoneIndices.TryGetValue(intermediateBone, out int intermediateIndex))
                return;

            string name = left ? "__LeftThumb0M__" : "__RightThumb0M__";
            var proxyObject = new GameObject(name) { hideFlags = HideFlags.HideAndDontSave };
            Transform proxy = proxyObject.transform;
            proxy.SetParent(hand, false);
            proxy.position = Vector3.Lerp(hand.position, intermediate.position, 0.5f);
            proxy.localRotation = Quaternion.identity;
            proxy.localScale = Vector3.one;
            intermediate.SetParent(proxy, true);

            humanBones.Add(new HumanBone
            {
                boneName = name,
                humanName = HumanTrait.BoneName[(int)proximalBone],
                limit = new HumanLimit { useDefaultValues = true }
            });
            _humanBoneTransforms[proximalBone] = proxy;
            _thumbLocalMotionBoneIndices.Add(intermediateIndex);
            int originalThumbRoot = FindCandidate(left
                ? new[] { "\u5de6\u89aa\u6307\uff10", "\u5de6\u89aa\u63070", "left thumb0", "LeftThumb0" }
                : new[] { "\u53f3\u89aa\u6307\uff10", "\u53f3\u89aa\u63070", "right thumb0", "RightThumb0" });
            if (originalThumbRoot >= 0 && originalThumbRoot != intermediateIndex)
                _thumbMotionParentIndices[intermediateIndex] = originalThumbRoot;
            if (_humanBoneIndices.TryGetValue(distalBone, out int distalIndex))
                _thumbLocalMotionBoneIndices.Add(distalIndex);
        }

        private void BuildHumanoidProxyHierarchy()
        {
            Reparent(HumanBodyBones.Hips, null);
            Reparent(HumanBodyBones.Spine, HumanBodyBones.Hips);
            Reparent(HumanBodyBones.Chest, HumanBodyBones.Spine);
            Reparent(HumanBodyBones.UpperChest, FirstMapped(HumanBodyBones.Chest, HumanBodyBones.Spine));
            HumanBodyBones torso = FirstMapped(HumanBodyBones.UpperChest, HumanBodyBones.Chest, HumanBodyBones.Spine);
            Reparent(HumanBodyBones.Neck, torso);
            Reparent(HumanBodyBones.Head, FirstMapped(HumanBodyBones.Neck, torso));
            Reparent(HumanBodyBones.LeftEye, HumanBodyBones.Head);
            Reparent(HumanBodyBones.RightEye, HumanBodyBones.Head);
            Reparent(HumanBodyBones.Jaw, HumanBodyBones.Head);

            BuildArmHierarchy(true, torso);
            BuildArmHierarchy(false, torso);
            BuildLegHierarchy(true);
            BuildLegHierarchy(false);

            _humanBoneWorldOrder.Clear();
            _humanBoneWorldOrder.AddRange(_humanBoneIndices.Values
                .Distinct()
                .OrderBy(index => GetHierarchyDepth(_bones[index])));
        }

        private void BuildArmHierarchy(bool left, HumanBodyBones torso)
        {
            HumanBodyBones shoulder = left ? HumanBodyBones.LeftShoulder : HumanBodyBones.RightShoulder;
            HumanBodyBones upper = left ? HumanBodyBones.LeftUpperArm : HumanBodyBones.RightUpperArm;
            HumanBodyBones lower = left ? HumanBodyBones.LeftLowerArm : HumanBodyBones.RightLowerArm;
            HumanBodyBones hand = left ? HumanBodyBones.LeftHand : HumanBodyBones.RightHand;
            Reparent(shoulder, torso);
            Reparent(upper, FirstMapped(shoulder, torso));
            Reparent(lower, upper);
            Reparent(hand, lower);

            BuildFingerHierarchy(hand,
                left ? HumanBodyBones.LeftThumbProximal : HumanBodyBones.RightThumbProximal,
                left ? HumanBodyBones.LeftThumbIntermediate : HumanBodyBones.RightThumbIntermediate,
                left ? HumanBodyBones.LeftThumbDistal : HumanBodyBones.RightThumbDistal);
            BuildFingerHierarchy(hand,
                left ? HumanBodyBones.LeftIndexProximal : HumanBodyBones.RightIndexProximal,
                left ? HumanBodyBones.LeftIndexIntermediate : HumanBodyBones.RightIndexIntermediate,
                left ? HumanBodyBones.LeftIndexDistal : HumanBodyBones.RightIndexDistal);
            BuildFingerHierarchy(hand,
                left ? HumanBodyBones.LeftMiddleProximal : HumanBodyBones.RightMiddleProximal,
                left ? HumanBodyBones.LeftMiddleIntermediate : HumanBodyBones.RightMiddleIntermediate,
                left ? HumanBodyBones.LeftMiddleDistal : HumanBodyBones.RightMiddleDistal);
            BuildFingerHierarchy(hand,
                left ? HumanBodyBones.LeftRingProximal : HumanBodyBones.RightRingProximal,
                left ? HumanBodyBones.LeftRingIntermediate : HumanBodyBones.RightRingIntermediate,
                left ? HumanBodyBones.LeftRingDistal : HumanBodyBones.RightRingDistal);
            BuildFingerHierarchy(hand,
                left ? HumanBodyBones.LeftLittleProximal : HumanBodyBones.RightLittleProximal,
                left ? HumanBodyBones.LeftLittleIntermediate : HumanBodyBones.RightLittleIntermediate,
                left ? HumanBodyBones.LeftLittleDistal : HumanBodyBones.RightLittleDistal);
        }

        private void BuildFingerHierarchy(HumanBodyBones hand, HumanBodyBones proximal,
            HumanBodyBones intermediate, HumanBodyBones distal)
        {
            Reparent(proximal, hand);
            Reparent(intermediate, proximal);
            Reparent(distal, intermediate);
        }

        private void BuildLegHierarchy(bool left)
        {
            HumanBodyBones upper = left ? HumanBodyBones.LeftUpperLeg : HumanBodyBones.RightUpperLeg;
            HumanBodyBones lower = left ? HumanBodyBones.LeftLowerLeg : HumanBodyBones.RightLowerLeg;
            HumanBodyBones foot = left ? HumanBodyBones.LeftFoot : HumanBodyBones.RightFoot;
            HumanBodyBones toes = left ? HumanBodyBones.LeftToes : HumanBodyBones.RightToes;
            Reparent(upper, HumanBodyBones.Hips);
            Reparent(lower, upper);
            Reparent(foot, lower);
            Reparent(toes, foot);
        }

        private void Reparent(HumanBodyBones child, HumanBodyBones? parent)
        {
            if (!_humanBoneTransforms.TryGetValue(child, out Transform childTransform)) return;
            Transform parentTransform = Root.transform;
            if (parent.HasValue && _humanBoneTransforms.TryGetValue(parent.Value, out Transform mappedParent))
                parentTransform = mappedParent;
            childTransform.SetParent(parentTransform, true);
        }

        private HumanBodyBones FirstMapped(params HumanBodyBones[] candidates)
        {
            foreach (HumanBodyBones candidate in candidates)
                if (_humanBoneTransforms.ContainsKey(candidate)) return candidate;
            return candidates[candidates.Length - 1];
        }

        private static int GetHierarchyDepth(Transform transform)
        {
            int depth = 0;
            for (Transform current = transform.parent; current != null; current = current.parent) depth++;
            return depth;
        }

        private void ForceHumanoidReferencePose(PmxCoordinateConversion coordinates)
        {
            bool swapsHorizontalSides = coordinates == PmxCoordinateConversion.MmdToUnityAndRotateY180 ||
                                        coordinates == PmxCoordinateConversion.RotateY180;
            Vector3 left = swapsHorizontalSides ? Vector3.left : Vector3.right;
            Vector3 right = -left;

            AlignChain(HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm, left);
            AlignChain(HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand, left);

            AlignChain(HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm, right);
            AlignChain(HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand, right);
        }

        private void AlignChain(HumanBodyBones parentBone, HumanBodyBones childBone, Vector3 desiredDirection)
        {
            if (!_humanBoneTransforms.TryGetValue(parentBone, out Transform parent) ||
                !_humanBoneTransforms.TryGetValue(childBone, out Transform child)) return;
            Vector3 currentDirection = child.position - parent.position;
            if (currentDirection.sqrMagnitude < 1e-8f) return;
            Quaternion correction = Quaternion.FromToRotation(currentDirection.normalized, desiredDirection.normalized);
            parent.rotation = correction * parent.rotation;
        }

        private bool TryBuildUnityHumanoidReferenceSkeleton(HumanBone[] humanBones,
            out SkeletonBone[] skeleton)
        {
            skeleton = null;
            GameObject poseRoot = null;
            try
            {
                // ModelImporter runs AvatarSetupTool.MakePoseValid before it builds a
                // Humanoid Avatar. That pass is important for fingers: it straightens
                // the terminal phalanges and creates the thumb reference rotations.
                // BuildHumanAvatar does not perform that editor-side setup by itself.
                Type setupTool = typeof(UnityEditor.Editor).Assembly.GetType("UnityEditor.AvatarSetupTool");
                if (setupTool == null) return false;

                const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                MethodInfo getModelBones = setupTool.GetMethods(flags).FirstOrDefault(method =>
                    method.Name == "GetModelBones" && method.GetParameters().Length == 3);
                MethodInfo getHumanBones = setupTool.GetMethods(flags).FirstOrDefault(method =>
                {
                    if (method.Name != "GetHumanBones") return false;
                    ParameterInfo[] parameters = method.GetParameters();
                    return parameters.Length == 2 &&
                           parameters[0].ParameterType == typeof(Dictionary<string, string>);
                });
                MethodInfo makePoseValid = setupTool.GetMethods(flags).FirstOrDefault(method =>
                    method.Name == "MakePoseValid" && method.GetParameters().Length == 1);
                if (getModelBones == null || getHumanBones == null || makePoseValid == null)
                    return false;

                poseRoot = UnityEngine.Object.Instantiate(Root);
                poseRoot.name = Root.name;
                poseRoot.hideFlags = HideFlags.HideAndDontSave;

                object modelBones = getModelBones.Invoke(null,
                    new object[] { poseRoot.transform, false, null });
                var mapping = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (HumanBone humanBone in humanBones)
                    mapping[humanBone.humanName] = humanBone.boneName;
                object wrappers = getHumanBones.Invoke(null, new[] { (object)mapping, modelBones });
                if (wrappers == null) return false;

                makePoseValid.Invoke(null, new[] { wrappers });
                skeleton = BuildSkeletonBones(poseRoot.transform);
                return skeleton != null && skeleton.Length > 0;
            }
            catch (Exception)
            {
                skeleton = null;
                return false;
            }
            finally
            {
                if (poseRoot != null) UnityEngine.Object.DestroyImmediate(poseRoot);
            }
        }

        private int FindCandidate(string[] names)
        {
            foreach (string name in names)
            {
                int index = _model.FindBone(name);
                if (index >= 0) return index;
            }
            return -1;
        }

        private static SkeletonBone[] BuildSkeletonBones(Transform root)
        {
            Transform[] transforms = root.GetComponentsInChildren<Transform>(true);
            var result = new SkeletonBone[transforms.Length];
            for (int index = 0; index < transforms.Length; index++)
            {
                Transform transform = transforms[index];
                result[index] = new SkeletonBone
                {
                    name = transform.name,
                    position = transform.localPosition,
                    rotation = transform.localRotation,
                    scale = transform.localScale
                };
            }
            return result;
        }

        private static HumanBoneRule Rule(HumanBodyBones bone, params string[] candidates)
        {
            return new HumanBoneRule(bone, candidates);
        }
    }

    internal sealed class PmxTargetHumanoidSourceRig : IDisposable
    {
        private List<PmxTargetFkAnimationClipBaker.TargetBinding> _bindings;
        private Transform[] _cloneTargets;
        private PmxAnimationClipBakerOptions _sourceOptions;
        private HumanPoseHandler _poseHandler;
        private GameObject _cloneRoot;
        private int _rootMotionSourceBoneIndex = -1;
        private Vector3 _rootMotionSourceRestPosition;
        private float _rootMotionTargetScale = 1f;
        internal int BindingCount => _bindings != null ? _bindings.Count : 0;

        private PmxTargetHumanoidSourceRig()
        {
        }

        private void Build(PmxModel model, GameObject targetRoot,
            PmxAnimationClipBakerOptions options)
        {
            if (model == null) throw new ArgumentNullException(nameof(model));
            Animator animator = targetRoot != null
                ? targetRoot.GetComponent<Animator>() ?? targetRoot.GetComponentInChildren<Animator>(true)
                : null;
            if (animator == null || animator.avatar == null || !animator.avatar.isValid ||
                !animator.avatar.isHuman)
                throw new InvalidOperationException(
                    "Target-calibrated Humanoid output requires a valid Humanoid Animator.");

            _bindings = PmxTargetFkAnimationClipBaker.BuildBindings(
                model, animator.transform, animator);
            if (!_bindings.Any(binding => binding.HumanBone == HumanBodyBones.Hips))
                throw new InvalidOperationException(
                    "The PMX and target Avatar must both provide a Hips/lower-body mapping.");

            Dictionary<Transform, Transform> cloneMap = CloneHierarchy(
                animator, out _cloneRoot);
            _cloneTargets = new Transform[_bindings.Count];
            for (int index = 0; index < _bindings.Count; index++)
            {
                PmxTargetFkAnimationClipBaker.TargetBinding binding = _bindings[index];
                if (!cloneMap.TryGetValue(binding.Target, out Transform clone))
                    throw new InvalidOperationException(
                        "Could not reproduce the target Avatar skeleton path: " + binding.Path);
                _cloneTargets[index] = clone;
                binding.RootToParentVector = clone.parent != null
                    ? clone.parent.worldToLocalMatrix * _cloneRoot.transform.localToWorldMatrix
                    : Matrix4x4.identity;
                if (binding.HumanBone == HumanBodyBones.Hips)
                {
                    _rootMotionSourceBoneIndex = FindRootMotionSourceBone(
                        model, binding.SourceBoneIndex);
                    _rootMotionSourceRestPosition =
                        model.Bones[_rootMotionSourceBoneIndex].Position;
                    float sourceReferenceHeight = Mathf.Abs(
                        _rootMotionSourceRestPosition.y * options.PositionScale);
                    float targetReferenceHeight = Mathf.Abs(
                        clone.position.y - _cloneRoot.transform.position.y);
                    if (sourceReferenceHeight > 1e-5f && targetReferenceHeight > 1e-5f)
                        _rootMotionTargetScale = targetReferenceHeight / sourceReferenceHeight;
                }
            }
            _sourceOptions = new PmxAnimationClipBakerOptions
            {
                PositionScale = options.PositionScale,
                CoordinateConversion = PmxCoordinateConversion.RotateY180
            };
            _poseHandler = new HumanPoseHandler(animator.avatar, _cloneRoot.transform);
        }

        internal static PmxTargetHumanoidSourceRig Create(PmxModel model, GameObject targetRoot,
            PmxAnimationClipBakerOptions options)
        {
            var rig = new PmxTargetHumanoidSourceRig();
            try
            {
                rig.Build(model, targetRoot, options ?? new PmxAnimationClipBakerOptions());
                return rig;
            }
            catch
            {
                rig.Dispose();
                throw;
            }
        }

        internal void Apply(PmxHumanoidPoseSample sample)
        {
            if (sample == null) throw new ArgumentNullException(nameof(sample));
            if (_poseHandler == null) throw new ObjectDisposedException(nameof(PmxTargetHumanoidSourceRig));
            for (int index = 0; index < _bindings.Count; index++)
            {
                PmxTargetFkAnimationClipBaker.TargetBinding binding = _bindings[index];
                Transform target = _cloneTargets[index];
                bool suppressMmdWaistCancellation =
                    binding.HumanBone == HumanBodyBones.LeftUpperLeg ||
                    binding.HumanBone == HumanBodyBones.RightUpperLeg;
                target.localRotation = PmxTargetFkAnimationClipBaker.EvaluateLocalRotation(
                    sample, _bindings, index, _sourceOptions, suppressMmdWaistCancellation);
                if (binding.HumanBone == HumanBodyBones.Hips)
                    target.localPosition = PmxTargetFkAnimationClipBaker.EvaluateRootLocalPosition(
                        sample, binding, _rootMotionSourceBoneIndex,
                        _rootMotionSourceRestPosition, _sourceOptions, _rootMotionTargetScale);
            }
        }

        private static int FindRootMotionSourceBone(PmxModel model, int hipsSourceBoneIndex)
        {
            int current = hipsSourceBoneIndex;
            while (current >= 0 && current < model.Bones.Count)
            {
                PmxBone bone = model.Bones[current];
                if (string.Equals(bone.NameJapanese, "\u8170", StringComparison.Ordinal) ||
                    string.Equals(bone.NameEnglish, "waist", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(bone.NameEnglish, "hips", StringComparison.OrdinalIgnoreCase))
                    return current;
                current = bone.ParentIndex;
            }
            return hipsSourceBoneIndex;
        }

        internal void GetHumanPose(ref HumanPose pose)
        {
            if (_poseHandler == null) throw new ObjectDisposedException(nameof(PmxTargetHumanoidSourceRig));
            _poseHandler.GetHumanPose(ref pose);
        }

        public void Dispose()
        {
            if (_poseHandler != null) _poseHandler.Dispose();
            if (_cloneRoot != null) UnityEngine.Object.DestroyImmediate(_cloneRoot);
            _poseHandler = null;
            _cloneRoot = null;
        }

        private static Dictionary<Transform, Transform> CloneHierarchy(Animator animator,
            out GameObject cloneRoot)
        {
            Transform sourceRoot = animator.transform;
            cloneRoot = new GameObject(sourceRoot.name)
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            cloneRoot.transform.localPosition = Vector3.zero;
            cloneRoot.transform.localRotation = Quaternion.identity;
            cloneRoot.transform.localScale = Vector3.one;
            var result = new Dictionary<Transform, Transform>
            {
                [sourceRoot] = cloneRoot.transform
            };
            Dictionary<string, SkeletonBone> skeleton =
                PmxTargetFkAnimationClipBaker.BuildSkeletonLookup(animator.avatar);
            HashSet<Transform> required = BuildRequiredTransforms(animator, skeleton);
            CloneChildren(sourceRoot, cloneRoot.transform, skeleton, required, result);
            return result;
        }

        private static HashSet<Transform> BuildRequiredTransforms(Animator animator,
            IReadOnlyDictionary<string, SkeletonBone> skeleton)
        {
            Transform root = animator.transform;
            var required = new HashSet<Transform> { root };
            for (int index = 0; index < (int)HumanBodyBones.LastBone; index++)
                AddWithAncestors(animator.GetBoneTransform((HumanBodyBones)index), root, required);
            foreach (Transform transform in root.GetComponentsInChildren<Transform>(true))
                if (skeleton.ContainsKey(transform.name))
                    AddWithAncestors(transform, root, required);
            return required;
        }

        private static void AddWithAncestors(Transform transform, Transform root,
            ISet<Transform> required)
        {
            for (Transform current = transform; current != null; current = current.parent)
            {
                required.Add(current);
                if (current == root) break;
            }
        }

        private static void CloneChildren(Transform source, Transform cloneParent,
            IReadOnlyDictionary<string, SkeletonBone> skeleton,
            ISet<Transform> required,
            IDictionary<Transform, Transform> result)
        {
            for (int index = 0; index < source.childCount; index++)
            {
                Transform child = source.GetChild(index);
                if (!required.Contains(child)) continue;
                var cloneObject = new GameObject(child.name)
                {
                    hideFlags = HideFlags.HideAndDontSave
                };
                Transform clone = cloneObject.transform;
                clone.SetParent(cloneParent, false);
                if (skeleton.TryGetValue(child.name, out SkeletonBone rest))
                {
                    clone.localPosition = rest.position;
                    clone.localRotation = rest.rotation;
                    clone.localScale = rest.scale;
                }
                else
                {
                    clone.localPosition = child.localPosition;
                    clone.localRotation = child.localRotation;
                    clone.localScale = child.localScale;
                }
                result[child] = clone;
                CloneChildren(child, clone, skeleton, required, result);
            }
        }
    }

    internal static class PmxTargetHumanoidAnimationClipBaker
    {
        internal static PmxAnimationBakeReport Bake(
            PmxHumanoidAnimationClipBaker.PreparedMotion prepared,
            PmxAnimationClipBakerOptions options,
            PmxTargetHumanoidSourceRig rig,
            Action<float, string> progress = null)
        {
            if (prepared == null) throw new ArgumentNullException(nameof(prepared));
            if (rig == null) throw new ArgumentNullException(nameof(rig));
            options = options ?? new PmxAnimationClipBakerOptions();
            int sampleCount = prepared.Samples.Length;
            float[] sampleTimes = PmxHumanoidAnimationClipBaker.CreateSampleTimes(
                sampleCount, prepared.Duration, options.SampleRate);
            var muscles = new PmxHumanoidAnimationClipBaker.FloatCurveAccumulator[HumanTrait.MuscleCount];
            for (int index = 0; index < muscles.Length; index++)
                muscles[index] = new PmxHumanoidAnimationClipBaker.FloatCurveAccumulator(sampleTimes);
            var rootTx = new PmxHumanoidAnimationClipBaker.FloatCurveAccumulator(sampleTimes);
            var rootTy = new PmxHumanoidAnimationClipBaker.FloatCurveAccumulator(sampleTimes);
            var rootTz = new PmxHumanoidAnimationClipBaker.FloatCurveAccumulator(sampleTimes);
            var rootQx = new PmxHumanoidAnimationClipBaker.FloatCurveAccumulator(sampleTimes);
            var rootQy = new PmxHumanoidAnimationClipBaker.FloatCurveAccumulator(sampleTimes);
            var rootQz = new PmxHumanoidAnimationClipBaker.FloatCurveAccumulator(sampleTimes);
            var rootQw = new PmxHumanoidAnimationClipBaker.FloatCurveAccumulator(sampleTimes);
            var humanPose = new HumanPose();
            Quaternion previousBodyRotation = Quaternion.identity;
            bool hasPreviousBodyRotation = false;

            for (int sample = 0; sample < sampleCount; sample++)
            {
                float vmdFrame = PmxHumanoidAnimationClipBaker.CalculateVmdFrame(
                    sample, sampleCount, options.SampleRate,
                    prepared.Motion.MaximumFrame);
                rig.Apply(prepared.Samples[sample]);
                rig.GetHumanPose(ref humanPose);
                PmxHumanoidAnimationClipBaker.ValidateHumanPose(humanPose, vmdFrame);
                Vector3 bodyPosition = PmxHumanoidAnimationClipBaker.ConvertHumanoidRootPosition(
                    humanPose.bodyPosition, options.CoordinateConversion);
                Quaternion bodyRotation = PmxHumanoidAnimationClipBaker.ConvertHumanoidRootRotation(
                    humanPose.bodyRotation, options.CoordinateConversion);
                if (hasPreviousBodyRotation && Quaternion.Dot(previousBodyRotation, bodyRotation) < 0f)
                    bodyRotation = new Quaternion(
                        -bodyRotation.x, -bodyRotation.y, -bodyRotation.z, -bodyRotation.w);
                previousBodyRotation = bodyRotation;
                hasPreviousBodyRotation = true;

                rootTx.Add(bodyPosition.x);
                rootTy.Add(bodyPosition.y);
                rootTz.Add(bodyPosition.z);
                rootQx.Add(bodyRotation.x);
                rootQy.Add(bodyRotation.y);
                rootQz.Add(bodyRotation.z);
                rootQw.Add(bodyRotation.w);
                for (int muscle = 0; muscle < muscles.Length; muscle++)
                    muscles[muscle].Add(humanPose.muscles[muscle]);
                if ((sample & 15) == 0 || sample + 1 == sampleCount)
                    progress?.Invoke(0.8f * (sample + 1f) / Mathf.Max(1, sampleCount),
                        $"Calibrating Humanoid frame {sample + 1}/{sampleCount}");
            }

            var clip = new AnimationClip
            {
                name = string.IsNullOrEmpty(prepared.Motion.ModelName)
                    ? "MMD Target-calibrated Humanoid Motion"
                    : prepared.Motion.ModelName,
                frameRate = options.SampleRate,
                legacy = false
            };
            var accumulators = new PmxHumanoidAnimationClipBaker.FloatCurveAccumulator[7 + muscles.Length];
            accumulators[0] = rootTx;
            accumulators[1] = rootTy;
            accumulators[2] = rootTz;
            accumulators[3] = rootQx;
            accumulators[4] = rootQy;
            accumulators[5] = rootQz;
            accumulators[6] = rootQw;
            Array.Copy(muscles, 0, accumulators, 7, muscles.Length);
            var optimizedKeys = new Keyframe[accumulators.Length][];
            Parallel.For(0, accumulators.Length, index =>
            {
                bool keepConstant = index < 7;
                PmxHumanoidAnimationClipBaker.FloatCurveAccumulator accumulator = accumulators[index];
                if (!keepConstant && accumulator.IsApproximatelyZero()) return;
                optimizedKeys[index] = PmxAnimationClipBaker.CreateLinearKeys(
                    accumulator.ToArray(), options.CurveReductionError);
            });

            var properties = new string[optimizedKeys.Length];
            properties[0] = "RootT.x";
            properties[1] = "RootT.y";
            properties[2] = "RootT.z";
            properties[3] = "RootQ.x";
            properties[4] = "RootQ.y";
            properties[5] = "RootQ.z";
            properties[6] = "RootQ.w";
            for (int muscle = 0; muscle < muscles.Length; muscle++)
                properties[7 + muscle] =
                    PmxHumanoidAnimationClipBaker.GetAnimatorMusclePropertyName(muscle);
            PmxHumanoidAnimationClipBaker.WriteAnimatorCurves(clip, properties, optimizedKeys);
            clip.EnsureQuaternionContinuity();
            progress?.Invoke(0.95f, "Finalizing target-calibrated Humanoid animation");
            return new PmxAnimationBakeReport
            {
                Clip = clip,
                BoundBoneCount = rig.BindingCount,
                SampleCount = sampleCount
            };
        }

        internal static PmxAnimationBakeReport BakeToAsset(string pmxPath, string vmdPath,
            GameObject targetRoot, string outputAssetPath,
            PmxAnimationClipBakerOptions options = null, Action<float, string> progress = null)
        {
            if (targetRoot == null) throw new ArgumentNullException(nameof(targetRoot));
            PmxAnimationClipBaker.ValidateAssetPath(outputAssetPath);
            options = options ?? new PmxAnimationClipBakerOptions();
            PmxModel model = PmxModelCache.Load(pmxPath);
            VmdMotion motion = VmdParser.Parse(PmxAnimationClipBaker.ToPhysicalPath(vmdPath));
            PmxAnimationBakeReport report;
            using (var session = new PmxHumanoidAnimationClipBaker.BatchSession(model, options, false))
            {
                PmxHumanoidAnimationClipBaker.PreparedMotion prepared = session.Prepare(motion);
                report = session.BakeTargetHumanoid(prepared, targetRoot, progress);
            }
            if (options.BakeMorphs)
                report.BoundMorphCount = PmxAnimationClipBaker.BakeMorphCurves(
                    report.Clip, motion, targetRoot.transform);
            report.Clip.name = Path.GetFileNameWithoutExtension(outputAssetPath);
            progress?.Invoke(0.96f, "Writing target-calibrated Humanoid AnimationClip asset");
            AssetDatabase.DeleteAsset(outputAssetPath);
            AssetDatabase.CreateAsset(report.Clip, outputAssetPath);
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(outputAssetPath, ImportAssetOptions.ForceSynchronousImport);
            report.Clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(outputAssetPath);
            progress?.Invoke(1f, "Conversion complete");
            return report;
        }
    }

    [Serializable]
    public sealed class PmxTargetFkBakeOptions
    {
        // Quaternion curves are reduced as a unit and measured in degrees.
        public float RotationErrorDegrees = 0.01f;
        // Root translation is reduced as a Vector3 and measured in Unity units.
        public float PositionError = 0.00005f;
        public bool BakeRootTranslation = true;
    }

    public static class PmxTargetFkAnimationClipBaker
    {
        internal sealed class TargetBinding
        {
            internal HumanBodyBones HumanBone;
            internal int SourceBoneIndex;
            internal Transform Target;
            internal string Path;
            internal Quaternion RestLocalRotation;
            internal Quaternion RestWorldRotation;
            internal Quaternion RestParentWorldRotation;
            internal Vector3 RestLocalPosition;
            internal Matrix4x4 RootToParentVector;
            internal int ParentBindingIndex = -1;
        }

        private sealed class TargetCurveData
        {
            internal string Path;
            internal Keyframe[][] Rotation;
            internal Keyframe[][] Position;
        }

        internal static PmxAnimationBakeReport Bake(PmxModel model,
            PmxHumanoidAnimationClipBaker.PreparedMotion prepared, GameObject targetRoot,
            PmxAnimationClipBakerOptions options, PmxTargetFkBakeOptions fkOptions,
            Action<float, string> progress = null)
        {
            if (model == null) throw new ArgumentNullException(nameof(model));
            if (prepared == null) throw new ArgumentNullException(nameof(prepared));
            if (targetRoot == null) throw new ArgumentNullException(nameof(targetRoot));
            options = options ?? new PmxAnimationClipBakerOptions();
            fkOptions = fkOptions ?? new PmxTargetFkBakeOptions();

            Animator animator = targetRoot.GetComponent<Animator>() ??
                                targetRoot.GetComponentInChildren<Animator>(true);
            if (animator == null || animator.avatar == null || !animator.avatar.isValid ||
                !animator.avatar.isHuman)
                throw new InvalidOperationException(
                    "Target-model FK fidelity output requires a valid Humanoid Animator.");

            List<TargetBinding> bindings = BuildBindings(model, targetRoot.transform, animator);
            if (!bindings.Any(binding => binding.HumanBone == HumanBodyBones.Hips))
                throw new InvalidOperationException(
                    "The PMX and target Avatar must both provide a Hips/lower-body mapping.");

            int sampleCount = prepared.Samples.Length;
            var curveData = new TargetCurveData[bindings.Count];
            var sampleTimes = new float[sampleCount];
            for (int sampleIndex = 0; sampleIndex < sampleCount; sampleIndex++)
                sampleTimes[sampleIndex] = sampleIndex == sampleCount - 1
                    ? prepared.Duration
                    : sampleIndex / options.SampleRate;
            progress?.Invoke(0f, "Preparing target-model FK curves");
            Parallel.For(0, bindings.Count, bindingIndex =>
            {
                curveData[bindingIndex] = BuildCurves(
                    model, prepared, sampleTimes, bindings, bindingIndex, options, fkOptions);
            });

            var clip = new AnimationClip
            {
                name = string.IsNullOrEmpty(prepared.Motion.ModelName)
                    ? "MMD Target FK Motion"
                    : prepared.Motion.ModelName,
                frameRate = options.SampleRate,
                legacy = false
            };

            int animatedBoneCount = 0;
            for (int index = 0; index < curveData.Length; index++)
            {
                TargetCurveData data = curveData[index];
                if (data == null) continue;
                bool wrote = WriteVectorCurves(clip, data.Path, "m_LocalPosition", data.Position);
                wrote |= WriteQuaternionCurves(clip, data.Path, data.Rotation);
                if (wrote) animatedBoneCount++;
                progress?.Invoke(
                    0.75f + 0.18f * (index + 1f) / Mathf.Max(1, curveData.Length),
                    $"Writing target FK curves {index + 1}/{curveData.Length}");
            }

            var report = new PmxAnimationBakeReport
            {
                Clip = clip,
                BoundBoneCount = animatedBoneCount,
                SampleCount = sampleCount
            };
            if (options.BakeMorphs)
                report.BoundMorphCount = PmxAnimationClipBaker.BakeMorphCurves(
                    clip, prepared.Motion, targetRoot.transform);
            clip.EnsureQuaternionContinuity();
            progress?.Invoke(0.95f, "Finalizing target-model FK animation");
            return report;
        }

        public static PmxAnimationBakeReport BakeToAsset(string pmxPath, string vmdPath,
            GameObject targetRoot, string outputAssetPath,
            PmxAnimationClipBakerOptions options = null, PmxTargetFkBakeOptions fkOptions = null,
            Action<float, string> progress = null)
        {
            PmxAnimationClipBaker.ValidateAssetPath(outputAssetPath);
            options = options ?? new PmxAnimationClipBakerOptions();
            PmxModel model = PmxModelCache.Load(pmxPath);
            VmdMotion motion = VmdParser.Parse(PmxAnimationClipBaker.ToPhysicalPath(vmdPath));
            PmxAnimationBakeReport report;
            using (var session = new PmxHumanoidAnimationClipBaker.BatchSession(model, options, false))
            {
                PmxHumanoidAnimationClipBaker.PreparedMotion prepared = session.Prepare(motion);
                report = session.BakeTargetFk(prepared, targetRoot, fkOptions, progress);
            }
            report.Clip.name = Path.GetFileNameWithoutExtension(outputAssetPath);
            progress?.Invoke(0.96f, "Writing target FK AnimationClip asset");
            AssetDatabase.DeleteAsset(outputAssetPath);
            AssetDatabase.CreateAsset(report.Clip, outputAssetPath);
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(outputAssetPath, ImportAssetOptions.ForceSynchronousImport);
            report.Clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(outputAssetPath);
            progress?.Invoke(1f, "Conversion complete");
            return report;
        }

        internal static List<TargetBinding> BuildBindings(PmxModel model, Transform targetRoot,
            Animator animator)
        {
            Dictionary<string, SkeletonBone> skeleton = BuildSkeletonLookup(animator.avatar);
            var restWorldRotations = new Dictionary<Transform, Quaternion>();
            var result = new List<TargetBinding>();
            var usedSources = new HashSet<int>();
            var usedTargets = new HashSet<Transform>();

            foreach (PmxHumanoidSourceRig.HumanBoneRule rule in PmxHumanoidSourceRig.HumanBoneRules)
            {
                Transform target = animator.GetBoneTransform(rule.HumanBone);
                if (target == null || !target.IsChildOf(targetRoot) || !usedTargets.Add(target)) continue;
                int sourceIndex = FindSourceBone(model, rule.Candidates, usedSources);
                if (sourceIndex < 0) continue;
                usedSources.Add(sourceIndex);

                Quaternion restLocalRotation = skeleton.TryGetValue(target.name, out SkeletonBone restBone)
                    ? restBone.rotation
                    : target.localRotation;
                Vector3 restLocalPosition = skeleton.TryGetValue(target.name, out restBone)
                    ? restBone.position
                    : target.localPosition;
                Quaternion restWorldRotation = GetRestWorldRotation(
                    target, targetRoot, skeleton, restWorldRotations);
                Quaternion restParentWorldRotation = target.parent != null
                    ? GetRestWorldRotation(target.parent, targetRoot, skeleton, restWorldRotations)
                    : Quaternion.identity;
                Matrix4x4 rootToParent = target.parent != null
                    ? target.parent.worldToLocalMatrix * targetRoot.localToWorldMatrix
                    : Matrix4x4.identity;

                result.Add(new TargetBinding
                {
                    HumanBone = rule.HumanBone,
                    SourceBoneIndex = sourceIndex,
                    Target = target,
                    Path = AnimationUtility.CalculateTransformPath(target, targetRoot),
                    RestLocalRotation = PmxAnimationClipBaker.NormalizeSafe(restLocalRotation),
                    RestWorldRotation = PmxAnimationClipBaker.NormalizeSafe(restWorldRotation),
                    RestParentWorldRotation = PmxAnimationClipBaker.NormalizeSafe(restParentWorldRotation),
                    RestLocalPosition = restLocalPosition,
                    RootToParentVector = rootToParent
                });
            }

            var byTarget = new Dictionary<Transform, int>();
            for (int index = 0; index < result.Count; index++) byTarget[result[index].Target] = index;
            for (int index = 0; index < result.Count; index++)
            {
                for (Transform parent = result[index].Target.parent;
                     parent != null && parent != targetRoot;
                     parent = parent.parent)
                {
                    if (!byTarget.TryGetValue(parent, out int parentIndex)) continue;
                    result[index].ParentBindingIndex = parentIndex;
                    break;
                }
            }
            return result;
        }

        private static TargetCurveData BuildCurves(PmxModel model,
            PmxHumanoidAnimationClipBaker.PreparedMotion prepared,
            float[] times,
            IReadOnlyList<TargetBinding> bindings, int bindingIndex,
            PmxAnimationClipBakerOptions options, PmxTargetFkBakeOptions fkOptions)
        {
            TargetBinding binding = bindings[bindingIndex];
            int count = prepared.Samples.Length;
            var rotations = new Quaternion[count];
            Vector3[] positions = binding.HumanBone == HumanBodyBones.Hips && fkOptions.BakeRootTranslation
                ? new Vector3[count]
                : null;
            Quaternion previous = Quaternion.identity;
            bool hasPrevious = false;
            Vector3 sourceRestPosition = model.Bones[binding.SourceBoneIndex].Position;

            for (int sampleIndex = 0; sampleIndex < count; sampleIndex++)
            {
                PmxHumanoidPoseSample sample = prepared.Samples[sampleIndex];
                Quaternion local = EvaluateLocalRotation(sample, bindings, bindingIndex, options);
                if (hasPrevious && Quaternion.Dot(previous, local) < 0f)
                    local = new Quaternion(-local.x, -local.y, -local.z, -local.w);
                rotations[sampleIndex] = local;
                previous = local;
                hasPrevious = true;

                if (positions == null) continue;
                positions[sampleIndex] = EvaluateRootLocalPosition(
                    sample, binding, sourceRestPosition, options);
            }

            Keyframe[][] rotationCurves = BuildQuaternionCurves(
                times, rotations, binding.RestLocalRotation,
                Mathf.Max(0f, fkOptions.RotationErrorDegrees));
            Keyframe[][] positionCurves = positions != null
                ? BuildVectorCurves(times, positions, binding.RestLocalPosition,
                    Mathf.Max(0f, fkOptions.PositionError))
                : null;
            return rotationCurves == null && positionCurves == null
                ? null
                : new TargetCurveData
                {
                    Path = binding.Path,
                    Rotation = rotationCurves,
                    Position = positionCurves
                };
        }

        private static Keyframe[][] BuildQuaternionCurves(float[] times, Quaternion[] values,
            Quaternion rest, float maximumErrorDegrees)
        {
            if (values.Length == 0) return null;
            bool differsFromRest = false;
            bool changes = false;
            for (int index = 0; index < values.Length; index++)
            {
                differsFromRest |= Quaternion.Angle(rest, values[index]) > 0.00001f;
                if (index > 0) changes |= Quaternion.Angle(values[0], values[index]) > 0.00001f;
            }
            if (!differsFromRest) return null;
            int[] kept = changes
                ? ReduceQuaternionSamples(times, values, maximumErrorDegrees)
                : new[] { 0 };
            var result = new Keyframe[4][];
            for (int component = 0; component < 4; component++)
            {
                var keys = new Keyframe[kept.Length];
                for (int keyIndex = 0; keyIndex < kept.Length; keyIndex++)
                {
                    int source = kept[keyIndex];
                    Quaternion value = values[source];
                    float componentValue = component == 0 ? value.x :
                        component == 1 ? value.y : component == 2 ? value.z : value.w;
                    keys[keyIndex] = new Keyframe(times[source], componentValue);
                }
                result[component] = PmxAnimationClipBaker.CreateLinearKeys(keys);
            }
            return result;
        }

        private static Keyframe[][] BuildVectorCurves(float[] times, Vector3[] values,
            Vector3 rest, float maximumError)
        {
            if (values.Length == 0) return null;
            bool differsFromRest = false;
            bool changes = false;
            for (int index = 0; index < values.Length; index++)
            {
                differsFromRest |= (values[index] - rest).sqrMagnitude > 1e-14f;
                if (index > 0) changes |= (values[index] - values[0]).sqrMagnitude > 1e-14f;
            }
            if (!differsFromRest) return null;
            int[] kept = changes ? ReduceVectorSamples(times, values, maximumError) : new[] { 0 };
            var result = new Keyframe[3][];
            for (int component = 0; component < 3; component++)
            {
                var keys = new Keyframe[kept.Length];
                for (int keyIndex = 0; keyIndex < kept.Length; keyIndex++)
                {
                    int source = kept[keyIndex];
                    Vector3 value = values[source];
                    keys[keyIndex] = new Keyframe(
                        times[source], component == 0 ? value.x : component == 1 ? value.y : value.z);
                }
                result[component] = PmxAnimationClipBaker.CreateLinearKeys(keys);
            }
            return result;
        }

        private static int[] ReduceQuaternionSamples(float[] times, Quaternion[] values,
            float maximumErrorDegrees)
        {
            if (values.Length <= 2 || maximumErrorDegrees <= 0f)
                return Enumerable.Range(0, values.Length).ToArray();
            return ReduceSamples(times, values.Length, (start, end, index) =>
            {
                float factor = GetFactor(times, start, end, index);
                Quaternion expected = Quaternion.LerpUnclamped(values[start], values[end], factor);
                return Quaternion.Angle(expected, values[index]);
            }, maximumErrorDegrees);
        }

        private static int[] ReduceVectorSamples(float[] times, Vector3[] values, float maximumError)
        {
            if (values.Length <= 2 || maximumError <= 0f)
                return Enumerable.Range(0, values.Length).ToArray();
            return ReduceSamples(times, values.Length, (start, end, index) =>
            {
                float factor = GetFactor(times, start, end, index);
                return (Vector3.LerpUnclamped(values[start], values[end], factor) - values[index]).magnitude;
            }, maximumError);
        }

        private static int[] ReduceSamples(float[] times, int count,
            Func<int, int, int, float> getError, float maximumError)
        {
            var keep = new bool[count];
            keep[0] = true;
            keep[count - 1] = true;
            var ranges = new Stack<int>();
            ranges.Push(0);
            ranges.Push(count - 1);
            while (ranges.Count > 0)
            {
                int end = ranges.Pop();
                int start = ranges.Pop();
                float largest = -1f;
                int split = -1;
                for (int index = start + 1; index < end; index++)
                {
                    float error = getError(start, end, index);
                    if (error <= largest) continue;
                    largest = error;
                    split = index;
                }
                if (split < 0 || largest <= maximumError) continue;
                keep[split] = true;
                ranges.Push(start);
                ranges.Push(split);
                ranges.Push(split);
                ranges.Push(end);
            }
            var result = new List<int>();
            for (int index = 0; index < count; index++) if (keep[index]) result.Add(index);
            return result.ToArray();
        }

        private static float GetFactor(float[] times, int start, int end, int index)
        {
            float duration = times[end] - times[start];
            return Mathf.Abs(duration) > 1e-12f ? (times[index] - times[start]) / duration : 0f;
        }

        private static bool WriteQuaternionCurves(AnimationClip clip, string path, Keyframe[][] curves)
        {
            if (curves == null) return false;
            string[] properties =
            {
                "m_LocalRotation.x", "m_LocalRotation.y", "m_LocalRotation.z", "m_LocalRotation.w"
            };
            for (int index = 0; index < properties.Length; index++)
                AnimationUtility.SetEditorCurve(clip,
                    EditorCurveBinding.FloatCurve(path, typeof(Transform), properties[index]),
                    new AnimationCurve(curves[index]));
            return true;
        }

        private static bool WriteVectorCurves(AnimationClip clip, string path, string property,
            Keyframe[][] curves)
        {
            if (curves == null) return false;
            for (int index = 0; index < 3; index++)
                AnimationUtility.SetEditorCurve(clip,
                    EditorCurveBinding.FloatCurve(
                        path, typeof(Transform), property + "." + (index == 0 ? "x" : index == 1 ? "y" : "z")),
                    new AnimationCurve(curves[index]));
            return true;
        }

        internal static Quaternion EvaluateLocalRotation(PmxHumanoidPoseSample sample,
            IReadOnlyList<TargetBinding> bindings, int bindingIndex,
            PmxAnimationClipBakerOptions options,
            bool suppressUnmappedParentRotation = false)
        {
            TargetBinding binding = bindings[bindingIndex];
            Quaternion sourceWorld = PmxAnimationClipBaker.ConvertRotation(
                sample.WorldRotations[binding.SourceBoneIndex], options);
            Quaternion sourceParentWorld = Quaternion.identity;
            if (binding.ParentBindingIndex >= 0)
            {
                TargetBinding parentBinding = bindings[binding.ParentBindingIndex];
                sourceParentWorld = PmxAnimationClipBaker.ConvertRotation(
                    sample.WorldRotations[parentBinding.SourceBoneIndex], options);
            }
            if (suppressUnmappedParentRotation && binding.ParentBindingIndex >= 0)
            {
                // Standard MMD waist rigs place a helper with -1 waist rotation
                // between LowerBody and each upper leg. Reproducing that helper in
                // Humanoid requires roughly 180 degrees of counter-twist in each hip
                // during a turn, which lies far outside the muscle range. Preserve the
                // leg's own FK rotation but let it inherit the Humanoid root rotation.
                Quaternion sourceLocal = PmxAnimationClipBaker.ConvertRotation(
                    sample.LocalRotations[binding.SourceBoneIndex], options);
                sourceWorld = PmxAnimationClipBaker.NormalizeSafe(
                    sourceParentWorld * sourceLocal);
            }
            // PMX rotations are evaluated in the source model/parent basis. Preserve
            // that world-space motion on the target reference axes, then derive the
            // local value against the animated mapped ancestor. Directly appending the
            // relative PMX quaternion to RestLocalRotation rotates around the target's
            // FBX-local axes instead, which is especially wrong for rigs whose hips and
            // legs have 90/180-degree reference rotations.
            Quaternion desiredWorld = PmxAnimationClipBaker.NormalizeSafe(
                sourceWorld * binding.RestWorldRotation);
            Quaternion desiredParentWorld = binding.RestParentWorldRotation;
            if (binding.ParentBindingIndex >= 0)
                desiredParentWorld = PmxAnimationClipBaker.NormalizeSafe(
                    sourceParentWorld * binding.RestParentWorldRotation);
            return PmxAnimationClipBaker.NormalizeSafe(
                Quaternion.Inverse(desiredParentWorld) * desiredWorld);
        }

        internal static Vector3 EvaluateRootLocalPosition(PmxHumanoidPoseSample sample,
            TargetBinding binding, Vector3 sourceRestPosition,
            PmxAnimationClipBakerOptions options)
        {
            return EvaluateRootLocalPosition(sample, binding, binding.SourceBoneIndex,
                sourceRestPosition, options, 1f);
        }

        internal static Vector3 EvaluateRootLocalPosition(PmxHumanoidPoseSample sample,
            TargetBinding binding, int sourceBoneIndex, Vector3 sourceRestPosition,
            PmxAnimationClipBakerOptions options, float targetScale)
        {
            Vector3 sourceDisplacement =
                sample.WorldPositions[sourceBoneIndex] - sourceRestPosition;
            Vector3 rootDisplacement = PmxAnimationClipBaker.ConvertPosition(
                sourceDisplacement, options) * (options.PositionScale * targetScale);
            return binding.RestLocalPosition +
                   binding.RootToParentVector.MultiplyVector(rootDisplacement);
        }

        internal static int FindSourceBone(PmxModel model, IEnumerable<string> candidates,
            ISet<int> usedSources)
        {
            foreach (string candidate in candidates)
            {
                int index = model.FindBone(candidate);
                if (index >= 0 && (usedSources == null || !usedSources.Contains(index))) return index;
            }
            return -1;
        }

        internal static Dictionary<string, SkeletonBone> BuildSkeletonLookup(Avatar avatar)
        {
            var result = new Dictionary<string, SkeletonBone>(StringComparer.Ordinal);
            try
            {
                foreach (SkeletonBone bone in avatar.humanDescription.skeleton)
                    if (!string.IsNullOrEmpty(bone.name) && !result.ContainsKey(bone.name))
                        result.Add(bone.name, bone);
            }
            catch (Exception)
            {
                // Scene-local transforms remain a valid fallback for generated or unusual Avatars.
            }
            return result;
        }

        private static Quaternion GetRestWorldRotation(Transform transform, Transform targetRoot,
            IReadOnlyDictionary<string, SkeletonBone> skeleton,
            IDictionary<Transform, Quaternion> cache)
        {
            if (transform == null || transform == targetRoot) return Quaternion.identity;
            if (cache.TryGetValue(transform, out Quaternion cached)) return cached;
            Quaternion parent = GetRestWorldRotation(transform.parent, targetRoot, skeleton, cache);
            Quaternion local = skeleton.TryGetValue(transform.name, out SkeletonBone bone)
                ? bone.rotation
                : transform.localRotation;
            Quaternion result = PmxAnimationClipBaker.NormalizeSafe(parent * local);
            cache[transform] = result;
            return result;
        }
    }
}
