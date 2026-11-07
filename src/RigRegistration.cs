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
    // Registration stages guides and keys before committing a complete rig.
    public sealed partial class Plugin
    {
        internal List<Rig> Confirm(OCIItem item, IList<Mapping> maps, Rig editing)
        {
            if (!Rig.IsItem(item) || maps.Count == 0 || editing != null && (maps.Count != 1 || !Rigs.Contains(editing) || editing.Item != item))
                throw new ArgumentException("確定する部位を選んでください。");
            List<Rig> staged = new List<Rig>();
            foreach (Mapping map in maps)
            {
                try
                {
                    Rig rig = new Rig(item, map);
                    foreach (Rig existing in ForItem(item))
                        if (existing != editing && SameEndpoints(rig, existing))
                            throw new InvalidOperationException("同じ骨の部位が登録済みです。一覧の「変更」または「有効化」を使ってください。");
                    Check(rig, editing);
                    foreach (Rig earlier in staged) CheckPair(rig, earlier);
                    staged.Add(rig);
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(map.Name + " · " + ex.Message, ex);
                }
            }
            List<OCIFolder> created = new List<OCIFolder>();
            List<HandlePose> originalHandles = new List<HandlePose>();
            List<Channel> channels = new List<Channel>();
            bool fkWasEnabled = item.itemInfo.enableFK;
            bool fkChanged = false;
            OCIFolder group = FindGroup(item);
            bool newGroup = group == null;
            Dictionary<OCIChar.BoneInfo, Vector3> before = new Dictionary<OCIChar.BoneInfo, Vector3>();
            Dictionary<OCIChar.BoneInfo, Quaternion> displayed = new Dictionary<OCIChar.BoneInfo, Quaternion>();
            try
            {
                if (newGroup) group = CreateGroup(item);
                foreach (Rig rig in staged)
                {
                    rig.Group = group;
                    for (int i = 0; i < rig.ControlCount; i++)
                    {
                        bool root = rig.IsRootControl(i);
                        OCIFolder folder = null;
                        if (root && editing != null && editing.Map.Root == rig.Map.Root && editing.RootControl != null)
                        {
                            folder = editing.RootControl;
                            rig.RootRestPosition = editing.RootRestPosition;
                        }
                        else if (!root && editing != null)
                        {
                            int slot = i == 0 ? 0 : editing.Map.Middles.IndexOf(rig.Map.Middles[i - 1]) + 1;
                            if (i == 1 && slot == 0 && editing.Map.Middles.Count > 0 &&
                                !rig.Map.Middles.Contains(editing.Map.Middles[0])) slot = 1;
                            if ((i == 0 || slot > 0) && slot <= editing.MiddleIndices.Length && slot < editing.Controls.Count)
                                folder = editing.Controls[slot];
                        }
                        bool keepPose = folder != null && folder.objectItem != null &&
                            (root || (i == 0 ? editing.Map.Tip == rig.Map.Tip : editing.Map.Middles.Contains(rig.Map.Middles[i - 1])));
                        if (folder != null && folder.objectItem != null)
                        {
                            originalHandles.Add(new HandlePose(folder));
                        }
                        else
                        {
                            folder = AddObjectFolder.Add();
                            created.Add(folder);
                            folder.name = rig.Map.Name +
                                (root ? " · 起点" : i == 0 ? " · 赤" : " · 青 " + i);
                            AttachControl(folder, group);
                        }
                        if (!keepPose)
                        {
                            if (root) folder.guideObject.changeAmount.pos = rig.RootRestPosition;
                            else Place(folder, rig.ControlPosition(i), rig.ControlRotation(i));
                        }
                        Configure(folder, i == 0, true);
                        rig.Controls.Add(folder);
                    }
                    rig.BindRootControl();
                    channels.AddRange(TimelineAccess.Controls(rig));
                }
                if (!fkWasEnabled)
                {
                    foreach (OCIChar.BoneInfo bone in item.listBones)
                    {
                        if (!Rig.HasGuide(bone)) continue;
                        before.Add(bone, bone.guideObject.changeAmount.rot);
                        displayed.Add(bone, Rig.Bone(bone).localRotation);
                    }
                    fkChanged = true;
                    foreach (OCIChar.BoneInfo bone in displayed.Keys)
                        bone.guideObject.changeAmount.rot = displayed[bone].eulerAngles;
                    item.ActiveFK(true);
                    ApplyFK(item);
                }
                if (TimelineReady) Timeline.Key(channels, 0, false);
            }
            catch
            {
                if (fkChanged)
                {
                    item.ActiveFK(fkWasEnabled);
                    foreach (OCIChar.BoneInfo bone in before.Keys) bone.guideObject.changeAmount.rot = before[bone];
                    foreach (OCIChar.BoneInfo bone in displayed.Keys) Rig.Bone(bone).localRotation = displayed[bone];
                }
                foreach (HandlePose pose in originalHandles) pose.Restore();
                foreach (OCIFolder folder in created) DeleteFolder(folder);
                if (newGroup) DeleteEmptyGroup(group);
                throw;
            }
            if (editing != null)
            {
                editing.ReleaseRoot(staged[0].RootControl != null && staged[0].RootControl == editing.RootControl);
                Rigs.Remove(editing);
                // Surplus guides stay as independent scene objects so their
                // existing Timeline keys remain accessible after reassigning.
                foreach (OCIFolder folder in editing.Controls)
                    if (!staged[0].Controls.Contains(folder) && folder != null && folder.objectItem != null)
                    {
                        staged[0].Retired.Add(folder);
                        Configure(folder, true, false);
                    }
                staged[0].Retired.AddRange(editing.Retired);
            }
            Rigs.AddRange(staged);
            Sort();
            Note(TimelineReady ? staged.Count + "部位を確定しました。" : "部位を確定しました。初期キー未作成");
            return staged;
        }

        sealed class HandlePose
        {
            readonly OCIFolder folder;
            readonly Vector3 position, rotation;
            internal HandlePose(OCIFolder folder)
            {
                this.folder = folder;
                position = folder.guideObject.changeAmount.pos;
                rotation = folder.guideObject.changeAmount.rot;
            }
            internal void Restore()
            {
                folder.guideObject.changeAmount.pos = position;
                folder.guideObject.changeAmount.rot = rotation;
                folder.guideObject.ForceUpdate();
            }
        }

        static bool SameEndpoints(Rig a, Rig b)
        {
            return a.Map.Waist == b.Map.Waist && a.Map.Root == b.Map.Root && a.Map.Tip == b.Map.Tip;
        }
    }
}
