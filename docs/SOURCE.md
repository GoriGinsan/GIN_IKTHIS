# Source guide

Start with `Plugin.cs` for the BepInEx lifecycle and the frame loop. The partial `Plugin` files share one instance, but each file owns a distinct operation. Persistent per-item rigs and transient editor drafts are separate.

| File | Responsibility |
| --- | --- |
| `Plugin.cs` | Startup, Option button, Harmony hooks, frame updates and shutdown |
| `ItemEditorState.cs` | Per-item unfinished UI choices and last-clicked workspace target |
| `Interface.cs` | Settings, assignment preview and bake controls |
| `Authoring.cs` | Selection ownership, native FK visibility and drag auto-key |
| `ViewportMarkers.cs` | Clickable red/blue/green viewport markers |
| `RigManagement.cs` | Item-scoped lookup, overlap checks, enable/remove and root controls |
| `RigRegistration.cs` | Transactional guide creation, initial keys and mapping replacement |
| `GuideObjects.cs`, `GuideGroups.cs` | Native guide setup and collapsed per-item helper hierarchy |
| `Rig.cs`, `TwoBone.cs` | Chain validation and pose solving |
| `Recommendations.cs` | Name/hierarchy-based assignment candidates |
| `Drivers.cs` | Conflicting drivers and reversible NodesConstraints transfers |
| `SceneController.cs` | Scene save/load, import mapping, duplication and deletion |
| `TimelineAccess.cs` | Optional Timeline boundary and typed output channels |
| `NativeTimeline.cs` | Native models, track ownership and rollback transactions |
| `TimelineCapture.cs` | Engine-frame observation and concurrent-edit detection |
| `TimelineBake.cs` | Output validation, sampling, native FK/position key commit |
| `TimelinePlayback.cs` | Native interpolation and temporary pose restoration |
| `BakeKeyReduction.cs` | Error-bounded reduction of captured output samples |

## State ownership

`Rig.Item` is the actual Studio object, not its display name. All guide and key operations use that identity. A change to item A cannot select a same-named bone or channel on item B. `ForItem` is the entry point for item-local actions. Scene-wide finalization is explicit.

`ItemEditorState` caches unfinished choices by `OCIItem` identity. Switching items releases temporary native FK click layers before restoring the next item's draft. The cache is cleared when a scene is cleared or finalized; deleted items are removed. These drafts do not become saved mappings until confirmation.

## Bake flow

1. `BeginBake` or `BeginSharing` selects and validates the affected rigs.
2. `BakeObserved` snapshots source keys and the current cursor.
3. Each sample seeks Timeline, waits for animation, expressions, constraints and IK to finish, then records the displayed transforms.
4. A source-key edit, scene change or cancellation aborts before committing output.
5. `BakeKeyReduction` selects the output samples. It does not read or edit unrelated Timeline tracks.
6. One native Timeline transaction writes the outputs and applies the constraint transfers. A failure restores both keys and links.
7. Success disables live IK and reports the written key count. Scene-wide finalization then removes authoring data.

The synchronous sampler remains for deterministic internal verification and scenes without external engine-driven inputs. The UI uses the engine-frame observer.

## Compatibility boundaries

Timeline types are isolated behind `TimelineAccess.Connect`, so normal pose editing can start without Timeline. KKPE and NodesConstraints are inspected through their actual installed APIs without redistributing their assemblies. Keep those compatibility checks and rollback paths when changing the integration.
