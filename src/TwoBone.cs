using System;
using UnityEngine;

namespace GinIkThis
{
    // Axis-independent two-link IK. No stretching or generated skeleton is used.
    public static class TwoBone
    {
        public static bool Finite(float x) { return !Single.IsNaN(x) && !Single.IsInfinity(x); }
        public static bool Finite(Vector3 v) { return Finite(v.x) && Finite(v.y) && Finite(v.z); }
        public static bool Finite(Quaternion q) { return Finite(q.x) && Finite(q.y) && Finite(q.z) && Finite(q.w); }
        public static Vector3 BendDirection(Vector3 axis, Vector3 hint)
        {
            Vector3 bend = hint - axis * Vector3.Dot(axis, hint);
            if (bend.sqrMagnitude < 1e-10f)
            {
                Vector3 reference = Mathf.Abs(Vector3.Dot(axis, Vector3.up)) < .9f ? Vector3.up : Vector3.right;
                bend = reference - axis * Vector3.Dot(axis, reference);
            }
            return bend.normalized;
        }

        public static Vector3 Elbow(Vector3 root, Vector3 target, Vector3 pole, float upper, float lower, Vector3 fallback, out Vector3 reachable)
        {
            if (!Finite(root) || !Finite(target) || !Finite(pole) || !Finite(upper) || !Finite(lower) || upper < 1e-7f || lower < 1e-7f)
                throw new ArgumentException("Invalid or zero-length IK segment.");
            Vector3 delta = target - root;
            Vector3 axis = delta.sqrMagnitude > 1e-12f ? delta.normalized : fallback.normalized;
            if (axis.sqrMagnitude < .5f) axis = Vector3.forward;
            float epsilon = Mathf.Min(Mathf.Min(upper, lower) * .25f, (upper + lower) * 1e-5f);
            float distance = Mathf.Clamp(delta.magnitude, Mathf.Abs(upper - lower) + epsilon, upper + lower - epsilon);
            float along = (upper * upper - lower * lower + distance * distance) / (2f * distance);
            float height = Mathf.Sqrt(Mathf.Max(0f, upper * upper - along * along));
            reachable = root + axis * distance;
            return root + axis * along + BendDirection(axis, pole - root) * height;
        }

    }
}

