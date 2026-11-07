using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx;
using HarmonyLib;
using KKAPI.Studio.SaveLoad;
using KKAPI.Studio.UI.Toolbars;
using Studio;
using UnityEngine;
using UnityEngine.EventSystems;

namespace GinIkThis
{
    // Per-item rig lookup, conflict checks and enable/remove operations.
    public sealed partial class Plugin
    {
        internal List<Rig> ForItem(OCIItem item)
        {
            return Rigs.FindAll(delegate(Rig rig) { return rig.Item == item; });
        }

        internal void Check(Rig rig, Rig ignore)
        {
            foreach (Rig other in Rigs)
                if (other.Enabled && other != ignore && other.Item == rig.Item)
                    CheckPair(rig, other);
            drivers.Check(rig, false);
        }

        internal void CheckBake(Rig rig)
        {
            CheckBake(rig, false);
        }

        internal void CheckBake(Rig rig, bool observed)
        {
            foreach (Rig other in ForItem(rig.Item))
                if (other.Enabled && other != rig) CheckPair(rig, other);
            drivers.Check(rig, true, observed);
        }

        static void CheckPair(Rig a, Rig b)
        {
            if (a.Map.Waist && b.Map.Waist) throw new InvalidOperationException("腰の移動は1アイテムにつき1部位です。");
            foreach (OCIChar.BoneInfo bone in a.Bones)
                if (Array.IndexOf(b.Bones, bone) >= 0)
                    throw new InvalidOperationException(b.Map.Name + "と駆動する骨が重複しています · " + Rig.Bone(bone).name);
        }

        void Sort()
        {
            Rigs.Sort(delegate(Rig a, Rig b)
            {
                if (a.Item != b.Item)
                {
                    int depth = Depth(a.Item.guideObject.transformTarget).CompareTo(Depth(b.Item.guideObject.transformTarget));
                    return depth != 0 ? depth : a.Item.objectInfo.dicKey.CompareTo(b.Item.objectInfo.dicKey);
                }
                int order = b.Map.Waist.CompareTo(a.Map.Waist);
                return order != 0 ? order : Depth(Rig.Bone(a.Bones[0])).CompareTo(Depth(Rig.Bone(b.Bones[0])));
            });
        }

        static int Depth(Transform t)
        {
            int depth = 0;
            for (; t != null; t = t.parent) depth++;
            return depth;
        }

        internal static void ApplyFK(OCIItem item)
        {
            foreach (OCIChar.BoneInfo bone in item.listBones)
                if (Rig.HasGuide(bone)) Rig.Bone(bone).localRotation = Quaternion.Euler(bone.guideObject.changeAmount.rot);
        }

        internal void SetEnabled(Rig rig, bool enabled)
        {
            if (!enabled) { Disable(rig); return; }
            if (!rig.Valid) throw new InvalidOperationException("操作点が見つかりません。部位を再設定してください。");
            if (!rig.Item.itemInfo.enableFK) throw new InvalidOperationException("アイテムの標準FKを有効にしてください。");
            List<DriverCheck.Transfer> transfers = rig.NodeLinks == 0 ? new List<DriverCheck.Transfer>() :
                drivers.Transfers(new[] { rig }, true);
            try
            {
                foreach (DriverCheck.Transfer transfer in transfers) transfer.Apply();
                rig.Validate();
                Check(rig, rig);
            }
            catch
            {
                foreach (DriverCheck.Transfer transfer in transfers) transfer.Restore();
                throw;
            }
            rig.NodeLinks = 0;
            rig.Error = null;
            rig.Baked = false;
            rig.LegacyDisabled = false;
            rig.Enabled = true;
        }

        void Disable(Rig rig)
        {
            rig.Enabled = false;
            rig.ReleaseRoot(false);
            if (rig.Map.Waist && Rig.IsItem(rig.Item) && rig.WaistPositionGuide != null) rig.WaistPositionGuide.ForceUpdate();
        }

        internal void Remove(Rig rig)
        {
            List<OCIFolder> guides = OwnedFolders(rig);
            foreach (OCIFolder folder in guides) drivers.CheckRemoval(folder);
            Disable(rig);
            Rigs.Remove(rig);
            foreach (OCIFolder folder in guides) DeleteFolder(folder);
            DeleteEmptyGroup(rig.Group);
        }

        static List<OCIFolder> OwnedFolders(Rig rig)
        {
            List<OCIFolder> result = new List<OCIFolder>(rig.Controls);
            result.AddRange(rig.Retired);
            result.RemoveAll(delegate(OCIFolder folder) { return folder == null || folder.objectItem == null; });
            return result;
        }

        internal Rig AddRootControl(Rig rig)
        {
            if (!rig.Enabled || !rig.Valid || rig.Map.Waist || rig.Map.MoveRoot)
                throw new InvalidOperationException("有効なチェーンの起点を選んでください。");
            Mapping map = rig.Map.Copy();
            map.MoveRoot = true;
            return Confirm(rig.Item, new[] { map }, rig)[0];
        }

        void ResetRootControl(Rig rig)
        {
            OCIFolder folder = rig.RootControl;
            GuideObject guide = folder.guideObject;
            Vector3 previous = guide.changeAmount.pos;
            if (previous == rig.RootRestPosition) return;
            guide.changeAmount.pos = rig.RootRestPosition;
            try
            {
                if (autoKey && TimelineReady && !Timeline.Playing)
                    Timeline.Key(new[] { new Channel(folder, guide, false) }, Timeline.Time, true);
            }
            catch
            {
                guide.changeAmount.pos = previous;
                throw;
            }
            Singleton<UndoRedoManager>.Instance.Push(new GuideCommand.MoveEqualsCommand(new[]
            {
                new GuideCommand.EqualsInfo { dicKey = guide.dicKey, oldValue = previous, newValue = rig.RootRestPosition }
            }));
        }

    }
}
