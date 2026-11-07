# GIN IKTHIS

**[Download GIN IKTHIS v1.0.0 (installation ZIP)](https://github.com/GoriGinsan/GIN_IKTHIS/releases/download/v1.0.0/GIN_IKTHIS_v1.0.0.zip)**

Animate FK Studio items with IK in **Koikatsu / Koikatsu Party CharaStudio**. Move red endpoints, blue bend guides and optional green root controls directly in the viewport, then finalize the animation to ordinary Timeline FK tracks for sharing.

**Version 1.0.0** keeps each item's editor independent, reduces redundant bake keys automatically, and separates the source by responsibility for easier maintenance.

**You do not need Git, the source code, or a GitHub account to install this plugin.** This is a **KK build**. Koikatsu Sunshine is not supported by this DLL.

[All releases](https://github.com/GoriGinsan/GIN_IKTHIS/releases) · [Build from source](docs/development.md) · [Tested features](docs/testing.md)

## Requirements

- **CharaStudio** with [BepInEx 5](https://github.com/BepInEx/BepInEx/releases).
- **KKAPI**, from [Illusion Modding API releases](https://github.com/IllusionMods/IllusionModdingAPI/releases).
- **ExtensibleSaveFormat**, from [BepisPlugins releases](https://github.com/IllusionMods/BepisPlugins/releases), for editable scene data.
- **Timeline**, from [official HSPlugins releases](https://github.com/IllusionMods/HSPlugins/releases), for animation keys and FK bake. Pose editing also works without Timeline.
- **NodesConstraints**, from the same [HSPlugins releases](https://github.com/IllusionMods/HSPlugins/releases), if your scene uses attachments.
- **KKPE**, from the same releases, when baking translation on an FK bone that has no native position channel.

Choose the **KK / Koikatsu** dependencies, not KKS, HS or HS2. Follow Timeline's upstream dependency notice, including the maintained [ShalltyUtils fork](https://github.com/Lusiocc/ShalltyUtils/releases) when required by your Timeline release. IKTHIS's bake reduction does not call ShalltyUtils's scene-wide cleanup and introduces no extra cleanup dependency.

Dependencies and item assets are separate downloads. They are not included in this package. Tested versions and limits are listed in [verification](docs/testing.md).

## Install

1. Close Studio and download the installation ZIP above.
2. Extract it and copy its **BepInEx** folder into the game folder containing **CharaStudio.exe**. Allow folders to merge.
3. Select a Studio item with FK bones. Open **Option**, then click **IK THIS** in the left-side button group.

The panel starts closed. Item selection displays its usable viewport controls without keeping the settings panel on screen. Rope This and IK THIS have independent Option buttons.

The ZIP installs just one file, keeping the path used by the preview builds:

```text
BepInEx/
  plugins/
    GINLifeHacks/
      GIN IKTHIS.dll
```

This is a **BepInEx plugin ZIP**, not a Sideloader `.zipmod`. Install it into the game folder, not `mods`.

### Updating a preview build

Replace the existing **GIN IKTHIS.dll**. Keep only one copy under `BepInEx/plugins`, including its subfolders. If you installed it elsewhere, replace it there or move that older copy outside the plugin folder before installing this ZIP. Keep your scenes and configuration. The plugin identity and editable scene format remain compatible with the 0.2.x preview builds.

## Assign bones and animate

1. Select one FK item and click **IK THIS**.
2. Use **Auto assign** to preview likely limb chains, or **Manual** to click the root, middle and tip FK bones in that order. The bone list offers the same choices. A fourth click resets the unfinished three-point choice.
3. Click **Confirm**. The visible pose is preserved; missing guide keys are added at **0:00**, without moving the playhead or replacing existing zero keys.
4. Select the item and move its red/blue dots directly. Red controls endpoint position and rotation; blue controls the bend. Use Studio's native move/rotation axes. Unassigned FK bones remain available for the neck, body and other parts.
5. Enable **Auto key** if desired, or use **Key at current time**. Auto key is off by default and records only changed channels on release.

The settings UI currently uses Japanese labels: **Auto assign** is `自動割り当て`, **Manual** is `手動で指定`, and **Confirm** is `確定`.

You can animate several items at once. Selecting another item or one of its controls switches the open panel to that item, including while a preview or manual assignment is unfinished. Each item keeps its own unfinished choices during that Studio session. With native multiple selection, the last clicked workspace item is active. Only confirmed assignments are saved in the scene.

Controls are kept in a collapsed **IK THIS** child group for each item. Native FK controls outside enabled IK chains remain visible. Details include extra blue guides for a multi-joint chain and an optional translation-only green root guide. Red/blue targets remain in world space when that root moves. Waist movement uses the item's position if its bone lacks a native translation channel.

## Timeline FK Bake and scene sharing

Open **Timeline FK Bake** in the settings panel. The range initially covers the full Timeline duration.

- **Reduce unnecessary keys** is on by default. It removes constant and interpolation-redundant samples only from the newly baked channels. Choose **Precise / Standard / Compact** (`精密 / 標準 / 軽量`); **Standard** is the default. The completion message shows input and output key counts. Turn reduction off to keep every sampled frame.
- Bake the selected item to stop only its live IK. Other items keep animating.
- Existing output keys are protected unless you explicitly select overwrite. Keys outside the chosen interval and unrelated tracks are preserved.
- Incoming red-guide **NodesConstraints** attachments transfer to the real endpoint bones by default. Their parent, offsets and other settings are retained. Contact takes priority: unreachable targets can change the final bone segment's length.
- On an intermediate, editable bake, enabling that part again returns the attachments to its red guide and restores the endpoint's original local position.

Save an **editing copy** first. Use **Finalize the whole scene for sharing** to bake all active parts and remove IKTHIS synchronization, unused helper groups and guide keys. Save the result under a new name.

**Recipients do not need IKTHIS to play the finalized animation.** They still need the scene's original assets, Timeline, NodesConstraints, KKPE where used, and other ordinary dependencies. Removing the DLL from an unfinalized editing scene does not bake its IK.

NodesConstraints 1.6.3 duplicates external attachments when both source and target objects are selected. Select both together when duplicating linked items.

## For developers

- [`src/`](src): complete plugin source, split into lifecycle, per-item editor state, rig operations, solvers and Timeline stages.
- [`assets/`](assets): the embedded Option button image.
- [`tests/`](tests): isolated Studio verification sources. Private scenes and third-party assets are not bundled.
- [`build.ps1`](build.ps1) and [`package.py`](package.py): deterministic local build and runtime-only ZIP packaging.
- [Source guide](docs/SOURCE.md), [development guide](docs/development.md), [verification](docs/testing.md) and [changes](CHANGELOG.md).

Use the **Download** link for installation. GitHub's **Code → Download ZIP** downloads development files instead. Report problems through [Issues](https://github.com/GoriGinsan/GIN_IKTHIS/issues) with plugin versions, the affected operation and relevant error text.
