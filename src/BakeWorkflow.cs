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
    // Coordinates baking, cancellation and finalization; Timeline owns key transactions.
    public sealed partial class Plugin
    {
        internal int LastBakeDenseKeys, LastBakeWrittenKeys;
        internal BakeQuality ReductionQuality = BakeQuality.Standard;
        string bakeReduction = "";

        internal void RecordBakeSize(int dense, int written, bool reduced)
        {
            LastBakeDenseKeys = dense;
            LastBakeWrittenKeys = written;
            float percent = dense == 0 ? 0 : 100f * (dense - written) / dense;
            bakeReduction = reduced ? "（軽量化 " + dense + " → " + written + "、" +
                percent.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "%削減）" : "";
        }

        internal void FinalizeForSharing(int fps, bool overwrite, bool reduceKeys)
        {
            SharingPlan plan = PrepareSharing(false);
            int keys = plan.Live.Count == 0 ? 0 : Timeline.BakeRigs(this, plan.Live, 0, Timeline.Duration, fps, overwrite, reduceKeys);
            FinishSharing(plan, keys, false);
        }

        sealed class SharingPlan
        {
            internal readonly List<Rig> Live = new List<Rig>();
            internal readonly List<OCIFolder> Guides = new List<OCIFolder>();
            internal readonly HashSet<OCIFolder> Groups = new HashSet<OCIFolder>();
        }

        SharingPlan PrepareSharing(bool observed)
        {
            if (!TimelineReady || Timeline.Playing) throw new InvalidOperationException("Timelineを停止してから配布用に確定してください。");
            if (pendingRestores != 0) throw new InvalidOperationException("シーンや複製の復元を待ってから確定してください。");
            if (Rigs.Count == 0) throw new InvalidOperationException("確定するIK部位がありません。");
            SharingPlan plan = new SharingPlan();
            foreach (Rig rig in Rigs)
            {
                if (!rig.Valid || !rig.Item.itemInfo.enableFK)
                    throw new InvalidOperationException(rig.Map.Name + " のFKまたはガイドを確認してください。");
                if (rig.Enabled) plan.Live.Add(rig);
                else if ((!rig.Baked && !rig.LegacyDisabled) || !Timeline.HasBakedTracks(rig))
                    throw new InvalidOperationException(rig.Item.treeNodeObject.textName + " · " + rig.Map.Name +
                        " に確認できる焼き込みがありません。有効化して焼き込むか、部位を解除してください。");
                foreach (OCIFolder guide in OwnedFolders(rig))
                {
                    drivers.CheckRemoval(guide, false, observed);
                    if (!plan.Guides.Contains(guide)) plan.Guides.Add(guide);
                }
                if (rig.Group != null) plan.Groups.Add(rig.Group);
            }
            foreach (OCIFolder group in plan.Groups)
            {
                drivers.CheckRemoval(group, true);
                foreach (ObjectInfo child in group.folderInfo.child)
                    if (!plan.Guides.Exists(delegate(OCIFolder guide) { return guide.objectInfo == child; }))
                        throw new InvalidOperationException("IK THISグループに別のオブジェクトがあります。先に移動してください。");
            }
            return plan;
        }

        void FinishSharing(SharingPlan plan, int keys, bool observed)
        {
            HashSet<OCIFolder> retained = observed && KeepNodes ? drivers.ReferencedFolders(plan.Guides) : new HashSet<OCIFolder>();
            if (observed && !KeepNodes) drivers.RemoveIncoming(plan.Guides);
            foreach (OCIFolder guide in plan.Guides)
                if (!retained.Contains(guide)) DeleteFolder(guide);
            foreach (OCIFolder group in plan.Groups) DeleteEmptyGroup(group);
            Rigs.Clear();
            ResetDraft();
            editors.Clear();
            suggestions.Clear();
            detailRig = removing = null;
            RestoreBoneSpheres();
            Note("配布用に確定しました。標準FK " + keys + " キーを追加し、IK同期を解除しました。" +
                (keys > 0 ? bakeReduction : "") + "Studioでシーンを保存してください。");
        }

        internal List<DriverCheck.Transfer> PrepareNodeTransfers(List<Rig> rigs)
        {
            return KeepNodes ? drivers.Transfers(rigs, false) : new List<DriverCheck.Transfer>();
        }

        internal void BeginBake(OCIItem item, float from, float to, int fps, bool overwrite, bool reduce)
        {
            if (Baking) throw new InvalidOperationException("焼き込みの完了を待ってください。");
            List<Rig> rigs = ForItem(item).FindAll(delegate(Rig rig) { return rig.Enabled; });
            StartCoroutine(RunBake(Timeline.BakeObserved(this, rigs, from, to, fps, overwrite, reduce, delegate(int count)
            {
                Note("標準FKへ " + count + " キーを焼き込み、ライブIKを停止しました。" + bakeReduction);
            })));
        }

        internal void BeginSharing(int fps, bool overwrite, bool reduce)
        {
            if (Baking) throw new InvalidOperationException("焼き込みの完了を待ってください。");
            SharingPlan plan = PrepareSharing(true);
            if (plan.Live.Count == 0) { FinishSharing(plan, 0, true); return; }
            StartCoroutine(RunBake(Timeline.BakeObserved(this, plan.Live, 0, Timeline.Duration, fps, overwrite, reduce,
                delegate(int count) { FinishSharing(plan, count, true); })));
        }

        IEnumerator RunBake(IEnumerator workflow)
        {
            Baking = true;
            CancelBake = false;
            try
            {
                while (true)
                {
                    bool more = false;
                    Exception error = null;
                    try { more = workflow.MoveNext(); }
                    catch (Exception ex) { error = ex; }
                    if (error != null) { Note(error.Message); Logger.LogWarning(error); break; }
                    if (!more) break;
                    yield return workflow.Current;
                }
            }
            finally
            {
                IDisposable disposable = workflow as IDisposable;
                if (disposable != null) disposable.Dispose();
                Baking = false;
            }
        }

        internal void Key(OCIItem item, bool zero)
        {
            if (!TimelineReady) throw new InvalidOperationException("初期キー未作成 · Timelineを利用できません。");
            List<Channel> channels = new List<Channel>();
            foreach (Rig rig in ForItem(item))
                if (rig.Valid && (rig.Enabled || zero)) channels.AddRange(TimelineAccess.Controls(rig));
            if (channels.Count == 0) throw new InvalidOperationException("記録するガイドがありません。");
            Timeline.Key(channels, zero ? 0 : Timeline.Time, !zero);
            Note(zero ? "不足していた0:00のキーを追加しました。" : "現在時刻にガイドのキーを記録しました。");
        }

        internal bool MissingInitial(Rig rig)
        {
            if (!rig.Valid || !TimelineReady) return true;
            foreach (Channel channel in TimelineAccess.Controls(rig))
                if (!Timeline.HasZero(channel)) return true;
            return false;
        }

    }
}
