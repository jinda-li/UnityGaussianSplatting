# MR_Eiffel — bubble, poke, throw, arrive

One button, on a Quest 3, in passthrough:

1. Press → a miniature Eiffel Tower appears in a blue refractive bubble at chest
   height in front of you, drawn as a drifting cloud of its own splat points.
2. Poke the bubble with a finger (hand tracking) or a controller → it swells,
   goes glassy and bursts; the miniature drops out under gravity.
3. Catch it, or pick it up off the floor. Hold it, turn it over, look at it.
   Nothing else is in the room.
4. Throw it at the blue ring on the floor. A throw that will land inside the
   ring grows on the way, condensing out of its own particles into the solid
   tower, and touches down full-size; the world arrives around you hexagon by
   hexagon while it does.
5. Press the same button → back to the room, with a fresh bubble.

A throw that misses is left to physics. It bounces, it rolls, you go and pick it
up. A target you cannot miss is not a target.

## Building the scene

```
Tools > Eiffel MR > Build MR_Eiffel Scene
```

That creates `Assets/EiffelMR/MR_Eiffel.unity`, drops in the existing
`GardenMR/Prefabs/3DGS_MR_Shell` prefab for the rig, passthrough, AR session and
dive controller, and builds the play group under one root. Re-running
`Rebuild Play Group In Open Scene` replaces the play group and leaves the rig
alone.

## What still needs doing by hand

- **Hand tracking** is installed (`com.unity.xr.hands`) and the OpenXR
  *Hand Tracking Subsystem* feature is on for Android. What is not done is the
  rig side: the hands need poke interactors, or `TowerBubble.m_ExtraPokeTips`
  needs the index-tip joints. Without either, the bubble still pops from a
  controller-driven `XRPokeInteractor` - so it is testable, just not with a
  finger.
- **Assign the Eiffel splat** to the dive controller and re-run the builder.
  Until then the bubble holds the box placeholder, and the builder says so.
- **`ThrownTower.m_LandingSpawn`** has to point at a `SplatSpawnPoint` in the
  Eiffel splat, or the landing grows the sky and nothing else. Use the hero
  viewpoint: splat-local `(34, -116, 1.65)`.
- **The particle look needs a splat.** `TowerParticles.shader` is assigned to
  the splat renderer by the builder, so it only takes effect once there is an
  Eiffel splat to assign. Verified against the Botanical Garden splat in the
  meantime: `_TowerSolidify` 0 draws sparse drifting dots, 1 is pixel-for-pixel
  the stock splat shader.
- **`HexSkyReveal`'s material wants the sky cubemap** the splat was trained
  under, or the tiles arrive grey.
- **Opaque Texture** must be on in the URP asset, or the bubble refracts black.

## One tower, magnified

The miniature is the scene. There is no model of a tower that gets swapped for a
real one when you throw it - the thing in the bubble *is* the splat, held at
`TabletopDiveController.m_DefaultTableScale` (0.0009, which is 29 cm for a 324 m
tower) with a `GaussianCutout` hiding everything that is not the tower.

Throwing it grows that same splat along the arc; the landing hands it to
`Dive()`, which carries on growing it to 1:1 and pins the spawn point under the
player's feet; the cutout opening is what makes the rest of the park appear
around the tower that was already in their hand. Nothing is ever swapped.

That is also why `ThrownTower` lives on the dive rig rather than on a model of
its own, and why the bubble *follows* the rig instead of parenting it - the rig
belongs to the dive controller, which places it and owns its pose.

The particles are the same argument one level down. A gaussian splat already is
a cloud of points, so `TowerParticles.shader` does not add particles next to the
tower - it draws the tower's own samples as the points they are, tiny and
drifting, and lets them settle into full gaussians as `_TowerSolidify` goes to
1. Every mote that bobs inside the bubble is a mote of the world the player
lands in. That is also why it is a render shader on the splat rather than a
Unity particle system: a particle system would be a second object, which is the
one thing this design does not allow.

Point size is in screen pixels and drift is in splat-local units, both on
purpose. The same tower is 29 cm in the hand and 324 m overhead; a world-space
point size legible at one is invisible at the other, and a world-space drift
tuned for the miniature disappears at 1:1.

The box placeholder is only built when no splat is present, so the scene stays
testable before the asset is trained. The moment a splat is assigned, re-run the
builder and it wires the rig instead.

## Why it is built this way

`TabletopDiveController` already does the hard part of step 4 - anchored
logarithmic growth of the splat world, the tunnelling vignette, and the
passthrough/skybox flip. None of that is reimplemented here. What is new is the
bubble, the poke, the target, the growth of the miniature along its own arc, and
the hexagon reveal; the landing hands over to `Dive()`.

The button always returns you to a bubble, from any state. This is a demo people
hand to someone who has never worn a headset: whatever they manage to do -
throw the tower behind a sofa, end up inside the world, lose the model under a
table - one press has to put a bubble back in front of them.
