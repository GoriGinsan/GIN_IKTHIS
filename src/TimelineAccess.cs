using System;
using System.Collections;
using System.Collections.Generic;
using BepInEx.Bootstrap;
using Studio;
using UnityEngine;

namespace GinIkThis
{
    internal sealed class Channel
    {
        internal readonly ObjectCtrlInfo Owner;
        internal readonly GuideObject Guide;
        internal readonly bool Rotation;
        internal readonly bool KkpePosition;
        internal Channel(ObjectCtrlInfo owner, GuideObject guide, bool rotation) : this(owner, guide, rotation, false) { }
        internal Channel(ObjectCtrlInfo owner, GuideObject guide, bool rotation, bool kkpePosition)
        {
            Owner = owner;
            Guide = guide;
            Rotation = rotation;
            KkpePosition = kkpePosition;
        }
    }

    internal abstract class TimelineAccess
    {
        internal const string Guid = "com.joan6694.illusionplugins.timeline";
        internal abstract bool Ready { get; }
        internal abstract float Time { get; }
        internal abstract float Duration { get; }
        internal abstract bool Playing { get; }
        internal abstract bool HasZero(Channel channel);
        internal abstract void Key(IEnumerable<Channel> channels, float time, bool overwrite);
        internal abstract void Copy(OCIFolder source, OCIFolder target);
        internal abstract int BakeRigs(Plugin plugin, List<Rig> rigs, float start, float end, int fps, bool overwrite, bool reduce);
        internal abstract IEnumerator BakeObserved(Plugin plugin, List<Rig> rigs, float start, float end, int fps,
            bool overwrite, bool reduce, Action<int> completed);
        internal abstract bool HasBakedTracks(Rig rig);

        internal void Bake(Plugin plugin, OCIItem item, float start, float end, int fps, bool overwrite)
        {
            BakeRigs(plugin, plugin.ForItem(item).FindAll(delegate(Rig rig) { return rig.Enabled; }), start, end, fps, overwrite, false);
        }

        // Keep optional Timeline types out of any method Studio calls when
        // Timeline is missing, including plugin discovery and Awake.
        internal static TimelineAccess Connect()
        {
            if (!Chainloader.PluginInfos.ContainsKey(Guid)) return null;
            Type bridge = typeof(TimelineAccess).Assembly.GetType("GinIkThis.NativeTimeline", true);
            return (TimelineAccess)Activator.CreateInstance(bridge, true);
        }

        internal static IEnumerable<Channel> Controls(Rig rig)
        {
            for (int i = 0; i < rig.Controls.Count; i++)
            {
                OCIFolder folder = rig.Controls[i];
                yield return new Channel(folder, folder.guideObject, false);
                if (i == 0) yield return new Channel(folder, folder.guideObject, true);
            }
        }
    }
}
