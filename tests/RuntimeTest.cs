using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using BepInEx;
using Studio;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Timeline;
using RootMotion.FinalIK;
using Native = Timeline.Timeline;
using Frame = Timeline.Keyframe;
#if !INDEPENDENT
using GinIkThis;
#endif

[BepInPlugin("local.kkworkshop.0a52b20541f5.gin-ikthis.verification", "GIN IKTHIS Verification", "0.1.0")]
[BepInProcess("CharaStudio")]
public sealed class RuntimeTest : BaseUnityPlugin
{
    const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    const string Id = "local.kkworkshop.0a52b20541f5.gin-ikthis";
    string output, phase;
    readonly List<string> checks = new List<string>();
    Studio.Studio StudioApp { get { return Singleton<Studio.Studio>.Instance; } }

    IEnumerator Start()
    {
        string[] args = Environment.GetCommandLineArgs();
        int index = Array.IndexOf(args, "-ginIkThisVerification");
        if (index < 0 || !File.Exists(Path.Combine(Paths.GameRootPath, ".kk-workshop-test"))) yield break;
        phase = args[index + 1];
        output = args[index + 2];
        IEnumerator test = Run();
        while (true)
        {
            Exception failure = null;
            bool more = false;
            try { more = test.MoveNext(); }
            catch (Exception ex) { failure = ex; }
            if (!more || failure != null)
            {
                File.WriteAllText(Path.Combine(output, phase + ".txt"),
                    (failure == null ? "PASS\n" : "FAIL\n") + String.Join("\n", checks.ToArray()) + (failure == null ? "" : "\n" + failure));
                Application.Quit();
                yield break;
            }
            yield return test.Current;
        }
    }

    void Check(bool ok, string message)
    {
        if (!ok) throw new InvalidOperationException(message);
        checks.Add(message);
        Logger.LogInfo("PASS " + message);
    }

    static object Member(object obj, string name)
    {
        FieldInfo field = obj.GetType().GetField(name, Flags);
        return field == null ? obj.GetType().GetProperty(name, Flags).GetValue(obj, null) : field.GetValue(obj);
    }

    int Resolve(int slot)
    {
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type type = assembly.GetType("Sideloader.AutoResolver.UniversalAutoResolver");
            if (type == null) continue;
            foreach (object row in (IEnumerable)type.GetField("LoadedStudioResolutionInfo", Flags).GetValue(null))
                if ((string)Member(row, "GUID") == "local.kkworkshop.0a52b20541f5.gin-genshin-creatures" &&
                    (int)Member(row, "Slot") == slot) return (int)Member(row, "LocalSlot");
        }
        return -1;
    }

    OCIItem Add(int id, string name, Vector3 position)
    {
        int slot = Resolve(id);
        Check(slot >= 0, name + " resolved by Sideloader");
        OCIItem item = AddObjectItem.Add(70000424, 70000498, slot);
        Check(item != null && item.listBones != null && item.listBones.Count > 10, name + " real FK prefab");
        item.treeNodeObject.textName = name;
        item.guideObject.changeAmount.pos = position;
        item.guideObject.ForceUpdate();
        return item;
    }

    static OCIChar.BoneInfo Bone(OCIItem item, string name)
    {
        OCIChar.BoneInfo bone = item.listBones.Find(delegate(OCIChar.BoneInfo b) { return b.guideObject.transformTarget.name == name; });
        if (bone == null) throw new ArgumentException("Missing bone " + name);
        return bone;
    }

    static Transform TransformOf(OCIChar.BoneInfo b) { return b.guideObject.transformTarget; }
    static Transform TransformOf(ObjectCtrlInfo item) { return item.guideObject.transformTarget; }
    static object NativeSelf() { return typeof(Native).GetField("_self", Flags).GetValue(null); }
    static IEnumerable<Interpolable> Tracks() { return ((Dictionary<int, Interpolable>)typeof(Native).GetField("_interpolables", Flags).GetValue(NativeSelf())).Values; }
    static void Play(bool value) { typeof(Native).GetMethod(value ? "Play" : "Pause", Flags).Invoke(null, null); }

    void Seek(float time)
    {
        Type type = typeof(Native);
        object native = NativeSelf();
        MethodInfo seek = type.GetMethod("Seek", Flags, null, new[] { typeof(float) }, null) ??
            type.GetMethod("SeekPlaybackTime", Flags, null, new[] { typeof(float) }, null);
        seek.Invoke(seek.IsStatic ? null : native, new object[] { time });
        MethodInfo interpolate = type.GetMethod("Interpolate", Flags);
        interpolate.Invoke(native, new object[] { true });
        interpolate.Invoke(native, new object[] { false });
        foreach (ObjectCtrlInfo obj in StudioApp.dicObjectCtrl.Values) obj.guideObject.ForceUpdate();
        foreach (ObjectCtrlInfo obj in StudioApp.dicObjectCtrl.Values)
        {
            OCIItem item = obj as OCIItem;
            if (item == null || !item.itemInfo.enableFK) continue;
            foreach (OCIChar.BoneInfo bone in item.listBones)
                TransformOf(bone).localRotation = Quaternion.Euler(bone.guideObject.changeAmount.rot);
        }
    }

    static Interpolable Track(ObjectCtrlInfo obj, string id, GuideObject guide)
    {
        foreach (Interpolable track in Tracks())
            if (track.oci == obj && track.id == id && ReferenceEquals(track.parameter, guide)) return track;
        return null;
    }

    static int CountTracks()
    {
        int count = 0;
        foreach (Interpolable track in Tracks()) count++;
        return count;
    }

    void Save(string name)
    {
        foreach (ObjectCtrlInfo obj in StudioApp.dicObjectCtrl.Values) obj.OnSavePreprocessing();
        StudioApp.sceneInfo.cameraSaveData = StudioApp.cameraCtrl.Export();
        Check(StudioApp.sceneInfo.Save(Path.Combine(output, name)), "Native Studio scene save " + name);
    }

    static Button FindButton(string name)
    {
        foreach (Button button in Resources.FindObjectsOfTypeAll<Button>())
            if ((button.name == name || button.name == "Button " + name + " (StudioAPI)") && button.gameObject.scene.IsValid())
                return button;
        return null;
    }

    static void ClickToolbar(string name)
    {
        Button button = FindButton(name);
        PointerEventData click = new PointerEventData(EventSystem.current) { button = PointerEventData.InputButton.Left };
        ExecuteEvents.Execute(button.image.gameObject, click, ExecuteEvents.pointerClickHandler);
    }

    IEnumerator Run()
    {
        float deadline = Time.realtimeSinceStartup + 95;
        while (Time.realtimeSinceStartup < deadline && (!KKAPI.Studio.StudioAPI.StudioLoaded || Resolve(70000429) < 0)) yield return null;
        for (int i = 0; i < 65; i++) yield return null;
        Check(KKAPI.Studio.StudioAPI.StudioLoaded, "Studio ready, Unity " + Application.unityVersion + ", game " + Application.version);
#if INDEPENDENT
        Check(!BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey(Id), "GIN IKTHIS absent at startup");
        if (phase == "user-independent")
        {
            Check(StudioApp.LoadScene(Path.Combine(output, "user-shared.png")), "User baked scene opens without IKTHIS");
            for (int i = 0; i < 120; i++) yield return null;
            IEnumerator user = VerifyUserScene();
            while (user.MoveNext()) yield return user.Current;
            yield break;
        }
        if (phase == "root-shared" || phase == "root-kohola-shared")
        {
            bool creature = phase == "root-kohola-shared";
            string prefix = creature ? "root-kohola" : "root";
            Check(StudioApp.LoadScene(Path.Combine(output, prefix + "-shared.png")), "Translated-root scene opens without IKTHIS");
            for (int i = 0; i < 85; i++) yield return null;
            OCIItem human = FindItem(creature ? 70000434 : 70000429);
            string[] expected = File.ReadAllLines(Path.Combine(output, prefix + "-bones.txt"));
            Transform root = TransformOf(Bone(human, expected[0]));
            Transform tip = TransformOf(Bone(human, expected[1]));
            Interpolable position = RootPositionTrack(human, root);
            Check(StudioApp.dicObjectCtrl.Count == 1 && position != null && position.keyframes.Count >= 2,
                "Only the item remains; KKPE resolves the serialized Bone Position track");
            Seek(0);
            yield return new WaitForEndOfFrame();
            Vector3 start = root.localPosition, planted = tip.position;
            Seek(1);
            yield return new WaitForEndOfFrame();
            Check(Vector3.Distance(root.localPosition, start) > .004f && Vector3.Distance(tip.position, planted) < .001f,
                "Scrubbing moves the root while the baked tip stays planted; displacement " + Vector3.Distance(root.localPosition, start) +
                ", tip error " + Vector3.Distance(tip.position, planted));
            Seek(0);
            Play(true);
            bool moved = false;
            float maximumTipError = 0;
            for (int i = 0; i < 120 && Native.playbackTime < .95f; i++)
            {
                yield return new WaitForEndOfFrame();
                maximumTipError = Mathf.Max(maximumTipError, Vector3.Distance(tip.position, planted));
                moved |= Vector3.Distance(root.localPosition, start) > .004f;
            }
            Play(false);
            Check(moved && maximumTipError < .001f, "Natural playback animates bone translation and standard FK together; maximum tip error " + maximumTipError);
            yield break;
        }
        if (phase == "fk-mixed-shared")
        {
            Check(StudioApp.LoadScene(Path.Combine(output, "fk-mixed-shared.png")), "Mixed FK/IK animation opens without IKTHIS");
            for (int i = 0; i < 85; i++) yield return null;
            OCIItem dragon = FindItem(70000434);
            OCIChar.BoneInfo neck = Bone(dragon, "Bone_Neck01_M");
            Interpolable neckTrack = Track(dragon, "guideObjectRot", neck.guideObject);
            Check(StudioApp.dicObjectCtrl.Count == 1 && neckTrack != null && neckTrack.keyframes.Count == 2,
                "Original independent FK track survives without any IK helper objects");
            Seek(0);
            Quaternion first = TransformOf(neck).localRotation;
            Seek(1);
            Check(Quaternion.Angle(first, TransformOf(neck).localRotation) > 10, "Standard Timeline animates the independent neck");
            yield break;
        }
        bool shared = phase == "shared";
        Check(StudioApp.LoadScene(Path.Combine(output, shared ? "shared.png" : "baked.png")), "Baked scene loads without IKTHIS");
        for (int i = 0; i < 85; i++) yield return null;
        OCIItem item = FindItem(70000429);
        Check(item != null && item.itemInfo.enableFK, "Native FK remains enabled without IKTHIS");
        List<OCIFolder> handles = new List<OCIFolder>();
        foreach (ObjectCtrlInfo obj in StudioApp.dicObjectCtrl.Values)
            if (obj is OCIFolder) handles.Add((OCIFolder)obj);
        if (shared)
        {
            Check(handles.Count == 1 && handles[0].name == "Unrelated", "Shared scene has no IKTHIS guides; unrelated folder retained");
            Check(Track(handles[0], "guideObjectPos", handles[0].guideObject) != null, "Unrelated Timeline animation retained in share copy");
        }
        else foreach (OCIFolder folder in handles)
            if (!(folder.parentInfo is OCIFolder)) Studio.Studio.DeleteNode(folder.treeNodeObject);
        for (int i = 0; i < 5; i++) yield return null;
        float sampleError = VerifySamples(item, shared ? "sharing-samples.txt" : "samples.txt");
        Check(sampleError < .001f, "Native positions match live IK at 11 sampled times without IKTHIS guides, error " + sampleError);
        if (shared)
        {
            OCIItem animal = FindItem(70000436);
            Check(animal != null && animal.itemInfo.enableFK && VerifyAnimal(animal) < .001f,
                "Second item's Timeline FK also survives clean share");
        }
        Seek(0);
        Play(true);
        float maximum = 0;
        int frames = 0;
        for (int i = 0; i < 35; i++)
        {
            yield return new WaitForEndOfFrame();
            if (Native.playbackTime >= 1) break;
            foreach (Interpolable track in Tracks())
            {
                if (track.oci != item || track.id != "guideObjectRot") continue;
                GuideObject guide = (GuideObject)track.parameter;
                maximum = Mathf.Max(maximum, Quaternion.Angle(guide.transformTarget.localRotation, Quaternion.Euler(guide.changeAmount.rot)));
            }
            frames++;
        }
        Check(Native.playbackTime > .05f && frames > 3 && maximum < .12f, "Natural Timeline playback drives standard FK, max error " + maximum + " degrees");
        Play(false);
#else
        Plugin plugin = Plugin.Instance;
        Check(plugin != null && FindButton("GIN IKTHIS") != null, "IKTHIS real Option button registered");
        Check(FindButton("GIN Rope This") != null && FindButton("GIN Rope This") != FindButton("GIN IKTHIS"), "Rope This 1.1.3 real Option button present independently");
        Check(plugin.TimelineReady, "Timeline native tracks ready " + typeof(Native).Assembly.GetName().Version);
        if (phase == "v1-multi")
        {
            IEnumerator multi = MultipleItemEditors(plugin);
            while (multi.MoveNext()) yield return multi.Current;
            yield break;
        }
        if (phase == "v1-reduction")
        {
            VerifyKeyReduction();
            yield break;
        }
        if (phase == "v1-ui")
        {
            Check(StudioApp.LoadScene(Path.Combine(output, "v1-multiple-items.png")), "The two-item UI fixture loads");
            for (int i = 0; i < 85; i++) yield return null;
            OCIItem first = plugin.Rigs[0].Item;
            OCIItem second = plugin.Rigs.Find(delegate(Rig r) { return r.Item != first; }).Item;
            first.treeNodeObject.textName = "IK Item A";
            second.treeNodeObject.textName = "IK Item B";
            Plugin.Select(first);
            Check(!plugin.Shown, "The V1 settings panel starts closed");
            File.WriteAllText(Path.Combine(output, "ui-ready.txt"), "v1-ui");
            bool sawFirst = false, sawSecond = false, sawCompact = false;
            while (!File.Exists(Path.Combine(output, "ui-stop.txt")))
            {
                sawFirst |= plugin.Shown && plugin.Item == first;
                sawSecond |= plugin.Shown && plugin.Item == second;
                sawCompact |= plugin.Shown && plugin.ReductionQuality == BakeQuality.Compact;
                yield return null;
            }
            Check(sawFirst && sawSecond && sawCompact && plugin.Rigs.TrueForAll(delegate(Rig r) { return r.Enabled; }),
                "Physical Option, workspace and quality-grid clicks display both active items and Compact reduction");
            Application.CaptureScreenshot(Path.Combine(output, "v1-ui.png"));
            yield return new WaitForEndOfFrame();
            yield break;
        }
        if (phase == "constraint-probe")
        {
            Check(StudioApp.LoadScene(Path.GetFullPath(Path.Combine(output, "../user-bake-fixture/original.png"))), "Original constrained scene loads");
            for (int i = 0; i < 120; i++) yield return null;
            Play(false);
            OCIItem target = FindItem(70000427);
            Type type = null;
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                if ((type = assembly.GetType("NodesConstraints.NodesConstraints")) != null) break;
            object nodes = type.GetField("_self", Flags).GetValue(null);
            IList rows = (IList)Member(nodes, "_constraints");
            List<Rig> linked = new List<Rig>();
            List<object> links = new List<object>();
            foreach (Rig rig in plugin.Rigs)
                foreach (object row in rows)
                    if ((Transform)Member(row, "childTransform") == TransformOf(rig.Controls[0]))
                    { linked.Add(rig); links.Add(row); }
            Check(linked.Count == 2, "Two original incoming hand constraints identified");
            List<string> data = new List<string>();
            float[] probeLengths = new float[linked.Count];
            for (int n = 0; n < linked.Count; n++)
                probeLengths[n] = Vector3.Distance(TransformOf(linked[n].Map.Tip).position,
                    TransformOf(linked[n].Bones[linked[n].Bones.Length - 2]).position);
            for (int mode = 0; mode < 3; mode++)
            {
                if (mode == 1)
                {
                    plugin.KeepNodes = false;
                    plugin.BeginBake(target, 0, Native.duration, 30, false, true);
                    while (plugin.Baking) yield return null;
                    Check(plugin.Rigs.TrueForAll(delegate(Rig rig) { return rig.Baked; }), "FK-only comparison bake completes");
                }
                if (mode == 2)
                    for (int n = 0; n < linked.Count; n++)
                    {
                        object row = links[n];
                        row.GetType().GetField("childTransform", Flags).SetValue(row, TransformOf(linked[n].Map.Tip));
                        row.GetType().GetField("child", Flags).SetValue(row, linked[n].Map.Tip.guideObject);
                        Interpolable rotation = Track(target, "guideObjectRot", linked[n].Map.Tip.guideObject);
                        if (rotation != null) rotation.enabled = false;
                    }
                float contact = 0, probeLengthError = 0, velocity = 0, relativeStep = 0;
                Vector3[] previous = new Vector3[linked.Count];
                Vector3[] priorStep = new Vector3[linked.Count];
                for (int frame = 0; frame <= 575; frame++)
                {
                    float at = Mathf.Min(Native.duration, frame / 60f);
                    Seek(at);
                    yield return null;
                    yield return new WaitForEndOfFrame();
                    for (int n = 0; n < linked.Count; n++)
                    {
                        Rig rig = linked[n]; object row = links[n];
                        Transform tip = TransformOf(rig.Map.Tip), parent = (Transform)Member(row, "parentTransform");
                        Vector3 goal = parent.TransformPoint((Vector3)Member(row, "positionOffset"));
                        Vector3 relative = parent.InverseTransformPoint(tip.position);
                        Vector3 step = relative - previous[n];
                        float error = Vector3.Distance(tip.position, goal);
                        float stretch = Mathf.Abs(Vector3.Distance(tip.position, TransformOf(rig.Bones[rig.Bones.Length - 2]).position) - probeLengths[n]);
                        contact = Mathf.Max(contact, error); probeLengthError = Mathf.Max(probeLengthError, stretch);
                        if (frame > 1) { velocity = Mathf.Max(velocity, step.magnitude); relativeStep = Mathf.Max(relativeStep, (step-priorStep[n]).magnitude); }
                        previous[n] = relative; priorStep[n] = step;
                        data.Add(mode + "\t" + F(at) + "\t" + n + "\t" + F(error) + "\t" + F(stretch));
                    }
                }
                checks.Add("Mode " + mode + " contact=" + contact + " length error=" + probeLengthError + " relative step=" + velocity + " second difference=" + relativeStep);
            }
            File.WriteAllLines(Path.Combine(output, "constraint-probe.tsv"), data.ToArray());
            yield break;
        }
        if (phase == "user-ui")
        {
            Check(StudioApp.LoadScene(Path.GetFullPath(Path.Combine(output, "../user-bake-fixture/original.png"))), "The user's scene loads for the physical UI check");
            for (int i = 0; i < 120; i++) yield return null;
            Play(false);
            Plugin.Select(FindItem(70000427));
            Seek(.72f);
            File.WriteAllText(Path.Combine(output, "ui-ready.txt"), "READY");
            bool observed = false, selectedBaked = false;
            while (!File.Exists(Path.Combine(output, "ui-stop.txt")))
            {
                observed |= plugin.Baking;
                selectedBaked |= plugin.Rigs.Count == 5 &&
                    plugin.Rigs.TrueForAll(delegate(Rig rig) { return rig.Baked && !rig.Enabled; });
                yield return null;
            }
            Check(observed && selectedBaked && plugin.Rigs.Count == 0 && StudioApp.dicObjectCtrl.Count == 3,
                "Physical IKTHIS UI clicks bake the selected item and then finalize the linked scene for sharing");
            Check(LiveConstraints().Count == 5 && BoneConstraints(FindItem(70000427)).Count == 2,
                "The physical UI workflow preserves the five original links and attaches the hands to the head");
            Application.CaptureScreenshot(Path.Combine(output, "user-ui.png"));
            yield return new WaitForEndOfFrame();
            Save("user-ui-shared.png");
            yield break;
        }
        if (phase == "user-bake" || phase == "user-selected" || phase == "user-cancel" || phase == "user-modify" ||
            phase == "user-reload" || phase == "user-node-edit" || phase == "user-fk-only" || phase == "user-transfer-rollback" || phase == "user-compact")
        {
            IEnumerator user = UserBake(plugin);
            while (user.MoveNext()) yield return user.Current;
            yield break;
        }
        if (phase == "user-probe")
        {
            Check(StudioApp.LoadScene(Path.GetFullPath(Path.Combine(output, "../user-bake-fixture/original.png"))), "User scene loads in isolated Studio");
            for (int i = 0; i < 180; i++) yield return null;
            Play(false);
            List<int> keys = new List<int>(StudioApp.dicObjectCtrl.Keys);
            keys.Sort();
            List<string> diagnostic = new List<string>();
            for (int i = 0; i < keys.Count; i++)
            {
                ObjectCtrlInfo obj = StudioApp.dicObjectCtrl[keys[i]];
                OCIItem item = obj as OCIItem;
                diagnostic.Add("object " + i + " key " + keys[i] + " " + obj.GetType().Name + " " + obj.treeNodeObject.textName +
                    (item == null ? "" : " bones=" + (item.listBones == null ? -1 : item.listBones.Count)));
            }
            diagnostic.Add("duration " + Native.duration + " cursor " + Native.playbackTime);
            foreach (Rig rig in plugin.Rigs)
            {
                diagnostic.Add("rig " + rig.Map.Name + " enabled=" + rig.Enabled + " valid=" + rig.Valid + " error=" + rig.Error);
                try { plugin.CheckBake(rig); diagnostic.Add("check bake OK"); }
                catch (Exception ex) { diagnostic.Add("check bake " + ex); }
            }
            File.WriteAllLines(Path.Combine(output, "user-diagnostic.txt"), diagnostic.ToArray());
            try
            {
                plugin.FinalizeForSharing(30, false, true);
                diagnostic.Add("Finalize succeeded");
            }
            catch (Exception ex) { diagnostic.Add("Finalize failed " + ex); }
            File.WriteAllLines(Path.Combine(output, "user-diagnostic.txt"), diagnostic.ToArray());
            yield break;
        }
        if (phase == "root-ui" || phase == "root-marker")
        {
            Check(StudioApp.LoadScene(Path.Combine(output, "root-editing.png")), "Interactive root fixture loads");
            for (int i = 0; i < 85; i++) yield return null;
            OCIItem item = FindItem(70000429);
            Rig rig = plugin.ForItem(item).Find(delegate(Rig r) { return r.Map.MoveRoot; });
            StudioApp.workInfo.visibleAxis = true;
            StudioApp.cameraCtrl.ReflectOption();
            GuideObject green = rig.RootControl.guideObject;
            Plugin.Select(item);
            Seek(1);
            for (int i = 0; i < 8; i++) yield return null;
            Vector3 initial = green.changeAmount.pos;
            Vector3 plant = TransformOf(rig.Map.Tip).position;
            if (phase == "root-marker")
            {
                typeof(Plugin).GetField("autoKey", Flags).SetValue(plugin, true);
                IDictionary dots = (IDictionary)Member(plugin, "markers");
                MarkerDrag handler = ((Image)dots[rig.RootControl]).GetComponent<MarkerDrag>();
                Vector2 screen = Camera.main.WorldToScreenPoint(green.transformTarget.position);
                PointerEventData pointer = new PointerEventData(EventSystem.current)
                {
                    button = PointerEventData.InputButton.Left, pressPosition = screen, position = screen
                };
                handler.OnBeginDrag(pointer);
                pointer.position += Vector2.right * 2f;
                handler.OnDrag(pointer);
                handler.OnEndDrag(pointer);
                yield return new WaitForEndOfFrame();
                Interpolable track = Track(rig.RootControl, "guideObjectPos", green);
                Check(Vector3.Distance(green.changeAmount.pos, initial) > .0005f &&
                    Vector3.Distance(TransformOf(rig.Map.Root).position, green.transformTarget.position) < .0001f &&
                    Vector3.Distance(TransformOf(rig.Map.Tip).position, plant) < .001f &&
                    track.keyframes.Count == 2 && (Vector3)track.keyframes[1].value == green.changeAmount.pos,
                    "The green marker's native begin/drag/end handlers translate in parent space and replace only the current position key");
                yield break;
            }
            bool selected = false, moved = false;
            File.WriteAllText(Path.Combine(output, "ui-ready.txt"), "READY");
            while (!File.Exists(Path.Combine(output, "ui-stop.txt")))
            {
                selected |= GuideObjectManager.Instance.selectObject == green && GuideObjectManager.Instance.mode == 0;
                moved |= selected && Vector3.Distance(green.changeAmount.pos, initial) > .0005f;
                yield return null;
            }
            Check(selected && moved && !plugin.Shown &&
                Vector3.Distance(TransformOf(rig.Map.Tip).position, plant) < .001f,
                "Physical viewport selection and native position-field editing move the parent with the panel closed and retain the red pin");
            Application.CaptureScreenshot(Path.Combine(output, "root-ui.png"));
            yield return new WaitForEndOfFrame();
            yield break;
        }
        if (phase == "root" || phase == "root-reload" || phase == "root-no-kkpe" || phase == "root-kkpe-disabled" || phase == "root-kohola" || phase == "root-observed")
        {
            IEnumerator run = RootWorkflow(plugin);
            while (run.MoveNext()) yield return run.Current;
            yield break;
        }
        if (phase == "fk-ui")
        {
            Check(StudioApp.LoadScene(Path.Combine(output, "fk-ui-scene.png")), "Interactive Koholasaurus scene loaded");
            for (int i = 0; i < 85; i++) yield return null;
            OCIItem item = FindItem(70000434);
            Check(item != null && plugin.ForItem(item).Count >= 4, "Koholasaurus owns its limb rigs");
            StudioApp.workInfo.visibleAxis = true;
            StudioApp.cameraCtrl.ReflectOption();
            GuideObjectManager.Instance.mode = 1;
            Plugin.Select(item);
            for (int i = 0; i < 8; i++) yield return null;
            bool sawIk = false;
            string selectedFk = null;
            GuideObject previous = null;
            File.WriteAllText(Path.Combine(output, "ui-ready.txt"), "READY");
            while (!File.Exists(Path.Combine(output, "ui-stop.txt")))
            {
                GuideObject guide = GuideObjectManager.Instance.selectObject;
                if (guide != null && guide != previous)
                {
                    foreach (Rig rig in plugin.ForItem(item))
                        if (rig.Controls.Exists(delegate(OCIFolder folder) { return folder.guideObject == guide; })) sawIk = true;
                    OCIChar.BoneInfo bone = item.listBones.Find(delegate(OCIChar.BoneInfo candidate) { return candidate.guideObject == guide; });
                    if (sawIk && bone != null && !plugin.ForItem(item).Exists(delegate(Rig rig) { return Array.IndexOf(rig.Bones, bone) >= 0; }))
                        selectedFk = TransformOf(bone).name;
                    File.WriteAllText(Path.Combine(output, "ui-selected.txt"), guide.transformTarget.name);
                }
                previous = guide;
                yield return null;
            }
            Check(sawIk && selectedFk != null, "Physical viewport clicks selected IK then free FK " + selectedFk);
            yield break;
        }
        if (phase == "fk-mixed")
        {
            IEnumerator run = MixedWorkflow(plugin);
            while (run.MoveNext()) yield return run.Current;
            yield break;
        }
        if (phase == "kohola" || phase == "kohola-reload" || phase == "ui")
        {
            IEnumerator run = Kohola(plugin, phase != "kohola");
            while (run.MoveNext()) yield return run.Current;
            yield break;
        }
        if (phase == "migrate")
        {
            Check(StudioApp.LoadScene(Path.Combine(output, "editing-v0.2.0.png")), "Original 0.2.0 editable scene loads");
            for (int i = 0; i < 85; i++) yield return null;
            Check(plugin.Rigs.Count == 3 && plugin.Rigs.TrueForAll(delegate(Rig rig)
            {
                return rig.Enabled && rig.Group.parentInfo == rig.Item && !plugin.MissingInitial(rig);
            }), "All existing mappings and Timeline keys migrated into owner groups");
            OCIItem migratedItem = FindItem(70000429);
            Rig migratedFoot = plugin.ForItem(migratedItem).Find(delegate(Rig rig) { return !rig.Map.Waist; });
            GuideObject blue = migratedFoot.Controls[1].guideObject;
            Interpolable track = Track(migratedFoot.Controls[1], "guideObjectPos", blue);
            Check(track != null && track.keyframes.ContainsKey(.5f) && track.keyframes.ContainsKey(1),
                "Migrated blue guide keeps its original keyed times");
            float error = 0;
            for (int i = 0; i <= 10; i++)
            {
                Seek(i * .1f);
                foreach (Rig rig in plugin.ForItem(migratedItem)) rig.Solve();
                error = Mathf.Max(error, Vector3.Distance(TransformOf(migratedFoot.Map.Tip).position, TransformOf(migratedFoot.Controls[0]).position));
            }
            Check(error < .002f, "Migrated world-space targets remain usable after key evaluation");
            yield break;
        }
        if (phase == "workflow")
        {
            IEnumerator run = Workflow(plugin);
            while (run.MoveNext()) yield return run.Current;
            yield break;
        }
        if (phase == "legacy")
        {
            Check(StudioApp.LoadScene(Path.Combine(output, "baked-v0.1.0.png")), "Original 0.1.0 baked scene loads");
            for (int i = 0; i < 85; i++) yield return null;
            OCIItem original = FindItem(70000429);
            Check(plugin.Rigs.Count == 10 && plugin.ForItem(original).TrueForAll(delegate(Rig rig)
            {
                return !rig.Enabled && rig.LegacyDisabled && plugin.Timeline.HasBakedTracks(rig);
            }), "0.1.0 disabled baked mappings are recognized by their existing standard key sets");
            Interpolable track = Track(original, "guideObjectPos", original.guideObject);
            object first = track.keyframes[0];
            plugin.FinalizeForSharing(30, false, true);
            Check(plugin.Rigs.Count == 0 && ReferenceEquals(first, track.keyframes[0]),
                "Legacy clean share retains previously baked standard FK and cleans up sync");
            yield break;
        }
        if (phase == "recover")
        {
            Check(StudioApp.LoadScene(Path.Combine(output, "offline-scene.png")), "Offline scene accepted with Timeline installed");
            for (int i = 0; i < 85; i++) yield return null;
            Check(plugin.Rigs.Count == 1 && plugin.Rigs[0].Enabled && plugin.MissingInitial(plugin.Rigs[0]), "Offline rig restored with its pending initial-key status");
            Seek(.43f);
            plugin.Key(plugin.Rigs[0].Item, true);
            Check(CountTracks() == 3 && !plugin.MissingInitial(plugin.Rigs[0]) && Mathf.Abs(Native.playbackTime - .43f) < .00001f,
                "Missing 0:00 guide keys added after enabling Timeline without moving cursor");
            yield break;
        }
        if (phase == "reload")
        {
            Check(StudioApp.LoadScene(Path.Combine(output, "editing.png")), "Fresh process opens editing scene");
            for (int i = 0; i < 85; i++) yield return null;
            Check(plugin.Rigs.Count == 10 && plugin.Rigs.TrueForAll(delegate(Rig r) { return r.Enabled && r.Valid && !plugin.MissingInitial(r); }),
                "10 live rigs, all native guide tracks and initial keys restored");
            OCIItem original = FindItem(70000429);
            Seek(.5f);
            yield return new WaitForEndOfFrame();
            OCIChar.BoneInfo restoredFoot = Bone(original, "bip_foot_L");
            Rig restoredFootRig = plugin.ForItem(original).Find(delegate(Rig r) { return r.Map.Tip == restoredFoot; });
            Check(Vector3.Distance(TransformOf(restoredFoot).position,
                TransformOf(restoredFootRig.Controls[0]).position) < .007f, "Reloaded live waist/foot result");
            Plugin.Select(original);
            StudioApp.Duplicate();
            for (int i = 0; i < 70; i++) yield return null;
            Check(plugin.Rigs.Count == 15, "Native item-only duplication clones the five mapped parts");
            foreach (Rig rig in plugin.Rigs)
                if (rig.Item != original && rig.Item.itemInfo.no == original.itemInfo.no)
                    Check(rig.Valid && !plugin.MissingInitial(rig) && !plugin.Rigs.Exists(delegate(Rig r)
                    {
                        return r.Item == original && r.Controls.Contains(rig.Controls[0]);
                    }), "Duplicate owns its own guide/keys " + rig.Map.Name);
            Check(StudioApp.ImportScene(Path.Combine(output, "editing.png")), "Native scene import");
            for (int i = 0; i < 85; i++) yield return null;
            HashSet<OCIFolder> unique = new HashSet<OCIFolder>();
            foreach (Rig rig in plugin.Rigs) foreach (OCIFolder control in rig.Controls)
                if (!unique.Add(control)) throw new InvalidOperationException("Shared guide after import");
            Check(plugin.Rigs.Count == 25 && plugin.Rigs.TrueForAll(delegate(Rig r) { return r.Valid && !plugin.MissingInitial(r); }),
                "Import adds 10 distinct rigs without rebinding existing tracks");
            yield break;
        }

        OCIItem human = Add(70000429, "Hilichurl", Vector3.zero);
        OCIItem quadruped = Add(70000436, "Rifthound", new Vector3(1.7f, 0, 0));
        OCIItem hatchling = Add(70000427, "Geovishap Hatchling", new Vector3(-1.7f, 0, 0));
        Plugin.Select(human);
        ClickToolbar("GIN Rope This");
        BepInEx.PluginInfo rope = BepInEx.Bootstrap.Chainloader.PluginInfos["local.kkworkshop.0a52b20541f5.gin-rope-this"];
        Check(!plugin.Shown && (bool)Member(rope.Instance, "shown"), "Rope This callback opens only Rope This");
        ClickToolbar("GIN Rope This");
        ClickToolbar("GIN IKTHIS");
        Check(plugin.Shown && plugin.Item == human, "IKTHIS Option callback opens the selected FK item (shown=" + plugin.Shown +
            ", message=" + plugin.Message + ")");
        Check(!(bool)Member(rope.Instance, "shown"), "IKTHIS callback leaves Rope This closed");
        Vector3 originalFoot = TransformOf(Bone(human, "bip_foot_L")).position;
        int countBefore = StudioApp.dicObjectCtrl.Count;
        Invoke(plugin, "FindSuggestions");
        Check(StudioApp.dicObjectCtrl.Count == countBefore && CountTracks() == 0 &&
            Vector3.Distance(originalFoot, TransformOf(Bone(human, "bip_foot_L")).position) < .0001f,
            "Auto preview leaves pose, guides and Timeline unchanged");
        List<Mapping> humanMaps = Auto(human), animalMaps = Auto(quadruped);
        Check(humanMaps.Count == 4, "Hilichurl automatic arm/leg candidates 4/4");
        Check(animalMaps.Count == 4, "Rifthound automatic fore/hind candidates 4/4");
        Seek(.72f);
        Invoke(plugin, "CommitDraft");
        yield return new WaitForEndOfFrame();
        Check(plugin.ForItem(human).Count == 4 && Mathf.Abs(Native.playbackTime - .72f) < .00001f &&
            Vector3.Distance(originalFoot, TransformOf(Bone(human, "bip_foot_L")).position) < .0001f,
            "Confirm preserves visible pose and cursor at 0.72 seconds");
        plugin.Confirm(quadruped, animalMaps, null);
        Rig foot = plugin.ForItem(human).Find(delegate(Rig r) { return r.Map.Tip == Bone(human, "bip_foot_L"); });
        Rig animalFoot = plugin.ForItem(quadruped)[0];
        Check(foot != null && !plugin.MissingInitial(foot) && !plugin.MissingInitial(animalFoot), "0:00 red position/rotation and blue position keys inserted");
        Interpolable footTrack = Track(foot.Controls[0], "guideObjectPos", foot.Controls[0].guideObject);
        Interpolable otherTrack = Track(animalFoot.Controls[0], "guideObjectPos", animalFoot.Controls[0].guideObject);
        object firstKey = footTrack.keyframes[0], unrelatedKey = otherTrack.keyframes[0];
        int trackCount = CountTracks();
        OCIFolder originalRed = foot.Controls[0], originalBlue = foot.Controls[1];
        foot = plugin.Confirm(human, new[] { foot.Map.Copy() }, foot)[0];
        Check(foot.Controls[0] == originalRed && foot.Controls[1] == originalBlue && CountTracks() == trackCount &&
            ReferenceEquals(firstKey, footTrack.keyframes[0]) && ReferenceEquals(unrelatedKey, otherTrack.keyframes[0]),
            "Reassignment reuses red/blue tracks and preserves existing keys on both items");
        bool blocked = false;
        try { plugin.Confirm(human, new[] { foot.Map.Copy() }, null); }
        catch (InvalidOperationException) { blocked = true; }
        Check(blocked, "Overlapping active FK bone rejected");
        object constraint = AddConstraint(TransformOf(quadruped), TransformOf(foot.Map.Middles[0]));
        plugin.SetEnabled(foot, false);
        bool detected = false;
        try { plugin.SetEnabled(foot, true); }
        catch (InvalidOperationException ex) { detected = ex.Message.Contains("NodesConstraints"); }
        RemoveConstraint(constraint);
        plugin.SetEnabled(foot, true);
        Check(detected, "Active NodesConstraints rotation on the knee prevents re-enabling that part");
        plugin.SetEnabled(foot, false);
        LimbIK existingIK = human.objectItem.AddComponent<LimbIK>();
        existingIK.solver.SetChain(TransformOf(foot.Map.Root), TransformOf(foot.Map.Middles[0]),
            TransformOf(foot.Map.Tip), human.objectItem.transform);
        bool finalIkDetected = false;
        try { plugin.SetEnabled(foot, true); }
        catch (InvalidOperationException ex) { finalIkDetected = ex.Message.Contains("LimbIK"); }
        UnityEngine.Object.DestroyImmediate(existingIK);
        plugin.SetEnabled(foot, true);
        Check(finalIkDetected, "Existing active FinalIK LimbIK rejects the same bones");
        int beforeFailure = StudioApp.dicObjectCtrl.Count;
        try
        {
            Play(true);
            plugin.Confirm(hatchling, new[] { Auto(hatchling)[0] }, null);
            throw new Exception("Confirmation should fail while playing");
        }
        catch (InvalidOperationException) { }
        finally { Play(false); }
        Check(StudioApp.dicObjectCtrl.Count == beforeFailure && plugin.ForItem(hatchling).Count == 0 && CountTracks() == trackCount,
            "Failed confirmation rolls back staged guides, mapping and keys");
        bool failedKey = false;
        try { plugin.Timeline.Key(new[] { new Channel(originalRed, originalRed.guideObject, false), null }, .31f, false); }
        catch { failedKey = true; }
        Check(failedKey && !footTrack.keyframes.ContainsKey(.31f) && ReferenceEquals(firstKey, footTrack.keyframes[0]), "Partial native key transaction rolled back");

        Mapping waistMap = new Mapping { Name = "腰", Waist = true, Tip = Bone(human, "bip_pelvis") };
        Rig waist = plugin.Confirm(human, new[] { waistMap }, null)[0];
        Vector3 waistHome = TransformOf(waist.Controls[0]).position;
        Quaternion waistRotation = TransformOf(waist.Controls[0]).rotation;
        Vector3 footTarget = TransformOf(foot.Controls[0]).position;
        Vector3 otherPosition = TransformOf(animalFoot.Map.Tip).position;
        Plugin.Place(waist.Controls[0], waistHome + new Vector3(.008f, -.02f, .005f), waistRotation);
        yield return new WaitForEndOfFrame();
        float footError = Vector3.Distance(TransformOf(foot.Map.Tip).position, footTarget);
        Check(Vector3.Distance(TransformOf(foot.Controls[0]).position, footTarget) < .00001f && footError < .004f &&
            Vector3.Distance(TransformOf(animalFoot.Map.Tip).position, otherPosition) < .0001f,
            "Waist translation keeps reachable foot planted; error " + footError + " world units");
        Quaternion redRotation = TransformOf(foot.Controls[0]).rotation;
        Quaternion editedRotation = redRotation * Quaternion.Euler(12, -8, 15);
        Plugin.Place(foot.Controls[0], footTarget, editedRotation);
        yield return new WaitForEndOfFrame();
        Check(Quaternion.Angle(TransformOf(foot.Map.Tip).rotation, editedRotation) < .08f, "Red guide controls the tip rotation");
        Plugin.Place(foot.Controls[0], footTarget, redRotation);

        Plugin.Select(hatchling);
        Invoke(plugin, "UseSelection");
        Invoke(plugin, "BeginManual", new Mapping { Name = "しっぽ" }, null, null, false);
        OCIChar.BoneInfo a = Bone(hatchling, "Bone_Tail_01"), b = Bone(hatchling, "Bone_Tail_02"), c = Bone(hatchling, "Bone_Tail_05");
        yield return new WaitForEndOfFrame();
        Check(a.active && a.guideObject.guideSelect.GetComponent<Collider>().enabled, "Manual mode makes the actual FK sphere collider available with FK initially off");
        ClickBone(a);
        ClickBone(Bone(human, "bip_knee_L"));
        Check((int)Member(plugin, "step") == 1 && plugin.Message.Contains("対象"), "Foreign bone click leaves the manual step unchanged");
        ClickBone(c);
        Check((int)Member(plugin, "step") == 1, "Invalid middle leaves the manual step unchanged");
        ClickBone(b);
        ClickBone(c);
        Check((int)Member(plugin, "step") == 3 && plugin.ForItem(hatchling).Count == 0, "Native FK guide clicks create only a preview");
        ClickBone(a);
        Check((int)Member(plugin, "step") == 0 && plugin.ForItem(human).Count == 5, "Fourth click resets the draft and keeps confirmed parts");
        ClickBone(a);
        ClickBone(b);
        ClickBone(c);
        Invoke(plugin, "CommitDraft");
        Rig tail = plugin.ForItem(hatchling)[0];
        Invoke(plugin, "BeginManual", tail.Map.Copy(), tail, null, true);
        plugin.Pick(Bone(hatchling, "Bone_Tail_04"));
        Invoke(plugin, "CommitDraft");
        tail = plugin.ForItem(hatchling)[0];
        Check(tail.Controls.Count == 3 && tail.Bones.Length == 5, "Five-joint tail includes unselected FK bones and two ordered blue guides");
        Vector3 middleA = TransformOf(tail.Bones[1]).position, middleB = TransformOf(tail.Bones[3]).position;
        Vector3 endHome = TransformOf(tail.Map.Tip).position;
        float[] lengths = Lengths(tail);
        Plugin.Place(tail.Controls[0], Vector3.Lerp(endHome, TransformOf(tail.Bones[0]).position, .12f), TransformOf(tail.Controls[0]).rotation);
        Plugin.Place(tail.Controls[1], middleA + new Vector3(.055f, .025f, 0), Quaternion.identity);
        Plugin.Place(tail.Controls[2], middleB + new Vector3(-.045f, .05f, 0), Quaternion.identity);
        yield return new WaitForEndOfFrame();
        float tailError = Vector3.Distance(TransformOf(tail.Map.Tip).position, TransformOf(tail.Controls[0]).position);
        float[] after = Lengths(tail);
        float lengthError = 0;
        for (int i = 0; i < lengths.Length; i++) lengthError = Mathf.Max(lengthError, Mathf.Abs(lengths[i] - after[i]));
        Check(Vector3.Distance(middleA, TransformOf(tail.Bones[1]).position) > .003f &&
            Vector3.Distance(middleB, TransformOf(tail.Bones[3]).position) > .003f && lengthError < .0001f && tailError < .003f,
            "Two blue guides shape the tail with fixed bone lengths; target error " + tailError + ", length error " + lengthError);
        Vector3 tailRoot = TransformOf(tail.Bones[0]).position;
        Vector3 axis = (endHome - tailRoot).normalized;
        float reach = 0;
        foreach (float length in lengths) reach += length;
        Plugin.Place(tail.Controls[0], tailRoot + axis * reach * 1.25f, TransformOf(tail.Controls[0]).rotation);
        yield return new WaitForEndOfFrame();
        Check(Vector3.Distance(TransformOf(tail.Bones[0]).position, tailRoot) < .0001f &&
            Vector3.Distance(TransformOf(tail.Map.Tip).position, tailRoot + axis * reach) < .0005f,
            "Unreachable red guide clamps at total chain reach with the root fixed");
        Plugin.Place(tail.Controls[0], tailRoot + axis * reach * .999f, TransformOf(tail.Controls[0]).rotation);
        yield return new WaitForEndOfFrame();
        Check(Vector3.Distance(TransformOf(tail.Map.Tip).position, TransformOf(tail.Controls[0]).position) < .0005f,
            "Nearly extended tail still reaches red with crossed blue goals");

        Seek(1);
        Plugin.Place(waist.Controls[0], waistHome + new Vector3(.025f, -.035f, .01f), waistRotation * Quaternion.Euler(0, 9, 0));
        plugin.Key(human, false);
        Check(Track(waist.Controls[0], "guideObjectPos", waist.Controls[0].guideObject).keyframes.ContainsKey(1), "Current cursor key inserted at one second");
        Seek(.5f);
        Plugin.Select(human);
        Invoke(plugin, "UseSelection");
        StudioApp.workInfo.visibleAxis = true;
        StudioApp.cameraCtrl.ReflectOption();
        Studio.CameraControl.CameraData camera = StudioApp.cameraCtrl.Export();
        camera.pos = new Vector3(-.62f, .62f, 0);
        camera.rotate = Vector3.zero;
        camera.distance = new Vector3(0, 0, -4.0f);
        camera.parse = 34;
        StudioApp.cameraCtrl.Import(camera);
        for (int i = 0; i < 8; i++) yield return null;
        Application.CaptureScreenshot(Path.Combine(output, "studio.png"));
        yield return new WaitForEndOfFrame();
        Save("editing.png");
        WriteSamples(human);
        Seek(.72f);
        plugin.Timeline.Bake(plugin, human, 0, 1, 30, false);
        Check(plugin.ForItem(human).TrueForAll(delegate(Rig r) { return !r.Enabled; }) && animalFoot.Enabled && tail.Enabled,
            "Bake stops only the selected item's live rigs");
        Check(Mathf.Abs(Native.playbackTime - .72f) < .00001f && ReferenceEquals(unrelatedKey, otherTrack.keyframes[0]),
            "Bake preserves cursor and another item's track");
        Interpolable positionTrack = Track(human, "guideObjectPos", human.guideObject);
        Check(positionTrack != null && positionTrack.keyframes.Count == 31, "Bake emits 31 standard item position keys for waist");
        float bakeError = VerifySamples(human);
        Check(bakeError < .001f, "Baked standard tracks match live IK at 11 sampled times; max error " + bakeError);
        plugin.SetEnabled(waist, true);
        bool protectedKey = false;
        try { plugin.Timeline.Bake(plugin, human, 0, 1, 30, false); }
        catch (InvalidOperationException) { protectedKey = true; }
        plugin.SetEnabled(waist, false);
        Check(protectedKey && positionTrack.keyframes.Count == 31, "Existing native bake keys protected without explicit overwrite");
        Save("baked.png");
        object outsideStart = positionTrack.keyframes[0], outsideEnd = positionTrack.keyframes[1];
        foreach (Rig rig in plugin.ForItem(human)) plugin.SetEnabled(rig, true);
        plugin.Timeline.Bake(plugin, human, .2f, .8f, 60, true);
        Check(ReferenceEquals(outsideStart, positionTrack.keyframes[0]) && ReferenceEquals(outsideEnd, positionTrack.keyframes[1]) &&
            positionTrack.keyframes.ContainsKey(.2f) && positionTrack.keyframes.ContainsKey(.8f) &&
            positionTrack.keyframes.Count > 31 && plugin.ForItem(human).TrueForAll(delegate(Rig r) { return !r.Enabled; }),
            "Explicit 60 fps overwrite replaces only interval keys and preserves keys outside");
        OCIItem unfamiliar = Add(70000428, "Golden Wolflord", Vector3.zero);
        Check(Auto(unfamiliar).Count == 0, "Golden Wolflord custom names require manual assignment");
        Plugin.Select(unfamiliar);
        Invoke(plugin, "UseSelection");
        Invoke(plugin, "BeginManual", new Mapping { Name = "Wolflord chain" }, null, null, false);
        Mapping custom = FindUnfamiliarChain(unfamiliar);
        ClickBone(custom.Root);
        ClickBone(custom.Middles[0]);
        ClickBone(custom.Tip);
        ClickBone(custom.Root);
        Check((int)Member(plugin, "step") == 0, "Custom-name item resets on the fourth FK click");
        ClickBone(custom.Root);
        ClickBone(custom.Middles[0]);
        ClickBone(custom.Tip);
        Invoke(plugin, "CommitDraft");
        Check(plugin.ForItem(unfamiliar).Count == 1 && plugin.ForItem(unfamiliar)[0].Valid, "Golden Wolflord custom FK chain confirmed");
#endif
    }

    OCIItem FindItem(int id)
    {
        int slot = Resolve(id);
        foreach (ObjectCtrlInfo obj in StudioApp.dicObjectCtrl.Values)
        {
            OCIItem item = obj as OCIItem;
            if (item != null && item.itemInfo.no == slot) return item;
        }
        return null;
    }

    static string[] Samples = { "bip_pelvis", "bip_foot_L", "bip_foot_R", "bip_hand_L", "bip_hand_R", "bip_knee_L" };
    float VerifySamples(OCIItem item) { return VerifySamples(item, "samples.txt"); }

    float VerifySamples(OCIItem item, string file)
    {
        float error = 0;
        foreach (string row in File.ReadAllLines(Path.Combine(output, file)))
        {
            string[] fields = row.Split(' ');
            Seek(Single.Parse(fields[0], CultureInfo.InvariantCulture));
            for (int i = 0; i < Samples.Length; i++)
            {
                Vector3 expected = new Vector3(Single.Parse(fields[1 + i * 3], CultureInfo.InvariantCulture),
                    Single.Parse(fields[2 + i * 3], CultureInfo.InvariantCulture), Single.Parse(fields[3 + i * 3], CultureInfo.InvariantCulture));
                error = Mathf.Max(error, Vector3.Distance(expected, TransformOf(Bone(item, Samples[i])).position));
            }
        }
        return error;
    }

    float VerifyAnimal(OCIItem animal)
    {
        string[] rows = File.ReadAllLines(Path.Combine(output, "animal-samples.txt"));
        float error = 0;
        foreach (string row in rows)
        {
            string[] values = row.Split(' ');
            Seek(Single.Parse(values[0], CultureInfo.InvariantCulture));
            Vector3 expected = new Vector3(Single.Parse(values[1], CultureInfo.InvariantCulture),
                Single.Parse(values[2], CultureInfo.InvariantCulture), Single.Parse(values[3], CultureInfo.InvariantCulture));
            error = Mathf.Max(error, Vector3.Distance(expected, TransformOf(Bone(animal, values[4])).position));
        }
        return error;
    }

    static List<object> LiveConstraints()
    {
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type type = assembly.GetType("NodesConstraints.NodesConstraints");
            if (type == null) continue;
            object instance = type.GetField("_self", Flags).GetValue(null);
            List<object> result = new List<object>();
            foreach (object row in (IEnumerable)Member(instance, "_constraints")) result.Add(row);
            return result;
        }
        return new List<object>();
    }

    static List<object> BoneConstraints(OCIItem item)
    {
        List<object> result = new List<object>();
        foreach (object row in LiveConstraints())
        {
            Transform child = Member(row, "childTransform") as Transform;
            if ((bool)Member(row, "enabled") && child != null &&
                item.listBones.Exists(delegate(OCIChar.BoneInfo bone) { return TransformOf(bone) == child; }))
                result.Add(row);
        }
        return result;
    }

    IEnumerator VerifyUserScene()
    {
        OCIItem item = FindItem(70000427);
        Check(item != null && item.itemInfo.enableFK && item.listBones.Count == 46, "The original Geovishap prefab and its 46 FK bones are present");
        float maximumPosition = 0, maximumRotation = 0;
        List<object> follows = BoneConstraints(item);
        string[] lines = File.ReadAllLines(Path.Combine(output, "user-reference.tsv"));
        float previous = -1;
        foreach (string line in lines)
        {
            string[] fields = line.Split('\t');
            float time = Single.Parse(fields[0], CultureInfo.InvariantCulture);
            if (time != previous)
            {
                Seek(time);
                yield return null;
                yield return new WaitForEndOfFrame();
                previous = time;
            }
            Transform bone = TransformOf(Bone(item, fields[1]));
            bool followed = follows.Exists(delegate(object row)
            {
                Transform target = (Transform)Member(row, "childTransform");
                return bone == target || bone.IsChildOf(target);
            });
            if (followed) continue;
            Vector3 position = new Vector3(Number(fields[2]), Number(fields[3]), Number(fields[4]));
            Quaternion rotation = new Quaternion(Number(fields[5]), Number(fields[6]), Number(fields[7]), Number(fields[8]));
            maximumPosition = Mathf.Max(maximumPosition, Vector3.Distance(bone.position, position));
            maximumRotation = Mathf.Max(maximumRotation, Quaternion.Angle(bone.rotation, rotation));
        }
        float positionBudget = phase == "user-compact" ? .005f : .002f;
        float rotationBudget = phase == "user-compact" ? .5f : .2f;
        Check(maximumPosition < positionBudget && maximumRotation < rotationBudget,
            "Unconstrained FK bones match the live source; position error " + maximumPosition + ", rotation error " + maximumRotation);
        Seek(.72f);
        Play(true);
        float initialTime = Native.playbackTime;
        int frames = 0;
        float error = 0;
        for (int i = 0; i < 45; i++)
        {
            yield return new WaitForEndOfFrame();
            foreach (Interpolable track in Tracks())
                if (track.oci == item && track.id == "guideObjectRot")
                {
                    GuideObject guide = track.parameter as GuideObject;
                    if (guide != null) error = Mathf.Max(error,
                        Quaternion.Angle(guide.transformTarget.localRotation, Quaternion.Euler(guide.changeAmount.rot)));
                }
            frames++;
        }
        Play(false);
        Check(Native.playbackTime > initialTime && frames == 45 && error < .12f,
            "The baked scene naturally plays native FK; maximum FK application error " + error);
        if (follows.Count > 0)
        {
            Check(follows.Count == 2 && LiveConstraints().Count == 5, "All five original constraints remain; two drive the actual hand bones");
            Seek(0);
            Play(true);
            float until = Time.realtimeSinceStartup + Native.duration + .1f;
            float contactError = 0, angleError = 0, residualStep = 0;
            Vector3[] previousContact = new Vector3[follows.Count];
            int observedFrames = 0;
            while (Time.realtimeSinceStartup < until)
            {
                yield return new WaitForEndOfFrame();
                for (int n = 0; n < follows.Count; n++)
                {
                    object row = follows[n];
                    Transform parent = (Transform)Member(row, "parentTransform"), child = (Transform)Member(row, "childTransform");
                    Vector3 goal = parent.TransformPoint((Vector3)Member(row, "positionOffset"));
                    contactError = Mathf.Max(contactError, Vector3.Distance(child.position, goal));
                    angleError = Mathf.Max(angleError, Quaternion.Angle(child.rotation, parent.rotation * (Quaternion)Member(row, "rotationOffset")));
                    Vector3 relative = parent.InverseTransformPoint(child.position);
                    if (observedFrames > 0) residualStep = Mathf.Max(residualStep, Vector3.Distance(relative, previousContact[n]));
                    previousContact[n] = relative;
                }
                observedFrames++;
            }
            Play(false);
            Check(observedFrames > 90 && contactError < .00001f && angleError < .12f && residualStep < .00001f,
                "Natural playback across the full clip keeps both hands attached without relative shaking; frames " + observedFrames +
                ", contact error " + contactError + ", relative step " + residualStep + ", angle error " + angleError);
        }
    }

    static float Number(string value) { return Single.Parse(value, CultureInfo.InvariantCulture); }

    static Interpolable RootPositionTrack(OCIItem item, Transform root)
    {
        foreach (Interpolable track in Tracks())
            if (track.oci == item && track.owner == "KKPE" && track.id == "bonePos" &&
                (Transform)Member(track.parameter, "value") == root) return track;
        return null;
    }

#if !INDEPENDENT
    IEnumerator UserBake(Plugin plugin)
    {
        bool reload = phase == "user-reload";
        string path = reload ? Path.Combine(output, "user-shared.png") : phase == "user-node-edit" ? Path.Combine(output, "user-selected.png") :
            Path.GetFullPath(Path.Combine(output, "../user-bake-fixture/original.png"));
        Check(StudioApp.LoadScene(path), "The provided user scene opens");
        for (int i = 0; i < 120; i++) yield return null;
        Play(false);
        OCIItem item = FindItem(70000427);
        Check(item != null && item.listBones.Count == 46, "Geovishap loads with its original FK hierarchy");
        if (reload)
        {
            Check(plugin.Rigs.Count == 0 && StudioApp.dicObjectCtrl.Count == 3, "Reloaded share retains both other objects and has no IKTHIS helpers");
            IEnumerator verify = VerifyUserScene();
            while (verify.MoveNext()) yield return verify.Current;
            yield break;
        }
        if (phase == "user-node-edit")
        {
            int bindings = 0;
            foreach (Rig rig in plugin.Rigs) bindings += rig.NodeLinks;
            Check(bindings == 2 && BoneConstraints(item).Count == 2, "Saved authoring data restores both transferred constraint bindings");
            Dictionary<Transform, Vector3> boneRest = new Dictionary<Transform, Vector3>();
            foreach (object row in BoneConstraints(item))
                boneRest.Add((Transform)Member(row, "childTransform"), (Vector3)Member(row, "originalChildPosition"));
            foreach (Rig rig in plugin.Rigs) plugin.SetEnabled(rig, true);
            yield return null;
            yield return new WaitForEndOfFrame();
            Check(BoneConstraints(item).Count == 0 && LiveConstraints().Count == 5 &&
                plugin.Rigs.TrueForAll(delegate(Rig rig) { return rig.Enabled && rig.NodeLinks == 0; }),
                "Re-enabling moves the original constraints back to the IK guides and releases their bone offsets");
            foreach (KeyValuePair<Transform, Vector3> bone in boneRest)
                Check(Vector3.Distance(bone.Key.localPosition, bone.Value) < .000001f, "Returning to IK restores the original hand bone length");
            plugin.BeginBake(item, 0, Native.duration, 30, true, true);
            while (plugin.Baking) yield return null;
            Check(BoneConstraints(item).Count == 2 && plugin.Rigs.TrueForAll(delegate(Rig rig) { return rig.Baked; }),
                "Baking again transfers both links to the hand bones");
            HashSet<Transform> originalParents = new HashSet<Transform>();
            foreach (object row in BoneConstraints(item)) originalParents.Add((Transform)Member(row, "parentTransform"));
            Plugin.Select(item);
            // NodesConstraints 1.6.3 clones a link when both endpoints are
            // included in Studio's duplication selection.
            foreach (ObjectCtrlInfo obj in StudioApp.dicObjectCtrl.Values)
                if (obj is OCIChar)
                    StudioApp.treeNodeCtrl.GetType().GetMethod("AddSelectNode", Flags).Invoke(
                        StudioApp.treeNodeCtrl, new object[] { obj.treeNodeObject, true });
            StudioApp.Duplicate();
            for (int i = 0; i < 85; i++) yield return null;
            OCIItem duplicate = null;
            foreach (ObjectCtrlInfo obj in StudioApp.dicObjectCtrl.Values)
            {
                OCIItem candidate = obj as OCIItem;
                if (candidate != null && candidate != item && candidate.listBones != null && candidate.listBones.Count == 46)
                    duplicate = candidate;
            }
            Check(duplicate != null && BoneConstraints(duplicate).Count == 2 && BoneConstraints(item).Count == 2,
                "Duplicating the item together with its constraint source gives the copy its own two bone constraints");
            Check(BoneConstraints(duplicate).TrueForAll(delegate(object row)
                { return !originalParents.Contains((Transform)Member(row, "parentTransform")); }),
                "The duplicate follows its copied character rather than the original character");
            yield break;
        }
        if (phase == "user-fk-only") plugin.KeepNodes = false;
        if (phase == "user-compact") plugin.ReductionQuality = BakeQuality.Compact;
        Check(plugin.Rigs.Count == 5 && plugin.Rigs.TrueForAll(delegate(Rig rig) { return rig.Enabled && rig.Valid; }),
            "All five original live rigs restore, including the two constrained red hands");
        HashSet<ObjectCtrlInfo> helpers = new HashSet<ObjectCtrlInfo>();
        foreach (Rig rig in plugin.Rigs)
        {
            helpers.Add(rig.Group);
            foreach (OCIFolder control in rig.Controls) helpers.Add(control);
        }
        Dictionary<Interpolable, SortedList<float, Frame>> retained = new Dictionary<Interpolable, SortedList<float, Frame>>();
        foreach (Interpolable track in Tracks())
            if (!helpers.Contains(track.oci)) retained.Add(track, new SortedList<float, Frame>(track.keyframes));
        if (phase == "user-transfer-rollback")
        {
            List<object> inputRows = new List<object>();
            List<Rig> inputRigs = new List<Rig>();
            foreach (Rig rig in plugin.Rigs)
                foreach (object row in LiveConstraints())
                    if ((Transform)Member(row, "childTransform") == TransformOf(rig.Controls[0]))
                    { inputRows.Add(row); inputRigs.Add(rig); }
            Check(inputRows.Count == 2, "Both incoming links are present before the fault test");
            object failingRow = inputRows[1];
            GuideObject failingGuide = inputRigs[1].Map.Tip.guideObject;
            bool injected = false;
            Action fail = delegate
            {
                if ((Transform)Member(failingRow, "childTransform") == failingGuide.transformTarget)
                { injected = true; throw new InvalidOperationException("Injected link transfer failure"); }
            };
            int originalTracks = CountTracks();
            failingGuide.changeAmount.onChangePos += fail;
            try
            {
                plugin.BeginSharing(30, false, true);
                while (plugin.Baking) yield return null;
            }
            finally { failingGuide.changeAmount.onChangePos -= fail; }
            bool restored = true;
            for (int i = 0; i < inputRows.Count; i++)
                restored &= (Transform)Member(inputRows[i], "childTransform") == TransformOf(inputRigs[i].Controls[0]);
            Check(injected && restored && CountTracks() == originalTracks && BoneConstraints(item).Count == 0 &&
                plugin.Rigs.TrueForAll(delegate(Rig rig) { return rig.Enabled && !rig.Baked && rig.NodeLinks == 0; }),
                "A failure on the second link rolls back both transferred links, all output tracks and rig states");
            yield break;
        }
        if (phase == "user-cancel" || phase == "user-modify")
        {
            Seek(.72f);
            yield return null;
            yield return new WaitForEndOfFrame();
            int count = CountTracks(), objects = StudioApp.dicObjectCtrl.Count;
            plugin.BeginSharing(30, false, true);
            for (int i = 0; i < 8; i++) yield return null;
            Interpolable changed = Track(item, "guideObjectPos", item.guideObject);
            object newValue = null;
            if (phase == "user-modify")
            {
                newValue = (Vector3)changed.keyframes[0].value + Vector3.right * .01f;
                changed.keyframes[0].value = newValue;
            }
            else plugin.CancelBake = true;
            while (plugin.Baking) yield return null;
            Check(CountTracks() == count && StudioApp.dicObjectCtrl.Count == objects && plugin.Rigs.Count == 5 &&
                plugin.Rigs.TrueForAll(delegate(Rig rig) { return rig.Enabled; }) && Mathf.Abs(Native.playbackTime - .72f) < .00001f,
                "Canceling observation preserves all original tracks, guides, live rigs and the cursor");
            if (phase == "user-modify")
                Check(System.Object.Equals(changed.keyframes[0].value, newValue) && plugin.Message.Contains("キーやトラック"),
                    "Changing an input key aborts the bake and preserves the user's changed key");
            yield break;
        }
        List<string> reference = new List<string>();
        foreach (float at in new[] { 0f, 1f / 30, .5f, 1f, 2f, 3f, 4.5f, 6f, 8f, 9f, Native.duration })
        {
            Seek(at);
            yield return null;
            yield return new WaitForEndOfFrame();
            foreach (OCIChar.BoneInfo info in item.listBones)
            {
                Transform bone = TransformOf(info);
                Vector3 pos = bone.position;
                Quaternion rot = bone.rotation;
                reference.Add(String.Join("\t", new[] { F(at), bone.name, F(pos.x), F(pos.y), F(pos.z), F(rot.x), F(rot.y), F(rot.z), F(rot.w) }));
            }
        }
        File.WriteAllLines(Path.Combine(output, "user-reference.tsv"), reference.ToArray());
        Seek(.72f);
        yield return null;
        yield return new WaitForEndOfFrame();
        if (phase == "user-selected") plugin.BeginBake(item, 0, Native.duration, 30, false, true);
        else plugin.BeginSharing(30, false, true);
        float deadline = Time.realtimeSinceStartup + 100;
        while (plugin.Baking && Time.realtimeSinceStartup < deadline) yield return null;
        Check(!plugin.Baking && Mathf.Abs(Native.playbackTime - .72f) < .00001f,
            "The observed bake completes and restores the 0.72-second cursor; " + plugin.Message);
        Check(plugin.LastBakeDenseKeys > plugin.LastBakeWrittenKeys && plugin.LastBakeWrittenKeys > 0,
            "The completed bake reports fewer output keys than per-frame sampling: " + plugin.LastBakeDenseKeys + " -> " + plugin.LastBakeWrittenKeys);
        if (phase == "user-selected")
            Check(plugin.Rigs.Count == 5 && plugin.Rigs.TrueForAll(delegate(Rig rig) { return !rig.Enabled && rig.Baked; }),
                "Selected-item bake succeeds on both linked hands and the other three parts");
        else Check(plugin.Rigs.Count == 0 && StudioApp.dicObjectCtrl.Count == 3,
            "Share finalization removes only IKTHIS guides and retains both other objects; " + plugin.Message);
        bool untouched = true;
        int retainedKeys = 0;
        foreach (KeyValuePair<Interpolable, SortedList<float, Frame>> row in retained)
        {
            untouched &= row.Key.keyframes.Count == row.Value.Count;
            foreach (KeyValuePair<float, Frame> frame in row.Value)
            {
                untouched &= row.Key.keyframes.ContainsKey(frame.Key) && ReferenceEquals(row.Key.keyframes[frame.Key], frame.Value);
                retainedKeys++;
            }
        }
        Check(untouched, "All " + retained.Count + " unrelated or independent FK tracks and their " + retainedKeys + " original keys are retained");
        IEnumerator playback = VerifyUserScene();
        while (playback.MoveNext()) yield return playback.Current;
        Save(phase == "user-selected" ? "user-selected.png" : phase == "user-fk-only" ? "user-fk-only.png" :
            phase == "user-compact" ? "user-compact.png" : "user-shared.png");
    }

    static string F(float value) { return value.ToString("R", CultureInfo.InvariantCulture); }

    IEnumerator RootWorkflow(Plugin plugin)
    {
        if (phase == "root-reload")
        {
            Check(StudioApp.LoadScene(Path.Combine(output, "root-editing.png")), "Edited root scene reopens");
            for (int i = 0; i < 85; i++) yield return null;
            OCIItem restored = FindItem(70000429);
            Rig rig = plugin.ForItem(restored).Find(delegate(Rig r) { return r.Map.MoveRoot; });
            Check(rig != null && rig.Enabled && rig.RootControl.parentInfo == rig.Group,
                "Root guide and the collapsed item-owned group are restored");
            Seek(1);
            yield return new WaitForEndOfFrame();
            Transform bone = TransformOf(rig.Map.Root);
            Check(Vector3.Distance(bone.localPosition, rig.RootRestPosition) > .004f &&
                Vector3.Distance(bone.position, TransformOf(rig.RootControl).position) < .0001f &&
                Vector3.Distance(TransformOf(rig.Map.Tip).position, TransformOf(rig.Controls[0]).position) < .001f,
                "Reloaded Timeline drives the root in parent coordinates while retaining the red pin");
            plugin.SetEnabled(rig, false);
            Check(Vector3.Distance(bone.localPosition, rig.RootRestPosition) < .0001f, "Disabling a restored root releases its positional offset");
            plugin.SetEnabled(rig, true);
            Plugin.Select(restored);
            StudioApp.Duplicate();
            for (int i = 0; i < 85; i++) yield return null;
            Rig duplicate = plugin.Rigs.Find(delegate(Rig r) { return r.Item != restored && r.Map.MoveRoot; });
            Check(duplicate != null && duplicate.RootControl != rig.RootControl &&
                duplicate.RootControl.guideObject.parent == TransformOf(duplicate.Map.Root).parent &&
                Track(duplicate.RootControl, "guideObjectPos", duplicate.RootControl.guideObject).keyframes.Count == 2,
                "Duplicating binds the green guide to the new item's bone and copies its own Timeline track");
            Vector3 originalPosition = bone.position;
            duplicate.RootControl.guideObject.changeAmount.pos += Vector3.right * .003f;
            yield return new WaitForEndOfFrame();
            Check(Vector3.Distance(bone.position, originalPosition) < .0001f, "Moving the duplicate root leaves the source root untouched");
            Check(StudioApp.ImportScene(Path.Combine(output, "root-editing.png")), "Root scene imports through Studio");
            for (int i = 0; i < 85; i++) yield return null;
            HashSet<OCIFolder> unique = new HashSet<OCIFolder>();
            int roots = 0;
            foreach (Rig current in plugin.Rigs)
                if (current.Map.MoveRoot)
                {
                    roots++;
                    unique.Add(current.RootControl);
                    Check(current.RootControl.guideObject.parent == TransformOf(current.Map.Root).parent,
                        "Each imported or duplicated root uses its owning item's parent coordinates");
                }
            Check(roots == 3 && unique.Count == 3, "Import and duplication keep three independent root controls");
            yield break;
        }
        bool creature = phase == "root-kohola";
        string prefix = creature ? "root-kohola" : "root";
        OCIItem human = Add(creature ? 70000434 : 70000429, creature ? "Creature - Koholasaurus" : "Hilichurl", Vector3.zero);
        List<Rig> limbs = plugin.Confirm(human, Auto(human), null);
        Rig foot = creature ? limbs[0] : limbs.Find(delegate(Rig r) { return r.Map.Tip == Bone(human, "bip_foot_L"); });
        Transform root = TransformOf(foot.Map.Root), tip = TransformOf(foot.Map.Tip);
        Interpolable red = Track(foot.Controls[0], "guideObjectPos", foot.Controls[0].guideObject);
        Interpolable blue = Track(foot.Controls[1], "guideObjectPos", foot.Controls[1].guideObject);
        object redKey = red.keyframes[0], blueKey = blue.keyframes[0];
        Vector3 original = root.localPosition, planted = tip.position;
        float length = Vector3.Distance(root.position, TransformOf(foot.Map.Middles[0]).position);
        Seek(.72f);
        foot = plugin.AddRootControl(foot);
        GuideObject green = foot.RootControl.guideObject;
        Interpolable greenTrack = Track(foot.RootControl, "guideObjectPos", green);
        yield return new WaitForEndOfFrame();
        Check(greenTrack != null && greenTrack.keyframes.Count == 1 && greenTrack.keyframes.ContainsKey(0) &&
            Track(foot.RootControl, "guideObjectRot", green) == null && !green.enableRot && !green.enableScale &&
            foot.RootControl.parentInfo == foot.Group && foot.Group.parentInfo == human &&
            Mathf.Abs(Native.playbackTime - .72f) < .00001f && Vector3.Distance(root.localPosition, original) < .00001f &&
            ReferenceEquals(redKey, red.keyframes[0]) && ReferenceEquals(blueKey, blue.keyframes[0]),
            "Adding a movement-only guide keeps the pose, red/blue keys, item hierarchy and cursor; only one zero key is added");
        int trackCount = CountTracks();
        object rootZero = greenTrack.keyframes[0];
        foot = plugin.Confirm(human, new[] { foot.Map.Copy() }, foot)[0];
        Check(foot.RootControl.guideObject == green && CountTracks() == trackCount && ReferenceEquals(rootZero, greenTrack.keyframes[0]),
            "Reconfirming reuses the same green guide, track and existing zero key");
        StudioApp.workInfo.visibleAxis = true;
        StudioApp.cameraCtrl.ReflectOption();
        Plugin.Select(human);
        yield return new WaitForEndOfFrame();
        IDictionary markers = (IDictionary)Member(plugin, "markers");
        Image marker = markers[foot.RootControl] as Image;
        Check(!plugin.Shown && marker != null && marker.gameObject.activeInHierarchy && marker.color.g > marker.color.r,
            "The green control is visible on selection with the IKTHIS window closed");
        Seek(1);
        GuideObjectManager.Instance.mode = 1;
        marker.GetComponent<Button>().onClick.Invoke();
        Check(GuideObjectManager.Instance.selectObject == green && GuideObjectManager.Instance.mode == 0,
            "The green marker activates standard translation axes in one click");
        typeof(Plugin).GetField("autoKey", Flags).SetValue(plugin, true);
        Vector3 delta = (planted - root.position).normalized * .022f;
        DragMove(green, delta);
        yield return new WaitForEndOfFrame();
        Vector3 moved = root.localPosition;
        Check(Vector3.Distance(moved, original) > .004f && Vector3.Distance(tip.position, planted) < .001f &&
            Mathf.Abs(Vector3.Distance(root.position, TransformOf(foot.Map.Middles[0]).position) - length) < .0001f &&
            greenTrack.keyframes.Count == 2 && greenTrack.keyframes.ContainsKey(1),
            "Native move and autokey shift the root, retain bone length and hold the reachable red pin");
        Seek(.5f);
        yield return new WaitForEndOfFrame();
        Check(Vector3.Distance(root.localPosition, Vector3.Lerp(original, moved, .5f)) < .0001f,
            "Scrubbing the position track interpolates the actual parent bone");
        if (phase == "root-no-kkpe")
        {
            bool blocked = false;
            int tracks = CountTracks();
            try { plugin.FinalizeForSharing(30, false, true); }
            catch (InvalidOperationException ex) { blocked = ex.Message.Contains("KKPE"); }
            Check(blocked && foot.Enabled && CountTracks() == tracks && Vector3.Distance(tip.position, planted) < .001f,
                "Without KKPE, live root translation works and sharing safely keeps the source intact");
            yield break;
        }
        if (phase == "root-kkpe-disabled")
        {
            Component controller = human.guideObject.transformTarget.GetComponent("PoseController");
            object editor = Member(controller, "_bonesEditor");
            Transform untouched = TransformOf(Bone(human, "bip_pelvis"));
            Vector3 before = untouched.localPosition;
            editor.GetType().GetMethod("SetBonePosition", Flags).Invoke(editor, new object[] { untouched, before + Vector3.up * .1f });
            Check(!((Behaviour)controller).enabled, "Fixture keeps an existing KKPE bone edit disabled");
            int beforeTracks = CountTracks();
            bool blocked = false;
            try { plugin.FinalizeForSharing(30, false, true); }
            catch (InvalidOperationException ex) { blocked = ex.Message.Contains("無効化中"); }
            Check(blocked && foot.Enabled && CountTracks() == beforeTracks && !((Behaviour)controller).enabled &&
                Vector3.Distance(untouched.localPosition, before) < .0001f,
                "The bake preserves dormant unrelated KKPE edits and commits no partial keys");
            yield break;
        }
        Vector3 itemPosition = human.guideObject.changeAmount.pos;
        human.guideObject.SetWorldPos(TransformOf(human).position + delta * .25f);
        yield return new WaitForEndOfFrame();
        Check(Vector3.Distance(TransformOf(foot.RootControl).position, root.position) < .0001f &&
            Vector3.Distance(tip.position, planted) < .001f,
            "The green guide follows a moving ancestor while the red pin remains in world space");
        human.guideObject.changeAmount.pos = itemPosition;
        typeof(Plugin).GetField("autoKey", Flags).SetValue(plugin, false);
        Invoke(plugin, "ResetRootControl", foot);
        yield return new WaitForEndOfFrame();
        Check(Vector3.Distance(root.localPosition, original) < .00001f && greenTrack.keyframes.Count == 2,
            "Reset restores the original root position without changing keys when autokey is off");
        Seek(1);
        yield return new WaitForEndOfFrame();
        Bounds bounds = human.arrayRender[0].bounds;
        foreach (Renderer renderer in human.arrayRender) bounds.Encapsulate(renderer.bounds);
        Studio.CameraControl.CameraData view = StudioApp.cameraCtrl.Export();
        view.pos = bounds.center;
        view.rotate = new Vector3(5, 25, 0);
        view.distance = new Vector3(0, 0, -Mathf.Max(bounds.size.x, bounds.size.y, bounds.size.z) * 2.1f);
        view.parse = 34;
        StudioApp.cameraCtrl.Import(view);
        Plugin.Select(human);
        for (int i = 0; i < 4; i++) yield return null;
        Application.CaptureScreenshot(Path.Combine(output, prefix + "-controls.png"));
        yield return new WaitForEndOfFrame();
        Save(prefix + "-editing.png");
        File.WriteAllLines(Path.Combine(output, prefix + "-bones.txt"), new[] { root.name, tip.name });
        if (phase == "root-observed")
        {
            plugin.BeginSharing(30, false, true);
            while (plugin.Baking) yield return null;
        }
        else plugin.FinalizeForSharing(30, false, true);
        Check(plugin.Rigs.Count == 0 && StudioApp.dicObjectCtrl.Count == 1 && RootPositionTrack(human, root) != null &&
            Track(human, "guideObjectRot", foot.Map.Root.guideObject) != null,
            "Sharing writes KKPE position plus standard FK and removes every IK helper");
        Save(prefix + "-shared.png");
    }

    IEnumerator MixedWorkflow(Plugin plugin)
    {
        OCIItem dragon = Add(70000434, "Creature - Koholasaurus", new Vector3(0, 1.236f, -.025f));
        List<Mapping> maps = Auto(dragon);
        Check(maps.Count == 4, "The rollback baseline auto assigns the four limbs");
        List<Rig> rigs = plugin.Confirm(dragon, maps, null);
        OCIChar.BoneInfo neck = Bone(dragon, "Bone_Neck01_M");
        GuideObject fk = neck.guideObject;
        GuideObject driven = rigs[0].Map.Middles[0].guideObject;
        StudioApp.workInfo.visibleAxis = true;
        StudioApp.cameraCtrl.ReflectOption();
        Studio.CameraControl.CameraData camera = StudioApp.cameraCtrl.Export();
        Bounds bounds = dragon.arrayRender[0].bounds;
        foreach (Renderer renderer in dragon.arrayRender) bounds.Encapsulate(renderer.bounds);
        camera.pos = bounds.center;
        camera.rotate = new Vector3(8, 75, 0);
        camera.distance = new Vector3(0, 0, -Mathf.Max(bounds.size.x, bounds.size.y, bounds.size.z) * 2f);
        camera.parse = 34;
        StudioApp.cameraCtrl.Import(camera);
        dragon.treeNodeObject.treeState = TreeNodeObject.TreeState.Open;
        Plugin.Select(dragon);
        for (int i = 0; i < 8; i++) yield return null;
        IDictionary markers = (IDictionary)Member(plugin, "markers");
        int visible = 0;
        foreach (DictionaryEntry row in markers)
            if (((Image)row.Value).gameObject.activeInHierarchy) visible++;
        Collider nativeCollider = fk.guideSelect.GetComponent<Collider>();
        Check(!plugin.Shown && visible == 8 && fk.visibleOutside && fk.guideSelect.draw && nativeCollider.enabled &&
            nativeCollider.gameObject.activeInHierarchy && !driven.visibleOutside && !driven.guideSelect.draw,
            "Eight IK markers and a live native neck FK sphere coexist; IK-owned bone sphere is hidden");
        Application.CaptureScreenshot(Path.Combine(output, "fk-mixed-viewport.png"));
        yield return new WaitForEndOfFrame();
        ((Image)markers[rigs[0].Controls[0]]).GetComponent<Button>().onClick.Invoke();
        yield return null;
        yield return new WaitForEndOfFrame();
        Check(GuideObjectManager.Instance.selectObject == rigs[0].Controls[0].guideObject &&
            nativeCollider.gameObject.layer == LayerMask.NameToLayer("Studio/Col") &&
            fk.guideSelect.draw && !driven.guideSelect.draw,
            "Switching to a red guide keeps native free FK spheres clickable");
        ClickBone(neck);
        yield return null;
        yield return new WaitForEndOfFrame();
        Check(GuideObjectManager.Instance.selectObject == fk && GuideObjectManager.Instance.mode == 1 &&
            ((Image)markers[rigs[0].Controls[0]]).gameObject.activeInHierarchy,
            "Native neck FK selection activates rotation axes without losing IK markers");
        plugin.Timeline.Key(new[] { new Channel(dragon, fk, true) }, 0, false);
        Seek(1);
        Quaternion original = TransformOf(neck).localRotation;
        fk.Rotation(Vector3.up, 17);
        plugin.Timeline.Key(new[] { new Channel(dragon, fk, true) }, 1, true);
        yield return new WaitForEndOfFrame();
        Check(Quaternion.Angle(original, TransformOf(neck).localRotation) > 10 &&
            Vector3.Distance(TransformOf(rigs[0].Map.Tip).position, TransformOf(rigs[0].Controls[0]).position) < .001f,
            "Rotating the unassigned neck leaves the registered limb live");
        plugin.SetEnabled(rigs[0], false);
        yield return null;
        yield return new WaitForEndOfFrame();
        Check(driven.visibleOutside && driven.guideSelect.draw, "Disabling a rig releases its bones to native FK immediately");
        plugin.SetEnabled(rigs[0], true);
        yield return null;
        yield return new WaitForEndOfFrame();
        Check(!driven.visibleOutside, "Re-enabling the rig hides only its driven FK guides again");
        Interpolable track = Track(dragon, "guideObjectRot", fk);
        object end = track.keyframes[1];
        plugin.FinalizeForSharing(30, false, true);
        Check(ReferenceEquals(track, Track(dragon, "guideObjectRot", fk)) && ReferenceEquals(end, track.keyframes[1]),
            "Finalizing the IK leaves independent native FK keys untouched");
        Save("fk-mixed-shared.png");
    }

    IEnumerator Kohola(Plugin plugin, bool reload)
    {
        OCIItem item;
        if (reload)
        {
            Check(StudioApp.LoadScene(Path.Combine(output, "kohola-scene.png")), "Koholasaurus scene reloads");
            for (int i = 0; i < 85; i++) yield return null;
            item = FindItem(70000434);
        }
        else
        {
            item = Add(70000434, "Creature - Koholasaurus", new Vector3(0, 1.236f, -.025f));
            List<Mapping> maps = Auto(item);
            Check(maps.Count == 4, "Koholasaurus auto assigns all four legs");
            Mapping tail = new Mapping { Name = "しっぽ", Root = Bone(item, "Bone_Tail01_M"), Tip = Bone(item, "Bone_Tail06_M") };
            tail.Middles.Add(Bone(item, "Bone_Tail03_M"));
            maps.Add(tail);
            plugin.Confirm(item, maps, null);
        }
        List<Rig> rigs = plugin.ForItem(item);
        Check(rigs.Count == 5 && rigs.TrueForAll(delegate(Rig rig) { return rig.Enabled && !plugin.MissingInitial(rig); }),
            "Five live Koholasaurus parts with initial keys");
        OCIFolder group = rigs[0].Group;
        Check(group.parentInfo == item && item.itemInfo.child.Count == 1 && group.folderInfo.child.Count == 10 &&
            group.treeNodeObject.treeState == TreeNodeObject.TreeState.Close &&
            rigs.TrueForAll(delegate(Rig rig) { return rig.Group == group; }), "One collapsed item-child group owns all ten guides");
        Check(StudioApp.sceneInfo.dicObject.Count == 1, "No top-level guide or folder entries");
        StudioApp.workInfo.visibleAxis = true;
        StudioApp.cameraCtrl.ReflectOption();
        GuideObjectManager.Instance.mode = 1;
        if (!reload)
        {
            Studio.CameraControl.CameraData camera = StudioApp.cameraCtrl.Export();
            Bounds bounds = item.arrayRender[0].bounds;
            foreach (Renderer renderer in item.arrayRender) bounds.Encapsulate(renderer.bounds);
            camera.pos = bounds.center;
            camera.rotate = new Vector3(8, 75, 0);
            camera.distance = new Vector3(0, 0, -Mathf.Max(bounds.size.x, bounds.size.y, bounds.size.z) * 1.9f);
            camera.parse = 34;
            StudioApp.cameraCtrl.Import(camera);
            item.treeNodeObject.treeState = TreeNodeObject.TreeState.Open;
            for (int i = 0; i < 8; i++) yield return null;
            Save("kohola-scene.png");
        }
        Plugin.Select(item);
        yield return null;
        yield return new WaitForEndOfFrame();
        IDictionary markers = (IDictionary)Member(plugin, "markers");
        int visible = 0;
        foreach (DictionaryEntry row in markers)
            if (((Image)row.Value).gameObject.activeInHierarchy) visible++;
        Check(!plugin.Shown && visible == 10, "All ten markers render in rotation mode with panel closed");
        if (phase == "ui")
        {
            Dictionary<GuideObject, Vector3> home = new Dictionary<GuideObject, Vector3>();
            Dictionary<GuideObject, bool> color = new Dictionary<GuideObject, bool>();
            foreach (Rig rig in rigs)
                for (int i = 0; i < rig.Controls.Count; i++)
                {
                    GuideObject guide = rig.Controls[i].guideObject;
                    home.Add(guide, guide.changeAmount.pos);
                    color.Add(guide, i == 0);
                }
            bool redSelected = false, blueSelected = false, moved = false;
            File.WriteAllText(Path.Combine(output, "ui-ready.txt"), "READY");
            while (!File.Exists(Path.Combine(output, "ui-stop.txt")))
            {
                GuideObject selected = GuideObjectManager.Instance.selectObject;
                bool red;
                if (selected != null && color.TryGetValue(selected, out red))
                {
                    redSelected |= red;
                    blueSelected |= !red && GuideObjectManager.Instance.mode == 0;
                    moved |= !red && (selected.changeAmount.pos - home[selected]).sqrMagnitude > 1e-8f;
                }
                yield return null;
            }
            Check(redSelected && blueSelected && moved, "Real viewport clicks select red and blue, and the selected guide moves");
        }
        else
        {
            Application.CaptureScreenshot(Path.Combine(output, "kohola-guides.png"));
            yield return new WaitForEndOfFrame();
            if (reload)
            {
                OCIFolder folder = rigs[0].Controls[1];
                GuideObject guide = folder.guideObject;
                GameObject dot = ((Image)markers[folder]).gameObject;
                Seek(.55f);
                typeof(Plugin).GetField("autoKey", Flags).SetValue(plugin, true);
                Vector3 original = guide.transformTarget.position;
                Vector2 screen = Camera.main.WorldToScreenPoint(original);
                PointerEventData pointer = new PointerEventData(EventSystem.current)
                {
                    button = PointerEventData.InputButton.Left, pressPosition = screen, position = screen
                };
                ExecuteEvents.Execute(dot, pointer, ExecuteEvents.beginDragHandler);
                pointer.position += new Vector2(26, 31);
                ExecuteEvents.Execute(dot, pointer, ExecuteEvents.dragHandler);
                ExecuteEvents.Execute(dot, pointer, ExecuteEvents.endDragHandler);
                Check(Vector3.Distance(original, guide.transformTarget.position) > .01f &&
                    Track(folder, "guideObjectPos", guide).keyframes.ContainsKey(.55f),
                    "Studio drag events move the blue dot and record its optional position key");
            }
        }
    }

    IEnumerator Workflow(Plugin plugin)
    {
        OCIItem human = Add(70000429, "Hilichurl", Vector3.zero);
        OCIItem animal = Add(70000436, "Rifthound", new Vector3(1.3f, 0, 0));
        Plugin.Select(human);
        ClickToolbar("GIN IKTHIS");
        Invoke(plugin, "BeginManual", new Mapping { Name = "左脚" }, null, null, false);
        ClickBone(Bone(human, "bip_hip_L"));
        ClickBone(Bone(human, "bip_knee_L"));
        ClickBone(Bone(human, "bip_foot_L"));
        Check(CountTracks() == 0, "Manual preview creates zero keys");
        Seek(.72f);
        Invoke(plugin, "CommitDraft");
        Rig foot = plugin.ForItem(human)[0];
        Check(foot.Controls.Count == 2 && CountTracks() == 3, "Manual confirm creates exactly two guides and three channels");
        foreach (Interpolable track in Tracks())
            Check(track.oci is OCIFolder && track.keyframes.Count == 1 && track.keyframes.ContainsKey(0),
                "Manual channel has one key at 0:00 and no FK track");
        bool duplicate = false;
        plugin.SetEnabled(foot, false);
        try { plugin.Confirm(human, new[] { foot.Map.Copy() }, null); }
        catch (InvalidOperationException) { duplicate = true; }
        plugin.SetEnabled(foot, true);
        Check(duplicate && CountTracks() == 3 && plugin.ForItem(human).Count == 1, "Re-registering the same disabled mapping cannot duplicate guides or keys");
        ClickToolbar("GIN IKTHIS");
        StudioApp.workInfo.visibleAxis = true;
        StudioApp.cameraCtrl.ReflectOption();
        Plugin.Select(human);
        Studio.CameraControl.CameraData camera = StudioApp.cameraCtrl.Export();
        camera.pos = new Vector3(0, .65f, 0);
        camera.rotate = Vector3.zero;
        camera.distance = new Vector3(0, 0, -3.7f);
        camera.parse = 34;
        StudioApp.cameraCtrl.Import(camera);
        yield return null;
        yield return null;
        yield return new WaitForEndOfFrame();
        GuideObject red = foot.Controls[0].guideObject, blue = foot.Controls[1].guideObject;
        IDictionary markers = (IDictionary)Member(plugin, "markers");
        Check(!plugin.Shown && red.visibleOutside && blue.visibleOutside && markers.Contains(foot.Controls[0]) && markers.Contains(foot.Controls[1]) &&
            ((Image)markers[foot.Controls[0]]).gameObject.activeInHierarchy && ((Image)markers[foot.Controls[1]]).gameObject.activeInHierarchy &&
            !Bone(human, "bip_knee_L").guideObject.visibleOutside, "Selecting item displays clickable IK guides with the panel closed and suppresses overlapping FK spheres"
            + " (panel=" + plugin.Shown + " visibility=" + red.visibleOutside + "/" + blue.visibleOutside +
            " sphere=" + red.guideSelect.draw + "/" + blue.guideSelect.draw + " FK=" + Bone(human, "bip_knee_L").guideObject.visibleOutside + ")");
        GuideObjectManager.Instance.mode = 1;
        ClickControl(blue);
        yield return new WaitForEndOfFrame();
        Check(GuideObjectManager.Instance.selectObject == blue && GuideObjectManager.Instance.mode == 0 && red.visibleOutside,
            "Direct blue sphere click selects native move axes and keeps sibling guides visible");
        yield return null;
        Application.CaptureScreenshot(Path.Combine(output, "direct-guides.png"));
        yield return new WaitForEndOfFrame();
        Seek(.5f);
        DragMove(blue, Vector3.zero);
        Interpolable bluePos = Track(foot.Controls[1], "guideObjectPos", blue);
        Check(bluePos.keyframes.Count == 1, "Unchanged drag writes no keys");
        DragMove(blue, new Vector3(0, .02f, 0));
        Check(bluePos.keyframes.Count == 1, "Automatic keying is opt-in");
        typeof(Plugin).GetField("autoKey", Flags).SetValue(plugin, true);
        DragMove(blue, new Vector3(.01f, 0, .015f));
        DragMove(blue, new Vector3(.005f, 0, 0));
        Interpolable redPos = Track(foot.Controls[0], "guideObjectPos", red);
        Interpolable redRot = Track(foot.Controls[0], "guideObjectRot", red);
        Check(bluePos.keyframes.Count == 2 && bluePos.keyframes.ContainsKey(.5f) && redPos.keyframes.Count == 1 && redRot.keyframes.Count == 1,
            "Two blue drags at the same cursor produce one position key and no keys on other channels");
        ClickControl(red);
        GuideObjectManager.Instance.mode = 1;
        DragRotation(red, 8);
        Check(redRot.keyframes.Count == 2 && redRot.keyframes.ContainsKey(.5f) && redPos.keyframes.Count == 1,
            "Red rotation drag keys only rotation");
        Plugin.Select(animal);
        yield return null;
        yield return new WaitForEndOfFrame();
        Check(!red.visibleOutside && Bone(human, "bip_knee_L").guideObject.visibleOutside,
            "Switching items hides previous guides and restores its ordinary FK spheres");

        Rig waist = plugin.Confirm(human, new[] { new Mapping { Name = "腰", Waist = true, Tip = Bone(human, "bip_pelvis") } }, null)[0];
        Rig fore = plugin.Confirm(animal, new[] { Auto(animal)[0] }, null)[0];
        OCIFolder unrelated = AddObjectFolder.Add();
        unrelated.name = "Unrelated";
        plugin.Timeline.Key(new[] { new Channel(unrelated, unrelated.guideObject, false) }, 0, false);
        Interpolable unrelatedTrack = Track(unrelated, "guideObjectPos", unrelated.guideObject);
        Seek(1);
        foreach (Rig rig in new[] { waist, fore })
        {
            Transform control = TransformOf(rig.Controls[0]);
            Plugin.Place(rig.Controls[0], control.position + new Vector3(0, -.03f, .01f), control.rotation);
        }
        plugin.Key(human, false);
        plugin.Key(animal, false);
        Seek(.5f);
        Save("sharing-editable.png");
        WriteSamples(human, "sharing-samples.txt");
        using (StreamWriter writer = new StreamWriter(Path.Combine(output, "animal-samples.txt")))
            for (int i = 0; i <= 10; i++)
            {
                float at = i * .1f;
                Seek(at);
                fore.Solve();
                Vector3 p = TransformOf(fore.Map.Tip).position;
                writer.WriteLine(String.Format(CultureInfo.InvariantCulture, "{0:R} {1:R} {2:R} {3:R} {4}",
                    at, p.x, p.y, p.z, TransformOf(fore.Map.Tip).name));
            }
        int count = CountTracks();
        plugin.SetEnabled(fore, false);
        bool refused = false;
        try { plugin.FinalizeForSharing(30, false, true); }
        catch (InvalidOperationException) { refused = true; }
        Check(refused && plugin.Rigs.Count == 3 && CountTracks() == count && foot.Enabled,
            "Finalization refuses paused unbaked parts without changing guides or tracks");
        plugin.SetEnabled(fore, true);
        plugin.Timeline.Key(new[] { new Channel(animal, fore.Map.Tip.guideObject, true) }, 0, false);
        count = CountTracks();
        refused = false;
        try { plugin.FinalizeForSharing(30, false, true); }
        catch (InvalidOperationException) { refused = true; }
        Check(refused && CountTracks() == count && foot.Enabled && fore.Enabled &&
            Track(human, "guideObjectRot", foot.Map.Tip.guideObject) == null,
            "A conflict on the second item leaves the first item and all guides unchanged");
        Seek(.72f);
        plugin.FinalizeForSharing(30, true, true);
        yield return new WaitForEndOfFrame();
        Check(plugin.Rigs.Count == 0 && red == null && blue == null && unrelated.objectItem != null &&
            ReferenceEquals(unrelatedTrack, Track(unrelated, "guideObjectPos", unrelated.guideObject)),
            "Clean share removes owned guides, keys and sync across both items while preserving unrelated objects");
        Check(Mathf.Abs(Native.playbackTime - .72f) < .00001f, "Clean share preserves the cursor");
        int outputs = 0, keys = 0;
        foreach (Interpolable track in Tracks())
        {
            if (!(track.oci is OCIItem)) continue;
            outputs++;
            keys += track.keyframes.Count;
        }
        Check(outputs > 5 && keys < outputs * (int)(Native.duration * 30 / 3),
            "Sparse bake kept " + keys + " keys across " + outputs + " tracks; dense baseline " + outputs * (1 + (int)(Native.duration * 30)));
        float humanError = VerifySamples(human, "sharing-samples.txt"), animalError = VerifyAnimal(animal);
        Check(humanError < .001f && animalError < .001f, "Reduced native tracks agree with live poses, maximum error " + Mathf.Max(humanError, animalError));
        Save("shared.png");
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type type = assembly.GetType("ExtensibleSaveFormat.ExtendedSave");
            if (type != null) Check(type.GetMethod("GetSceneExtendedDataById").Invoke(null, new object[] { Id }) == null,
                "Share save contains no IKTHIS extended scene data");
        }
    }

    static void ClickControl(GuideObject guide)
    {
        guide.guideSelect.OnPointerClick(new PointerEventData(EventSystem.current) { button = PointerEventData.InputButton.Left });
    }

    static void DragMove(GuideObject guide, Vector3 displacement)
    {
        GuideMove axis = guide.GetComponentInChildren<GuideMove>();
        PointerEventData pointer = new PointerEventData(EventSystem.current) { button = PointerEventData.InputButton.Left };
        axis.OnInitializePotentialDrag(pointer);
        axis.OnBeginDrag(pointer);
        if (displacement != Vector3.zero) guide.MoveWorld(displacement);
        axis.OnEndDrag(pointer);
    }

    static void DragRotation(GuideObject guide, float degrees)
    {
        GuideRotation axis = guide.GetComponentInChildren<GuideRotation>();
        PointerEventData pointer = new PointerEventData(EventSystem.current)
        {
            button = PointerEventData.InputButton.Left,
            position = Camera.main.WorldToScreenPoint(guide.transformTarget.position)
        };
        axis.OnInitializePotentialDrag(pointer);
        axis.OnBeginDrag(pointer);
        guide.Rotation(Vector3.up, degrees);
        axis.OnEndDrag(pointer);
    }

    IEnumerator MultipleItemEditors(Plugin plugin)
    {
        OCIItem first = Add(70000429, "Same named item", Vector3.zero);
        OCIItem second = Add(70000429, "Same named item", new Vector3(2, 0, 0));
        Mapping a = Auto(first)[0], b = Auto(second)[0];
        Plugin.Select(first);
        ClickToolbar("GIN IKTHIS");
        Invoke(plugin, "FindSuggestions");
        int tracks = CountTracks();
        Plugin.Select(second);
        yield return null; yield return null;
        Check(plugin.Item == second && ((IList)Member(plugin, "suggestions")).Count == 0,
            "Selecting another item switches the open panel even during an automatic preview");
        Invoke(plugin, "FindSuggestions");
        Plugin.Select(first);
        yield return null; yield return null;
        Check(plugin.Item == first && ((IList)Member(plugin, "suggestions")).Count == 4 && CountTracks() == tracks,
            "Each identically named item retains its own automatic preview without creating keys");
        Invoke(plugin, "BeginManual", a.Copy(), null, null, false);
        // Start an incomplete assignment rather than reusing the complete map.
        Invoke(plugin, "BeginManual", new Mapping { Name = "Left limb" }, null, null, false);
        plugin.Pick(a.Root);
        Plugin.Select(second);
        yield return null; yield return null;
        Check(plugin.Item == second && ((IList)Member(plugin, "suggestions")).Count == 4,
            "Leaving a manual draft displays the other item's own preview");
        Invoke(plugin, "BeginManual", new Mapping { Name = "Left limb" }, null, null, false);
        plugin.Pick(b.Root); plugin.Pick(b.Middles[0]);
        Plugin.Select(first);
        yield return null; yield return null;
        Check(plugin.Item == first && (int)Member(plugin, "step") == 1 && ((Mapping)Member(plugin, "draft")).Root == a.Root,
            "Returning to the first item restores its first manual click");
        plugin.Pick(a.Middles[0]); plugin.Pick(a.Tip); Invoke(plugin, "CommitDraft");
        Rig rigA = plugin.ForItem(first)[0];
        Plugin.Select(second);
        yield return null; yield return null;
        Check(plugin.Item == second && (int)Member(plugin, "step") == 2 && ((Mapping)Member(plugin, "draft")).Root == b.Root,
            "Returning to the second item restores its independent second manual click");
        plugin.Pick(b.Tip); Invoke(plugin, "CommitDraft");
        Rig rigB = plugin.ForItem(second)[0];
        Check(rigA.Controls[0] != rigB.Controls[0] && rigA.Controls[1] != rigB.Controls[1] && CountTracks() == 6,
            "Two identical bone hierarchies own separate red/blue guides and six initial channels");
        Vector3 tipB = TransformOf(b.Tip).position;
        Plugin.Select(rigA.Controls[0]);
        yield return null; yield return null;
        Check(plugin.Item == first && plugin.ForItem(plugin.Item).Count == 1,
            "Clicking a helper activates its owning item's panel");
        Seek(.6f);
        typeof(Plugin).GetField("autoKey", Flags).SetValue(plugin, true);
        DragMove(rigA.Controls[0].guideObject, new Vector3(.01f, -.01f, .005f));
        yield return null; yield return new WaitForEndOfFrame();
        Check(Vector3.Distance(tipB, TransformOf(b.Tip).position) < .0001f && rigA.Enabled && rigB.Enabled,
            "Moving one item's IK leaves the other item's pose and live solver unchanged");
        Interpolable aPosition = Track(rigA.Controls[0], "guideObjectPos", rigA.Controls[0].guideObject);
        Interpolable bPosition = Track(rigB.Controls[0], "guideObjectPos", rigB.Controls[0].guideObject);
        Check(aPosition.keyframes.ContainsKey(.6f) && bPosition.keyframes.Count == 1,
            "Automatic keying records only the manipulated item's channel");
        Frame aKey = aPosition.keyframes[.6f];
        Plugin.Select(rigB.Controls[0]);
        yield return null; yield return null;
        Vector3 bHome = TransformOf(rigB.Controls[0]).position;
        Plugin.Place(rigB.Controls[0], Vector3.Lerp(bHome, TransformOf(b.Root).position, .08f), TransformOf(rigB.Controls[0]).rotation);
        plugin.Key(second, false);
        Check(ReferenceEquals(aKey, aPosition.keyframes[.6f]) && bPosition.keyframes.ContainsKey(.6f),
            "Keying the second item keeps the first item's existing key object");
        Seek(0); yield return null; yield return new WaitForEndOfFrame();
        Vector3 aZero = TransformOf(a.Tip).position, bZero = TransformOf(b.Tip).position;
        Seek(.6f); yield return null; yield return new WaitForEndOfFrame();
        Check(Vector3.Distance(aZero, TransformOf(a.Tip).position) > .001f && Vector3.Distance(bZero, TransformOf(b.Tip).position) > .001f &&
            Vector3.Distance(TransformOf(a.Tip).position, TransformOf(b.Tip).position) > 1,
            "Scrubbing animates both independent IK items while only one is active");
        Plugin.Select(first);
        typeof(TreeNodeCtrl).GetMethod("AddSelectNode", Flags).Invoke(StudioApp.treeNodeCtrl, new object[] { second.treeNodeObject, true });
        yield return null; yield return null;
        Check(plugin.Item == second, "The last clicked item is active during native multiple selection");
        Check(!plugin.CanUseMarker(rigA.Controls[0], Vector2.zero), "An inactive item's stale marker cannot capture a click");
        OCIFolder unrelated = AddObjectFolder.Add();
        Plugin.Select(unrelated);
        yield return null; yield return null;
        Check(plugin.Item == null && rigA.Enabled && rigB.Enabled,
            "Selecting an unrelated object clears the panel target while both items keep animating");
        Plugin.Select(first);
        yield return null; yield return null;
        plugin.SetEnabled(rigA, false);
        Check(rigB.Enabled && plugin.Item == first, "Disabling one item does not disable the other");
        plugin.SetEnabled(rigA, true);
        Save("v1-multiple-items.png");
    }

    void VerifyKeyReduction()
    {
        const int count = 121;
        float[] times = new float[count];
        object[,] samples = new object[count, 3];
        for (int i = 0; i < count; i++)
        {
            float t = i / 30f;
            times[i] = t;
            samples[i, 0] = Quaternion.Euler(0, 70 * Mathf.Sin(t * .7f), 10 * Mathf.Sin(t));
            samples[i, 1] = new Vector3(.03f * Mathf.Sin(t), i == 60 ? .01f : 0, 0);
            samples[i, 2] = i % 2 == 0 ? Quaternion.identity : new Quaternion(0, 0, 0, -1);
        }
        int[] totals = new int[3];
        float[] angleBudgets = { .0251f, .1001f, .2501f };
        float[] positionBudgets = { .0000501f, .0002001f, .0005001f };
        for (int quality = 0; quality < 3; quality++)
        {
          int total = 0;
          float angle = 0, distance = 0;
          for (int c = 0; c < 3; c++)
          {
            bool rotation = c != 1;
            bool[] keep = BakeKeyReduction.Select(times, samples, c, rotation, (BakeQuality)quality);
            Check(keep[0] && keep[count - 1], "Reduction preserves interval boundaries on channel " + c);
            if (c == 1) Check(keep[60], "Reduction retains a one-frame position impulse");
            int left = 0, kept = 1;
            for (int right = 1; right < count; right++)
            {
                if (!keep[right]) continue;
                kept++;
                for (int i = left; i <= right; i++)
                {
                    float t = (times[i] - times[left]) / (times[right] - times[left]);
                    if (rotation) angle = Mathf.Max(angle, AccurateAngle((Quaternion)samples[i, c],
                        Quaternion.SlerpUnclamped((Quaternion)samples[left, c], (Quaternion)samples[right, c], t)));
                    else distance = Mathf.Max(distance, Vector3.Distance((Vector3)samples[i, c],
                        Vector3.LerpUnclamped((Vector3)samples[left, c], (Vector3)samples[right, c], t)));
                }
                left = right;
            }
            total += kept;
            if (c == 2) Check(kept == 2, "Identical rotations including opposite quaternion signs retain two keys");
          }
          totals[quality] = total;
          Check(total < count && angle < angleBudgets[quality] && distance < positionBudgets[quality],
              ((BakeQuality)quality) + " reduction preserves smooth motion and sharp changes; " + (count * 3) + " -> " + total +
              " keys, angle " + angle + ", position " + distance);
        }
        Check(totals[0] >= totals[1] && totals[1] >= totals[2], "The three quality settings offer progressively smaller output");
    }

    static float AccurateAngle(Quaternion a, Quaternion b)
    {
        double dot = (double)a.x*b.x + (double)a.y*b.y + (double)a.z*b.z + (double)a.w*b.w;
        double normA = (double)a.x*a.x + (double)a.y*a.y + (double)a.z*a.z + (double)a.w*a.w;
        double normB = (double)b.x*b.x + (double)b.y*b.y + (double)b.z*b.z + (double)b.w*b.w;
        return (float)(2 * Math.Acos(Math.Min(1, Math.Abs(dot) / Math.Sqrt(normA * normB))) * 180 / Math.PI);
    }

    static List<Mapping> Auto(OCIItem item)
    {
        List<Mapping> result = new List<Mapping>();
        foreach (Suggestion row in Recommendations.Find(item)) if (row.Map != null) result.Add(row.Map);
        return result;
    }

    static Mapping FindUnfamiliarChain(OCIItem item)
    {
        foreach (OCIChar.BoneInfo a in item.listBones)
            foreach (OCIChar.BoneInfo b in item.listBones)
            {
                if (!Rig.Below(b, a)) continue;
                foreach (OCIChar.BoneInfo c in item.listBones)
                {
                    if (!Rig.Below(c, b)) continue;
                    Mapping map = new Mapping { Name = "Wolflord chain", Root = a, Tip = c };
                    map.Middles.Add(b);
                    try { new Rig(item, map); return map; }
                    catch (InvalidOperationException) { }
                }
            }
        throw new InvalidOperationException("No eligible native Wolflord FK chain");
    }

    object AddConstraint(Transform source, Transform target)
    {
        Type type = null;
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            type = assembly.GetType("NodesConstraints.NodesConstraints");
            if (type != null) break;
        }
        object instance = type.GetField("_self", Flags).GetValue(null);
        MethodInfo add = type.GetMethod("AddConstraint", Flags, null,
            new[] { typeof(bool), typeof(Transform), typeof(Transform), typeof(bool), typeof(Vector3),
                typeof(bool), typeof(Quaternion), typeof(bool), typeof(Vector3), typeof(string) }, null);
        object constraint = add.Invoke(instance, new object[] { true, source, target, false, Vector3.zero,
            true, Quaternion.identity, false, Vector3.one, "IKTHIS regression" });
        if (constraint == null) throw new Exception("Failed to create real NodesConstraints link");
        return constraint;
    }

    void RemoveConstraint(object row)
    {
        Type type = row.GetType().DeclaringType;
        object instance = type.GetField("_self", Flags).GetValue(null);
        IList constraints = (IList)type.GetField("_constraints", Flags).GetValue(instance);
        type.GetMethod("RemoveConstraintAt", Flags).Invoke(instance, new object[] { constraints.IndexOf(row) });
    }

    static void Invoke(Plugin plugin, string name, params object[] args) { typeof(Plugin).GetMethod(name, Flags).Invoke(plugin, args); }

    static void ClickBone(OCIChar.BoneInfo bone)
    {
        PointerEventData click = new PointerEventData(EventSystem.current) { button = PointerEventData.InputButton.Left };
        bone.guideObject.guideSelect.OnPointerClick(click);
    }

    static float[] Lengths(Rig rig)
    {
        float[] lengths = new float[rig.Bones.Length - 1];
        for (int i = 0; i < lengths.Length; i++)
            lengths[i] = Vector3.Distance(TransformOf(rig.Bones[i]).position, TransformOf(rig.Bones[i + 1]).position);
        return lengths;
    }

    void WriteSamples(OCIItem item) { WriteSamples(item, "samples.txt"); }

    void WriteSamples(OCIItem item, string file)
    {
        using (StreamWriter writer = new StreamWriter(Path.Combine(output, file)))
            for (int frame = 0; frame <= 30; frame += 3)
            {
                float time = frame / 30f;
                Seek(time);
                foreach (Rig rig in Plugin.Instance.ForItem(item)) rig.Solve();
                writer.Write(time.ToString("R", CultureInfo.InvariantCulture));
                foreach (string name in Samples)
                {
                    Vector3 p = TransformOf(Bone(item, name)).position;
                    writer.Write(String.Format(CultureInfo.InvariantCulture, " {0:R} {1:R} {2:R}", p.x, p.y, p.z));
                }
                writer.WriteLine();
            }
    }
#endif
}

