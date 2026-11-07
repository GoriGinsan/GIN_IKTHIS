# Changelog

## 1.0.0

- First formal Koikatsu release, based on the tested 0.2.5 preview.
- Follow the active item during automatic previews and manual assignment. Keep unfinished editor choices separately for each item, including identically named items. Native multiple selection uses the last clicked item.
- Keep live IK and Timeline key ownership independent across items; stale markers cannot capture the previous item's input.
- Automatically reduce newly baked keys. Offer Precise, Standard and Compact settings, with Standard enabled by default, and report the number of written keys.
- Preserve interval boundaries, sharp changes, unrelated tracks and protected existing keys during reduction. The dense sampling option remains available.
- Separate lifecycle, rig management, registration, editor state, bake observation, output commit, playback and reduction into named source files. Add a source map and ownership/transaction notes.
- Retain the contact-priority NodesConstraints transfer and return-to-editing workflow from 0.2.5.
