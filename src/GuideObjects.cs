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
    // Native Studio helper-guide creation, appearance and selection.
    public sealed partial class Plugin
    {
        internal static void Place(OCIFolder folder, Vector3 position, Quaternion rotation)
        {
            GuideObject guide = folder.guideObject;
            Transform target = guide.transformTarget;
            target.position = position;
            target.rotation = rotation;
            guide.changeAmount.pos = target.localPosition;
            guide.changeAmount.rot = target.localEulerAngles;
            guide.ForceUpdate();
        }

        void Configure(OCIFolder folder, bool red, bool show)
        {
            if (folder == null || folder.objectItem == null) return;
            GuideObject guide = folder.guideObject;
            if (guide == null || guide.guideSelect == null) return;
            if (guide.enableRot != red || guide.enableScale || !guide.enablePos)
                guide.SetEnable(1, red ? 1 : 0, 0);
            guide.calcScale = false;
            guide.scaleRate = 1f;
            guide.scaleRot = .075f;
            guide.scaleSelect = .1f;
            bool previous;
            if (!guideColors.TryGetValue(guide, out previous) || red != previous)
            {
                guideColors[guide] = red;
                guide.guideSelect.color = red ? Color.red : Color.blue;
            }
            guide.guideSelect.treeNodeObject = folder.treeNodeObject;
            guide.visibleOutside = show;
        }

        static void DeleteFolder(OCIFolder folder)
        {
            if (folder != null && folder.objectItem != null && Singleton<Studio.Studio>.IsInstance())
                Studio.Studio.DeleteNode(folder.treeNodeObject);
        }

        internal static void Select(ObjectCtrlInfo info)
        {
            Singleton<Studio.Studio>.Instance.treeNodeCtrl.SelectSingle(info.treeNodeObject, false);
        }

        internal static void SelectControl(OCIFolder folder)
        {
            Select(folder);
            GuideObjectManager manager = GuideObjectManager.Instance;
            if (manager.mode == 2 || manager.mode == 1 && !folder.guideObject.enableRot) manager.mode = 0;
        }

    }
}
