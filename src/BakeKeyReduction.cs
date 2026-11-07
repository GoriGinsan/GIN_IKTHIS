using System;
using System.Collections.Generic;
using UnityEngine;

namespace GinIkThis
{
    internal enum BakeQuality { Precise, Standard, Compact }

    // This operates on captured output samples only, before Timeline commits them.
    // Never run a scene-wide cleanup command: it would also touch the user's keys.
    internal static class BakeKeyReduction
    {
        internal static bool[] Select(float[] times, object[,] samples, int channel, bool rotation)
        {
            return Select(times, samples, channel, rotation, BakeQuality.Standard);
        }

        internal static bool[] Select(float[] times, object[,] samples, int channel, bool rotation, BakeQuality quality)
        {
            float positionTolerance = quality == BakeQuality.Precise ? .00005f : quality == BakeQuality.Compact ? .0005f : .0002f;
            float angleTolerance = quality == BakeQuality.Precise ? .025f : quality == BakeQuality.Compact ? .25f : .1f;
            float rotationDistanceSquared = 4 * Mathf.Pow(Mathf.Sin(angleTolerance * Mathf.Deg2Rad / 4), 2);
            bool[] keep = new bool[times.Length];
            Stack<KeyValuePair<int, int>> spans = new Stack<KeyValuePair<int, int>>();
            spans.Push(new KeyValuePair<int, int>(0, times.Length - 1));
            while (spans.Count > 0)
            {
                KeyValuePair<int, int> span = spans.Pop();
                int first = span.Key, last = span.Value;
                keep[first] = keep[last] = true;
                int split = -1;
                float worstError = 1;
                for (int index = first + 1; index < last; index++)
                {
                    float factor = (times[index] - times[first]) / (times[last] - times[first]);
                    float error = NormalizedError(samples[index, channel], samples[first, channel],
                        samples[last, channel], factor, rotation, positionTolerance, rotationDistanceSquared);
                    if (error > worstError)
                    {
                        worstError = error;
                        split = index;
                    }
                }
                if (split < 0) continue;
                spans.Push(new KeyValuePair<int, int>(first, split));
                spans.Push(new KeyValuePair<int, int>(split, last));
            }
            return keep;
        }

        static float NormalizedError(object sample, object first, object last, float factor, bool rotation,
            float positionTolerance, float rotationDistanceSquared)
        {
            if (!rotation)
            {
                Vector3 predicted = Vector3.LerpUnclamped((Vector3)first, (Vector3)last, factor);
                return ((Vector3)sample - predicted).sqrMagnitude / (positionTolerance * positionTolerance);
            }
            Quaternion expected = Quaternion.SlerpUnclamped((Quaternion)first, (Quaternion)last, factor);
            Quaternion actual = (Quaternion)sample;
            // q and -q encode the same rotation. Chord distance remains accurate
            // for small errors where Acos(dot) loses float precision.
            float sign = Quaternion.Dot(actual, expected) < 0 ? -1 : 1;
            float x = actual.x - expected.x * sign, y = actual.y - expected.y * sign;
            float z = actual.z - expected.z * sign, w = actual.w - expected.w * sign;
            return (x*x + y*y + z*z + w*w) / rotationDistanceSquared;
        }
    }
}
