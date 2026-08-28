# BlendShape Curve Remapper

Unity Editor tool for remapping selected blend-shape animation curves while preserving key times and forcing every processed key to unweighted Linear tangents.

Open it from `Tools/YAMO/Animation/BlendShape Curve Remapper`.

## Workflow

1. Assign an `AnimationClip`.
2. Choose a Mesh track. Meshes are identified by the exact `SkinnedMeshRenderer` binding path stored in the clip.
3. Select one or more `blendShape.*` properties found under that Mesh track.
4. Adjust the mapping values, then create a processed copy or overwrite an editable native `.anim` asset.

The target identity is the exact pair of Mesh binding path and property name. A property with the same name on another Mesh path is left untouched.

## Default mapping

- Input `<= 10` becomes `0`.
- Input strictly between `10` and `85` is linearly remapped from `0` to `20`.
- Input `>= 85` becomes `100`.

All thresholds and outputs are editable and persisted through `EditorPrefs`. The default output creates a new `<source>_BlendShapeRemapped.anim`. Overwriting is limited to editable native `.anim` main assets and registers a Unity Undo operation.

The mapping is not idempotent: processing an already processed clip compresses its middle values again.

## Package integration

The implementation is compiled by the package's core `YAMO.UnityTools.Editor` assembly and has no dependencies beyond `UnityEngine` and `UnityEditor`.

EditMode coverage lives in `Tests/Editor/BlendShapeCurveRemapperTests.cs` and runs through `YAMO.UnityTools.Editor.Tests`.
