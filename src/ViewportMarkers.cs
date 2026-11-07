using System.Collections.Generic;
using Studio;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace GinIkThis
{
    public sealed partial class Plugin
    {
        Canvas markerCanvas;
        Texture2D markerTexture;
        Sprite markerSprite;
        readonly Dictionary<OCIFolder, Image> markers = new Dictionary<OCIFolder, Image>();
        readonly HashSet<OCIFolder> visibleMarkers = new HashSet<OCIFolder>();

        Image CreateMarker(OCIFolder folder, Color color)
        {
            if (markerCanvas == null)
            {
                GameObject canvas = new GameObject("IKTHIS viewport guides", typeof(RectTransform),
                    typeof(Canvas), typeof(GraphicRaycaster));
                canvas.transform.SetParent(transform, false);
                markerCanvas = canvas.GetComponent<Canvas>();
                markerCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
                markerCanvas.sortingOrder = -10;
                const int size = 32;
                markerTexture = new Texture2D(size, size, TextureFormat.RGBA32, false);
                markerTexture.filterMode = FilterMode.Bilinear;
                Color32[] pixels = new Color32[size * size];
                for (int y = 0; y < size; y++)
                    for (int x = 0; x < size; x++)
                    {
                        float distance = new Vector2(x - 15.5f, y - 15.5f).magnitude;
                        byte value = distance < 12f ? (byte)255 : (byte)22;
                        pixels[y * size + x] = new Color32(value, value, value,
                            (byte)(255 * Mathf.Clamp01(15.8f - distance)));
                    }
                markerTexture.SetPixels32(pixels);
                markerTexture.Apply();
                markerSprite = Sprite.Create(markerTexture, new Rect(0, 0, size, size), new Vector2(.5f, .5f));
            }
            GameObject marker = new GameObject("IKTHIS guide", typeof(RectTransform), typeof(CanvasRenderer),
                typeof(Image), typeof(Button));
            marker.transform.SetParent(markerCanvas.transform, false);
            Image image = marker.GetComponent<Image>();
            image.sprite = markerSprite;
            image.color = color;
            image.rectTransform.anchorMin = image.rectTransform.anchorMax = Vector2.zero;
            image.rectTransform.sizeDelta = new Vector2(19, 19);
            Button button = marker.GetComponent<Button>();
            button.transition = Selectable.Transition.None;
            button.navigation = new Navigation { mode = Navigation.Mode.None };
            MarkerDrag movement = marker.AddComponent<MarkerDrag>();
            movement.Plugin = this;
            movement.Folder = folder;
            button.onClick.AddListener(delegate
            {
                if (CanUseMarker(folder, Input.mousePosition)) SelectControl(folder);
            });
            markers.Add(folder, image);
            return image;
        }

        void UpdateMarkers(OCIItem item)
        {
            Camera camera = Camera.main;
            if (!Rig.IsItem(item) || camera == null || !item.treeNodeObject.visible ||
                !Singleton<Studio.Studio>.Instance.workInfo.visibleAxis)
            {
                HideMarkers();
                return;
            }
            visibleMarkers.Clear();
            foreach (Rig rig in Rigs)
            {
                if (rig.Item != item || !rig.Enabled || !rig.Valid || rig.Group == null || !rig.Group.treeNodeObject.visible) continue;
                for (int i = 0; i < rig.Controls.Count; i++)
                {
                    OCIFolder folder = rig.Controls[i];
                    GuideObject guide = folder.guideObject;
                    guide.guideSelect.draw = false;
                    Vector3 p = camera.WorldToScreenPoint(guide.transformTarget.position);
                    if (!TwoBone.Finite(p) || p.z <= camera.nearClipPlane ||
                        p.x < 0 || p.x > Screen.width || p.y < 0 || p.y > Screen.height) continue;
                    Image image;
                    if (!markers.TryGetValue(folder, out image))
                    {
                        Color color = rig.IsRootControl(i) ? new Color(.3f, .95f, .4f) :
                            i == 0 ? new Color(1f, .22f, .24f) : new Color(.25f, .64f, 1f);
                        image = CreateMarker(folder, color);
                    }
                    image.rectTransform.anchoredPosition = new Vector2(p.x, p.y);
                    image.gameObject.SetActive(true);
                    visibleMarkers.Add(folder);
                }
            }
            foreach (KeyValuePair<OCIFolder, Image> row in markers)
                if (row.Value != null && !visibleMarkers.Contains(row.Key)) row.Value.gameObject.SetActive(false);
        }

        void HideMarkers()
        {
            foreach (Image image in markers.Values)
                if (image != null) image.gameObject.SetActive(false);
        }

        void RemoveMarker(OCIFolder folder)
        {
            Image image;
            if (folder != null && markers.TryGetValue(folder, out image))
            {
                markers.Remove(folder);
                if (image != null) Destroy(image.gameObject);
            }
        }

        void DisposeMarkers()
        {
            if (markerCanvas != null) Destroy(markerCanvas.gameObject);
            if (markerSprite != null) Destroy(markerSprite);
            if (markerTexture != null) Destroy(markerTexture);
            markers.Clear();
        }

        internal bool CanUseMarker(OCIFolder folder, Vector2 screen)
        {
            Rig rig = folder.objectItem == null ? null : ControlRig(folder.guideObject);
            return rig != null && rig.Item == SelectedItem() && rig.Enabled && !Picking && !Baking &&
                (!Shown || !Window.Contains(new Vector2(screen.x, Screen.height - screen.y)));
        }

        internal void BeginMarkerKey(MarkerDrag marker, OCIFolder folder)
        {
            drag = null;
            if (!autoKey || !TimelineReady || Timeline.Playing) return;
            GuideObject guide = folder.guideObject;
            drag = new Drag { Axis = marker, Time = Timeline.Time };
            drag.Channels.Add(new Channel(folder, guide, false));
            drag.Before.Add(guide.changeAmount.pos);
        }
    }

    internal sealed class MarkerDrag : MonoBehaviour, IBeginDragHandler, IDragHandler, IEndDragHandler
    {
        internal Plugin Plugin;
        internal OCIFolder Folder;
        GuideObject active;
        Camera view;
        float depth;
        Vector3 oldLocal, startWorld;

        public void OnBeginDrag(PointerEventData pointer)
        {
            if (pointer.button != PointerEventData.InputButton.Left || !Plugin.CanUseMarker(Folder, pointer.pressPosition)) return;
            view = Camera.main;
            if (view == null) return;
            Plugin.Select(Folder);
            GuideObjectManager.Instance.mode = 0;
            active = Folder.guideObject;
            oldLocal = active.changeAmount.pos;
            Vector3 position = active.transformTarget.position;
            depth = view.WorldToScreenPoint(position).z;
            startWorld = position - World(pointer.pressPosition);
            GuideObjectManager.Instance.operationTarget = active;
            Plugin.BeginMarkerKey(this, Folder);
        }

        public void OnDrag(PointerEventData pointer)
        {
            if (active == null || view == null) return;
            Vector3 position = startWorld + World(pointer.position);
            if (TwoBone.Finite(position)) active.SetWorldPos(position);
        }

        public void OnEndDrag(PointerEventData pointer)
        {
            if (active == null) return;
            End();
            Vector3 now = active.changeAmount.pos;
            if (now != oldLocal)
            {
                GuideCommand.EqualsInfo edit = new GuideCommand.EqualsInfo
                {
                    dicKey = active.dicKey,
                    oldValue = oldLocal,
                    newValue = now
                };
                Singleton<UndoRedoManager>.Instance.Push(new GuideCommand.MoveEqualsCommand(new[] { edit }));
            }
            active = null;
            Plugin.FinishDrag(this);
        }

        void OnDisable()
        {
            End();
            active = null;
        }

        void End()
        {
            if (active != null && Singleton<GuideObjectManager>.IsInstance() && GuideObjectManager.Instance.operationTarget == active)
                GuideObjectManager.Instance.operationTarget = null;
        }

        Vector3 World(Vector2 screen) { return view.ScreenToWorldPoint(new Vector3(screen.x, screen.y, depth)); }
    }
}
