# Facial conversion (0.10.5)

- Convert Facial CSV to Anim: batch convert time_s CSV files to Face blendShape clips; validates timestamps and channels, preserves existing output assets.
- Convert Facial FBX to Anim (ARKit 52): extract canonical ARKit channels, select source mesh path, optionally fill missing channels with zero. Output target is Face under the Animator root; values are unchanged.
- CSV toolbar button is available on Unity 6.3+; both conversion menus support Unity 2021.3.
- Assets/Add Selected Prefab to Scene instantiates the selected prefab with Undo support.
- Screenshot capture reads the displayed Game View final render target, including post processing and camera stacking; a rendered Game View is required.
- MMRP namespace and MotionBuilder bone aliases use the shared upstream mapping fix e76e8a5.
- Avatar Snapshot changes are excluded from this release.

## 0.10.6 compatibility fix

Unity 7 lifecycle attributes are version guarded. Object IDs, assembly lookup and preview-scene cloning use a shared version adapter, and scene renderer queries support Unity 2021.3.
