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
    // Native interpolation and pose snapshots used during capture and rollback.
    internal sealed partial class NativeTimeline
    {
        static bool Bracket(Interpolable track, float time, out Frame from, out Frame to, out float factor)
        {
            KeyValuePair<float, Frame> left = default(KeyValuePair<float, Frame>), right = default(KeyValuePair<float, Frame>);
            foreach (KeyValuePair<float, Frame> key in track.keyframes)
            {
                if (key.Key > time) { right = key; break; }
                left = key;
            }
            if (left.Value == null) left = right;
            if (right.Value == null) right = left;
            from = left.Value;
            to = right.Value;
            factor = left.Key == right.Key ? 0 : left.Value.curve.Evaluate((time - left.Key) / (right.Key - left.Key));
            return from != null;
        }

        static void Evaluate(Interpolable track, float time)
        {
            Frame left, right;
            float factor;
            if (!Bracket(track, time, out left, out right, out factor)) return;
            if (track.canInterpolateBefore && !track.InterpolateBefore(left.value, right.value, factor) ||
                track.canInterpolateAfter && !track.InterpolateAfter(left.value, right.value, factor))
                throw new InvalidOperationException("Timelineトラックを評価できません · " + track.name);
        }

        sealed class GuidePose
        {
            readonly GuideObject guide;
            readonly Vector3 pos, rot, scale, localPos, localScale;
            readonly Quaternion localRot;
            internal GuidePose(GuideObject guide)
            {
                this.guide = guide;
                pos = guide.changeAmount.pos;
                rot = guide.changeAmount.rot;
                scale = guide.changeAmount.scale;
                localPos = guide.transformTarget.localPosition;
                localRot = guide.transformTarget.localRotation;
                localScale = guide.transformTarget.localScale;
            }
            internal void ResetAmounts()
            {
                guide.changeAmount.pos = pos;
                guide.changeAmount.rot = rot;
                guide.changeAmount.scale = scale;
            }
            internal void RestoreTransform()
            {
                guide.transformTarget.localPosition = localPos;
                guide.transformTarget.localRotation = localRot;
                guide.transformTarget.localScale = localScale;
            }
        }
    }
}
