using System.Collections.Generic;
using Studio;
using UnityEngine;

namespace GinIkThis
{
    // Only transient editor state lives here. SceneController saves confirmed rigs.
    // Object identity keeps identically named items and bones independent.
    internal sealed class ItemEditorState
    {
        internal Mapping Draft;
        internal Suggestion Fixing;
        internal Rig Editing, Detail, Removing;
        internal readonly List<Suggestion> Suggestions = new List<Suggestion>();
        internal bool Details, BoneList, AddMiddle, BakeOpen, Fps60, Overwrite;
        internal bool Reduce = true;
        internal BakeQuality Quality = BakeQuality.Standard;
        internal int Step;
        internal string Filter, Start, End, Message;
        internal Vector2 Scroll, BoneScroll;
    }

    public sealed partial class Plugin
    {
        readonly Dictionary<OCIItem, ItemEditorState> editors = new Dictionary<OCIItem, ItemEditorState>();
        TreeNodeObject lastSelectedNode;

        void SaveEditor()
        {
            if (!Rig.IsItem(Item)) return;
            ItemEditorState state = new ItemEditorState
            {
                Draft = draft, Fixing = fixing, Editing = editing, Detail = detailRig, Removing = removing,
                Details = details, BoneList = list, AddMiddle = addMiddle, Step = step,
                Filter = filter, Start = start, End = end, Message = Message,
                Scroll = scroll, BoneScroll = boneScroll, BakeOpen = bakeOpen,
                Fps60 = fps60, Overwrite = overwrite, Reduce = reduce, Quality = ReductionQuality
            };
            state.Suggestions.AddRange(suggestions);
            editors[Item] = state;
        }

        void RestoreEditor(OCIItem item)
        {
            ItemEditorState state;
            if (item == null || !editors.TryGetValue(item, out state)) return;
            draft = state.Draft;
            fixing = state.Fixing;
            editing = state.Editing;
            detailRig = state.Detail;
            removing = state.Removing;
            suggestions.AddRange(state.Suggestions);
            details = state.Details;
            list = state.BoneList;
            addMiddle = state.AddMiddle;
            step = state.Step;
            filter = state.Filter;
            start = state.Start;
            end = state.End;
            Message = state.Message;
            scroll = state.Scroll;
            boneScroll = state.BoneScroll;
            bakeOpen = state.BakeOpen;
            fps60 = state.Fps60;
            overwrite = state.Overwrite;
            reduce = state.Reduce;
            ReductionQuality = state.Quality;
            if (draft != null && Shown) ShowManualBones();
        }

        void ReleaseManualBones()
        {
            foreach (OCIChar.BoneInfo bone in temporaryGuides)
                if (Rig.HasGuide(bone) && (!Rig.IsItem(Item) || !Item.itemInfo.enableFK)) bone.active = false;
            temporaryGuides.Clear();
        }

        void ShowManualBones()
        {
            foreach (OCIChar.BoneInfo bone in Item.listBones)
                if (Rig.HasGuide(bone) && !bone.active)
                {
                    bone.active = true;
                    temporaryGuides.Add(bone);
                }
        }

        OCIItem OwnerOf(ObjectCtrlInfo selected)
        {
            OCIItem item = selected as OCIItem;
            if (Rig.IsItem(item)) return item;
            Rig rig = selected == null ? null : ControlRig(selected.guideObject);
            return rig == null ? null : rig.Item;
        }

        static void AfterWorkspaceSelection(TreeNodeObject _node)
        {
            if (Instance != null) Instance.lastSelectedNode = _node;
        }
    }
}
