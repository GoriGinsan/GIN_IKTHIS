using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using KKAPI.Studio.SaveLoad;
using Studio;
using UnityEngine;
using UnityEngine.EventSystems;

namespace GinIkThis
{
    public sealed partial class Plugin
    {
        bool autoKey;
        readonly Dictionary<GuideObject, bool> hiddenSpheres = new Dictionary<GuideObject, bool>();
        readonly HashSet<GuideObject> drivenSpheres = new HashSet<GuideObject>();
        readonly List<GuideObject> releasedSpheres = new List<GuideObject>();
        OCIItem borrowedFkSelection;
        Drag drag;

        sealed class Drag
        {
            internal object Axis;
            internal float Time;
            internal readonly List<Channel> Channels = new List<Channel>();
            internal readonly List<Vector3> Before = new List<Vector3>();
        }

        static HarmonyMethod Hook(string name)
        {
            return new HarmonyMethod(typeof(Plugin).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic));
        }

        OCIItem SelectedItem()
        {
            ObjectCtrlInfo[] selection = Studio.Studio.GetSelectObjectCtrl();
            if (selection == null || selection.Length == 0) return null;
            // Studio's multi-selection is a HashSet, so its first entry is not
            // the last clicked item. Remember the native selection event instead.
            foreach (ObjectCtrlInfo selected in selection)
                if (selected != null && selected.treeNodeObject == lastSelectedNode) return OwnerOf(selected);
            return OwnerOf(selection[0]);
        }

        Rig ControlRig(GuideObject guide)
        {
            if (guide == null) return null;
            foreach (Rig rig in Rigs)
                foreach (OCIFolder control in rig.Controls)
                    if (control != null && control.guideObject == guide) return rig;
            return null;
        }

        bool HasLive(OCIItem item)
        {
            return item != null && Rigs.Exists(delegate(Rig rig) { return rig.Item == item && rig.Enabled && rig.Valid; });
        }

        void UpdateAuthoring()
        {
            if (StudioSaveLoadApi.LoadInProgress || StudioSaveLoadApi.ImportInProgress)
            {
                drag = null;
                RestoreBoneSpheres();
                return;
            }
            OCIItem selected = SelectedItem();
            if (Shown && !Baking && selected != Item) Choose(selected);
            SyncFkSelection(selected);
            drivenSpheres.Clear();
            if (selected != null && !Picking)
                foreach (Rig rig in Rigs)
                {
                    if (rig.Item != selected || !rig.Enabled || !rig.Valid) continue;
                    foreach (OCIChar.BoneInfo bone in rig.Bones)
                        if (Rig.HasGuide(bone)) drivenSpheres.Add(bone.guideObject);
                }
            releasedSpheres.Clear();
            foreach (KeyValuePair<GuideObject, bool> row in hiddenSpheres)
            {
                if (row.Key != null && drivenSpheres.Contains(row.Key)) continue;
                if (row.Key != null) row.Key.visibleOutside = row.Value;
                releasedSpheres.Add(row.Key);
            }
            foreach (GuideObject guide in releasedSpheres) hiddenSpheres.Remove(guide);
            foreach (GuideObject guide in drivenSpheres)
            {
                if (!hiddenSpheres.ContainsKey(guide)) hiddenSpheres.Add(guide, guide.visibleOutside);
                guide.visibleOutside = false;
            }
        }

        static bool HasWorkspaceSelection(OCIItem item)
        {
            ObjectCtrlInfo[] selection = Studio.Studio.GetSelectObjectCtrl();
            return selection != null && Array.IndexOf(selection, item) >= 0;
        }

        void SyncFkSelection(OCIItem selected)
        {
            // Native Studio selects the helper folder when an IK marker is
            // clicked. Keep that item's FK click layers until the user leaves it.
            OCIItem borrowed = selected != null && !HasWorkspaceSelection(selected) ? selected : null;
            if (borrowed == borrowedFkSelection) return;
            if (Rig.IsItem(borrowedFkSelection)) borrowedFkSelection.OnSelect(HasWorkspaceSelection(borrowedFkSelection));
            borrowedFkSelection = borrowed;
            if (Rig.IsItem(borrowedFkSelection)) borrowedFkSelection.OnSelect(true);
        }

        void RestoreBoneSpheres()
        {
            foreach (KeyValuePair<GuideObject, bool> row in hiddenSpheres)
                if (row.Key != null) row.Key.visibleOutside = row.Value;
            hiddenSpheres.Clear();
            SyncFkSelection(null);
        }

        void DrawKeyButtons(OCIItem item)
        {
            bool previous = GUI.enabled;
            GUI.enabled = previous && TimelineReady && !Timeline.Playing && HasLive(item);
            if (GUILayout.Button("現在時刻にキー")) Attempt(delegate { Key(item, false); });
            autoKey = GUILayout.Toggle(autoKey, "自動キー");
            GUI.enabled = previous;
        }


        static void AfterGuideClick(GuideSelect __instance, PointerEventData _eventData)
        {
            Plugin plugin = Instance;
            if (plugin == null || plugin.Picking || _eventData.button != PointerEventData.InputButton.Left) return;
            GuideObject guide = __instance.guideObject;
            Rig rig = plugin.ControlRig(guide);
            if (rig == null || !rig.Enabled) return;
            GuideObjectManager manager = GuideObjectManager.Instance;
            if (manager.mode == 2 || manager.mode == 1 && !guide.enableRot) manager.mode = 0;
        }

        static void AfterBeginDrag(GuideBase __instance, PointerEventData __0)
        {
            Plugin plugin = Instance;
            if (plugin == null || __0.button != PointerEventData.InputButton.Left) return;
            plugin.drag = null;
            bool rotation = __instance is GuideRotation;
            if (!rotation && !(__instance is GuideMove)) return;
            if (!plugin.autoKey || !plugin.TimelineReady || plugin.Timeline.Playing || plugin.Picking || plugin.Baking) return;
            GuideObject primary = __instance.guideObject;
            Rig active = plugin.ControlRig(primary);
            if (active == null || !active.Enabled) return;
            GuideObject[] selected = GuideObjectManager.Instance.selectObjects;
            if (selected == null) return;
            Drag drag = new Drag { Axis = __instance, Time = plugin.Timeline.Time };
            foreach (GuideObject guide in selected)
            {
                Rig rig = plugin.ControlRig(guide);
                if (rig == null || rig.Item != active.Item || !rig.Enabled || !rig.Valid ||
                    (rotation ? !guide.enableRot : !guide.enablePos)) continue;
                OCIFolder control = rig.Controls.Find(delegate(OCIFolder folder) { return folder.guideObject == guide; });
                drag.Channels.Add(new Channel(control, guide, rotation));
                drag.Before.Add(rotation ? guide.changeAmount.rot : guide.changeAmount.pos);
            }
            plugin.drag = drag;
        }

        static void AfterEndDrag(GuideBase __instance)
        {
            Plugin plugin = Instance;
            if (plugin != null) plugin.FinishDrag(__instance);
        }

        internal void FinishDrag(object source)
        {
            if (drag == null || !ReferenceEquals(drag.Axis, source)) return;
            Drag completed = drag;
            drag = null;
            if (!autoKey || !TimelineReady || Timeline.Playing || Baking ||
                Mathf.Abs(Timeline.Time - completed.Time) > .00001f) return;
            List<Channel> changed = new List<Channel>();
            for (int i = 0; i < completed.Channels.Count; i++)
            {
                Channel channel = completed.Channels[i];
                Rig rig = ControlRig(channel.Guide);
                if (rig == null || !rig.Valid || !rig.Enabled) continue;
                Vector3 before = completed.Before[i];
                Vector3 after = channel.Rotation ? channel.Guide.changeAmount.rot : channel.Guide.changeAmount.pos;
                if (channel.Rotation)
                    after = new Vector3(Mathf.DeltaAngle(before.x, after.x), Mathf.DeltaAngle(before.y, after.y), Mathf.DeltaAngle(before.z, after.z)) + before;
                if ((after - before).sqrMagnitude > 1e-12f) changed.Add(channel);
            }
            if (changed.Count == 0) return;
            Attempt(delegate
            {
                Timeline.Key(changed, completed.Time, true);
                Note("自動キー · " + changed.Count + "チャンネルを記録しました。");
            });
        }
        static bool BeforeGuideClick(GuideSelect __instance, PointerEventData _eventData)
        {
            Plugin plugin = Instance;
            if (plugin == null || !plugin.Picking || _eventData.button != PointerEventData.InputButton.Left) return true;
            plugin.lastGuideClickFrame = Time.frameCount;
            OCIChar.BoneInfo picked = plugin.Item.listBones.Find(delegate(OCIChar.BoneInfo bone)
            {
                return Rig.HasGuide(bone) && bone.guideObject.guideSelect == __instance;
            });
            plugin.Attempt(delegate { plugin.Pick(picked); });
            return false;
        }

    }
}
