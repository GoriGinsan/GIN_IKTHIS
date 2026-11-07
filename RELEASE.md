Animate FK Studio items with direct IK controls, then share the finalized animation without requiring IKTHIS.

## Download

- **Koikatsu / Koikatsu Party (KK):** [GIN_IKTHIS_v1.0.0.zip](https://github.com/GoriGinsan/GIN_IKTHIS/releases/download/v1.0.0/GIN_IKTHIS_v1.0.0.zip)

Close Studio and extract the ZIP's **BepInEx** folder into the game folder containing **CharaStudio.exe**. Replace your existing **GIN IKTHIS.dll** and keep only one copy under the plugin folders. This is a **KK build**, not a Koikatsu Sunshine build.

## Changes

- Move red endpoints, blue bend guides and optional green root controls directly with Studio's native axes. Unassigned FK bones remain individually editable.
- Animate several FK items independently. The open panel follows the active item and retains each item's unfinished assignment. With native multiple selection, the last clicked item is active.
- Reduce redundant bake keys automatically, with **Precise / Standard / Compact** choices and a before/after key count. **Standard** is enabled by default. Existing unrelated keys and protected output keys are retained.
- Keep incoming **NodesConstraints** attachments when finalizing: links move from the red guides to the actual endpoint bones. Contact takes priority for unreachable targets.
- Organize the complete source into lifecycle, item editor state, rig operations, solvers and Timeline stages, with a [source guide](https://github.com/GoriGinsan/GIN_IKTHIS/blob/v1.0.0/docs/SOURCE.md).

The panel starts closed and opens from **Option → IK THIS**. Save an editing copy before finalizing the whole scene. Recipients of the finalized animation need the original assets, Timeline, NodesConstraints and KKPE where used, but **do not need IKTHIS**.

## Verification and requirements

Passed **33 isolated runtime phases**, including physical UI checks, same-named multi-item editing, save/restart and playback with IKTHIS removed. The two-item test reduced **2,408 → 31 keys**; the supplied linked scene reduced **4,913 → 4,415** with Standard or **3,824** with Compact, while preserving 65 independent tracks and 949 existing keys. Reduction depends on the motion.

See the [tested scope and limitations](https://github.com/GoriGinsan/GIN_IKTHIS/blob/v1.0.0/docs/testing.md) for the isolated runtime results, key counts and tested plugin versions.

Requires **BepInEx 5**, **KKAPI** and **ExtensibleSaveFormat**. Timeline is required for animation/bake; NodesConstraints and KKPE are needed for the scene features that use them. Dependencies are separate downloads: see [installation requirements](https://github.com/GoriGinsan/GIN_IKTHIS/blob/v1.0.0/README.md#requirements).

English documentation, complete source, tests, original button assets and build instructions are available in the [repository](https://github.com/GoriGinsan/GIN_IKTHIS/tree/v1.0.0). The installation ZIP contains only `BepInEx/plugins/GINLifeHacks/GIN IKTHIS.dll`.
