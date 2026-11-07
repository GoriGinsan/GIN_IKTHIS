using System;
using Studio;
using UnityEngine;

namespace GinIkThis
{
    public sealed partial class Plugin
    {
        OCIFolder FindGroup(OCIItem item)
        {
            foreach (Rig rig in Rigs)
                if (rig.Item == item && rig.Group != null && rig.Group.objectItem != null) return rig.Group;
            return null;
        }

        static OCIFolder CreateGroup(OCIItem item)
        {
            OCIFolder group = AddObjectFolder.Add();
            group.name = "IK THIS";
            try
            {
                Singleton<Studio.Studio>.Instance.treeNodeCtrl.SetParent(group.treeNodeObject, item.treeNodeObject);
                if (group.parentInfo != item) throw new InvalidOperationException("IK THISをアイテムの子に置けません。");
                PrepareGroup(group);
                group.treeNodeObject.treeState = TreeNodeObject.TreeState.Close;
                return group;
            }
            catch
            {
                DeleteFolder(group);
                throw;
            }
        }

        static void PrepareGroup(OCIFolder group)
        {
            // The saved Studio hierarchy owns the guides. Their actual transform
            // frame remains common space so legacy world keys and foot plants stay intact.
            GuideObject guide = group.guideObject;
            Transform target = guide.transformTarget;
            target.SetParent(Singleton<Manager.Scene>.Instance.commonSpace.transform, false);
            guide.parent = null;
            guide.nonconnect = false;
            guide.changeAmount.pos = Vector3.zero;
            guide.changeAmount.rot = Vector3.zero;
            guide.changeAmount.scale = Vector3.one;
            target.localPosition = Vector3.zero;
            target.localRotation = Quaternion.identity;
            target.localScale = Vector3.one;
            guide.calcScale = false;
            guide.SetEnable(0, 0, 0);
            guide.visibleOutside = false;
        }

        static void AttachControl(OCIFolder folder, OCIFolder group)
        {
            if (folder.parentInfo == group)
            {
                folder.guideObject.ForceUpdate();
                return;
            }
            if (folder.parentInfo != null) throw new InvalidOperationException("IKガイドに別の親が付いています。");
            GuideObject guide = folder.guideObject;
            Vector3 position = guide.changeAmount.pos;
            Vector3 rotation = guide.changeAmount.rot;
            Singleton<Studio.Studio>.Instance.treeNodeCtrl.SetParent(folder.treeNodeObject, group.treeNodeObject);
            if (folder.parentInfo != group) throw new InvalidOperationException("IKガイドをグループに置けません。");
            // Both parents use the same world basis. Keep the actual stored values
            // as well as the GuideObject and Timeline track identities.
            guide.changeAmount.pos = position;
            guide.changeAmount.rot = rotation;
            guide.ForceUpdate();
            group.treeNodeObject.treeState = TreeNodeObject.TreeState.Close;
        }

        static void DeleteEmptyGroup(OCIFolder group)
        {
            if (group != null && group.objectItem != null && group.folderInfo.child.Count == 0) DeleteFolder(group);
        }
    }
}
