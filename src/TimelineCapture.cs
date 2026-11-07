using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Xml;
using Studio;
using Timeline;
using UnityEngine;
using Native = Timeline.Timeline;
using Frame = Timeline.Keyframe;

namespace GinIkThis
{
    // Standard Timeline models and serialization, as used by Creatures IK.
    // Observes complete engine frames and rejects edits made during capture.
    internal sealed partial class NativeTimeline
    {
        sealed class CapturedBake
        {
            internal List<Channel> Channels;
            internal float[] Times;
            internal object[,] Samples;
            internal List<DriverCheck.Transfer> Transfers;
        }

        sealed class SourceTrack
        {
            readonly Interpolable track;
            readonly bool enabled;
            readonly SortedList<float, Frame> frames;
            readonly List<object> values = new List<object>();
            readonly List<UnityEngine.Keyframe[]> curves = new List<UnityEngine.Keyframe[]>();

            internal SourceTrack(Interpolable track)
            {
                this.track = track;
                enabled = track.enabled;
                frames = new SortedList<float, Frame>(track.keyframes);
                foreach (Frame frame in frames.Values)
                {
                    values.Add(frame.value);
                    curves.Add(frame.curve.keys);
                }
            }

            internal bool Unchanged(Dictionary<int, Interpolable> current)
            {
                Interpolable existing;
                if (!current.TryGetValue(track.GetHashCode(), out existing) || !ReferenceEquals(track, existing) ||
                    track.enabled != enabled || track.keyframes.Count != frames.Count) return false;
                for (int i = 0; i < frames.Count; i++)
                {
                    Frame frame;
                    if (!track.keyframes.TryGetValue(frames.Keys[i], out frame) || !ReferenceEquals(frame, frames.Values[i]) ||
                        !System.Object.Equals(frame.value, values[i])) return false;
                    UnityEngine.Keyframe[] keys = frame.curve.keys;
                    if (keys.Length != curves[i].Length) return false;
                    for (int k = 0; k < keys.Length; k++)
                        if (!keys[k].Equals(curves[i][k])) return false;
                }
                return true;
            }
        }

        void Seek(float time)
        {
            seek.Invoke(Instance, new object[] { time });
            interpolate.Invoke(Instance, new object[] { true });
            interpolate.Invoke(Instance, new object[] { false });
        }

        internal override IEnumerator BakeObserved(Plugin plugin, List<Rig> rigs, float start, float end, int fps,
            bool overwrite, bool reduce, Action<int> completed)
        {
            List<Channel> channels = ValidateBake(plugin, rigs, start, end, fps, overwrite, true);
            if (seek == null || interpolate == null)
                throw new NotSupportedException("このTimelineで再生位置を評価できません。");
            CapturedBake capture = new CapturedBake
            {
                Channels = channels, Times = SampleTimes(start, end, fps), Transfers = plugin.PrepareNodeTransfers(rigs)
            };
            capture.Samples = new object[capture.Times.Length, channels.Count];
            List<SourceTrack> source = new List<SourceTrack>();
            foreach (Interpolable track in Tracks.Values) source.Add(new SourceTrack(track));
            List<GuidePose> original = new List<GuidePose>();
            HashSet<GuideObject> saved = new HashSet<GuideObject>();
            foreach (Rig rig in rigs)
            {
                if (saved.Add(rig.Item.guideObject)) original.Add(new GuidePose(rig.Item.guideObject));
                foreach (OCIChar.BoneInfo bone in rig.Item.listBones)
                    if (Rig.HasGuide(bone) && saved.Add(bone.guideObject)) original.Add(new GuidePose(bone.guideObject));
                foreach (OCIFolder control in rig.Controls)
                    if (saved.Add(control.guideObject)) original.Add(new GuidePose(control.guideObject));
            }
            float cursor = Time;
            int generation = plugin.SceneGeneration;
            bool committed = false;
            try
            {
                for (int i = 0; i < capture.Times.Length; i++)
                {
                    CheckCapture(plugin, rigs, generation);
                    foreach (GuidePose pose in original) pose.ResetAmounts();
                    Seek(capture.Times[i]);
                    // Let animation, expressions, constraints and the normal
                    // IK LateUpdate run before recording the displayed pose.
                    yield return null;
                    yield return new WaitForEndOfFrame();
                    CheckCapture(plugin, rigs, generation);
                    if (Playing || !Mathf.Approximately(Time, capture.Times[i]))
                        throw new InvalidOperationException("再生位置が変更されたため、焼き込みを中止しました。");
                    for (int c = 0; c < channels.Count; c++)
                    {
                        object value = Sample(channels[c]);
                        if (channels[c].Rotation ? !TwoBone.Finite((Quaternion)value) : !TwoBone.Finite((Vector3)value))
                            throw new InvalidOperationException("記録できない骨の変換があります · " + channels[c].Guide.transformTarget.name);
                        capture.Samples[i, c] = value;
                    }
                    plugin.Message = "焼き込み中 · " + (i + 1) + " / " + capture.Times.Length;
                }
                foreach (GuidePose pose in original) pose.ResetAmounts();
                Seek(cursor);
                yield return null;
                yield return new WaitForEndOfFrame();
                CheckCapture(plugin, rigs, generation);
                if (Tracks.Count != source.Count ||
                    !source.TrueForAll(delegate(SourceTrack track) { return track.Unchanged(Tracks); }))
                    throw new InvalidOperationException("Timelineのキーやトラックが変更されたため、焼き込みを中止しました。");
                int count = BakeRigs(plugin, rigs, start, end, fps, overwrite, reduce, capture);
                committed = true;
                completed(count);
            }
            finally
            {
                if (!committed && plugin.SceneGeneration == generation && Ready)
                {
                    Native.Pause();
                    foreach (GuidePose pose in original) pose.ResetAmounts();
                    Seek(cursor);
                    foreach (GuidePose pose in original) pose.RestoreTransform();
                }
            }
        }

        static void CheckCapture(Plugin plugin, List<Rig> rigs, int generation)
        {
            if (plugin.CancelBake || plugin.SceneGeneration != generation)
                throw new OperationCanceledException("焼き込みを中止しました。");
            foreach (Rig rig in rigs)
                if (!rig.Enabled || !rig.Valid || !plugin.Rigs.Contains(rig))
                    throw new InvalidOperationException(rig.Map.Name + "のIKが変更されたため、焼き込みを中止しました。");
        }

        List<Channel> ValidateBake(Plugin plugin, List<Rig> rigs, float start, float end, int fps, bool overwrite, bool observed)
        {
            if (!Ready || Playing) throw new InvalidOperationException("Timelineを停止してから焼き込んでください。");
            if (!TwoBone.Finite(start) || !TwoBone.Finite(end) || start < 0 || end <= start || end > Duration || fps != 30 && fps != 60)
                throw new ArgumentException("Timelineの範囲内で開始・終了秒と30／60 fpsを指定してください。");
            if (rigs.Count == 0) throw new InvalidOperationException("有効なIK部位がありません。");
            List<Channel> channels = new List<Channel>();
            foreach (Rig rig in rigs)
            {
                plugin.CheckBake(rig, observed);
                channels.AddRange(Outputs(rig));
            }
            foreach (Channel channel in channels)
            {
                if (channel.KkpePosition)
                    new KkpePositionState(channel, Parameter(channel, Model(ModelOwner(channel), Id(channel))));
                Interpolable track = Find(channel);
                if (track == null) continue;
                if (!track.enabled && track.keyframes.Count > 0)
                    throw new InvalidOperationException("焼き込み先の無効トラックをTimelineで確認してください。");
                foreach (float key in track.keyframes.Keys)
                    if (!overwrite && InRange(key, start, end))
                        throw new InvalidOperationException(channel.Owner.treeNodeObject.textName + " · " + track.name +
                            " に既存の標準キーがあります。置き換える場合は「上書き」を選んでください。");
            }
            double count = Math.Ceiling(((double)end - start) * fps - .0001) + 1;
            if (count > 7201 || count * channels.Count > 180000)
                throw new ArgumentException("区間を短くしてください。上限は7,201サンプル、18万キーです。");
            return channels;
        }

        static float[] SampleTimes(float start, float end, int fps)
        {
            double count = Math.Ceiling(((double)end - start) * fps - .0001) + 1;
            float[] times = new float[Math.Max(2, (int)count)];
            for (int i = 0; i < times.Length; i++)
            {
                times[i] = i == times.Length - 1 ? end : (float)((double)start + (double)i / fps);
                if (i > 0 && (times[i] <= times[i - 1] || Mathf.Approximately(times[i], times[i - 1])))
                    throw new InvalidOperationException("サンプルを区別する時間精度が不足しています。");
            }
            return times;
        }

    }
}
