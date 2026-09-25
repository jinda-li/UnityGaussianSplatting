// UkemiXR Splat Walk: open a Gaussian splat (.spz / .ply), walk around it
// with the Unity locomotion rig, and step into it with a WebXR headset.
//
//   rendering   three.js + Spark (World Labs' splat renderer, the one Marble
//               uses), which also handles the stereo views in WebXR
//   collision   collision/VoxelWorld.js - voxelised from the splats at load
//   locomotion  locomotion/* - ports of the Unity VRPlayerLocomotion scripts
//   VR          plain WebXR (immersive-vr, local-floor) through three.js,
//               with an in-headset menu (xr/VrMenu.js)
//
// App modes (class on <body>):
//   intro   landing page over the live scene, slow cinematic camera
//   walk    the locomotion rig, first/third person, collision
//   object  fallback for captures with no floor to stand on (a statue, a
//           product scan): orbit around it instead of walking

import * as THREE from 'three';
import { OrbitControls } from 'three/addons/controls/OrbitControls.js';
import { SparkRenderer, SplatMesh } from '@sparkjsdev/spark';
import { VoxelWorld } from './collision/VoxelWorld.js';
import { PlayerInput } from './locomotion/PlayerInput.js';
import { InputSources } from './locomotion/InputSources.js';
import { CameraRig } from './locomotion/CameraRig.js';
import { PlayerController } from './locomotion/PlayerController.js';
import { Avatar, AVATARS } from './avatar/Avatar.js';
import { XrControllers } from './xr/XrControllers.js';
import { VrMenu } from './xr/VrMenu.js';
import { SAMPLES } from './samples.js';
import { loadSettings, saveSettings } from './settings.js';
import { SITE } from './site.js';

const params = new URLSearchParams(location.search);
const $ = (id) => document.getElementById(id);
const FORMATS = ['spz', 'ply', 'splat', 'ksplat', 'sog'];

// Optional emulated headset (Meta's IWER) for testing the VR path without one.
if (params.has('xremu')) {
  const { XRDevice, metaQuest3 } = await import('iwer');
  const device = new XRDevice(metaQuest3);
  device.installRuntime({ forceInstall: true });
  window.__xrDevice = device;
}

// ---------------------------------------------------------------- renderer

const canvas = $('view');
let renderer;
try {
  renderer = new THREE.WebGLRenderer({ canvas, antialias: false, preserveDrawingBuffer: params.has('test') });
} catch (err) {
  document.body.innerHTML = '<p class="noscript">This browser does not support WebGL2, so it cannot show 3D scenes. Please use a recent Chrome, Edge or Safari.</p>';
  throw err;
}
renderer.setPixelRatio(Math.min(devicePixelRatio, 1.75));
renderer.setSize(innerWidth, innerHeight, false);
renderer.xr.enabled = true;
renderer.xr.setReferenceSpaceType('local-floor');

const scene = new THREE.Scene();
scene.background = new THREE.Color(0x0b0e13);

const spark = new SparkRenderer({ renderer });
scene.add(spark);

scene.add(new THREE.HemisphereLight(0xffffff, 0x3a3f47, 2.2));
const sun = new THREE.DirectionalLight(0xffffff, 1.6);
sun.position.set(2, 5, 3);
scene.add(sun);

// rig = Unity's XR Origin; camera = the HMD.
const rig = new THREE.Group();
rig.name = 'XR Origin';
const camera = new THREE.PerspectiveCamera(70, innerWidth / innerHeight, 0.03, 1000);
rig.add(camera);
scene.add(rig);

// Fade shell around the eyes: darkens when the head is inside a wall. Cuts
// (including the jump back into the head) are instant, with no blink.
const fade = new THREE.Mesh(
  new THREE.SphereGeometry(0.12, 16, 12),
  new THREE.MeshBasicMaterial({ color: 0x000000, side: THREE.BackSide, transparent: true, opacity: 0, depthTest: false, depthWrite: false }),
);
fade.renderOrder = 1e6;
fade.visible = false;
camera.add(fade);
let wallFade = 0;

// ---------------------------------------------------------------- state

const settings = loadSettings();
const avatar = new Avatar(AVATARS[params.get('avatar')] || AVATARS.xbot);
scene.add(avatar.root);
const avatarReady = avatar.load().catch((e) => console.warn('avatar failed to load', e));

const input = new PlayerInput();
const sources = new InputSources(canvas);
let world = null;
let splatMesh = null;
let flipped = false;
let spawn = null;
let spawnYaw = 0;
let current = null; // { id, name, url?, file? }
let userScene = null; // last file the user opened, for the VR menu
let mode = 'intro';
let pendingExplore = false;
let loadToken = 0;
let orbit = null;

const player = new PlayerController({ input, cameraRig: null, world: null, avatar });
const cameraRig = new CameraRig({
  rig,
  camera,
  input,
  avatarHead: (out) => player.avatarHead(out),
  raycast: (o, d, max) => (world ? world.raycast(o.x, o.y, o.z, d.x, d.y, d.z, max) : Infinity),
});
player.cameraRig = cameraRig;

// Desktop look: yaw/pitch of the camera inside the rig. In XR the headset
// owns the camera pose and this is ignored.
const look = { yaw: 0, pitch: -0.05 };
function applyDesktopLook() {
  camera.position.set(0, 0, 0);
  camera.rotation.set(look.pitch, look.yaw, 0, 'YXZ');
}
applyDesktopLook();

function applySettings() {
  const inXr = renderer.xr.isPresenting;
  cameraRig.followMode = settings.follow === 'auto' ? (inXr ? 'discrete' : 'smooth') : settings.follow;
  cameraRig.catchUpInterval = settings.catchUp;
  cameraRig.orbitRadius = settings.orbit;
  cameraRig.snapAngleDeg = settings.snap;
  player.moveSpeed = settings.speed;
  if (world) world.setSolidWeight(settings.solid);
  debugDirty = true;
  saveSettings(settings);
}

// ---------------------------------------------------------------- modes

function setMode(next) {
  mode = next;
  document.querySelector('.bar').classList.remove('scrolled');
  document.body.classList.remove('mode-intro', 'mode-walk', 'mode-object');
  document.body.classList.add(`mode-${next}`);
  if (next !== 'walk') $('settings').hidden = true;
  if (next === 'object') setupOrbit();
  else disposeOrbit();
  if (next === 'walk' && world) {
    respawn();
    canvas.focus({ preventScroll: true });
  }
  updateHud();
}

function explore() {
  if (!world) {
    pendingExplore = true;
    return;
  }
  setMode(world.walkable ? 'walk' : 'object');
}

function goHome() {
  if (renderer.xr.isPresenting) return;
  setMode('intro');
  $('intro').scrollTo({ top: 0 });
  attractT0 = time;
}

// ---------------------------------------------------------------- loading

async function loadSplat(entry) {
  const { url, file } = entry;
  const title = entry.name || file?.name || url.split('/').pop();
  const token = ++loadToken;
  showProgress(title, 'Downloading…', 0);
  let mesh = null;
  try {
    if (file && isLikelyMobile() && file.size > 350 * 1048576) {
      toast(`Large file (${mb(file.size)}) — a phone may run out of memory`, true);
    }
    const options = {
      onProgress: (e) => {
        if (token === loadToken && e.lengthComputable && e.total) {
          setProgress(e.loaded / e.total, `Downloading… ${mb(e.loaded)} / ${mb(e.total)}`);
        }
      },
    };
    if (file) {
      setProgress(null, 'Reading file…');
      options.fileBytes = new Uint8Array(await file.arrayBuffer());
      options.fileName = file.name;
    } else {
      options.url = url;
    }
    mesh = new SplatMesh(options);
    await mesh.initialized;
    if (token !== loadToken) { mesh.dispose?.(); return; }
    if (countSplats(mesh) === 0) throw new Error('The file contains no splats');

    setProgress(null, 'Building collision…');
    await nextFrame();
    const t0 = performance.now();
    const built = buildWorldFor(mesh, entry);
    if (token !== loadToken) { mesh.dispose?.(); return; }

    // Swap only once the new scene is fully ready: a bad file never leaves
    // you with an empty screen.
    if (splatMesh) {
      scene.remove(splatMesh);
      splatMesh.dispose?.();
    }
    splatMesh = mesh;
    scene.add(mesh);
    world = built.world;
    flipped = built.flipped;
    player.world = world;
    current = { ...entry, name: title };
    if (file) userScene = { id: 'user', name: title, file };
    spawn = built.spawn;
    spawnYaw = spawn.ok ? world.openYaw(spawn.x, spawn.y, spawn.z, 0) : 0;
    world.walkable = built.walkable;
    updateSceneInfo(Math.round(performance.now() - t0));
    hideProgress();
    refreshSceneLists();
    menu.redraw();
    debugDirty = true;

    if (renderer.xr.isPresenting) {
      setMode(world.walkable ? 'walk' : 'object');
      placeXrForMode();
    } else if (mode === 'intro' && !pendingExplore) {
      attractT0 = time;
    } else {
      pendingExplore = false;
      setMode(world.walkable ? 'walk' : 'object');
    }
    if (!world.walkable) toast('This capture has no floor to walk on, so it opens as a 3D view — drag to look around, scroll to zoom');
    else if (mode !== 'intro') toast(`Now walking: ${title}`);
  } catch (err) {
    console.error(err);
    mesh?.dispose?.();
    if (token === loadToken) {
      hideProgress();
      pendingExplore = false;
      toast(`Couldn’t open this file: ${friendlyError(err)}${splatMesh ? '. The current scene is still loaded.' : ''}`, true);
    }
  }
}

function countSplats(mesh) {
  let n = 0;
  mesh.forEachSplat(() => { ++n; });
  return n;
}

// Collision for a freshly loaded mesh, and which way up it goes.
//
// Our samples (Marble / World Labs exports) are +Y up with the floor at y≈0,
// so they are trusted as is. A user's file could be either: captures straight
// out of the original 3DGS trainer are usually upside down, and an indoor
// scene upside down is still "walkable" - on its ceiling. So both ways up are
// built and the one with more stuff standing on its floor wins. A scene with
// less than MIN_WALK_AREA to walk on (a statue, one sofa) opens in object
// mode instead.
// A real room gives well over 10 m²; one sofa on its own gives ~3.
const MIN_WALK_AREA = 6;

function buildWorldFor(mesh, entry) {
  const attempt = (flip) => {
    orient(mesh, flip);
    const world = buildWorld(mesh);
    const spawn = world.findSpawnAnywhere();
    const area = spawn.ok ? world.reachableArea(spawn) : 0;
    const score = spawn.ok ? world.uprightScore(spawn) : -1;
    return { world, spawn, flipped: flip, area, score, walkable: spawn.ok && area >= MIN_WALK_AREA };
  };
  const forced = params.get('flip');
  const first = attempt(forced ? forced === '1' : false);
  if (forced || entry.upright) return first;
  const second = attempt(!first.flipped);
  const pick = (a, b) => {
    if (a.walkable !== b.walkable) return a.walkable ? a : b;
    return a.score >= b.score ? a : b;
  };
  const r = pick(first, second);
  orient(mesh, r.flipped);
  return r;
}

function orient(mesh, flip) {
  if (flip) mesh.quaternion.set(1, 0, 0, 0);
  else mesh.quaternion.identity();
  mesh.updateMatrixWorld(true);
}

function buildWorld(mesh) {
  const m = mesh.matrixWorld.clone();
  const mq = new THREE.Quaternion();
  const ms = new THREE.Vector3();
  m.decompose(new THREE.Vector3(), mq, ms);
  const sc = ms.x;
  const v = new THREE.Vector3();
  const q = new THREE.Quaternion();
  const count = countSplats(mesh);
  return VoxelWorld.build({
    count,
    forEach(cb) {
      mesh.forEachSplat((i, c, s, quat, opacity) => {
        v.copy(c).applyMatrix4(m);
        q.copy(quat).premultiply(mq);
        cb(v.x, v.y, v.z, s.x * sc, s.y * sc, s.z * sc, q.x, q.y, q.z, q.w, opacity);
      });
    },
  }, { solidWeight: settings.solid });
}

async function reflip() {
  if (!splatMesh) return;
  showProgress(current?.name || '', 'Rebuilding collision…', null);
  await nextFrame();
  flipped = !flipped;
  orient(splatMesh, flipped);
  world = buildWorld(splatMesh);
  player.world = world;
  spawn = world.findSpawnAnywhere();
  world.walkable = spawn.ok && world.reachableArea(spawn) >= MIN_WALK_AREA;
  spawnYaw = spawn.ok ? world.openYaw(spawn.x, spawn.y, spawn.z, 0) : 0;
  updateSceneInfo(world.stats.buildMs);
  hideProgress();
  setMode(world.walkable ? 'walk' : 'object');
}

function respawn() {
  if (!world || !spawn?.ok) return;
  look.yaw = 0;
  look.pitch = -0.05;
  rig.position.set(0, 0, 0);
  rig.quaternion.setFromAxisAngle(new THREE.Vector3(0, 1, 0), spawnYaw);
  if (!renderer.xr.isPresenting) applyDesktopLook();
  rig.updateMatrixWorld(true);
  player.placeAt(spawn.x, spawn.y, spawn.z, spawnYaw + Math.PI);
}

function updateSceneInfo(buildMs) {
  const st = world.stats;
  $('scene-info').textContent =
    `${current?.name}: ${st.splats.toLocaleString()} splats · ${st.voxel.toFixed(2)} m voxels · ${st.dims.join('×')} grid · ${buildMs} ms`;
}

// ---------------------------------------------------------------- object mode

function sceneBounds() {
  const b = world.stats.bounds;
  const c = new THREE.Vector3((b.min[0] + b.max[0]) / 2, (b.min[1] + b.max[1]) / 2, (b.min[2] + b.max[2]) / 2);
  const r = 0.5 * Math.hypot(b.max[0] - b.min[0], b.max[1] - b.min[1], b.max[2] - b.min[2]) - 0.75;
  return { c, r: Math.max(0.3, r) };
}

function setupOrbit() {
  disposeOrbit();
  if (!world || renderer.xr.isPresenting) return;
  avatar.setFirstPerson(true);
  const { c, r } = sceneBounds();
  rig.position.set(0, 0, 0);
  rig.quaternion.identity();
  rig.updateMatrixWorld(true);
  camera.position.set(c.x + r * 1.2, c.y + r * 0.5, c.z + r * 1.4);
  orbit = new OrbitControls(camera, canvas);
  orbit.target.copy(c);
  orbit.enableDamping = true;
  orbit.minDistance = r * 0.2;
  orbit.maxDistance = r * 6;
  orbit.update();
}

function disposeOrbit() {
  if (!orbit) return;
  orbit.dispose();
  orbit = null;
  applyDesktopLook();
}

// ---------------------------------------------------------------- XR

let xrSession = null;
let xrFrames = 0;
let xrSupported = false;
const xr = new XrControllers(renderer, rig);
const raycaster = new THREE.Raycaster();

const menu = new VrMenu({
  anisotropy: renderer.capabilities.getMaxAnisotropy(),
  items: () => {
    const scenes = SAMPLES.filter((s) => !s.dev).map((s) => ({
      id: s.id, kind: 'scene', label: s.name, sub: s.kind, thumb: s.thumb, active: current?.id === s.id,
    }));
    if (userScene) scenes.push({ id: 'user', kind: 'scene', label: userScene.name, sub: 'Your model', thumb: null, active: current?.id === 'user' });
    return [
      ...scenes,
      { id: 'close', kind: 'action', label: 'Continue', sub: 'Close menu', primary: true },
      { id: 'respawn', kind: 'action', label: 'Respawn', sub: 'Back to start' },
      { id: 'exit', kind: 'action', label: 'Exit VR', sub: 'Back to browser' },
    ];
  },
  onPick: (id) => {
    if (id === 'close') closeMenu();
    else if (id === 'respawn') { closeMenu(); respawn(); }
    else if (id === 'exit') { closeMenu(); xrSession?.end(); }
    else if (id === 'user' && userScene) { closeMenu(); if (current?.id !== 'user') loadSplat(userScene); }
    else {
      const s = SAMPLES.find((x) => x.id === id);
      closeMenu();
      if (s && current?.id !== s.id) loadSplat(s);
    }
  },
});
scene.add(menu.mesh);
xr.onSelect = (hand) => menu.select(hand);

function openMenu() {
  const head = cameraRig.hmdPosition(new THREE.Vector3());
  menu.show(head, cameraRig.hmdYaw());
  xr.setRaysVisible(true);
}
function closeMenu() {
  menu.hide();
  xr.setRaysVisible(false);
}

// A small label on the left controller for the first seconds in VR.
const hint = makeHintLabel('X / Y menu · left stick walk · right stick turn');
let hintUntil = 0;

async function initXr() {
  const label = $('vr-label');
  const button = $('vr');
  try {
    xrSupported = !!navigator.xr && (await navigator.xr.isSessionSupported('immersive-vr'));
  } catch { /* not supported */ }
  if (!xrSupported) {
    document.body.classList.add('xr-unsupported');
    label.textContent = window.isSecureContext ? 'Enter VR' : 'VR needs HTTPS';
    button.title = 'No VR headset found. Open this page in the Quest, Pico or Vision Pro browser to enter VR.';
    return;
  }
  label.textContent = 'Enter VR';
  button.disabled = false;
  button.addEventListener('click', toggleXr);
}

function vrHeroClicked() {
  if (xrSupported) {
    toggleXr();
    return;
  }
  const link = location.origin + location.pathname;
  navigator.clipboard?.writeText(link).catch(() => {});
  toast(`Open ${link} in your Quest, Pico or Vision Pro browser and tap “Enter VR” (link copied)`);
}

async function toggleXr() {
  if (xrSession) {
    await xrSession.end();
    return;
  }
  try {
    const session = await navigator.xr.requestSession('immersive-vr', {
      optionalFeatures: ['local-floor', 'bounded-floor', 'hand-tracking'],
    });
    xrSession = session;
    renderer.xr.setFramebufferScaleFactor(settings.xrScale);
    await renderer.xr.setSession(session);
    xrFrames = 0;
    $('vr-label').textContent = 'Exit VR';
    if (world) setMode(world.walkable ? 'walk' : 'object');
    else pendingExplore = true;
    applySettings();
    hintUntil = time + 12;
    session.addEventListener('end', () => {
      xrSession = null;
      closeMenu();
      $('vr-label').textContent = 'Enter VR';
      look.yaw = cameraRig.hmdYaw() - rigYaw();
      look.pitch = 0;
      applyDesktopLook();
      applySettings();
      if (world && mode === 'walk') player.enterIdle();
      if (mode === 'object') setupOrbit();
    });
  } catch (err) {
    console.error(err);
    toast(`Couldn’t enter VR: ${err?.message || err}`, true);
  }
}

// Once the headset reports a pose, put it where the mode wants it.
function placeXrForMode() {
  if (!world) return;
  if (mode === 'walk') {
    if (player.state === 'idle') player.enterIdle();
    else respawn();
  } else if (mode === 'object') {
    const { c, r } = sceneBounds();
    const d = Math.max(1.5, r * 1.6);
    rig.quaternion.identity();
    rig.position.set(c.x, c.y - 1.1, c.z + d);
    rig.updateMatrixWorld(true);
  }
}

function rigYaw() {
  return new THREE.Euler().setFromQuaternion(rig.quaternion, 'YXZ').y;
}

// ---------------------------------------------------------------- loop

const clock = new THREE.Clock();
let time = 0;
let attractT0 = 0;
let paused = false;
let renderEnabled = !params.has('norender');
let fpsFrames = 0, fpsTime = 0, fps = 0;
const NO_INPUT = { move: { x: 0, y: 0 }, right: { x: 0, y: 0 }, a: false, b: false };

function step(dt) {
  time += dt;
  const presenting = renderer.xr.isPresenting;
  let raw = sources.read(presenting ? renderer.xr.getSession() : null);

  if (presenting) {
    sources.consumeLook();
    if (++xrFrames === 3) placeXrForMode();
    if (xr.pressed('left', 4) || xr.pressed('left', 5)) (menu.open ? closeMenu() : openMenu());
    if (menu.open) {
      raw = NO_INPUT;
      for (const hand of ['right', 'left']) {
        const rc = xr.worldRay(hand, raycaster);
        if (rc) xr.showHit(hand, menu.pointer(hand, rc));
      }
    }
  } else if (mode === 'walk') {
    const d = sources.consumeLook();
    look.yaw -= d.x * 0.0035;
    look.pitch = THREE.MathUtils.clamp(look.pitch - d.y * 0.0035, -1.35, 1.35);
    applyDesktopLook();
  } else {
    sources.consumeLook();
  }

  if (mode === 'intro' && !presenting) {
    attract(time - attractT0);
    raw = NO_INPUT;
  }

  input.update(time, raw);
  if (world && mode === 'walk') {
    player.update(dt);
    cameraRig.lateUpdate(dt);
  } else if (mode === 'object') {
    orbit?.update();
  }

  // Room-scale: the body stops at walls but a real head does not. Fade out
  // while the headset is inside geometry or on the far side of it from where
  // the body stands (first person only - in third person the camera is
  // clipped against walls by the rig itself).
  let wallTarget = 0;
  if (presenting && world && mode === 'walk' && !player.isLocomoting) {
    const h = cameraRig.hmdPosition(new THREE.Vector3());
    const a = player.avatarHead(new THREE.Vector3());
    const d = h.clone().sub(a);
    const len = d.length();
    if (world.headInside(h.x, h.y, h.z) ||
        (len > 0.05 && world.raycast(a.x, a.y, a.z, d.x, d.y, d.z, len) < len)) wallTarget = 0.92;
  }
  wallFade += (wallTarget - wallFade) * Math.min(1, dt * 10);
  fade.visible = wallFade > 0.01;
  fade.material.opacity = wallFade;

  // Controller hint for the first seconds in the headset.
  const left = xr.hands.left;
  hint.visible = presenting && time < hintUntil && !!left && !menu.open;
  if (hint.visible && hint.parent !== left.grip) left.grip.add(hint);
  updateDebug();
  updateHelpCard(dt);
}

// Intro: a slow drift and look-around from the spawn, eye height.
function attract(t) {
  if (!world || !spawn?.ok) return;
  const base = new THREE.Vector3(spawn.x, spawn.y + player.eyeHeight, spawn.z);
  const fwd = new THREE.Vector3(-Math.sin(spawnYaw), 0, -Math.cos(spawnYaw));
  const right = new THREE.Vector3(-fwd.z, 0, fwd.x);
  const ease = Math.min(1, t / 3);
  base.addScaledVector(right, 0.35 * Math.sin((t * Math.PI * 2) / 46) * ease);
  base.addScaledVector(fwd, 0.25 * Math.sin((t * Math.PI * 2) / 61) * ease);
  rig.position.copy(base);
  rig.quaternion.setFromAxisAngle(new THREE.Vector3(0, 1, 0), spawnYaw + 0.3 * Math.sin((t * Math.PI * 2) / 38) * ease);
  camera.position.set(0, 0, 0);
  camera.rotation.set(-0.06, 0, 0, 'YXZ');
  avatar.setFirstPerson(true);
}

function frame() {
  const dt = Math.min(clock.getDelta(), 0.1);
  if (!paused) step(dt);
  window.ukemi?.afterFrame?.(dt);
  if (renderEnabled || renderer.xr.isPresenting) renderer.render(scene, camera);
  fpsFrames++;
  fpsTime += dt;
  if (fpsTime > 0.5) {
    fps = fpsFrames / fpsTime;
    fpsFrames = 0;
    fpsTime = 0;
    updateHud();
  }
}
renderer.setAnimationLoop(frame);

addEventListener('resize', () => {
  camera.aspect = innerWidth / innerHeight;
  camera.updateProjectionMatrix();
  renderer.setSize(innerWidth, innerHeight, false);
});

// ---------------------------------------------------------------- debug view

let debugPoints = null;
let debugDirty = true;
const debugCenter = new THREE.Vector3(1e9, 0, 0);

function clearDebug() {
  if (debugPoints) {
    scene.remove(debugPoints);
    debugPoints.geometry.dispose();
    debugPoints = null;
  }
}

function updateDebug() {
  if (!settings.debug || !world || mode !== 'walk') {
    clearDebug();
    return;
  }
  const p = player.body;
  const moved = Math.hypot(p.x - debugCenter.x, p.z - debugCenter.z) > 2.5 || Math.abs(p.y - debugCenter.y) > 0.3;
  if (!debugDirty && !moved && debugPoints) return;
  debugDirty = false;
  debugCenter.set(p.x, p.y, p.z);
  clearDebug();
  const vox = world.debugVoxels(p.x, p.y, p.z, 7);
  const n = vox.length / 4;
  const pos = new Float32Array(n * 3);
  const col = new Float32Array(n * 3);
  const walk = new THREE.Color(0x3ddc97), block = new THREE.Color(0xff7a45);
  for (let i = 0; i < n; ++i) {
    pos[i * 3] = vox[i * 4]; pos[i * 3 + 1] = vox[i * 4 + 1]; pos[i * 3 + 2] = vox[i * 4 + 2];
    (vox[i * 4 + 3] ? block : walk).toArray(col, i * 3);
  }
  const g = new THREE.BufferGeometry();
  g.setAttribute('position', new THREE.BufferAttribute(pos, 3));
  g.setAttribute('color', new THREE.BufferAttribute(col, 3));
  debugPoints = new THREE.Points(g, new THREE.PointsMaterial({
    size: world.voxel * 0.55, vertexColors: true, transparent: true, opacity: 0.85, depthWrite: false,
  }));
  debugPoints.renderOrder = 10;
  scene.add(debugPoints);
}

// ---------------------------------------------------------------- UI

// The controls card has done its job once someone has walked a few metres.
let walkedForHelp = 0;
function updateHelpCard(dt) {
  if (walkedForHelp < 0 || mode !== 'walk') return;
  walkedForHelp += player.speed * dt;
  if (walkedForHelp > 4) {
    walkedForHelp = -1;
    $('help').classList.add('collapsed');
    $('help-toggle').setAttribute('aria-expanded', 'false');
  }
}

function updateHud() {
  const s = $('hud-state');
  s.classList.toggle('third', mode === 'walk' && player.isLocomoting);
  s.classList.toggle('object', mode === 'object');
  s.textContent = mode === 'object' ? '3D view' : player.isLocomoting ? 'Walking' : 'Looking around';
  const b = player.body;
  const debugInfo = params.has('debug') ? ` · ${fps.toFixed(0)} fps · (${b.x.toFixed(1)}, ${b.y.toFixed(1)}, ${b.z.toFixed(1)})` : '';
  $('hud-info').textContent = !world ? '' : mode === 'object'
    ? `Drag to look around · scroll to zoom${debugInfo}`
    : `${current?.name ?? ''} · WASD to walk · drag to look${debugInfo}`;
}

// One progress UI for both modes: inline under the hero, a card otherwise.
let progressTitle = '';
function showProgress(title, stage, p) {
  progressTitle = title;
  const inline = mode === 'intro';
  $('intro-progress').hidden = !inline;
  $('loading').hidden = inline;
  $('loading-title').textContent = title;
  setProgress(p, stage);
}
function setProgress(p, stage) {
  for (const id of ['loading-bar', 'intro-progress-bar']) {
    const bar = $(id);
    bar.parentElement.classList.toggle('indeterminate', p === null);
    if (p !== null) bar.style.width = `${Math.round(p * 100)}%`;
  }
  if (stage) {
    $('loading-stage').textContent = stage;
    $('intro-progress-label').textContent = `${progressTitle} · ${stage}`;
  }
}
function hideProgress() {
  $('loading').hidden = true;
  $('intro-progress').hidden = true;
}

let toastTimer = 0;
function toast(msg, error = false) {
  const t = $('toast');
  t.textContent = msg;
  t.classList.toggle('error', error);
  t.hidden = false;
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => { t.hidden = true; }, error ? 7000 : 4000);
}

const mb = (n) => `${(n / 1048576).toFixed(1)} MB`;
const nextFrame = () => new Promise((r) => requestAnimationFrame(() => setTimeout(r, 0)));
const isLikelyMobile = () => matchMedia('(pointer: coarse)').matches && Math.min(screen.width, screen.height) < 820;

function friendlyError(err) {
  const m = String(err?.message || err || '');
  if (/fetch|network|Failed to load|404/i.test(m)) return 'it could not be downloaded (check the link or its CORS settings)';
  if (/memory|allocation/i.test(m)) return 'not enough memory for a file this large';
  if (/unknown|unsupported|format|header|magic|parse|invalid|missing|property|offset|bounds/i.test(m)) return 'it is incomplete or not a Gaussian splat file';
  return m || 'unknown error';
}

const ICON_ARROW = '<svg viewBox="0 0 20 20" aria-hidden="true"><path d="M5 10h10M11 6l4 4-4 4" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round"/></svg>';
const ICON_UPLOAD = '<svg viewBox="0 0 20 20" aria-hidden="true"><path d="M10 13V3.5M6.2 7.2 10 3.5l3.8 3.7M3.5 12.5v2A2 2 0 0 0 5.5 16.5h9a2 2 0 0 0 2-2v-2" fill="none" stroke="currentColor" stroke-width="1.4" stroke-linecap="round" stroke-linejoin="round"/></svg>';

function refreshSceneLists() {
  const sel = $('sample');
  const opts = SAMPLES.map((s) => `<option value="${s.id}">${s.name}</option>`);
  if (userScene) opts.push(`<option value="user">${escapeHtml(userScene.name)} (imported)</option>`);
  if (current && !['user', ...SAMPLES.map((s) => s.id)].includes(current.id)) {
    opts.push(`<option value="${escapeHtml(current.id)}">${escapeHtml(current.name)}</option>`);
  }
  sel.innerHTML = opts.join('');
  sel.value = current?.id ?? '';

  $('cards').innerHTML = SAMPLES.filter((s) => !s.dev).map((s) => `
    <button class="card" type="button" data-id="${s.id}">
      <img class="thumb" src="${s.thumb}" alt="${s.name}" loading="lazy" />
      ${current?.id === s.id ? '<span class="badge">Now showing</span>' : ''}
      <span class="go">${ICON_ARROW}</span>
      <span class="meta">
        <span class="title">${s.name}</span>
        <span class="kind">${s.kind}</span>
        <span class="sub">${s.sub}</span>
        <span class="tags">${s.tags.map((t) => `<span class="tag">${t}</span>`).join('')}</span>
      </span>
    </button>`).join('') + `
    <button class="card import" type="button" data-id="import">
      <span class="thumb">${ICON_UPLOAD}</span>
      <span class="meta">
        <span class="title">Walk your own capture</span>
        <span class="sub">Have a scan already? Open a .spz or .ply file, or drop it anywhere on this page.</span>
        <span class="tags"><span class="tag">Stays on your computer — never uploaded</span></span>
      </span>
    </button>`;
}

function escapeHtml(s) {
  return String(s).replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
}

function pickScene(id) {
  if (id === 'import') { $('file').click(); return; }
  if (id === 'user' && userScene) {
    if (current?.id === 'user') explore();
    else { pendingExplore = true; loadSplat(userScene); }
    return;
  }
  const s = SAMPLES.find((x) => x.id === id);
  if (!s) return;
  history.replaceState(null, '', `?scene=${s.id}`);
  if (current?.id === s.id && world) { explore(); return; }
  pendingExplore = true;
  loadSplat(s);
}

function setupUi() {
  refreshSceneLists();
  $('cards').addEventListener('click', (e) => {
    const card = e.target.closest('.card');
    if (card) pickScene(card.dataset.id);
  });
  $('sample').addEventListener('change', (e) => pickScene(e.target.value));
  $('explore').addEventListener('click', explore);
  $('vr-hero').addEventListener('click', vrHeroClicked);
  $('import-hero').addEventListener('click', () => $('file').click());
  $('explore-2').addEventListener('click', explore);
  // Contact buttons appear once src/site.js has somewhere to send people.
  const contact = SITE.contactUrl || (SITE.contactEmail && `mailto:${SITE.contactEmail}?subject=${encodeURIComponent('UkemiXR capture')}`);
  if (contact) $('closing-text').textContent = 'Walk the demo now, or tell us about the place you want people to visit.';
  for (const el of document.querySelectorAll('.contact-link')) {
    if (!contact) continue;
    el.href = el.id === 'contact-hero' ? '#contact' : contact;
    el.hidden = false;
  }
  const bar = document.querySelector('.bar');
  $('intro').addEventListener('scroll', () => bar.classList.toggle('scrolled', mode === 'intro' && $('intro').scrollTop > 40), { passive: true });
  $('home').addEventListener('click', (e) => { e.preventDefault(); goHome(); });

  // Open file / drag and drop.
  $('open').addEventListener('click', () => $('file').click());
  $('file').addEventListener('change', (e) => {
    const f = e.target.files[0];
    if (f) openFile(f);
    e.target.value = '';
  });
  let dragDepth = 0;
  const hasFiles = (e) => [...(e.dataTransfer?.types || [])].includes('Files');
  addEventListener('dragenter', (e) => { if (!hasFiles(e)) return; e.preventDefault(); if (++dragDepth === 1) $('drop').hidden = false; });
  addEventListener('dragleave', () => { if (--dragDepth <= 0) { dragDepth = 0; $('drop').hidden = true; } });
  addEventListener('dragover', (e) => e.preventDefault());
  addEventListener('drop', (e) => {
    e.preventDefault();
    dragDepth = 0;
    $('drop').hidden = true;
    const f = e.dataTransfer?.files?.[0];
    if (f) openFile(f);
  });

  // WASD on the landing page starts walking.
  addEventListener('keydown', (e) => {
    if (mode === 'intro' && world && /^(Key[WASD]|Arrow(Up|Down|Left|Right))$/.test(e.code) &&
        !['INPUT', 'SELECT', 'TEXTAREA'].includes(e.target.tagName)) explore();
  });

  // Settings.
  const toggle = $('settings-toggle');
  toggle.addEventListener('click', () => {
    const open = $('settings').hidden;
    $('settings').hidden = !open;
    toggle.setAttribute('aria-expanded', String(open));
  });
  const bindRange = (id, key, fmt) => {
    const el = $(`s-${id}`), out = $(`o-${id}`);
    el.value = settings[key];
    out.textContent = fmt(settings[key]);
    el.addEventListener('input', () => {
      settings[key] = +el.value;
      out.textContent = fmt(settings[key]);
      applySettings();
    });
  };
  bindRange('catchup', 'catchUp', (v) => `${v.toFixed(2)} s`);
  bindRange('orbit', 'orbit', (v) => `${v.toFixed(1)} m`);
  bindRange('snap', 'snap', (v) => `${v}°`);
  bindRange('speed', 'speed', (v) => `${v.toFixed(1)} m/s`);
  bindRange('solid', 'solid', (v) => v.toFixed(1));
  const follow = $('s-follow');
  follow.value = settings.follow;
  follow.addEventListener('change', () => { settings.follow = follow.value; applySettings(); });
  for (const key of ['debug']) {
    const el = $(`s-${key}`);
    el.checked = settings[key];
    el.addEventListener('change', () => { settings[key] = el.checked; applySettings(); });
  }
  $('s-flip').addEventListener('click', reflip);
  $('s-respawn').addEventListener('click', () => (mode === 'walk' ? respawn() : setupOrbit()));

  const help = $('help');
  $('help-toggle').addEventListener('click', () => {
    help.classList.toggle('collapsed');
    $('help-toggle').setAttribute('aria-expanded', String(!help.classList.contains('collapsed')));
  });
}

function openFile(f) {
  const ext = f.name.split('.').pop().toLowerCase();
  if (!FORMATS.includes(ext)) {
    toast(`.${ext} is not supported — use .spz, .ply, .splat, .ksplat or .sog`, true);
    return;
  }
  history.replaceState(null, '', location.pathname);
  pendingExplore = true;
  loadSplat({ id: 'user', name: f.name, file: f });
}

function makeHintLabel(text) {
  const c = document.createElement('canvas');
  c.width = 1024;
  c.height = 128;
  const g = c.getContext('2d');
  g.fillStyle = 'rgba(13,17,23,0.88)';
  g.beginPath();
  g.roundRect(0, 0, 1024, 128, 64);
  g.fill();
  g.fillStyle = '#ffffff';
  g.font = '600 48px system-ui, "PingFang SC", "Microsoft YaHei", sans-serif';
  g.textAlign = 'center';
  g.textBaseline = 'middle';
  g.fillText(text, 512, 66);
  const tex = new THREE.CanvasTexture(c);
  tex.colorSpace = THREE.SRGBColorSpace;
  tex.anisotropy = renderer.capabilities.getMaxAnisotropy();
  const m = new THREE.Mesh(new THREE.PlaneGeometry(0.24, 0.03), new THREE.MeshBasicMaterial({ map: tex, transparent: true, depthTest: false, toneMapped: false }));
  m.position.set(0, 0.06, -0.02);
  m.rotation.x = -0.6;
  m.renderOrder = 1e5;
  m.visible = false;
  return m;
}

// ---------------------------------------------------------------- test hooks

window.ukemi = {
  THREE, scene, camera, rig, renderer, input, sources, player, cameraRig, avatar, settings, menu, xr,
  get world() { return world; },
  get spawn() { return spawn; },
  get splatMesh() { return splatMesh; },
  get mode() { return mode; },
  get current() { return current; },
  get fade() { return fade.material.opacity; },
  loadSplat,
  applySettings,
  respawn,
  explore,
  goHome,
  toggleXr,
  openMenu,
  closeMenu,
  pause() { paused = true; },
  resume() { paused = false; clock.getDelta(); },
  step,
  setRender(v) { renderEnabled = v; },
  renderOnce() { renderer.render(scene, camera); },
};

// ---------------------------------------------------------------- boot

setupUi();
applySettings();
initXr();
setMode('intro');
requestAnimationFrame(() => $('boot').classList.add('gone'));
await avatarReady;
{
  const id = params.get('scene');
  const url = params.get('url');
  const sample = SAMPLES.find((s) => s.id === id) || (!url && id !== 'none' && SAMPLES[0]);
  if (params.has('walk')) pendingExplore = true;
  if (url) { pendingExplore = true; loadSplat({ id: 'url', url }); }
  else if (sample) loadSplat(sample);
}
