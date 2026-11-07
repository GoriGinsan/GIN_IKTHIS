using System;
using System.Collections.Generic;
using System.Globalization;
using Studio;
using UnityEngine;

namespace GinIkThis
{
    public sealed partial class Plugin
    {
        Rect Window = new Rect(68, 50, 465, 525);
        Vector2 scroll, boneScroll;
        readonly List<Suggestion> suggestions = new List<Suggestion>();
        readonly List<OCIChar.BoneInfo> temporaryGuides = new List<OCIChar.BoneInfo>();
        Mapping draft;
        Suggestion fixing;
        Rig editing, detailRig, removing;
        bool details, list, addMiddle;
        int step;
        string filter = "", start = "0", end = "1";
        bool fps60, overwrite, bakeOpen, shareArmed, shareOverwrite;
        bool reduce = true;
        Vector2 clickStart;
        int lastGuideClickFrame = -1;

        internal bool Picking { get { return Shown && Rig.IsItem(Item) && draft != null; } }

        void CheckUnmatchedClick()
        {
            if (!Picking) return;
            Vector2 mouse = new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y);
            if (Input.GetMouseButtonDown(0)) clickStart = mouse;
            if (Input.GetMouseButtonUp(0) && (mouse - clickStart).sqrMagnitude < 16 &&
                !Window.Contains(mouse) && !Window.Contains(clickStart))
                StartCoroutine(UnmatchedClick(draft, Time.frameCount));
        }

        System.Collections.IEnumerator UnmatchedClick(Mapping mapping, int frame)
        {
            yield return null;
            if (Picking && draft == mapping && lastGuideClickFrame < frame)
                Message = "対象アイテムのFKボーンの丸か、骨一覧を選んでください。";
        }

        void Choose(OCIItem item)
        {
            SaveEditor();
            ResetDraft();
            drag = null;
            Item = item;
            suggestions.Clear();
            detailRig = removing = null;
            details = false;
            bakeOpen = shareArmed = shareOverwrite = false;
            scroll = Vector2.zero;
            boneScroll = Vector2.zero;
            fps60 = overwrite = false;
            reduce = true;
            ReductionQuality = BakeQuality.Standard;
            start = "0";
            end = TimelineReady ? Timeline.Duration.ToString("R", CultureInfo.InvariantCulture) : "1";
            Message = item == null ? "ワークスペースでFK付きStudioアイテムを1つ選択してください。" : "骨の割り当て方法を選んでください。";
            RestoreEditor(item);
        }

        void Close()
        {
            Shown = false;
            ReleaseManualBones();
            RestoreBoneSpheres();
        }

        void ResetDraft()
        {
            ReleaseManualBones();
            draft = null;
            editing = null;
            fixing = null;
            addMiddle = false;
            list = false;
            step = 0;
            filter = "";
        }

        void BeginManual(Mapping map, Rig old, Suggestion suggestion, bool extra)
        {
            ResetDraft();
            draft = map;
            editing = old;
            fixing = suggestion;
            addMiddle = extra;
            step = draft.Tip != null ? 3 : 0;
            ShowManualBones();
            Message = extra ? "起点と先端の間にあるFKボーンを選んでください。" : "Studioの骨の丸か、骨一覧で指定してください。";
        }

        internal void Pick(OCIChar.BoneInfo bone)
        {
            if (!Picking) return;
            if (!Rig.Has(Item, bone)) throw new ArgumentException("対象アイテムのFKボーンをクリックしてください。");
            if (addMiddle)
            {
                if (!Rig.Below(bone, draft.Root) || !Rig.Below(draft.Tip, bone) || draft.Middles.Contains(bone))
                    throw new ArgumentException("起点と先端の間で、青ガイドに未使用の骨を選んでください。");
                draft.Middles.Add(bone);
                draft.Middles.Sort(delegate(OCIChar.BoneInfo a, OCIChar.BoneInfo b)
                {
                    return Depth(Rig.Bone(a)).CompareTo(Depth(Rig.Bone(b)));
                });
                draft.Pole = false;
                addMiddle = false;
                step = 3;
                Message = "青ガイドを追加する位置をプレビューしています。「確定」で登録します。";
                return;
            }
            if (step == 3)
            {
                draft.Root = draft.Tip = null;
                draft.Middles.Clear();
                step = 0;
                Message = "編集中の指定をリセットしました。次は起点です。";
                return;
            }
            if (draft.Waist)
            {
                Mapping candidate = draft.Copy();
                candidate.Tip = bone;
                new Rig(Item, candidate);
                draft = candidate;
                step = 3;
                Message = "腰の赤ガイドをプレビューしています。";
                return;
            }
            if (step == 0)
            {
                if (!Item.listBones.Exists(delegate(OCIChar.BoneInfo middle)
                {
                    return Rig.Below(middle, bone) &&
                        Item.listBones.Exists(delegate(OCIChar.BoneInfo tip) { return Rig.Below(tip, middle); });
                })) throw new ArgumentException("先に中間と先端のFKボーンが続く起点を選んでください。");
                draft.Root = bone;
                step = 1;
                Message = "次は起点の下にある中間の骨です。";
                return;
            }
            if (step == 1)
            {
                if (!Rig.Below(bone, draft.Root) || !Item.listBones.Exists(delegate(OCIChar.BoneInfo b) { return Rig.Below(b, bone); }))
                    throw new ArgumentException("起点より下で、その先にFKボーンがある骨を選んでください。");
                draft.Middles.Add(bone);
                step = 2;
                Message = "次は中間の下にある先端の骨です。";
                return;
            }
            Mapping test = draft.Copy();
            test.Tip = bone;
            Rig proposed = new Rig(Item, test);
            if (fixing == null && editing == null) test.Pole = proposed.Bones.Length == 3;
            draft = test;
            step = 3;
            Message = "「確定」で登録します。もう一度骨を選ぶと編集中の指定をリセットします。";
        }

        void FindSuggestions()
        {
            ResetDraft();
            suggestions.Clear();
            suggestions.AddRange(Recommendations.Find(Item));
            foreach (Suggestion row in suggestions)
            {
                if (row.Map == null) continue;
                try { Check(new Rig(Item, row.Map), null); }
                catch (InvalidOperationException ex) { row.Adopt = false; row.Error = ex.Message; }
            }
            Message = "候補を確認し、採用する部位にチェックを入れてください。";
        }

        void CommitDraft()
        {
            if (draft != null)
            {
                if (addMiddle || step != 3) throw new InvalidOperationException("骨の指定を完了してください。");
                if (fixing != null)
                {
                    fixing.Map = draft.Copy();
                    fixing.Adopt = true;
                    fixing.Error = null;
                    ResetDraft();
                    Note("候補を修正しました。もう一度「確定」で採用する部位を登録します。");
                    return;
                }
                Confirm(Item, new[] { draft }, editing);
            }
            else
            {
                List<Mapping> maps = new List<Mapping>();
                foreach (Suggestion row in suggestions)
                    if (row.Adopt && row.Map != null) maps.Add(row.Map);
                Confirm(Item, maps, null);
                suggestions.Clear();
            }
            detailRig = null;
            ResetDraft();
        }

        void OnGUI()
        {
            if (!Shown || !Singleton<Studio.Studio>.IsInstance()) return;
            if (Rig.IsItem(Item))
            {
                if (draft != null) Preview(draft);
                else foreach (Suggestion row in suggestions)
                    if (row.Adopt && row.Map != null) Preview(row.Map);
            }
            Window.width = Mathf.Min(465, Screen.width - 12);
            Window.height = Mathf.Min(565, Screen.height - 16);
            Window.x = Mathf.Clamp(Window.x, 0, Screen.width - Window.width);
            Window.y = Mathf.Clamp(Window.y, 0, Screen.height - Window.height);
            Window = GUILayout.Window(GetInstanceID(), Window, DrawWindow, "GIN IKTHIS");
        }

        void DrawWindow(int id)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(Rig.IsItem(Item) ? Item.treeNodeObject.textName : "アイテム未選択");
            if (GUILayout.Button("選択アイテムを使う", GUILayout.ExpandWidth(false))) Attempt(UseSelection);
            if (GUILayout.Button("閉じる", GUILayout.ExpandWidth(false))) Close();
            GUILayout.EndHorizontal();
            GUILayout.Label(Message);
            if (Baking)
            {
                GUILayout.Label("動きを記録しています。完了までStudioの操作を待ってください。");
                if (GUILayout.Button("焼き込みを中止")) CancelBake = true;
                GUI.DragWindow(new Rect(0, 0, Window.width, 20));
                return;
            }
            if (Rigs.Count > 0 && GUILayout.Button(bakeOpen ? "Timeline FK Bake を閉じる" : "Timeline FK Bake"))
            {
                bakeOpen = !bakeOpen;
                shareArmed = shareOverwrite = false;
            }
            if (Rig.IsItem(Item))
            {
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("自動割り当て")) Attempt(FindSuggestions);
                if (GUILayout.Button("手動で指定"))
                {
                    suggestions.Clear();
                    BeginManual(new Mapping { Name = "部位 " + (ForItem(Item).Count + 1) }, null, null, false);
                }
                bool previous = GUI.enabled;
                GUI.enabled = previous && (draft == null ? suggestions.Exists(delegate(Suggestion s) { return s.Adopt && s.Map != null; }) :
                    step == 3 && !addMiddle);
                if (GUILayout.Button("確定")) Attempt(CommitDraft);
                GUI.enabled = previous;
                GUILayout.EndHorizontal();
                scroll = GUILayout.BeginScrollView(scroll);
                if (bakeOpen) DrawBake();
                if (draft != null) DrawManual();
                else DrawSuggestions();
                DrawRegistered();
                GUILayout.EndScrollView();
            }
            else if (bakeOpen) DrawBake();
            GUI.DragWindow(new Rect(0, 0, Window.width, 20));
        }

        void DrawSuggestions()
        {
            foreach (Suggestion row in suggestions)
            {
                GUILayout.BeginHorizontal();
                if (row.Map == null) GUILayout.Label(row.Name + " · 手動指定が必要");
                else row.Adopt = GUILayout.Toggle(row.Adopt, row.Name);
                if (GUILayout.Button("修正", GUILayout.ExpandWidth(false)))
                    BeginManual(new Mapping { Name = row.Name, Pole = true }, null, row, false);
                GUILayout.EndHorizontal();
                if (row.Map != null) GUILayout.Label(Summary(row.Map));
                if (row.Error != null) GUILayout.Label(row.Error);
            }
        }

        void DrawManual()
        {
            GUILayout.Label(addMiddle ? "次に選ぶ骨 · 追加する中間" : draft.Waist && step == 0 ? "次に選ぶ骨 · 腰" :
                "次に選ぶ骨 · " + new[] { "起点", "中間（青）", "先端（赤）", "指定済み。次のクリックでリセット" }[step]);
            GUILayout.Label(Summary(draft));
            GUILayout.BeginHorizontal();
            GUILayout.Label("部位名", GUILayout.ExpandWidth(false));
            draft.Name = GUILayout.TextField(draft.Name, 64);
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(list ? "骨一覧を閉じる" : "骨一覧から選ぶ")) list = !list;
            if (GUILayout.Button("編集を取り消す")) ResetDraft();
            GUILayout.EndHorizontal();
            if (list && draft != null)
            {
                filter = GUILayout.TextField(filter);
                boneScroll = GUILayout.BeginScrollView(boneScroll, GUILayout.Height(154));
                foreach (OCIChar.BoneInfo bone in Item.listBones)
                {
                    if (!Rig.HasGuide(bone)) continue;
                    Transform t = Rig.Bone(bone);
                    if (t.name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (GUILayout.Button(t.name)) Attempt(delegate { Pick(bone); });
                }
                GUILayout.EndScrollView();
            }
        }

        void DrawRegistered()
        {
            List<Rig> rigs = ForItem(Item);
            if (rigs.Count > 0) GUILayout.Label("登録済みの部位");
            foreach (Rig rig in rigs)
            {
                GUILayout.BeginHorizontal();
                bool enable = GUILayout.Toggle(rig.Enabled, rig.Map.Name);
                if (enable != rig.Enabled) Attempt(delegate { SetEnabled(rig, enable); });
                if (GUILayout.Button("変更", GUILayout.ExpandWidth(false)))
                    BeginManual(rig.Map.Copy(), rig, null, false);
                if (GUILayout.Button("解除", GUILayout.ExpandWidth(false))) removing = rig;
                GUILayout.EndHorizontal();
                if (removing == rig)
                {
                    GUILayout.Label("この部位のガイドとガイドキーを解除します。");
                    GUILayout.BeginHorizontal();
                    if (GUILayout.Button("解除を実行")) { removing = null; Attempt(delegate { Remove(rig); }); }
                    if (GUILayout.Button("戻る")) removing = null;
                    GUILayout.EndHorizontal();
                }
                if (rig.Error != null) GUILayout.Label(rig.Error);
                if (rig.Baked) GUILayout.Label("標準FKに焼き込み済み");
                else if (MissingInitial(rig)) GUILayout.Label("初期キー未作成");
                if (rig.Map.Waist && rig.WaistPositionGuide == rig.Item.guideObject) GUILayout.Label("腰の移動はアイテム全体に適用");
                if (rig.Valid && rig.Enabled)
                {
                    GUILayout.BeginHorizontal();
                    for (int i = 0; i < rig.Controls.Count; i++)
                        if (GUILayout.Button(rig.IsRootControl(i) ? "緑 · 起点" : i == 0 ? "赤" : "青 " + i, GUILayout.ExpandWidth(false)))
                        {
                            OCIFolder folder = rig.Controls[i];
                            ResetDraft();
                            Attempt(delegate { SelectControl(folder); });
                        }
                    GUILayout.EndHorizontal();
                    if (!rig.Map.Waist && !Picking)
                    {
                        GUI.enabled = !TimelineReady || !Timeline.Playing;
                        if (rig.RootControl == null)
                        {
                            if (GUILayout.Button("起点の移動ガイドを追加", GUILayout.ExpandWidth(false))) Attempt(delegate
                            {
                                Rig updated = AddRootControl(rig);
                                SelectControl(updated.RootControl);
                            });
                        }
                        else if (GUILayout.Button("起点を元の位置に戻す", GUILayout.ExpandWidth(false)))
                            Attempt(delegate { ResetRootControl(rig); });
                        GUI.enabled = true;
                    }
                }
            }
            if (rigs.Count > 0)
            {
                GUILayout.BeginHorizontal();
                DrawKeyButtons(Item);
                GUI.enabled = TimelineReady && !Timeline.Playing;
                if (GUILayout.Button("0:00に初期キーを追加")) Attempt(delegate { Key(Item, true); });
                GUI.enabled = true;
                GUILayout.EndHorizontal();
            }
            if (GUILayout.Button(details ? "詳細を閉じる" : "詳細")) details = !details;
            if (!details) return;
            if (GUILayout.Button("腰・胴体の赤ガイドを指定"))
            {
                Rig waist = rigs.Find(delegate(Rig r) { return r.Map.Waist; });
                BeginManual(new Mapping { Waist = true, Name = "腰" }, waist, null, false);
            }
            if (draft != null && !draft.Waist)
                draft.Pole = GUILayout.Toggle(draft.Pole, "腕・脚の青ガイドを曲がる方向として使う");
            foreach (Rig rig in rigs)
                if (!rig.Map.Waist && rig.Bones.Length > 3 &&
                    GUILayout.Button(rig.Map.Name + " · 中間ガイドを追加"))
                {
                    detailRig = rig;
                    BeginManual(rig.Map.Copy(), rig, null, true);
                }
            if (detailRig != null && draft != null && editing == detailRig)
                GUILayout.Label("青ガイドは親子チェーンの順で並びます。");
        }

        void DrawBake()
        {
            GUILayout.Label("Timeline FK Bake");
            GUILayout.Label("焼き込みは標準FKキーを作り、ライブIKを停止します。");
            if (ForItem(Item).Exists(delegate(Rig rig) { return rig.Map.MoveRoot && !rig.Map.Root.guideObject.enablePos; }))
                GUILayout.Label("起点の位置はKKPEに記録し、このアイテムのKKPEを有効化します。配布先でもKKPEが必要です。");
            bool ready = TimelineReady && !Timeline.Playing && !Picking;
            GUI.enabled = ready;
            fps60 = GUILayout.Toggle(fps60, "60 fps（解除で30）");
            reduce = GUILayout.Toggle(reduce, "焼き込み後に不要なキーを削減（推奨）");
            if (reduce)
            {
                ReductionQuality = (BakeQuality)GUILayout.SelectionGrid((int)ReductionQuality,
                    new[] { "精密", "標準", "軽量" }, 3);
                GUILayout.Label("一定値や補間で再現できるキーを削減します。別トラックは保持します。");
            }
            KeepNodes = GUILayout.Toggle(KeepNodes, "NodeConstraintsを手先へ引き継ぐ（接触優先）");
            if (KeepNodes) GUILayout.Label("届かない目標では、接触を保つため肘から手先の距離が変わります。");
            GUI.enabled = ready && HasLive(Item);
            GUILayout.Label("選択アイテムの焼き込み区間（秒）");
            GUILayout.BeginHorizontal();
            start = GUILayout.TextField(start);
            end = GUILayout.TextField(end);
            GUILayout.EndHorizontal();
            overwrite = GUILayout.Toggle(overwrite, "区間内の既存キーを上書き");
            if (GUILayout.Button("選択アイテムを標準FKへ焼き込む")) Attempt(delegate
            {
                float from = Single.Parse(start, CultureInfo.InvariantCulture);
                float to = Single.Parse(end, CultureInfo.InvariantCulture);
                BeginBake(Item, from, to, fps60 ? 60 : 30, overwrite, reduce);
                overwrite = false;
            });
            GUI.enabled = ready && Rigs.Count > 0;
            if (!shareArmed)
            {
                if (GUILayout.Button("シーン全体を配布用に確定…")) { shareArmed = true; shareOverwrite = false; }
            }
            else
            {
                GUILayout.Label("編集用シーンを先に保存してください。全区間を標準FKにし、IKTHISのガイドとガイドキーを取り除きます。");
                GUILayout.Label("停止済みで焼き込みが確認できる部位は、現在の標準FKを使います。");
                shareOverwrite = GUILayout.Toggle(shareOverwrite, "全区間の既存FKキーを上書き");
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("配布用に確定")) Attempt(delegate
                {
                    BeginSharing(fps60 ? 60 : 30, shareOverwrite, reduce);
                    shareArmed = shareOverwrite = false;
                });
                if (GUILayout.Button("戻る")) shareArmed = false;
                GUILayout.EndHorizontal();
            }
            GUI.enabled = true;
        }

        static string BoneName(OCIChar.BoneInfo bone) { return Rig.HasGuide(bone) ? Rig.Bone(bone).name : "未選択"; }
        static string Summary(Mapping map)
        {
            if (map.Waist) return "腰 · " + BoneName(map.Tip);
            List<string> names = new List<string>();
            foreach (OCIChar.BoneInfo b in map.Middles) names.Add(BoneName(b));
            return BoneName(map.Root) + " → " + (names.Count == 0 ? "未選択" : String.Join(" → ", names.ToArray())) + " → " + BoneName(map.Tip);
        }

        static void Preview(Mapping map)
        {
            Camera view = Camera.main;
            if (view == null) return;
            Mark(view, map.Root, Color.yellow, "起点");
            Mark(view, map.Tip, new Color(1f, .35f, .3f), map.Waist ? "腰" : "赤");
            for (int i = 0; i < map.Middles.Count; i++)
                Mark(view, map.Middles[i], new Color(.35f, .65f, 1f), "青 " + (i + 1));
        }

        static void Mark(Camera view, OCIChar.BoneInfo bone, Color color, string title)
        {
            if (!Rig.HasGuide(bone)) return;
            Vector3 p = view.WorldToScreenPoint(Rig.Bone(bone).position);
            if (p.z <= 0) return;
            Color old = GUI.color;
            GUI.color = color;
            GUI.Label(new Rect(p.x - 4, Screen.height - p.y - 10, 190, 22), "● " + title + " · " + BoneName(bone));
            GUI.color = old;
        }
    }
}
