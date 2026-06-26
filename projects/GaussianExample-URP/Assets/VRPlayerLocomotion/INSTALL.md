# VR Player Locomotion — Installation

## 1. Import

Import `VRPlayerLocomotion` into your project.

## 2. Packages

Add to `Packages/manifest.json`:

```json
"com.unity.animation.rigging": "1.4.1",
"com.unity.inputsystem": "1.18.0",
"com.unity.render-pipelines.universal": "17.3.0",
"com.unity.xr.core-utils": "2.5.3",
"com.unity.xr.interaction.toolkit": "3.3.1",
"com.unity.xr.management": "4.5.3",
"com.unity.xr.openxr": "1.16.1",
"com.unity.xr.meta-openxr": "2.4.0"
```

For Quest builds, we recommend checking that these packages are installed in
Package Manager. Search by these names:
- **OpenXR Plugin** (`com.unity.xr.openxr`)
- **Animation Rigging** (`com.unity.animation.rigging`)
- **Meta OpenXR** / **Unity OpenXR Meta** (`com.unity.xr.meta-openxr`)

## 3. Project settings

**Input System** — `Player → Active Input Handling` → **Input System Package** or **Both**

**XR** — `XR Plug-in Management` → enable **OpenXR** on **Android** and **Standalone**

**OpenXR Android Features** — `XR Plug-in Management → OpenXR → Android`:
- Enable **Meta Quest Support**

**OpenXR** (per platform tab) → Interaction Profiles:
- Meta Quest Touch Plus Controller Profile (Quest 3)
- Oculus Touch Controller Profile (Quest 2 / fallback)
- Meta Quest Touch Pro Controller Profile (Quest Pro, optional)

If **Meta Quest Support** is not visible, switch the build target to Android, install
**Meta OpenXR / Unity OpenXR Meta**, then reopen Project Settings.

> No interaction profile = hands track but **no walking** (left stick stays zero).

## 4. Test

- Scene: `VRPlayerLocomotionScene.unity`
- Prefab: `VR Player Locomotion.prefab`
- Needs floor collider + headset (or XR Simulator)
- **Left stick** = move · **Right stick** = snap turn · **B** = dodge

Debug: in Play Mode, **VRPlayerControllerInput → Move Axis** should change when pushing the left stick.
