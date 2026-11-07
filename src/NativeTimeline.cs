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
    internal sealed partial class NativeTimeline : TimelineAccess
    {
        const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        readonly Type type = typeof(Native);
        readonly FieldInfo self, tracks, tree, models;
        readonly MethodInfo remove, refresh, clearSelection;
        readonly MethodInfo seek, interpolate;

        internal NativeTimeline()
        {
            self = type.GetField("_self", Flags);
            tracks = type.GetField("_interpolables", Flags);
            tree = type.GetField("_interpolablesTree", Flags);
            models = type.GetField("_interpolableModelsList", Flags);
            remove = type.GetMethod("RemoveInterpolable", Flags, null, new[] { typeof(Interpolable) }, null);
            refresh = type.GetMethod("UpdateInterpolablesView", Flags);
            clearSelection = type.GetMethod("SelectKeyframes", Flags, null, new[] { typeof(IEnumerable<KeyValuePair<float, Frame>>) }, null);
            seek = type.GetMethod("SeekPlaybackTime", Flags, null, new[] { typeof(float) }, null);
            interpolate = type.GetMethod("Interpolate", Flags, null, new[] { typeof(bool) }, null);
            if (self == null || tracks == null || tree == null || models == null || remove == null || refresh == null)
                throw new NotSupportedException("このTimelineの標準トラックには接続できません。");
        }

        object Instance { get { return self.GetValue(null); } }
        Dictionary<int, Interpolable> Tracks { get { return (Dictionary<int, Interpolable>)tracks.GetValue(Instance); } }
        internal override bool Ready { get { return Instance != null && Model("guideObjectRot") != null && Model("guideObjectPos") != null; } }
        internal override float Time { get { return Native.playbackTime; } }
        internal override float Duration { get { return Native.duration; } }
        internal override bool Playing { get { return Native.isPlaying; } }

        InterpolableModel Model(string id)
        {
            return Model("Timeline", id);
        }

        InterpolableModel Model(string owner, string id)
        {
            foreach (InterpolableModel model in (IEnumerable)models.GetValue(Instance))
                if (model.owner == owner && model.id == id) return model;
            return null;
        }

        static string Id(Channel channel) { return channel.KkpePosition ? "bonePos" : channel.Rotation ? "guideObjectRot" : "guideObjectPos"; }
        static string ModelOwner(Channel channel) { return channel.KkpePosition ? "KKPE" : "Timeline"; }

        static Transform KkpeBone(Interpolable track) { return KkpeBone(track.parameter); }

        static Transform KkpeBone(object parameter)
        {
            FieldInfo value = parameter == null ? null : parameter.GetType().GetField("value", Flags);
            return value == null ? null : value.GetValue(parameter) as Transform;
        }

        static Behaviour KkpeController(ObjectCtrlInfo owner, object parameter)
        {
            Type poseType = parameter.GetType().GetField("key", Flags).FieldType.Assembly.GetType("HSPE.PoseController", true);
            return owner.guideObject.transformTarget.GetComponent(poseType) as Behaviour;
        }

        static object Parameter(Channel channel, InterpolableModel model)
        {
            if (!channel.KkpePosition) return channel.Guide;
            string error = "起点の位置を焼き込むにはKKPEのBone Positionが必要です · " + channel.Guide.transformTarget.name;
            if (model == null || model.readParameterFromXml == null || !model.IsCompatibleWithTarget(channel.Owner))
                throw new InvalidOperationException(error);
            Transform root = channel.Owner.guideObject.transformTarget;
            List<string> names = new List<string>();
            for (Transform bone = channel.Guide.transformTarget; bone != root; bone = bone.parent)
                names.Add(bone.name);
            names.Reverse();
            XmlElement row = new XmlDocument().CreateElement("interpolable");
            row.SetAttribute("parameter", String.Join("/", names.ToArray()));
            object parameter = model.readParameterFromXml(channel.Owner, row);
            if (KkpeBone(parameter) != channel.Guide.transformTarget) throw new InvalidOperationException(error);
            return parameter;
        }

        Interpolable Find(Channel channel)
        {
            foreach (Interpolable track in Tracks.Values)
                if (track.oci == channel.Owner && track.owner == ModelOwner(channel) && track.id == Id(channel) &&
                    (channel.KkpePosition ? KkpeBone(track) == channel.Guide.transformTarget : ReferenceEquals(track.parameter, channel.Guide)))
                    return track;
            return null;
        }

        Interpolable Ensure(Channel channel, List<UndoTrack> undo)
        {
            Interpolable track = Find(channel);
            if (track != null)
            {
                undo.Add(new UndoTrack(track, false));
                return track;
            }
            InterpolableModel model = Model(ModelOwner(channel), Id(channel));
            object parameter = Parameter(channel, model);
            if (model == null || !model.IsCompatibleWithTarget(channel.Owner))
                throw new InvalidOperationException("標準トラックを作成できません · " + Id(channel));
            track = new Interpolable(channel.Owner, parameter, model);
            Tracks.Add(track.GetHashCode(), track);
            try
            {
                object nativeTree = tree.GetValue(Instance);
                nativeTree.GetType().GetMethod("AddLeaf", Flags).Invoke(nativeTree, new object[] { track, null });
            }
            catch
            {
                Tracks.Remove(track.GetHashCode());
                throw;
            }
            undo.Add(new UndoTrack(track, true));
            return track;
        }

        sealed class UndoTrack
        {
            internal readonly Interpolable Track;
            internal readonly bool Created, Enabled;
            internal readonly SortedList<float, Frame> Frames;
            internal readonly Dictionary<Frame, object> Values = new Dictionary<Frame, object>();
            internal UndoTrack(Interpolable track, bool created)
            {
                Track = track;
                Created = created;
                Enabled = track.enabled;
                Frames = new SortedList<float, Frame>(track.keyframes);
            }

            internal void Restore()
            {
                Track.enabled = Enabled;
                Track.keyframes.Clear();
                foreach (KeyValuePair<float, Frame> key in Frames) Track.keyframes.Add(key.Key, key.Value);
                foreach (KeyValuePair<Frame, object> value in Values) value.Key.value = value.Value;
            }
        }

        sealed class KkpePositionState
        {
            readonly object editor;
            readonly IDictionary dirty;
            readonly GameObject bone;
            readonly object previous;
            readonly Behaviour controller;
            readonly bool enabled;
            readonly MethodInfo apply;

            internal KkpePositionState(Channel channel, object parameter)
            {
                editor = parameter.GetType().GetField("key", Flags).GetValue(parameter);
                controller = KkpeController(channel.Owner, parameter);
                apply = editor.GetType().GetMethod("ApplyBoneManualCorrection", Flags);
                FieldInfo field = editor.GetType().GetField("_dirtyBones", Flags);
                if (controller == null || apply == null || field == null)
                    throw new NotSupportedException("このKKPEの骨位置処理には接続できません。");
                dirty = (IDictionary)field.GetValue(editor);
                bone = channel.Guide.transformTarget.gameObject;
                enabled = controller.enabled;
                if (!enabled && dirty.Count > 0)
                    throw new InvalidOperationException("このアイテムのKKPEに無効化中の編集があります。KKPEで編集内容を確認して有効化してから焼き込んでください。");
                object state = dirty[bone];
                if (state != null)
                    previous = Activator.CreateInstance(state.GetType(), Flags, null, new[] { state }, null);
            }

            internal void Apply()
            {
                controller.enabled = true;
                apply.Invoke(editor, null);
            }

            internal void Restore()
            {
                if (previous == null) dirty.Remove(bone);
                else dirty[bone] = previous;
                controller.enabled = enabled;
            }
        }

        void Transaction(Action<List<UndoTrack>> write)
        {
            List<UndoTrack> undo = new List<UndoTrack>();
            try
            {
                write(undo);
                refresh.Invoke(Instance, null);
            }
            catch
            {
                for (int i = undo.Count - 1; i >= 0; i--)
                {
                    UndoTrack row = undo[i];
                    if (row.Created) remove.Invoke(Instance, new object[] { row.Track });
                    else row.Restore();
                }
                refresh.Invoke(Instance, null);
                throw;
            }
        }

        static float Match(Interpolable track, float time)
        {
            foreach (float existing in track.keyframes.Keys)
                if (Mathf.Approximately(existing, time)) return existing;
            return time;
        }

        internal override bool HasZero(Channel channel)
        {
            Interpolable track = Find(channel);
            return track != null && track.keyframes.ContainsKey(0);
        }

        internal override void Key(IEnumerable<Channel> channels, float time, bool overwrite)
        {
            if (!Ready || Playing) throw new InvalidOperationException("Timelineを停止してからキーを追加してください。");
            Transaction(delegate(List<UndoTrack> undo)
            {
                foreach (Channel channel in channels)
                {
                    Interpolable track = Ensure(channel, undo);
                    float at = Match(track, time);
                    Frame existing;
                    if (track.keyframes.TryGetValue(at, out existing))
                    {
                        if (overwrite)
                        {
                            undo[undo.Count - 1].Values.Add(existing, existing.value);
                            existing.value = track.GetValue();
                        }
                    }
                    else track.keyframes.Add(at, new Frame(track.GetValue(), track, AnimationCurve.Linear(0, 0, 1, 1)));
                }
            });
        }

        internal override void Copy(OCIFolder source, OCIFolder target)
        {
            Transaction(delegate(List<UndoTrack> undo)
            {
                foreach (bool rotation in new[] { false, true })
                {
                    Interpolable original = Find(new Channel(source, source.guideObject, rotation));
                    Channel destination = new Channel(target, target.guideObject, rotation);
                    if (original == null || Find(destination) != null) continue;
                    Interpolable copy = Ensure(destination, undo);
                    copy.enabled = original.enabled;
                    foreach (KeyValuePair<float, Frame> key in original.keyframes)
                        copy.keyframes.Add(key.Key, new Frame(key.Value.value, copy, new AnimationCurve(key.Value.curve.keys)));
                }
            });
        }

    }
}
