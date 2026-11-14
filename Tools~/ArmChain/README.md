# YAMO Arm Chain bridge

`Editor/Animation/MocapArmChainPipeline.cs` launches `run_max.py` in a dedicated
3ds Max 2025 batch process. MMRP and OptiTrack use the same worker; the Unity
adapter supplies the eight unambiguous raw bone names and source time offsets.

The bridge has no Python dependency beyond Max's bundled `pymxs` and Python's
standard library. `arm_math.py` implements the calibrated hand goals and the
validated near-straight elbow-plane transition. `fbx_timing.py` checks actual
binary FBX key times. `test_arm_math.py` can run with ordinary Python.

`Binaries/2025/YamoArmChain.gup` is loaded only in the dedicated worker. It writes
native Biped rotation keys and ordinary FK controller keys; no custom controller
is instantiated or serialized into delivered MAX files.

To rebuild with the matching Autodesk 3ds Max 2025 SDK and a compatible MSVC toolset:

```powershell
cmake -S Native -B <build-directory-outside-Assets> -A x64 -DMAXSDK_ROOT="C:/Program Files/Autodesk/3ds Max 2025 SDK/maxsdk"
cmake --build <build-directory-outside-Assets> --config Release
```

Copy the resulting `YamoArmChain.gup` into `Binaries/2025/`. A different Max version
also requires updating the version gate, binary lookup and validation in the
Unity adapter; copying a binary between versions is unsupported.

Each run writes `job.json`, `progress.json`, intermediate pose buffers, the
verified scene, and `result.json` in its own directory. Results carry a unique
token and source/Base/output hashes. Unity promotes only a verified matching
result and retains prior files for rollback. A failed or cancelled worker does
not publish output files.

The input FBX has a reference pose at 0f and motion beginning at 1f. The worker
never rewrites the reference key. The saved MAX playback range is 1f through the
last motion frame; FK export excludes the reference and normalizes motion to 0f.
If the first motion pose already matches the source T-pose hand goals, that key
is also retained. Every actual motion frame is validated after MAX reload.

When outputs are inside the current Unity project's Assets, the adapter also
plays the imported Humanoid on the selected Avatar and compares every recorded
arm frame with the Generic FBX. `unity-replay.json` contains body-relative hand,
upper-arm, forearm and clavicle errors. A failed check restores previous output
files and importer metadata.
