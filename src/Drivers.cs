using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Bootstrap;
using RootMotion.FinalIK;
using Studio;
using UnityEngine;

namespace GinIkThis
{
    internal sealed class DriverCheck
    {
        const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        Type constraints;
        FieldInfo constraintInstance, constraintRows;
        bool searched;

        internal sealed class Transfer
        {
            internal readonly Rig Rig;
            readonly object row;
            readonly GuideObject target, originalGuide;
            readonly Transform originalTransform;
            readonly Vector3 originalPosition, originalScale;
            readonly Quaternion originalRotation;
            readonly bool restoreSource;
            readonly Vector3 sourcePosition, sourceScale;
            readonly Quaternion sourceRotation;

            internal Transfer(Rig rig, object row, GuideObject target, bool restoreSource)
            {
                Rig = rig;
                this.row = row;
                this.target = target;
                this.restoreSource = restoreSource;
                originalGuide = Field(row, "child") as GuideObject;
                originalTransform = Field(row, "childTransform") as Transform;
                sourcePosition = originalTransform.localPosition;
                sourceRotation = originalTransform.localRotation;
                sourceScale = originalTransform.localScale;
                originalPosition = (Vector3)Field(row, "originalChildPosition");
                originalRotation = (Quaternion)Field(row, "originalChildRotation");
                originalScale = (Vector3)Field(row, "originalChildScale");
                foreach (string name in new[] { "child", "childTransform", "originalChildPosition", "originalChildRotation", "originalChildScale" })
                    if (row.GetType().GetField(name, Flags) == null)
                        throw new NotSupportedException("このNodesConstraintsの接続先を変更できません。");
                Transform parent = Field(row, "parentTransform") as Transform;
                if (parent == null || parent == target.transformTarget || parent.IsChildOf(target.transformTarget))
                    throw new InvalidOperationException(rig.Map.Name + "のNodeConstraintsが手先の子を参照しています。");
            }

            void Set(string name, object value) { row.GetType().GetField(name, Flags).SetValue(row, value); }

            internal void Apply()
            {
                if (restoreSource)
                {
                    originalTransform.localPosition = originalPosition;
                    originalTransform.localRotation = originalRotation;
                    originalTransform.localScale = originalScale;
                    if (originalGuide != null)
                    {
                        originalGuide.changeAmount.pos = originalPosition;
                        originalGuide.changeAmount.rot = originalRotation.eulerAngles;
                    }
                }
                Transform bone = target.transformTarget;
                Set("child", target);
                Set("childTransform", bone);
                Set("originalChildPosition", bone.localPosition);
                Set("originalChildRotation", bone.localRotation);
                Set("originalChildScale", bone.localScale);
                if (!System.Object.Equals(Field(row, "enabled"), true)) return;
                foreach (string channel in new[] { "Position", "Rotation", "Scale" })
                    if (System.Object.Equals(Field(row, channel.ToLowerInvariant()), true))
                        row.GetType().GetMethod("Update" + channel, Flags).Invoke(row, null);
            }

            internal void Restore()
            {
                Set("child", originalGuide);
                Set("childTransform", originalTransform);
                Set("originalChildPosition", originalPosition);
                Set("originalChildRotation", originalRotation);
                Set("originalChildScale", originalScale);
                originalTransform.localPosition = sourcePosition;
                originalTransform.localRotation = sourceRotation;
                originalTransform.localScale = sourceScale;
            }
        }

        internal List<Transfer> Transfers(IList<Rig> rigs, bool backToGuides)
        {
            List<Transfer> result = new List<Transfer>();
            foreach (Rig rig in rigs)
            {
                int count = 0;
                foreach (object row in ConstraintRows())
                {
                    Transform source = backToGuides ? Rig.Bone(rig.Map.Tip) : rig.Controls[0].guideObject.transformTarget;
                    if (!System.Object.Equals(Field(row, "childTransform"), source)) continue;
                    if (rig.Map.Waist)
                        throw new InvalidOperationException(rig.Map.Name + "の腰ガイドの追従は、FKのみの焼き込みを選んでください。");
                    GuideObject target = backToGuides ? rig.Controls[0].guideObject : rig.Map.Tip.guideObject;
                    result.Add(new Transfer(rig, row, target, backToGuides));
                    count++;
                }
                if (backToGuides && count > 0 && count != rig.NodeLinks)
                    throw new InvalidOperationException(rig.Map.Name + "のNodeConstraintsが変更されています。接続先を確認してください。");
            }
            return result;
        }

        internal HashSet<OCIFolder> ReferencedFolders(IList<OCIFolder> folders)
        {
            HashSet<OCIFolder> result = new HashSet<OCIFolder>();
            foreach (object row in ConstraintRows())
                foreach (OCIFolder folder in folders)
                    if (System.Object.Equals(Field(row, "parentTransform"), folder.guideObject.transformTarget) ||
                        System.Object.Equals(Field(row, "childTransform"), folder.guideObject.transformTarget))
                        result.Add(folder);
            return result;
        }

        internal void Check(Rig rig, bool baking) { Check(rig, baking, false); }

        internal void Check(Rig rig, bool baking, bool observed)
        {
            HashSet<Transform> driven = new HashSet<Transform>();
            foreach (OCIChar.BoneInfo bone in rig.Bones)
            {
                if (!Rig.HasGuide(bone)) throw new InvalidOperationException("駆動するFKボーンが見つかりません。");
                driven.Add(Rig.Bone(bone));
            }
            if (rig.Map.Waist) driven.Add(rig.WaistPositionGuide.transformTarget);
            CheckOtherGinRigs(rig, driven);
            foreach (IK ik in rig.Item.objectItem.GetComponentsInChildren<IK>())
            {
                if (!ik.isActiveAndEnabled) continue;
                IKSolver solver = ik.GetIKSolver();
                if (solver == null || solver.IKPositionWeight <= 0) continue;
                IKSolver.Point[] points = solver.GetPoints();
                if (points == null) continue;
                foreach (IKSolver.Point point in points)
                    if (point != null && Matches(driven, point.transform, baking))
                        throw new InvalidOperationException("既存の" + ik.GetType().Name + "が骨を駆動しています · " + point.transform.name);
            }
            foreach (object row in ConstraintRows())
            {
                if (!System.Object.Equals(Field(row, "enabled"), true)) continue;
                Transform target = Field(row, "childTransform") as Transform;
                if (Matches(driven, target, baking))
                    throw new InvalidOperationException("NodesConstraintsが同じ変換を駆動しています · " + target.name);
                if (!baking || observed) continue;
                foreach (OCIFolder control in rig.Controls)
                    if (control != null && control.objectItem != null && target == control.guideObject.transformTarget)
                        throw new InvalidOperationException("NodesConstraintsが操作点を駆動しています · " + control.name);
            }
        }

        internal void CheckRemoval(OCIFolder folder)
        {
            CheckRemoval(folder, false);
        }

        internal void CheckRemoval(OCIFolder folder, bool allowChildren)
        {
            CheckRemoval(folder, allowChildren, false);
        }

        internal void CheckRemoval(OCIFolder folder, bool allowChildren, bool allowIncoming)
        {
            if (!allowChildren && folder.folderInfo.child.Count != 0)
                throw new InvalidOperationException(folder.name + " に子オブジェクトがあります。先に親子付けを解除してください。");
            Transform target = folder.guideObject.transformTarget;
            foreach (object row in ConstraintRows())
                if (System.Object.Equals(Field(row, "parentTransform"), target) ||
                    !allowIncoming && System.Object.Equals(Field(row, "childTransform"), target))
                    throw new InvalidOperationException(folder.name + " を使うNodesConstraintsがあります。配布確定の前にリンクを整理してください。");
            if (allowIncoming && constraints != null && constraints.GetMethod("RemoveConstraintAt", Flags) == null)
                throw new NotSupportedException("このNodesConstraintsのリンクを解除できません。");
        }

        internal void RemoveIncoming(IList<OCIFolder> folders)
        {
            IList rows = ConstraintRows() as IList;
            if (rows == null || constraints == null) return;
            MethodInfo remove = constraints.GetMethod("RemoveConstraintAt", Flags);
            object instance = constraintInstance.GetValue(null);
            for (int i = rows.Count - 1; i >= 0; i--)
            {
                Transform child = Field(rows[i], "childTransform") as Transform;
                foreach (OCIFolder folder in folders)
                    if (folder != null && child == folder.guideObject.transformTarget)
                    {
                        remove.Invoke(instance, new object[] { i });
                        break;
                    }
            }
        }

        IEnumerable ConstraintRows()
        {
            if (!searched)
            {
                searched = true;
                foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    constraints = assembly.GetType("NodesConstraints.NodesConstraints");
                    if (constraints == null) continue;
                    constraintInstance = constraints.GetField("_self", Flags);
                    constraintRows = constraints.GetField("_constraints", Flags);
                    break;
                }
            }
            object instance = constraintInstance == null ? null : constraintInstance.GetValue(null);
            if (instance == null || constraintRows == null) return new object[0];
            return constraintRows.GetValue(instance) as IEnumerable ?? new object[0];
        }

        static bool Matches(HashSet<Transform> driven, Transform target, bool includeParents)
        {
            if (target == null) return false;
            if (driven.Contains(target)) return true;
            if (includeParents)
                foreach (Transform bone in driven)
                    if (bone.IsChildOf(target)) return true;
            return false;
        }

        static void CheckOtherGinRigs(Rig rig, HashSet<Transform> driven)
        {
            foreach (object rope in Rows("gin-rope-this", "Ropes"))
            {
                if (!ReferenceEquals(Field(rope, "Item"), rig.Item)) continue;
                Transform[] bones = Field(rope, "Bones") as Transform[];
                if (bones == null) continue;
                foreach (Transform bone in bones)
                    if (driven.Contains(bone))
                        throw new InvalidOperationException("GIN Rope Thisが同じ骨を駆動しています · " + bone.name);
            }
            foreach (object limb in Rows("gin-creatures-ik", "Limbs"))
            {
                if (!ReferenceEquals(Field(limb, "Item"), rig.Item) || !System.Object.Equals(Field(limb, "Enabled"), true)) continue;
                foreach (string role in new[] { "Root", "Middle", "Tip" })
                {
                    OCIChar.BoneInfo bone = Field(limb, role) as OCIChar.BoneInfo;
                    if (Rig.HasGuide(bone) && driven.Contains(Rig.Bone(bone)))
                        throw new InvalidOperationException("GIN Creatures IKが同じ骨を駆動しています · " + Rig.Bone(bone).name);
                }
            }
        }

        static IEnumerable Rows(string slug, string field)
        {
            PluginInfo plugin;
            if (!Chainloader.PluginInfos.TryGetValue("local.kkworkshop.0a52b20541f5." + slug, out plugin) || plugin.Instance == null)
                return new object[0];
            return Field(plugin.Instance, field) as IEnumerable ?? new object[0];
        }

        static object Field(object row, string name)
        {
            FieldInfo field = row.GetType().GetField(name, Flags);
            return field == null ? null : field.GetValue(row);
        }
    }
}
