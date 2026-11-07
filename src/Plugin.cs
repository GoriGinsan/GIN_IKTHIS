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
    [BepInPlugin(Guid, "GIN IKTHIS", "1.0.0")]
    [BepInProcess("CharaStudio")]
    [BepInDependency(KKAPI.KoikatuAPI.GUID, BepInDependency.DependencyFlags.HardDependency)]
    [BepInDependency(TimelineAccess.Guid, BepInDependency.DependencyFlags.SoftDependency)]
    [DefaultExecutionOrder(1000000)]
    public sealed partial class Plugin : BaseUnityPlugin
    {
        public const string Guid = "local.kkworkshop.0a52b20541f5.gin-ikthis";
        internal static Plugin Instance;
        internal readonly List<Rig> Rigs = new List<Rig>();
        internal TimelineAccess Timeline;
        internal bool TimelineReady { get { return Timeline != null && Timeline.Ready; } }
        internal OCIItem Item;
        internal bool Shown;
        internal string Message = "";
        readonly DriverCheck drivers = new DriverCheck();
        readonly Dictionary<GuideObject, bool> guideColors = new Dictionary<GuideObject, bool>();
        SimpleToolbarButton toolbar;
        Texture2D icon;
        Harmony harmony;
        int generation;
        internal int SceneGeneration { get { return generation; } }
        internal bool Baking, CancelBake;
        internal bool KeepNodes = true;
        Studio.CameraControl camera;
        Studio.CameraControl.NoCtrlFunc previousGuard, guard;

        void Awake()
        {
            Instance = this;
            StudioSaveLoadApi.RegisterExtraBehaviour<SceneController>(Guid);
            harmony = new Harmony(Guid);
            harmony.Patch(typeof(GuideSelect).GetMethod("OnPointerClick"),
                new HarmonyMethod(typeof(Plugin).GetMethod("BeforeGuideClick", BindingFlags.Static | BindingFlags.NonPublic)),
                Hook("AfterGuideClick"), null, null, null);
            harmony.Patch(typeof(GuideBase).GetMethod("OnBeginDrag"), null, Hook("AfterBeginDrag"), null, null, null);
            harmony.Patch(typeof(GuideMove).GetMethod("OnEndDrag"), null, Hook("AfterEndDrag"), null, null, null);
            harmony.Patch(typeof(GuideRotation).GetMethod("OnEndDrag"), null, Hook("AfterEndDrag"), null, null, null);
            harmony.Patch(typeof(TreeNodeCtrl).GetMethod("AddSelectNode", BindingFlags.Instance | BindingFlags.NonPublic),
                null, Hook("AfterWorkspaceSelection"), null, null, null);
        }

        IEnumerator Start()
        {
            while (!KKAPI.Studio.StudioAPI.StudioLoaded) yield return null;
            Attempt(delegate { Timeline = TimelineAccess.Connect(); });
            using (Stream resource = GetType().Assembly.GetManifestResourceStream("GinIkThis.IKTHIS.png"))
            using (MemoryStream bytes = new MemoryStream())
            {
                byte[] buffer = new byte[4096];
                int read;
                while ((read = resource.Read(buffer, 0, buffer.Length)) > 0) bytes.Write(buffer, 0, read);
                Texture2D original = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                try
                {
                    if (!original.LoadImage(bytes.ToArray())) throw new IOException("IKTHIS.png");
                    icon = new Texture2D(32, 32, TextureFormat.RGBA32, false);
                    Color[] pixels = new Color[32 * 32];
                    for (int y = 0; y < 32; y++)
                        for (int x = 0; x < 32; x++)
                            pixels[y * 32 + x] = original.GetPixelBilinear((x + .5f) / 32, (y + .5f) / 32);
                    icon.SetPixels(pixels);
                    icon.Apply();
                }
                finally { Destroy(original); }
                icon.name = "GIN IKTHIS";
                icon.filterMode = FilterMode.Bilinear;
            }
            toolbar = new SimpleToolbarButton("GIN IKTHIS", null, delegate { return icon; }, this,
                delegate(PointerEventData.InputButton button) { if (button == PointerEventData.InputButton.Left) Toggle(); });
            ToolbarManager.AddLeftToolbarControl(toolbar);
            while (toolbar.ButtonObject == null) yield return null;
            Logger.LogInfo("GIN IKTHIS Option button ready.");
        }

        void Toggle()
        {
            if (Shown) { Close(); return; }
            Shown = true;
            UseSelection();
        }

        void UseSelection()
        {
            Choose(SelectedItem());
        }

        void Update()
        {
            if (!Singleton<Studio.Studio>.IsInstance()) return;
            UpdateAuthoring();
            CheckUnmatchedClick();
            Studio.CameraControl current = Singleton<Studio.Studio>.Instance.cameraCtrl;
            if (current != null && camera != current)
            {
                RestoreCamera();
                camera = current;
                previousGuard = camera.noCtrlCondition;
                guard = delegate
                {
                    Vector2 mouse = new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y);
                    return Shown && Window.Contains(mouse) ||
                        previousGuard != null && previousGuard();
                };
                camera.noCtrlCondition = guard;
            }
        }

        void RestoreCamera()
        {
            if (camera != null && camera.noCtrlCondition == guard) camera.noCtrlCondition = previousGuard;
            camera = null;
        }

        void LateUpdate()
        {
            if (StudioSaveLoadApi.LoadInProgress || StudioSaveLoadApi.ImportInProgress)
            {
                HideMarkers();
                return;
            }
            OCIItem selected = SelectedItem();
            foreach (Rig rig in Rigs)
            {
                bool show = selected == rig.Item && rig.Enabled && !Picking;
                for (int i = 0; i < rig.Controls.Count; i++)
                    Configure(rig.Controls[i], i == 0, show);
                if (!rig.Enabled) continue;
                try
                {
                    if (Time.frameCount % 15 == 0) Check(rig, rig);
                    if (rig.Map.Waist) rig.WaistPositionGuide.ForceUpdate();
                    rig.Solve();
                }
                catch (Exception ex)
                {
                    rig.ReleaseRoot(true);
                    Disable(rig);
                    rig.Error = ex.Message;
                    Note(rig.Map.Name + " · " + ex.Message);
                }
            }
            UpdateMarkers(Picking ? null : selected);
        }

        void OnDestroy()
        {
            foreach (Rig rig in Rigs) rig.ReleaseRoot(false);
            Close();
            RestoreBoneSpheres();
            DisposeMarkers();
            RestoreCamera();
            if (harmony != null) harmony.UnpatchSelf();
            if (toolbar != null) toolbar.Dispose();
            if (icon != null) Destroy(icon);
            if (Instance == this) Instance = null;
        }

        internal void Note(string message)
        {
            Message = message;
            Logger.LogInfo(message);
        }

        void Attempt(Action action)
        {
            try { action(); }
            catch (Exception ex)
            {
                TargetInvocationException invocation = ex as TargetInvocationException;
                Message = invocation != null && invocation.InnerException != null ? invocation.InnerException.Message : ex.Message;
                Logger.LogWarning(ex);
            }
        }

    }
}
