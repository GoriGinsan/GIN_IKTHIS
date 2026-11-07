# Tested scope and limitations — 1.0.0

Verified on 2026-11-07 in a disposable Koikatsu CharaStudio copy. **All 33 separate runtime phases passed against the released DLL.** The English [result manifest](runtime-results.json) identifies the DLL and phases; [runtime results](runtime-results.txt) contain the individual checks. This does not imply compatibility with every FK item or every plugin version.

## Environment

| Component | Tested version |
| --- | --- |
| Koikatsu CharaStudio | Game display 1.0 / Unity 5.6.2f1 |
| BepInEx | 5.4.23.2 |
| KKAPI | 1.48.1 |
| ExtensibleSaveFormat / Sideloader | 21.1.4.1 |
| Timeline | 1.5.6 |
| NodesConstraints | 1.6.3 |
| KKPE | 2.21.5 |
| ShalltyUtils | 1.3.2 |
| Rope This | 1.1.3 |

This release is **KK only**. KKS and other dependency versions were not runtime-verified.

## Active item and independent editing

Two copies of the same Hilichurl FK item, with identical display names and bone names, were assigned independently. The native selection events were used to switch the panel during automatic preview and after the first/second manual clicks. Each draft restored at its own stage. Each item retained its own red/blue guides and zero keys. Moving, keying or disabling one did not change the other; Timeline scrubbing animated both while only one was active.

The last clicked item was active during native multiple selection. Selecting a helper activated its owner. Selecting an unrelated object cleared the panel target without stopping either solver. A stale inactive marker was rejected. Physical Option/workspace clicks also verified the displayed item and the three quality buttons.

Unfinished editor drafts are session-local. Confirmed assignments, guides and track bindings are saved with the scene. Duplication and import use the native remapping rather than item or bone names.

## Key reduction

Reduction checks every captured sample against linear position interpolation or spherical rotation interpolation. Constant spans retain their interval endpoints. Sharp changes, including a one-frame impulse, are retained. Equivalent quaternions with opposite signs do not produce extra keys. The reducer touches only the new output samples inside the bake transaction.

| Setting | Rotation tolerance per output channel | Position tolerance per local output channel |
| --- | --- | --- |
| Precise | 0.025 degrees | 0.00005 Studio units |
| Standard, default | 0.1 degrees | 0.0002 Studio units |
| Compact | 0.25 degrees | 0.0005 Studio units |

The bounds apply at sampled times, not to every possible intermediate engine frame. Hierarchical rotations and scale can accumulate larger world-space differences. Turn reduction off to retain all sampled keys; choose 60 fps when more temporal detail is needed.

| Actual runtime fixture | Per-frame baseline | Reduced output |
| --- | ---: | ---: |
| Two-item IK/waist animation, Standard | 2,408 | 31 (98.7% fewer) |
| Supplied linked animation, 0–9.589 seconds, Standard | 4,913 | 4,415 (10.1% fewer) |
| Same linked animation, Compact | 4,913 | 3,824 (22.2% fewer) |
| Synthetic smooth/impulse/sign-equivalence channels, Standard | 363 | 54 |

Reduction depends on the motion; rapidly changing poses retain more keys. The two-item reduced output differed from the live source by at most about 0.000794 Studio units at the comparison times. For the linked scene, comparison at 11 times measured about **0.000876 units / 0.056 degrees** with Standard and **0.002649 units / 0.341 degrees** with Compact, away from constraint-driven endpoints.

No user-wide ShalltyUtils cleanup command is invoked. Its inspected cleanup operates on all Timeline tracks, while this implementation preserves unrelated tracks and existing keys outside the explicitly chosen output interval. Existing output keys remain protected unless overwrite is selected.

## Linked bake and sharing

The supplied scene contained Geovishap Hatchling, a character and another FK item, five live IK parts and five NodesConstraints links. Baking retained all **65 independent tracks / 949 existing keys**. The two incoming red-hand links were moved to the actual hand bones, preserving the parent, offsets and other link settings; all five constraints remained. Finalization left the three original objects and no IKTHIS helpers or saved IKTHIS mappings.

A fresh Studio process with **IKTHIS absent** loaded and naturally played the finalized Standard scene through the full clip and loop boundary. The measured hand-to-attachment position residual stayed below 0.000001 Studio units; relative frame-to-frame motion stayed below 0.0000011 in the observed playback. This checks contact stability rather than forcing every world-space bone to match its original constrained IK pose.

Contact takes priority. In the diagnostic fixture, unreachable targets changed the final arm segment length by up to about **0.045 Studio units**. The UI states this tradeoff. The transfer supports incoming endpoint constraints; waist-guide input constraints are refused with a message. Arbitrary cyclic or more complex driver arrangements are not promised.

An editable item bake survived save/restart. Re-enabling its part moved links back to the red guide and restored the original endpoint local position. Re-baking transferred them again. Duplication with both the source character and target item selected produced separate links to the copied character, matching NodesConstraints 1.6.3's native duplication behavior.

Injected failure on the second transfer restored both links, output tracks and live states. Cancellation and source-key edits during observation aborted without committing output. The cursor returned to its original nonzero time.

## Other regression coverage

Actual Hilichurl, Rifthound, Geovishap Hatchling, Golden Wolflord and Koholasaurus FK hierarchies were exercised. Coverage includes automatic human/quadruped candidates, manual third/fourth click behavior, initial keys at zero, unchanged drags, opt-in auto-key, ordinary FK neck animation alongside IK, waist movement, multi-joint tails with multiple poles, unreachable goals, conflicting drivers, green root translation, KKPE position export, existing-key protection, save/restart, import and duplication.

Separate processes checked finalized native playback with IKTHIS removed, root-position export with KKPE, safe refusal when required KKPE support was missing/disabled, pose editing with Timeline absent, and adding the missing zero keys after Timeline became available. The final Option, active-item, quality selection, selected-item bake and scene finalization controls were physically clicked in Studio.

Private input scenes, character cards, game assemblies and third-party assets/plugins are not redistributed. Only the IKTHIS runtime DLL is included in the installer. Source rebuilt from the release tree produced the same DLL SHA-256:

`7a7e9280462158402a65423949608b2be73b0438cf5af87d60b50b7f12d185ec`
