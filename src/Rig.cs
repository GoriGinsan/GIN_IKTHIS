using System;
using System.Collections.Generic;
using Studio;
using UnityEngine;

namespace GinIkThis
{
    internal sealed class Mapping
    {
        internal string Name;
        internal bool Waist, Pole, MoveRoot;
        internal OCIChar.BoneInfo Root, Tip;
        internal readonly List<OCIChar.BoneInfo> Middles = new List<OCIChar.BoneInfo>();

        internal Mapping Copy()
        {
            Mapping copy = new Mapping { Name = Name, Waist = Waist, Pole = Pole, MoveRoot = MoveRoot, Root = Root, Tip = Tip };
            copy.Middles.AddRange(Middles);
            return copy;
        }
    }

    internal sealed class Rig
    {
        internal readonly OCIItem Item;
        internal readonly Mapping Map;
        internal readonly OCIChar.BoneInfo[] Bones;
        internal readonly int[] MiddleIndices;
        internal readonly List<OCIFolder> Controls = new List<OCIFolder>();
        internal OCIFolder Group;
        internal readonly List<OCIFolder> Retired = new List<OCIFolder>();
        internal bool Enabled = true;
        internal bool Baked;
        internal int NodeLinks;
        internal bool LegacyDisabled;
        internal string Error;
        internal GuideObject WaistPositionGuide { get; private set; }
        internal Vector3 RootRestPosition;
        bool rootApplied;
        internal int ControlCount { get { return 1 + MiddleIndices.Length + (Map.MoveRoot ? 1 : 0); } }
        internal OCIFolder RootControl { get { return Map.MoveRoot && Controls.Count == ControlCount ? Controls[ControlCount - 1] : null; } }
        internal bool IsRootControl(int index) { return Map.MoveRoot && index == ControlCount - 1; }
        readonly Vector3[] points, rest, goals;
        readonly float[] lengths;
        readonly Quaternion[] previous;

        internal Rig(OCIItem item, Mapping map)
        {
            Item = item;
            Map = map.Copy();
            Map.Name = String.IsNullOrEmpty(map.Name) || map.Name.Trim().Length == 0 ? "IK部位" : map.Name.Trim();
            if (!IsItem(item) || !Has(item, map.Tip)) throw new ArgumentException("対象アイテムのFKボーンが必要です。");
            if (map.Waist)
            {
                WaistPositionGuide = map.Tip.guideObject.enablePos ? map.Tip.guideObject : item.guideObject;
                if (!WaistPositionGuide.enablePos) throw new ArgumentException("腰の位置を保存できません。");
                Bones = new[] { map.Tip };
                MiddleIndices = new int[0];
            }
            else
            {
                if (!Has(item, map.Root) || map.Middles.Count == 0 || !Below(map.Tip, map.Root))
                    throw new ArgumentException("起点から先端まで、同じ親子チェーンを選んでください。");
                Dictionary<Transform, OCIChar.BoneInfo> byTransform = new Dictionary<Transform, OCIChar.BoneInfo>();
                foreach (OCIChar.BoneInfo bone in item.listBones)
                    if (HasGuide(bone)) byTransform[Bone(bone)] = bone;
                List<OCIChar.BoneInfo> chain = new List<OCIChar.BoneInfo>();
                for (Transform t = Bone(map.Tip); t != Bone(map.Root); t = t.parent)
                {
                    OCIChar.BoneInfo joint;
                    if (byTransform.TryGetValue(t, out joint)) chain.Add(joint);
                }
                chain.Add(map.Root);
                chain.Reverse();
                MiddleIndices = new int[map.Middles.Count];
                int last = 0;
                for (int i = 0; i < MiddleIndices.Length; i++)
                {
                    int index = chain.IndexOf(map.Middles[i]);
                    if (index <= last || index >= chain.Count - 1) throw new ArgumentException("青ガイドは起点と先端の間で上流から順に指定してください。");
                    MiddleIndices[i] = last = index;
                }
                Bones = chain.ToArray();
            }
            foreach (OCIChar.BoneInfo b in Bones)
                if (!b.guideObject.enableRot) throw new ArgumentException("標準FK回転を使えない骨があります · " + Bone(b).name);
            if (Map.MoveRoot)
            {
                if (Map.Waist || Bone(Map.Root).parent == null)
                    throw new ArgumentException("位置を操作できるIKの起点が必要です。");
                RootRestPosition = Bone(Map.Root).localPosition;
            }
            points = new Vector3[Bones.Length];
            rest = new Vector3[Bones.Length];
            goals = new Vector3[Bones.Length];
            lengths = new float[Bones.Length - 1];
            previous = new Quaternion[Bones.Length];
            Measure();
        }

        internal static bool IsItem(OCIItem item)
        {
            return item != null && item.objectItem != null && item.itemFKCtrl != null && item.listBones != null && item.listBones.Count > 0;
        }

        internal static bool HasGuide(OCIChar.BoneInfo bone)
        {
            return bone != null && bone.guideObject != null && Bone(bone) != null;
        }

        internal static bool Has(OCIItem item, OCIChar.BoneInfo bone)
        {
            return HasGuide(bone) && item.listBones.Contains(bone);
        }

        internal static Transform Bone(OCIChar.BoneInfo bone) { return bone.guideObject.transformTarget; }

        internal static bool Below(OCIChar.BoneInfo child, OCIChar.BoneInfo parent)
        {
            return HasGuide(child) && HasGuide(parent) && Bone(child) != Bone(parent) && Bone(child).IsChildOf(Bone(parent));
        }

        internal bool Valid
        {
            get
            {
                return IsItem(Item) && Controls.Count == ControlCount &&
                    Controls.TrueForAll(delegate(OCIFolder c) { return c != null && c.objectItem != null && c.guideObject != null; });
            }
        }

        internal Vector3 ControlPosition(int index)
        {
            return Bone(IsRootControl(index) ? Map.Root : index == 0 ? Map.Tip : Map.Middles[index - 1]).position;
        }
        internal Quaternion ControlRotation(int index) { return index == 0 ? Bone(Map.Tip).rotation : Quaternion.identity; }

        internal void BindRootControl()
        {
            OCIFolder folder = RootControl;
            if (folder == null) return;
            GuideObject guide = folder.guideObject;
            // Keep the scene hierarchy under the collapsed IK group. Only the
            // position key's coordinate frame follows the bone's parent.
            guide.parent = Bone(Map.Root).parent;
            guide.nonconnect = true;
            guide.ForceUpdate();
        }

        internal void ReleaseRoot(bool keepPosition)
        {
            if (rootApplied && !keepPosition && HasGuide(Map.Root))
                Bone(Map.Root).localPosition = RootRestPosition;
            rootApplied = false;
        }

        internal void Validate() { Measure(); }

        void Measure()
        {
            if (!Has(Item, Map.Tip)) throw new InvalidOperationException("先端のFKボーンが見つかりません。");
            // The solver assumes rigid links in world space. Test the actual local
            // transforms instead of relying on lossyScale to detect shear.
            if (!Map.Waist)
            {
                for (Transform t = Bone(Map.Tip); t != null; t = t.parent)
                {
                    Vector3 s = t.localScale;
                    if (!TwoBone.Finite(s) || s.x <= 0f || s.y <= 0f || s.z <= 0f ||
                        Mathf.Abs(s.x - s.y) > s.x * .001f || Mathf.Abs(s.x - s.z) > s.x * .001f)
                        throw new InvalidOperationException("正の均等スケールが必要です · " + t.name);
                }
            }
            for (int i = 0; i < Bones.Length; i++)
            {
                if (!HasGuide(Bones[i]) || i > 0 && !Below(Bones[i], Bones[i - 1]))
                    throw new InvalidOperationException("FKボーンの親子関係が変更されています。");
                Transform t = Bone(Bones[i]);
                rest[i] = t.position;
                if (!TwoBone.Finite(rest[i])) throw new InvalidOperationException("骨の座標を計算できません · " + t.name);
                if (i == 0) continue;
                lengths[i - 1] = Vector3.Distance(rest[i - 1], rest[i]);
            }
            int from = 0;
            for (int i = 0; !Map.Waist && i <= MiddleIndices.Length; i++)
            {
                int to = i == MiddleIndices.Length ? Bones.Length - 1 : MiddleIndices[i];
                float length = 0;
                for (int n = from; n < to; n++) length += lengths[n];
                if (length < 1e-7f) throw new InvalidOperationException("選んだ骨の間に長さがありません。");
                from = to;
            }
        }

        internal void Solve()
        {
            if (!Valid) throw new InvalidOperationException("操作点またはアイテムが見つかりません。");
            if (!Item.itemInfo.enableFK) throw new InvalidOperationException("標準FKを有効にしてください。");
            foreach (OCIFolder folder in Controls)
            {
                if (Group == null || folder.parentInfo != Group)
                    throw new InvalidOperationException("アイテム配下のIK THISグループを確認してください。");
                if (!TwoBone.Finite(folder.guideObject.transformTarget.position)) throw new InvalidOperationException("操作点の座標を計算できません。");
            }
            for (int i = 0; i < Bones.Length; i++) previous[i] = Bone(Bones[i]).localRotation;
            Transform target = Controls[0].guideObject.transformTarget;
            if (!TwoBone.Finite(target.rotation)) throw new InvalidOperationException("赤ガイドの回転を計算できません。");
            Transform tip = Bone(Map.Tip);
            Transform translated = Map.Waist ? WaistPositionGuide.transformTarget :
                Map.MoveRoot ? Bone(Map.Root) : Item.guideObject.transformTarget;
            Vector3 beforePosition = translated.localPosition;
            try
            {
                if (Map.MoveRoot)
                {
                    GuideObject root = RootControl.guideObject;
                    if (translated.parent == null || root.parent != translated.parent ||
                        !TwoBone.Finite(root.changeAmount.pos))
                        throw new InvalidOperationException("起点の親または位置を確認してください。");
                    root.ForceUpdate();
                    translated.localPosition = root.changeAmount.pos;
                }
                Measure();
                if (Map.Waist)
                {
                    Matrix4x4 matrix = translated.parent == null ? Matrix4x4.identity : translated.parent.localToWorldMatrix;
                    if (!TwoBone.Finite(matrix.determinant) || Mathf.Abs(matrix.determinant) < 1e-12f)
                        throw new InvalidOperationException("腰の移動をアイテム座標に変換できません。");
                    translated.position += target.position - tip.position;
                }
                else if (NeedsPositionSolve(target.position))
                {
                    if (Bones.Length == 3 && Map.Pole) SolvePole(target.position);
                    else SolveChain(target.position);
                    for (int i = 0; i < lengths.Length; i++)
                    {
                        if (lengths[i] < 1e-7f) continue;
                        Transform joint = Bone(Bones[i]);
                        Vector3 oldDirection = Bone(Bones[i + 1]).position - joint.position;
                        Vector3 newDirection = points[i + 1] - points[i];
                        joint.rotation = Aim(oldDirection, newDirection, joint.rotation) * joint.rotation;
                    }
                }
                if (Quaternion.Angle(tip.rotation, target.rotation) > .001f) tip.rotation = target.rotation;
                if (!TwoBone.Finite(tip.position) || !TwoBone.Finite(tip.rotation) || !TwoBone.Finite(translated.localPosition))
                    throw new InvalidOperationException("IKの結果が無効です。");
                rootApplied = Map.MoveRoot;
            }
            catch
            {
                translated.localPosition = beforePosition;
                for (int i = 0; i < Bones.Length; i++) Bone(Bones[i]).localRotation = previous[i];
                throw;
            }
        }

        static Quaternion Aim(Vector3 from, Vector3 to, Quaternion basis)
        {
            Vector3 a = from.normalized, b = to.normalized;
            float dot = Vector3.Dot(a, b);
            Quaternion shortest = Quaternion.FromToRotation(a, b);
            if (dot > -.995f) return shortest;
            Vector3 axis = TwoBone.BendDirection(a, basis * Vector3.up);
            Quaternion turn = Quaternion.AngleAxis(180, axis);
            Quaternion stable = Quaternion.FromToRotation(turn * a, b) * turn;
            return Quaternion.Slerp(shortest, stable, Mathf.SmoothStep(0, 1, (-dot - .995f) / .005f));
        }

        bool NeedsPositionSolve(Vector3 target)
        {
            if ((target - rest[rest.Length - 1]).sqrMagnitude > 1e-12f) return true;
            for (int i = 0; i < MiddleIndices.Length; i++)
                if ((Controls[i + 1].guideObject.transformTarget.position - rest[MiddleIndices[i]]).sqrMagnitude > 1e-12f) return true;
            return false;
        }

        void SolvePole(Vector3 target)
        {
            points[0] = rest[0];
            points[1] = PolePoint(target, lengths[0], lengths[1], out points[2]);
        }

        Vector3 PolePoint(Vector3 target, float upper, float lower, out Vector3 endpoint)
        {
            Vector3 root = rest[0], delta = target - root;
            Vector3 middle = rest[MiddleIndices[0]];
            float reach = upper + lower;
            Vector3 reference = (rest[rest.Length - 1] - root).normalized;
            if (reference == Vector3.zero) reference = (middle - root).normalized;
            if (reference == Vector3.zero) reference = (rest[1] - root).normalized;
            float nearRoot = Mathf.SmoothStep(0, 1, delta.magnitude / (reach * .001f));
            Vector3 axis = Vector3.Slerp(reference, delta.normalized, nearRoot).normalized;
            if (axis == Vector3.zero) axis = reference;
            Vector3 natural = TwoBone.BendDirection(axis, middle - root);
            Vector3 pole = Controls[1].guideObject.transformTarget.position - root;
            Vector3 projected = pole - axis * Vector3.Dot(axis, pole);
            float weight = Mathf.SmoothStep(0, 1, projected.magnitude / (reach * .08f));
            Vector3 bend = Vector3.Lerp(natural, projected.normalized, weight);
            float opposed = Mathf.SmoothStep(0, 1, -Vector3.Dot(natural, projected.normalized));
            bend += Vector3.Cross(axis, natural) * (.18f * opposed * 4 * weight * (1 - weight));
            if (bend.sqrMagnitude < 1e-10f) bend = natural;
            return TwoBone.Elbow(root, root + axis * delta.magnitude, root + bend, upper, lower, axis, out endpoint);
        }

        void SolveChain(Vector3 target)
        {
            int tip = points.Length - 1;
            Vector3 root = rest[0], delta = target - root;
            float total = 0, longest = 0;
            foreach (float length in lengths) { total += length; longest = Mathf.Max(longest, length); }
            Vector3 axis = delta.sqrMagnitude > 1e-14f ? delta.normalized : (rest[tip] - root).normalized;
            if (axis == Vector3.zero) axis = (rest[MiddleIndices[0]] - root).normalized;
            if (delta.magnitude >= total)
            {
                points[0] = root;
                for (int i = 1; i <= tip; i++) points[i] = points[i - 1] + axis * lengths[i - 1];
                return;
            }
            float distance = Mathf.Max(delta.magnitude, Mathf.Max(0, 2 * longest - total));
            Vector3 endpoint = root + axis * distance;
            Vector3 polePoint = Vector3.zero;
            bool directionOnly = Map.Pole && MiddleIndices.Length == 1;
            if (directionOnly)
            {
                float upper = 0;
                for (int i = 0; i < MiddleIndices[0]; i++) upper += lengths[i];
                Vector3 ignored;
                polePoint = PolePoint(endpoint, upper, total - upper, out ignored);
            }
            // Spread each guide's displacement across the neighboring links.
            // Smooth weights keep an anchor from introducing a discontinuity.
            int left = 0;
            Vector3 leftShift = Vector3.zero;
            for (int anchor = 0; anchor <= MiddleIndices.Length; anchor++)
            {
                int right = anchor == MiddleIndices.Length ? tip : MiddleIndices[anchor];
                Vector3 rightTarget = right == tip ? endpoint :
                    directionOnly ? polePoint : Controls[anchor + 1].guideObject.transformTarget.position;
                Vector3 rightShift = rightTarget - rest[right];
                float span = 0, walked = 0;
                for (int i = left; i < right; i++) span += lengths[i];
                for (int i = left; i < right; i++)
                {
                    float fraction = span < 1e-7f ? 0 : walked / span;
                    goals[i] = rest[i] + Vector3.Lerp(leftShift, rightShift, Mathf.SmoothStep(0, 1, fraction));
                    walked += lengths[i];
                }
                left = right;
                leftShift = rightShift;
            }
            goals[tip] = endpoint;
            Array.Copy(goals, points, points.Length);
            Vector3 seed = TwoBone.BendDirection(axis, goals[MiddleIndices[0]] - root);
            for (int i = 1; i < tip; i++)
                points[i] += seed * (total * .001f * Mathf.Sin(Mathf.PI * i / tip));
            float tolerance = Mathf.Max(1e-6f, total * .00005f);
            // First fit the preferred shape, then converge with only the fixed
            // root and the red endpoint. Blue anchors are soft constraints.
            for (int pass = 0; pass < 112; pass++)
            {
                if (pass < 24)
                    for (int i = 1; i < tip; i++) points[i] = Vector3.Lerp(points[i], goals[i], .15f * (1 - pass / 24f));
                points[tip] = endpoint;
                for (int i = tip - 1; i >= 0; i--)
                    points[i] = AtLength(points[i + 1], points[i], lengths[i], rest[i] - rest[i + 1]);
                points[0] = root;
                for (int i = 1; i <= tip; i++)
                    points[i] = AtLength(points[i - 1], points[i], lengths[i - 1], rest[i] - rest[i - 1]);
                if (pass >= 24 && (points[tip] - endpoint).sqrMagnitude < tolerance * tolerance) break;
            }
            if ((points[tip] - endpoint).sqrMagnitude > tolerance * tolerance)
                FinishAtEndpoint(endpoint, total);
        }

        void FinishAtEndpoint(Vector3 endpoint, float remaining)
        {
            // Project each link's preferred point into the reachable shell
            // of the remaining links. This handles nearly straight chains,
            // where iterative FABRIK may otherwise converge too slowly.
            for (int i = 0; i < lengths.Length; i++)
            {
                float length = lengths[i];
                remaining = Mathf.Max(0, remaining - length);
                Vector3 origin = points[i];
                if (length < 1e-7f) { points[i + 1] = origin; continue; }
                Vector3 direct = endpoint - origin;
                float distance = direct.magnitude;
                if (distance < 1e-7f) continue;
                float longest = 0;
                for (int j = i + 1; j < lengths.Length; j++) longest = Mathf.Max(longest, lengths[j]);
                float lower = Mathf.Max(Mathf.Abs(distance - length), 2 * longest - remaining);
                float upper = Mathf.Max(lower, Mathf.Min(distance + length, remaining));
                Vector3 hint = points[i + 1] - origin;
                Vector3 preferred = AtLength(origin, points[i + 1], length, rest[i + 1] - rest[i]);
                float toGoal = Vector3.Distance(endpoint, preferred);
                float residual = Mathf.Clamp(toGoal, lower, upper);
                if (residual == toGoal) { points[i + 1] = preferred; continue; }
                Vector3 axis = direct / distance;
                float along = Mathf.Clamp((length * length + distance * distance - residual * residual) / (2 * distance), -length, length);
                float height = Mathf.Sqrt(Mathf.Max(0, length * length - along * along));
                points[i + 1] = origin + axis * along + TwoBone.BendDirection(axis, hint) * height;
            }
        }

        static Vector3 AtLength(Vector3 origin, Vector3 point, float length, Vector3 fallback)
        {
            Vector3 direction = point - origin;
            return origin + (direction.sqrMagnitude > 1e-16f ? direction.normalized : fallback.normalized) * length;
        }
    }
}
