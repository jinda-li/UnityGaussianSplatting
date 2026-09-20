# Trogir splat — credits

The scene in `Trogir.unity` is a third-party capture, not ours.

- **Title:** Get lost in the alleys of historical Trogir, Croatia (XGRIDS PortalCam)
- **Author:** Paolo Tosolini — https://superspl.at/user/tosolini
- **Source:** https://superspl.at/scene/14bac5b2
- **License:** CC BY 4.0 — http://creativecommons.org/licenses/by/4.0/
- **Requirements:** the author must be credited; commercial use is allowed.

Anything shipped or published from this scene has to carry that credit.

## What is in the project

Nothing from the capture is committed. `Assets/GaussianAssets/` is gitignored, so
the splat asset and the collision mesh are rebuilt locally from the download.

The download is a `.ssog`: a zip of SOG chunks holding seven complete levels of
detail of the same scene, from 359K splats up to 23M. 23M is past the package's
8.6M ceiling (`GaussianSplatAsset.kMaxSplats`), so level 2 — 5.76M splats — is
the finest one Unity can hold, and that is what the scene uses. Levels 4 (1.44M)
and 6 (359K) are the ones to reach for on a headset.

The LOD chunks carry no spherical harmonics, so the splats are view-independent.

## Rebuilding

Needs `splat-transform` on PATH (`npm i -g @playcanvas/splat-transform`).

Unpack the `.ssog` somewhere outside the project, then, from that folder:

```bash
splat-transform 2_0/meta.json 2_1/meta.json 2_2/meta.json 2_3/meta.json 2_4/meta.json 2_5/meta.json 2_6/meta.json 2_7/meta.json 2_8/meta.json 2_9/meta.json 2_10/meta.json -N Trogir_lod2.ply
```

```bash
splat-transform -w Trogir_lod2.ply --filter-box -70,-5,-12,80,12,156 --filter-cluster 1.0,0.999,0.1 --seed-pos 14.7745,1.5232,42.5117 trogir_full.voxel.json --voxel-params 0.1,0.1 --voxel-floor-fill 1.6 -K smooth
```

The box and the seed are in the engine frame — the same numbers the online viewer
shows — and the seed is the viewer's own start camera, so the flood fill starts
somewhere that is definitely inside the alleys. `--filter-cluster` is what throws
away the sky and the floaters; without it the flood fill leaks into them.

Then point the editor at the two files and run the menu items in order:

```bash
TROGIR_SCRATCH=<that folder> unity run --execute-method Trogir.EditorTools.TrogirSceneBuilder.BuildEverything
```

or `R2B → Trogir → 1/2/3` from the menu bar.
