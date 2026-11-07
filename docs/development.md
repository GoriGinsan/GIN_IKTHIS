# Build and verification

Build on Windows with a .NET SDK and your own Koikatsu references:

```powershell
.\build.ps1 -GamePath 'your Koikatsu game folder' -OutputDirectory '.\bin'
python .\package.py --dll '.\bin\GIN IKTHIS.dll' --output '.\dist'
```

The build targets Unity 5.6/.NET 3.5-era managed assemblies and C# 3. It does not produce a modern .NET runtime plugin. The game directory must contain BepInEx 5, KKAPI, ExtensibleSaveFormat and the KK Timeline DLL. The installation ZIP contains only the compiled plugin; game assemblies and third-party plugins are never bundled.

The build uses deterministic compilation. Rebuilding the released source against the same references should reproduce the published DLL. The version is set in `src/Plugin.cs`, `src/AssemblyInfo.cs` and `package.py`; keep those values, the Git tag and Release title aligned.

## Isolated Studio runner

Use a disposable Studio copy, never the live game. For the default layout, put it at `tests/studio`, add the `.kk-workshop-test` marker, and build the plugin to `tests/bin`. Place KKAPI directly under its `BepInEx/plugins`, and Timeline, KKPE, NodesConstraints and ShalltyUtils under `BepInEx/plugins/StudioWorkspaceTest`. The fixtures use Genshin Creatures GIN and GIN Toon Shaders assets, supplied separately. The runner refuses an unmarked or incorrectly located game.

```powershell
.\build.ps1 -GamePath '.\tests\studio' -OutputDirectory '.\tests\bin'
python .\tests\verify.py v1-multi v1-reduction workflow main
```

`GIN_IKTHIS_TEST_ROOT` can point to another disposable test root containing `studio` and `bin`. `GIN_IKTHIS_TEST_OUTPUT` overrides the output directory. The `user-*` phases require a separately supplied linked-scene fixture in `user-bake-fixture/original.png` beside the runtime directory. `user-bake` generates the finalized scene consumed by `user-reload` and `user-independent`; `user-selected` generates the editable bake consumed by `user-node-edit`. Independent phases start Studio with IKTHIS absent.

Run `user-ui` for the physical UI check. Open IK THIS, bake the selected item, and finalize the scene. Create `ui-stop.txt` in the output directory to finish. The general `ui`, `fk-ui` and `root-ui` phases exercise viewport controls. These interactive phases require actual interaction; an automated startup alone is not a UI pass.

See the [source guide](SOURCE.md) before changing lifecycle, selection or transaction ordering.
