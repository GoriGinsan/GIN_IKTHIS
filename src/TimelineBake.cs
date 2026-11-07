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
    // Validates native output channels, samples poses and commits them atomically.
    internal sealed partial class NativeTimeline
    {
        static bool InRange(float time, float start, float end)
        {
            return time >= start && time <= end || Mathf.Approximately(time, start) || Mathf.Approximately(time, end);
        }

        static IEnumerable<Channel> Outputs(Rig rig)
        {
            foreach (OCIChar.BoneInfo bone in rig.Bones)
            {
                CheckNativePath(rig.Item, bone.guideObject);
                yield return new Channel(rig.Item, bone.guideObject, true);
            }
            if (rig.Map.Waist) yield return new Channel(rig.Item, rig.WaistPositionGuide, false);
            if (rig.Map.MoveRoot)
            {
                GuideObject root = rig.Map.Root.guideObject;
                yield return new Channel(rig.Item, root, false, !root.enablePos);
            }
        }

        internal override bool HasBakedTracks(Rig rig)
        {
            HashSet<float> commonTimes = null;
            foreach (Channel channel in Outputs(rig))
            {
                Interpolable track = Find(channel);
                if (track == null || !track.enabled || track.keyframes.Count < 2) return false;
                if (!rig.LegacyDisabled) continue;
                if (commonTimes == null) commonTimes = new HashSet<float>(track.keyframes.Keys);
                else commonTimes.IntersectWith(track.keyframes.Keys);
                if (commonTimes.Count < 2) return false;
            }
            return true;
        }

        internal override int BakeRigs(Plugin plugin, List<Rig> rigs, float start, float end, int fps, bool overwrite, bool reduce)
        {
            return BakeRigs(plugin, rigs, start, end, fps, overwrite, reduce, null);
        }

        int BakeRigs(Plugin plugin, List<Rig> rigs, float start, float end, int fps, bool overwrite, bool reduce, CapturedBake capture)
        {
            List<Channel> channels = ValidateBake(plugin, rigs, start, end, fps, overwrite, capture != null);
            List<KkpePositionState> kkpeStates = new List<KkpePositionState>();
            HashSet<ObjectCtrlInfo> owners = new HashSet<ObjectCtrlInfo>();
            foreach (Rig rig in rigs)
            {
                for (ObjectCtrlInfo current = rig.Item; current != null; current = current.parentInfo) owners.Add(current);
                foreach (OCIFolder control in rig.Controls) owners.Add(control);
            }
            foreach (Channel channel in channels)
                if (channel.KkpePosition)
                    kkpeStates.Add(new KkpePositionState(channel, Parameter(channel, Model(ModelOwner(channel), Id(channel)))));
            float[] times = SampleTimes(start, end, fps);
            object[,] samples = new object[times.Length, channels.Count];
            if (capture != null)
            {
                if (capture.Channels.Count != channels.Count || capture.Times.Length != times.Length)
                    throw new InvalidOperationException("焼き込みの対象が変更されました。");
                for (int c = 0; c < channels.Count; c++)
                    if (channels[c].Owner != capture.Channels[c].Owner || channels[c].Guide != capture.Channels[c].Guide ||
                        channels[c].Rotation != capture.Channels[c].Rotation || channels[c].KkpePosition != capture.Channels[c].KkpePosition)
                        throw new InvalidOperationException("焼き込みの骨が変更されました。");
                samples = capture.Samples;
            }
            List<Interpolable> drivers = new List<Interpolable>();
            Dictionary<Interpolable, Transform> positionDrivers = new Dictionary<Interpolable, Transform>();
            Dictionary<Transform, Vector3> restorePositions = new Dictionary<Transform, Vector3>();
            Dictionary<GuideObject, GuidePose> restore = new Dictionary<GuideObject, GuidePose>();
            foreach (ObjectCtrlInfo owner in owners)
            {
                if (owner.guideObject != null) restore[owner.guideObject] = new GuidePose(owner.guideObject);
                OCIItem item = owner as OCIItem;
                if (!Rig.IsItem(item)) continue;
                foreach (OCIChar.BoneInfo bone in item.listBones)
                    if (Rig.HasGuide(bone)) restore[bone.guideObject] = new GuidePose(bone.guideObject);
            }
            foreach (Interpolable track in Tracks.Values)
            {
                if (track.enabled && owners.Contains(track.oci) && track.owner == "KKPE" && track.id == "bonePos")
                {
                    Transform bone = KkpeBone(track);
                    Behaviour controller = KkpeController(track.oci, track.parameter);
                    if (bone != null && controller != null && controller.enabled)
                    {
                        positionDrivers.Add(track, bone);
                        restorePositions[bone] = bone.localPosition;
                    }
                }
                GuideObject guide = track.parameter as GuideObject;
                if (track.enabled && owners.Contains(track.oci) && guide != null && track.owner == "Timeline" &&
                    (track.id == "guideObjectPos" || track.id == "guideObjectRot" || track.id == "guideObjectScale"))
                {
                    drivers.Add(track);
                    if (!restore.ContainsKey(guide)) restore.Add(guide, new GuidePose(guide));
                }
            }
            List<ObjectCtrlInfo> ordered = new List<ObjectCtrlInfo>(owners);
            ordered.Sort(delegate(ObjectCtrlInfo a, ObjectCtrlInfo b) { return Depth(a).CompareTo(Depth(b)); });
            try
            {
                for (int i = 0; capture == null && i < times.Length; i++)
                {
                    float time = times[i];
                    foreach (GuidePose state in restore.Values) state.ResetAmounts();
                    foreach (Interpolable track in drivers) Evaluate(track, time);
                    ApplyNativePose(ordered);
                    foreach (KeyValuePair<Interpolable, Transform> driver in positionDrivers)
                    {
                        Frame left, right;
                        float factor;
                        if (Bracket(driver.Key, time, out left, out right, out factor))
                            driver.Value.localPosition = Vector3.LerpUnclamped((Vector3)left.value, (Vector3)right.value, factor);
                    }
                    foreach (Rig rig in rigs) rig.Solve();
                    for (int c = 0; c < channels.Count; c++)
                    {
                        Channel channel = channels[c];
                        samples[i, c] = Sample(channel);
                    }
                }
            }
            finally
            {
                foreach (GuidePose state in restore.Values) state.ResetAmounts();
                foreach (GuidePose state in restore.Values) state.RestoreTransform();
                foreach (KeyValuePair<Transform, Vector3> state in restorePositions) state.Key.localPosition = state.Value;
            }
            int keyCount = 0;
            try
            {
                Transaction(delegate(List<UndoTrack> undo)
                {
                    for (int c = 0; c < channels.Count; c++)
                    {
                        Interpolable track = Ensure(channels[c], undo);
                        List<float> erase = new List<float>();
                        foreach (float key in track.keyframes.Keys)
                            if (InRange(key, start, end)) erase.Add(key);
                        if (erase.Count > 0)
                        {
                            if (!overwrite) throw new InvalidOperationException("標準キーが変更されたため焼き込みを中止しました。");
                            if (clearSelection != null)
                                clearSelection.Invoke(Instance, new object[] { new KeyValuePair<float, Frame>[0] });
                        }
                        foreach (float key in erase) track.keyframes.Remove(key);
                        bool[] keep = reduce ? BakeKeyReduction.Select(times, samples, c, channels[c].Rotation, plugin.ReductionQuality) : null;
                        for (int i = 0; i < times.Length; i++)
                        {
                            if (keep != null && !keep[i]) continue;
                            track.keyframes.Add(times[i], new Frame(samples[i, c], track, AnimationCurve.Linear(0, 0, 1, 1)));
                            keyCount++;
                        }
                        track.enabled = true;
                    }
                    // Apply only the freshly baked tracks at the current cursor.
                    foreach (Channel channel in channels)
                        if (!channel.KkpePosition) Evaluate(Find(channel), Time);
                    ApplyNativePose(ordered);
                    foreach (Channel channel in channels)
                        if (channel.KkpePosition) Evaluate(Find(channel), Time);
                    foreach (KkpePositionState state in kkpeStates) state.Apply();
                    if (capture != null)
                        foreach (DriverCheck.Transfer transfer in capture.Transfers) transfer.Apply();
                });
            }
            catch
            {
                if (capture != null)
                    foreach (DriverCheck.Transfer transfer in capture.Transfers) transfer.Restore();
                foreach (KkpePositionState state in kkpeStates) state.Restore();
                foreach (GuidePose state in restore.Values) state.ResetAmounts();
                foreach (GuidePose state in restore.Values) state.RestoreTransform();
                throw;
            }
            foreach (Rig rig in rigs) { rig.ReleaseRoot(true); rig.Enabled = false; rig.Baked = true; }
            if (capture != null)
                foreach (DriverCheck.Transfer transfer in capture.Transfers) transfer.Rig.NodeLinks++;
            plugin.RecordBakeSize(times.Length * channels.Count, keyCount, reduce);
            return keyCount;
        }

        static void ApplyNativePose(List<ObjectCtrlInfo> ordered)
        {
            foreach (ObjectCtrlInfo owner in ordered)
            {
                owner.guideObject.ForceUpdate();
                OCIItem item = owner as OCIItem;
                if (Rig.IsItem(item) && item.itemInfo.enableFK) Plugin.ApplyFK(item);
            }
        }

        static object Sample(Channel channel)
        {
            GuideObject guide = channel.Guide;
            Transform t = guide.transformTarget;
            if (channel.KkpePosition) return t.localPosition;
            Transform parent = guide.nonconnect ? guide.parent : null;
            if (channel.Rotation)
                return parent == null ? t.localRotation : Quaternion.Inverse(parent.rotation) * t.rotation;
            return parent == null ? t.localPosition : parent.InverseTransformPoint(t.position);
        }

        static void CheckNativePath(OCIItem item, GuideObject guide)
        {
            Transform itemRoot = item.guideObject.transformTarget;
            for (Transform t = guide.transformTarget; t != itemRoot; t = t.parent)
            {
                if (t == null || t.parent == null || t.name.IndexOf('/') >= 0 || t.parent.Find(t.name) != t)
                    throw new InvalidOperationException("Timelineの標準パスで特定できない骨があります · " + guide.transformTarget.name);
                int named = 0;
                foreach (Transform sibling in t.parent) if (sibling.name == t.name) named++;
                if (named > 1) throw new InvalidOperationException("標準パスに同名の兄弟があるため焼き込めません · " + t.name);
            }
        }

        static int Depth(ObjectCtrlInfo owner)
        {
            int count = 0;
            for (; owner != null; owner = owner.parentInfo) count++;
            return count;
        }

    }
}
