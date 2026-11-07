using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Xml;
using ExtensibleSaveFormat;
using KKAPI.Studio.SaveLoad;
using KKAPI.Utilities;
using Studio;
using UnityEngine;

namespace GinIkThis
{
    public sealed class SceneController : SceneCustomFunctionController
    {
        protected override void OnSceneSave()
        {
            if (Plugin.Instance == null) return;
            if (Plugin.Instance.Rigs.Count == 0 && Plugin.Instance.pendingRestores == 0)
            {
                SetExtendedData(null);
                return;
            }
            PluginData data = new PluginData { version = 1 };
            data.data["rigs"] = Plugin.Instance.EncodeScene();
            SetExtendedData(data);
        }

        protected override void OnSceneLoad(SceneOperationKind operation, ReadOnlyDictionary<int, ObjectCtrlInfo> objects)
        {
            Plugin plugin = Plugin.Instance;
            if (plugin == null) return;
            if (operation != SceneOperationKind.Import) plugin.ClearScene();
            if (operation == SceneOperationKind.Clear) return;
            PluginData data = GetExtendedData();
            object xml;
            if (data != null && data.version == 1 && data.data.TryGetValue("rigs", out xml))
                plugin.QueueScene(xml as string, objects);
        }

        protected override void OnObjectsCopied(ReadOnlyDictionary<int, ObjectCtrlInfo> objects)
        {
            if (Plugin.Instance != null) Plugin.Instance.QueueCopies(objects);
        }

        protected override void OnObjectDeleted(ObjectCtrlInfo deletedObject)
        {
            if (Plugin.Instance != null) Plugin.Instance.SceneObjectDeleted(deletedObject);
        }
    }

    public sealed partial class Plugin
    {
        internal int pendingRestores;
        internal void ClearScene()
        {
            generation++;
            Choose(null);
            editors.Clear();
            lastSelectedNode = null;
            RestoreBoneSpheres();
            HideMarkers();
            drag = null;
            foreach (Rig rig in Rigs) rig.ReleaseRoot(false);
            Rigs.Clear();
            guideColors.Clear();
        }

        internal void SceneObjectDeleted(ObjectCtrlInfo deleted)
        {
            RemoveMarker(deleted as OCIFolder);
            if (Item == deleted) Choose(null);
            OCIItem deletedItem = deleted as OCIItem;
            if (deletedItem != null) editors.Remove(deletedItem);
            if (deleted.guideObject != null) guideColors.Remove(deleted.guideObject);
            foreach (Rig rig in Rigs.ToArray())
            {
                rig.Retired.Remove(deleted as OCIFolder);
                if (rig.Item == deleted) { Rigs.Remove(rig); continue; }
                if (rig.Controls.Contains(deleted as OCIFolder))
                {
                    Disable(rig);
                    rig.Error = "操作点が削除されました。部位を変更して再設定してください。";
                }
            }
        }

        internal string EncodeScene()
        {
            XmlDocument document = new XmlDocument();
            XmlElement root = document.CreateElement("ginIkThis");
            document.AppendChild(root);
            foreach (Rig rig in Rigs)
            {
                if (!rig.Valid) continue;
                XmlElement node = document.CreateElement("rig");
                node.SetAttribute("item", XmlConvert.ToString(rig.Item.objectInfo.dicKey));
                if (rig.Group != null) node.SetAttribute("group", XmlConvert.ToString(rig.Group.objectInfo.dicKey));
                node.SetAttribute("name", rig.Map.Name);
                node.SetAttribute("waist", XmlConvert.ToString(rig.Map.Waist));
                node.SetAttribute("pole", XmlConvert.ToString(rig.Map.Pole));
                node.SetAttribute("enabled", XmlConvert.ToString(rig.Enabled));
                if (!rig.LegacyDisabled) node.SetAttribute("baked", XmlConvert.ToString(rig.Baked));
                if (rig.NodeLinks > 0) node.SetAttribute("nodeLinks", XmlConvert.ToString(rig.NodeLinks));
                if (!rig.Map.Waist) node.SetAttribute("root", BoneKey(rig.Item, rig.Map.Root));
                node.SetAttribute("tip", BoneKey(rig.Item, rig.Map.Tip));
                for (int i = 0; i < rig.Controls.Count; i++)
                {
                    XmlElement guide = document.CreateElement("guide");
                    guide.SetAttribute("key", XmlConvert.ToString(rig.Controls[i].objectInfo.dicKey));
                    if (rig.IsRootControl(i))
                    {
                        guide.SetAttribute("root", "true");
                        guide.SetAttribute("restX", XmlConvert.ToString(rig.RootRestPosition.x));
                        guide.SetAttribute("restY", XmlConvert.ToString(rig.RootRestPosition.y));
                        guide.SetAttribute("restZ", XmlConvert.ToString(rig.RootRestPosition.z));
                    }
                    else if (i > 0) guide.SetAttribute("bone", BoneKey(rig.Item, rig.Map.Middles[i - 1]));
                    node.AppendChild(guide);
                }
                foreach (OCIFolder folder in rig.Retired)
                {
                    if (folder == null || folder.objectItem == null) continue;
                    XmlElement retired = document.CreateElement("retired");
                    retired.SetAttribute("key", XmlConvert.ToString(folder.objectInfo.dicKey));
                    node.AppendChild(retired);
                }
                root.AppendChild(node);
            }
            return document.OuterXml;
        }

        static string BoneKey(OCIItem item, OCIChar.BoneInfo bone)
        {
            Transform transform = Rig.Bone(bone);
            List<string> names = new List<string>();
            for (Transform t = transform; t != item.guideObject.transformTarget; t = t.parent)
            {
                if (t == null) throw new InvalidOperationException("アイテム階層の外にあるFKボーンです。");
                // The name is encoded per segment so literal separators are safe.
                names.Add(t.GetSiblingIndex().ToString(CultureInfo.InvariantCulture) + ":" + Uri.EscapeDataString(t.name));
            }
            names.Reverse();
            return String.Join("/", names.ToArray());
        }

        static OCIChar.BoneInfo FindBone(OCIItem item, string key)
        {
            foreach (OCIChar.BoneInfo bone in item.listBones)
                if (Rig.HasGuide(bone) && BoneKey(item, bone) == key) return bone;
            throw new InvalidOperationException("保存したFKボーンを見つけられません · " + key);
        }

        internal void QueueScene(string xml, IDictionary<int, ObjectCtrlInfo> objects)
        {
            pendingRestores++;
            StartCoroutine(RestoreLater(xml, new Dictionary<int, ObjectCtrlInfo>(objects), generation));
        }

        IEnumerator RestoreLater(string xml, Dictionary<int, ObjectCtrlInfo> objects, int epoch)
        {
            // KKAPI's remap belongs to this load/import. Let Timeline finish
            // restoring its native tracks, then bind only objects in that remap.
            for (int i = 0; i < 30; i++) yield return null;
            pendingRestores--;
            if (epoch != generation || String.IsNullOrEmpty(xml)) yield break;
            Attempt(delegate { RestoreScene(xml, objects); });
        }

        void RestoreScene(string xml, Dictionary<int, ObjectCtrlInfo> objects)
        {
            if (xml.Length > 8 * 1024 * 1024) throw new IOException("IKTHISのシーンデータが大きすぎます。");
            XmlReaderSettings settings = new XmlReaderSettings { ProhibitDtd = true, XmlResolver = null };
            XmlDocument document = new XmlDocument { XmlResolver = null };
            using (StringReader text = new StringReader(xml))
            using (XmlReader reader = XmlReader.Create(text, settings))
                document.Load(reader);
            if (document.DocumentElement == null || document.DocumentElement.Name != "ginIkThis")
                throw new IOException("IKTHISのシーンデータが不正です。");
            HashSet<OCIFolder> claimed = new HashSet<OCIFolder>();
            foreach (Rig rig in Rigs) foreach (OCIFolder folder in rig.Controls) claimed.Add(folder);
            foreach (XmlNode row in document.DocumentElement.ChildNodes)
            {
                XmlElement node = row as XmlElement;
                if (node == null || node.Name != "rig") continue;
                Attempt(delegate
                {
                    OCIItem item = GetObject<OCIItem>(objects, node, "item");
                    if (!Rig.IsItem(item)) throw new InvalidOperationException("保存したFKアイテムが見つかりません。");
                    OCIFolder group = node.HasAttribute("group") ? GetObject<OCIFolder>(objects, node, "group") : FindGroup(item);
                    if (node.HasAttribute("group") && (group == null || group.parentInfo != item))
                        throw new InvalidOperationException("保存したIK THISグループが見つかりません。");
                    Mapping map = new Mapping
                    {
                        Name = node.GetAttribute("name"),
                        Waist = XmlConvert.ToBoolean(node.GetAttribute("waist")),
                        Pole = XmlConvert.ToBoolean(node.GetAttribute("pole")),
                        Tip = FindBone(item, node.GetAttribute("tip"))
                    };
                    if (!map.Waist) map.Root = FindBone(item, node.GetAttribute("root"));
                    List<OCIFolder> controls = new List<OCIFolder>();
                    Vector3 rootRest = Vector3.zero;
                    foreach (XmlElement element in node.GetElementsByTagName("guide"))
                    {
                        OCIFolder folder = GetObject<OCIFolder>(objects, element, "key");
                        if (folder == null || claimed.Contains(folder) || controls.Contains(folder) ||
                            folder.parentInfo != null && folder.parentInfo != group)
                            throw new InvalidOperationException(map.Name + "のガイドを特定できません。");
                        if (map.MoveRoot) throw new InvalidOperationException(map.Name + "の起点ガイドの順序が不正です。");
                        if (element.GetAttribute("root") == "true")
                        {
                            if (controls.Count == 0) throw new InvalidOperationException(map.Name + "の赤ガイドがありません。");
                            map.MoveRoot = true;
                            rootRest = new Vector3(XmlConvert.ToSingle(element.GetAttribute("restX")),
                                XmlConvert.ToSingle(element.GetAttribute("restY")), XmlConvert.ToSingle(element.GetAttribute("restZ")));
                            if (!TwoBone.Finite(rootRest)) throw new InvalidOperationException(map.Name + "の起点座標が無効です。");
                        }
                        else if (controls.Count > 0) map.Middles.Add(FindBone(item, element.GetAttribute("bone")));
                        controls.Add(folder);
                    }
                    Rig rig = new Rig(item, map);
                    rig.Controls.AddRange(controls);
                    if (!rig.Valid) throw new InvalidOperationException(map.Name + "のガイドが不足しています。");
                    if (group == null) group = CreateGroup(item);
                    else PrepareGroup(group);
                    rig.Group = group;
                    foreach (OCIFolder folder in controls) AttachControl(folder, group);
                    if (map.MoveRoot) rig.RootRestPosition = rootRest;
                    rig.BindRootControl();
                    rig.Enabled = false;
                    rig.Baked = node.GetAttribute("baked") == "true";
                    rig.NodeLinks = node.HasAttribute("nodeLinks") ? XmlConvert.ToInt32(node.GetAttribute("nodeLinks")) : 0;
                    bool enabled = XmlConvert.ToBoolean(node.GetAttribute("enabled"));
                    rig.LegacyDisabled = !enabled && !node.HasAttribute("baked");
                    foreach (XmlElement element in node.GetElementsByTagName("retired"))
                    {
                        OCIFolder folder = GetObject<OCIFolder>(objects, element, "key");
                        if (folder == null || claimed.Contains(folder) || controls.Contains(folder) ||
                            folder.parentInfo != null && folder.parentInfo != group) continue;
                        AttachControl(folder, group);
                        rig.Retired.Add(folder);
                        claimed.Add(folder);
                        Configure(folder, true, false);
                    }
                    Rigs.Add(rig);
                    foreach (OCIFolder folder in controls) claimed.Add(folder);
                    if (enabled) RestoreEnabled(rig);
                });
            }
            Sort();
        }

        void RestoreEnabled(Rig rig)
        {
            try { SetEnabled(rig, true); }
            catch (Exception ex) { rig.Error = ex.Message; Note(rig.Map.Name + " · " + ex.Message); }
        }

        static T GetObject<T>(IDictionary<int, ObjectCtrlInfo> objects, XmlElement node, string attribute) where T : ObjectCtrlInfo
        {
            ObjectCtrlInfo value;
            return objects.TryGetValue(XmlConvert.ToInt32(node.GetAttribute(attribute)), out value) ? value as T : null;
        }

        internal void QueueCopies(IDictionary<int, ObjectCtrlInfo> objects)
        {
            pendingRestores++;
            StartCoroutine(CopyLater(Rigs.ToArray(), new Dictionary<int, ObjectCtrlInfo>(objects), generation));
        }

        IEnumerator CopyLater(Rig[] originals, Dictionary<int, ObjectCtrlInfo> copied, int epoch)
        {
            for (int i = 0; i < 30; i++) yield return null;
            pendingRestores--;
            if (epoch != generation) yield break;
            foreach (Rig original in originals)
            {
                ObjectCtrlInfo entry;
                if (original.Valid && copied.TryGetValue(original.Item.objectInfo.dicKey, out entry) && entry is OCIItem)
                    Attempt(delegate { CopyRig(original, (OCIItem)entry, copied); });
            }
            Sort();
        }

        void CopyRig(Rig original, OCIItem item, Dictionary<int, ObjectCtrlInfo> copied)
        {
            Mapping map = original.Map.Copy();
            if (!map.Waist) map.Root = FindBone(item, BoneKey(original.Item, map.Root));
            map.Tip = FindBone(item, BoneKey(original.Item, map.Tip));
            for (int i = 0; i < map.Middles.Count; i++)
                map.Middles[i] = FindBone(item, BoneKey(original.Item, map.Middles[i]));
            Rig rig = new Rig(item, map);
            List<OCIFolder> created = new List<OCIFolder>();
            OCIFolder group = FindGroup(item);
            bool newGroup = false;
            try
            {
                ObjectCtrlInfo groupEntry;
                if (group == null && original.Group != null && copied.TryGetValue(original.Group.objectInfo.dicKey, out groupEntry))
                    group = groupEntry as OCIFolder;
                if (group == null) { group = CreateGroup(item); newGroup = true; }
                if (group.parentInfo != item) throw new InvalidOperationException("複製したIK THISグループの親を特定できません。");
                PrepareGroup(group);
                rig.Group = group;
                foreach (OCIFolder source in original.Controls)
                {
                    ObjectCtrlInfo entry;
                    OCIFolder target = copied.TryGetValue(source.objectInfo.dicKey, out entry) ? entry as OCIFolder : null;
                    if (target == null)
                    {
                        target = AddObjectFolder.Add();
                        created.Add(target);
                        target.name = source.name + " · 複製";
                        AttachControl(target, group);
                        if (source == original.RootControl)
                            target.guideObject.changeAmount.pos = source.guideObject.changeAmount.pos;
                        else Place(target, source.guideObject.transformTarget.position, source.guideObject.transformTarget.rotation);
                    }
                    else AttachControl(target, group);
                    // Studio duplicates child folders, but Timeline 1.5.6
                    // only duplicates item and character tracks itself.
                    if (TimelineReady) Timeline.Copy(source, target);
                    rig.Controls.Add(target);
                }
                if (map.MoveRoot) rig.RootRestPosition = original.RootRestPosition;
                rig.BindRootControl();
                foreach (OCIFolder source in original.Retired)
                {
                    ObjectCtrlInfo entry;
                    if (source == null || !copied.TryGetValue(source.objectInfo.dicKey, out entry)) continue;
                    OCIFolder target = entry as OCIFolder;
                    if (target == null) continue;
                    AttachControl(target, group);
                    if (TimelineReady) Timeline.Copy(source, target);
                    rig.Retired.Add(target);
                    Configure(target, true, false);
                }
                rig.Enabled = false;
                rig.Baked = original.Baked;
                rig.NodeLinks = original.NodeLinks;
                rig.LegacyDisabled = original.LegacyDisabled;
                Rigs.Add(rig);
                if (original.Enabled) RestoreEnabled(rig);
            }
            catch
            {
                foreach (OCIFolder folder in created) DeleteFolder(folder);
                if (newGroup) DeleteEmptyGroup(group);
                throw;
            }
        }
    }
}
