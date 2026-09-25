// UkemiXR Splat Walk: open a Gaussian splat (.spz / .ply), walk around it
// with the Unity locomotion rig, and step into it with a WebXR headset.
//
//   rendering   three.js + Spark (World Labs' splat renderer, the one Marble
//               uses), which also handles the stereo views in WebXR
//   collision   collision/VoxelWorld.js - voxelised from the splats at load
//   locomotion  locomotion/* - ports of the Unity VRPlayerLocomotion scripts
//   VR          plain WebXR (immersive-vr, local-floor) through three.js

import * as THREE from 'three';
import { SparkRenderer, SplatMesh } from '@sparkjsdev/spark';
import { VoxelWorld } from './collision/VoxelWorld.js';
import { PlayerInput } from './locomotion/PlayerInput.js';
import { InputSources } from './locomotion/InputSources.js';
import { CameraRig } from './locomotion/CameraRig.js';
import { PlayerController } from './locomotion/PlayerController.js';
import { Avatar } from './avatar/Avatar.js';
import { SAMPLES } from './samples.js';
import { loadSettings, saveSettings } from './settings.js';

const params = new URLSearchParams(location.search);
const $ = (id) => document.getElementById(id);

// Optional emulated headset (Meta's IWER) for testing the VR path without one.
if (params.has('xremu')) {
  const { XRDevice, metaQuest3 } = await import('iwer');
  const device = new XRDevice(metaQuest3);
  device.installRuntime({ forceInstall: true });
  window.__xrDevice = device;
}

// ---------------------------------------------------------------- renderer

const canvas = $('view');
const renderer = new THREE.WebGLRenderer({
  canvas,
  antialias: false,
  preserveDrawingBuffer: params.has('test'),
});
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

// Blink: a black shell around the eyes, faded in and out on big cuts.
const blink = new THREE.Mesh(
  new THREE.SphereGeometry(0.12, 16, 12),
  new THREE.MeshBasicMaterial({ color: 0x000000, side: THREE.BackSide, transparent: true, opacity: 0, depthTest: false, depthWrite: false }),
);
blink.renderOrder = 1e6;
blink.visible = false;
camera.add(blink);
let blinkT = 1;

// ---------------------------------------------------------------- state

const settings = loadSettings();
const avatar = new Avatar();
scene.add(avatar.root);
const avatarReady = avatar.load('/avatar/RobotExpressive.glb').catch((e) => {
  console.warn('avatar failed to load', e);
});

const input = new PlayerInput();
const sources = new InputSources(canvas);
let world = null;
let splatMesh = null;
let flipped = false;
let spawn = null;
let currentName = '';

const player = new PlayerController({ input, cameraRig: null, world: null, avatar });
const cameraRig = new CameraRig({
  rig,
  camera,
  input,
  avatarHead: (out) => player.avatarHead(out),
  raycast: (o, d, max) => (world ? world.raycast(o.x, o.y, o.z, d.x, d.y, d.z, max) : Infinity),
});
player.cameraRig = cameraRig;
cameraRig.onTeleport = (kind, dist) => {
  if (kind === 'return' && settings.blink && dist > 0.4) blinkT = 0;
};

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

// ---------------------------------------------------------------- loading

async function loadSplat({ url, file, name }) {
  const title = name || file?.name || url.split('/').pop();
  currentName = title;
  showLoading(title, '下载中…', 0);
  try {
    if (splatMesh) {
      scene.remove(splatMesh);
      splatMesh.dispose?.();
      splatMesh = null;
    }
    world = null;
    player.world = null;
    clearDebug();

    const options = {
      onProgress: (e) => {
        if (e.lengthComputable && e.total) setProgress(e.loaded / e.total, `下载中… ${mb(e.loaded)} / ${mb(e.total)}`);
      },
    };
    if (file) {
      setProgress(null, '读取文件…');
      options.fileBytes = new Uint8Array(await file.arrayBuffer());
      options.fileName = file.name;
    } else {
      options.url = url;
    }
    const mesh = new SplatMesh(options);
    await mesh.initialized;
    splatMesh = mesh;
    flipped = !!params.get('flip');
    applyOrientation();
    scene.add(mesh);

    setProgress(null, '生成碰撞体…');
    await nextFrame();
    rebuildWorld();
    if (!params.get('flip') && !world.findSpawn(0, 0).ok) {
      // Nothing to stand on this way up: most likely a capture straight out
      // of a 3DGS trainer (Y down). Try it the other way round.
      flipped = true;
      applyOrientation();
      rebuildWorld();
      if (!world.findSpawn(0, 0).ok) {
        flipped = false;
        applyOrientation();
        rebuildWorld();
      }
    }
    respawn();
    hideLoading();
    $('hud').hidden = false;
    toast(`${title} · ${world.stats.splats.toLocaleString()} splats`);
  } catch (err) {
    console.error(err);
    hideLoading();
    toast(`打开失败：${err?.message || err}`, true);
  }
}

function applyOrientation() {
  // Marble / World Labs exports are already +Y up with the floor at y≈0.
  // Captures straight out of the original 3DGS trainer are usually upside
  // down; "上下翻转" is a 180° turn about X, the fix Spark's docs use.
  if (flipped) splatMesh.quaternion.set(1, 0, 0, 0);
  else splatMesh.quaternion.identity();
  splatMesh.updateMatrixWorld(true);
}

function rebuildWorld() {
  const m = splatMesh.matrixWorld.clone();
  const mq = new THREE.Quaternion();
  const ms = new THREE.Vector3();
  m.decompose(new THREE.Vector3(), mq, ms);
  const sc = ms.x;
  const v = new THREE.Vector3();
  const q = new THREE.Quaternion();
  let count = 0;
  splatMesh.forEachSplat(() => { ++count; });
  world = VoxelWorld.build({
    count,
    forEach(cb) {
      splatMesh.forEachSplat((i, c, s, quat, opacity) => {
        v.copy(c).applyMatrix4(m);
        q.copy(quat).premultiply(mq);
        cb(v.x, v.y, v.z, s.x * sc, s.y * sc, s.z * sc, q.x, q.y, q.z, q.w, opacity);
      });
    },
  }, { solidWeight: settings.solid });
  player.world = world;
  const st = world.stats;
  $('scene-info').textContent =
    `${currentName}：${st.splats.toLocaleString()} splats，体素 ${st.voxel.toFixed(2)} m，网格 ${st.dims.join('×')}，${st.buildMs} ms`;
  debugDirty = true;
}

function respawn() {
  spawn = world.findSpawn(0, 0);
  if (!spawn.ok) toast('没有找到可以站立的地面，试试「上下翻转」', true);
  // Face -Z: the direction the capture camera looked (Marble convention).
  look.yaw = 0;
  look.pitch = -0.05;
  rig.position.set(0, 0, 0);
  rig.quaternion.identity();
  if (!renderer.xr.isPresenting) applyDesktopLook();
  rig.updateMatrixWorld(true);
  player.placeAt(spawn.x, spawn.y, spawn.z, Math.PI);
}

// ---------------------------------------------------------------- XR

let xrSession = null;
let xrFrames = 0;

async function initXr() {
  const label = $('vr-label');
  const button = $('vr');
  let supported = false;
  try {
    supported = !!navigator.xr && (await navigator.xr.isSessionSupported('immersive-vr'));
  } catch { /* not supported */ }
  if (!supported) {
    label.textContent = window.isSecureContext ? '需要 VR 头显' : '需要 HTTPS';
    button.title = '用 Quest / Pico / Vision Pro 等设备的浏览器打开本页即可进入 VR';
    return;
  }
  label.textContent = '进入 VR';
  button.disabled = false;
  button.addEventListener('click', toggleXr);
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
    $('vr-label').textContent = '退出 VR';
    applySettings();
    session.addEventListener('end', () => {
      xrSession = null;
      $('vr-label').textContent = '进入 VR';
      // Back to the desktop camera, still standing where the headset left us.
      look.yaw = cameraRig.hmdYaw() - rigYaw();
      look.pitch = 0;
      applyDesktopLook();
      applySettings();
      if (world) player.enterIdle();
    });
  } catch (err) {
    console.error(err);
    toast(`无法进入 VR：${err?.message || err}`, true);
  }
}

function rigYaw() {
  const e = new THREE.Euler().setFromQuaternion(rig.quaternion, 'YXZ');
  return e.y;
}

// ---------------------------------------------------------------- loop

const clock = new THREE.Clock();
let time = 0;
let paused = false;
let renderEnabled = !params.has('norender');
let fpsFrames = 0, fpsTime = 0, fps = 0;

function step(dt) {
  time += dt;
  const presenting = renderer.xr.isPresenting;
  const raw = sources.read(presenting ? renderer.xr.getSession() : null);

  if (!presenting) {
    const d = sources.consumeLook();
    look.yaw -= d.x * 0.0035;
    look.pitch = THREE.MathUtils.clamp(look.pitch - d.y * 0.0035, -1.35, 1.35);
    applyDesktopLook();
  } else {
    sources.consumeLook();
    // The first XR frames have no pose yet; once the headset reports one,
    // put it in the avatar's head (Unity: HmdReadyWatcher -> SnapRigToAvatarHead).
    if (++xrFrames === 3 && world) player.enterIdle();
  }

  input.update(time, raw);
  if (world) {
    player.update(dt);
    cameraRig.lateUpdate(dt);
  }

  if (blinkT < 1) {
    blinkT = Math.min(1, blinkT + dt / 0.25);
    blink.visible = true;
    blink.material.opacity = 1 - Math.abs(blinkT * 2 - 1);
  } else {
    blink.visible = false;
  }
  updateDebug();
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
  if (!settings.debug || !world) {
    clearDebug();
    return;
  }
  const p = player.body;
  const moved = Math.hypot(p.x - debugCenter.x, p.z - debugCenter.z) > 2.5 || Math.abs(p.y - debugCenter.y) > 0.3;
  if (!debugDirty && !moved) return;
  debugDirty = false;
  debugCenter.set(p.x, p.y, p.z);
  clearDebug();
  const vox = world.debugVoxels(p.x, p.y, p.z, 7);
  const n = vox.length / 4;
  const pos = new Float32Array(n * 3);
  const col = new Float32Array(n * 3);
  const walk = new THREE.Color(0x3ddc97), block = new THREE.Color(0xff7a45);
  for (let i = 0; i < n; ++i) {
    pos.set([vox[i * 4], vox[i * 4 + 1], vox[i * 4 + 2]], i * 3);
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

function updateHud() {
  const s = $('hud-state');
  const third = player.isLocomoting;
  s.textContent = third ? '第三人称 · 行走' : '第一人称';
  s.classList.toggle('third', third);
  const b = player.body;
  $('hud-info').textContent = world
    ? `${fps.toFixed(0)} fps · (${b.x.toFixed(1)}, ${b.y.toFixed(1)}, ${b.z.toFixed(1)})${player.lastHit && third ? ' · 碰撞' : ''}`
    : '';
}

function showLoading(title, stage, p) {
  $('loading').hidden = false;
  $('loading-title').textContent = title;
  setProgress(p, stage);
}
function setProgress(p, stage) {
  const bar = $('loading-bar');
  bar.parentElement.classList.toggle('indeterminate', p === null);
  if (p !== null) bar.style.width = `${Math.round(p * 100)}%`;
  if (stage) $('loading-stage').textContent = stage;
}
function hideLoading() { $('loading').hidden = true; }

let toastTimer = 0;
function toast(msg, error = false) {
  const t = $('toast');
  t.textContent = msg;
  t.classList.toggle('error', error);
  t.hidden = false;
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => { t.hidden = true; }, error ? 6000 : 3000);
}

const mb = (n) => `${(n / 1048576).toFixed(1)} MB`;
const nextFrame = () => new Promise((r) => requestAnimationFrame(() => setTimeout(r, 0)));

function setupUi() {
  // Samples.
  const sel = $('sample');
  sel.innerHTML = '<option value="" disabled>示例场景…</option>' +
    SAMPLES.map((s) => `<option value="${s.id}">${s.name}</option>`).join('');
  sel.addEventListener('change', () => {
    const s = SAMPLES.find((x) => x.id === sel.value);
    if (!s) return;
    history.replaceState(null, '', `?scene=${s.id}`);
    loadSplat({ url: s.url, name: s.name });
  });

  // Open file / drag and drop.
  $('open').addEventListener('click', () => $('file').click());
  $('file').addEventListener('change', (e) => {
    const f = e.target.files[0];
    if (f) openFile(f);
    e.target.value = '';
  });
  let dragDepth = 0;
  addEventListener('dragenter', (e) => { e.preventDefault(); if (++dragDepth === 1) $('drop').hidden = false; });
  addEventListener('dragleave', () => { if (--dragDepth <= 0) { dragDepth = 0; $('drop').hidden = true; } });
  addEventListener('dragover', (e) => e.preventDefault());
  addEventListener('drop', (e) => {
    e.preventDefault();
    dragDepth = 0;
    $('drop').hidden = true;
    const f = e.dataTransfer.files[0];
    if (f) openFile(f);
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
  for (const [id, key] of [['blink', 'blink'], ['debug', 'debug']]) {
    const el = $(`s-${id}`);
    el.checked = settings[key];
    el.addEventListener('change', () => { settings[key] = el.checked; applySettings(); });
  }
  $('s-flip').addEventListener('click', async () => {
    if (!splatMesh) return;
    flipped = !flipped;
    applyOrientation();
    showLoading(currentName, '重新生成碰撞体…', null);
    await nextFrame();
    rebuildWorld();
    respawn();
    hideLoading();
  });
  $('s-respawn').addEventListener('click', () => world && respawn());

  const help = $('help');
  $('help-toggle').addEventListener('click', () => {
    help.classList.toggle('collapsed');
    $('help-toggle').setAttribute('aria-expanded', String(!help.classList.contains('collapsed')));
  });
  if (matchMedia('(max-width: 720px)').matches) help.classList.add('collapsed');
}

function openFile(f) {
  const ext = f.name.split('.').pop().toLowerCase();
  if (!['spz', 'ply', 'splat', 'ksplat', 'sog'].includes(ext)) {
    toast(`不支持的格式：.${ext}（支持 .spz / .ply / .splat / .ksplat / .sog）`, true);
    return;
  }
  history.replaceState(null, '', location.pathname);
  $('sample').value = '';
  loadSplat({ file: f });
}

// ---------------------------------------------------------------- test hooks

window.ukemi = {
  THREE, scene, camera, rig, renderer, input, sources, player, cameraRig, avatar, settings,
  get world() { return world; },
  get spawn() { return spawn; },
  get splatMesh() { return splatMesh; },
  loadSplat,
  applySettings,
  respawn,
  toggleXr,
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
await avatarReady;
{
  const id = params.get('scene');
  const url = params.get('url');
  const sample = SAMPLES.find((s) => s.id === id) || (!url && SAMPLES[0]);
  if (url) loadSplat({ url });
  else if (sample) {
    $('sample').value = sample.id;
    loadSplat({ url: sample.url, name: sample.name });
  }
}
