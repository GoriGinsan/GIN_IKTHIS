using System;
using System.Collections;
using System.IO;
using System.Reflection;
using BepInEx;
using GinIkThis;
using Studio;
using UnityEngine;

[BepInPlugin("local.kkworkshop.0a52b20541f5.gin-ikthis.verification", "GIN IKTHIS Verification", "0.1.0")]
[BepInProcess("CharaStudio")]
public sealed class OfflineTest : BaseUnityPlugin
{
    IEnumerator Start()
    {
        string[] args = Environment.GetCommandLineArgs();
        int marker = Array.IndexOf(args, "-ginIkThisVerification");
        if (marker < 0 || !File.Exists(Path.Combine(Paths.GameRootPath, ".kk-workshop-test"))) yield break;
        string output = args[marker + 2];
        IEnumerator run = Verify(output);
        while (true)
        {
            bool next = false;
            Exception error = null;
            try { next = run.MoveNext(); }
            catch (Exception ex) { error = ex; }
            if (!next || error != null)
            {
                File.WriteAllText(Path.Combine(output, "offline.txt"), error == null ?
                    "PASS\nNo Timeline installed. Editing runs, the red guide reaches its target, and the scene saves with initial keys pending." : "FAIL\n" + error);
                Application.Quit();
                yield break;
            }
            yield return run.Current;
        }
    }

    IEnumerator Verify(string output)
    {
        while (!KKAPI.Studio.StudioAPI.StudioLoaded) yield return null;
        for (int i = 0; i < 65; i++) yield return null;
        Plugin plugin = Plugin.Instance;
        if (plugin == null || plugin.TimelineReady || BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey("com.joan6694.illusionplugins.timeline"))
            throw new Exception("Optional dependency startup failed.");
        int slot = -1;
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type type = assembly.GetType("Sideloader.AutoResolver.UniversalAutoResolver");
            if (type == null) continue;
            foreach (object entry in (IEnumerable)type.GetField("LoadedStudioResolutionInfo").GetValue(null))
                if ((int)Member(entry, "Slot") == 70000429 && (string)Member(entry, "GUID") == "local.kkworkshop.0a52b20541f5.gin-genshin-creatures")
                    slot = (int)Member(entry, "LocalSlot");
        }
        if (slot < 0) throw new Exception("Could not resolve Hilichurl.");
        OCIItem item = AddObjectItem.Add(70000424, 70000498, slot);
        Mapping map = Recommendations.Find(item).Find(delegate(Suggestion row) { return row.Map != null; }).Map;
        Rig limb = plugin.Confirm(item, new[] { map }, null)[0];
        if (!plugin.MissingInitial(limb) || !plugin.Message.Contains("初期キー未作成"))
            throw new Exception("Initial-key state was lost.");
        Transform red = limb.Controls[0].guideObject.transformTarget;
        Vector3 root = Rig.Bone(limb.Map.Root).position;
        Vector3 position = Vector3.Lerp(red.position, root, .08f) + Vector3.up * .015f;
        Plugin.Place(limb.Controls[0], position, red.rotation);
        yield return new WaitForEndOfFrame();
        if (Vector3.Distance(Rig.Bone(limb.Map.Tip).position, position) > .004f) throw new Exception("Offline tip missed target.");
        Studio.Studio app = Singleton<Studio.Studio>.Instance;
        foreach (ObjectCtrlInfo obj in app.dicObjectCtrl.Values) obj.OnSavePreprocessing();
        app.sceneInfo.cameraSaveData = app.cameraCtrl.Export();
        if (!app.sceneInfo.Save(Path.Combine(output, "offline-scene.png"))) throw new Exception("Could not save offline scene.");
    }

    static object Member(object obj, string name)
    {
        FieldInfo field = obj.GetType().GetField(name);
        return field == null ? obj.GetType().GetProperty(name).GetValue(obj, null) : field.GetValue(obj);
    }
}
