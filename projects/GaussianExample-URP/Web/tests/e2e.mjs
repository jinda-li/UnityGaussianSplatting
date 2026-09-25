// End-to-end check of the locomotion rig in a real browser page (headless
// Chromium), without a headset. Rendering is switched off (?norender) and the
// app's own step() is driven at a fixed 60 Hz, so this runs in seconds and is
// deterministic.
//
//   BASE=http://127.0.0.1:5173 node tests/e2e.mjs [scene]
//
// What it asserts, per the Unity rig's design:
//   - idle = first person: the camera sits in the avatar's head, avatar hidden
//   - stick -> locomotion = third person, avatar visible
//   - while walking the camera never moves continuously: it holds still and
//     cuts to the orbit point every catchUpInterval, and never changes yaw
//   - walking into furniture stops the body; it never leaves the scene or falls
//   - releasing the stick snaps the camera back into the head
//   - a right-stick flick turns the view by exactly snapAngle

import { chromium } from 'playwright';

const BASE = process.env.BASE || 'http://127.0.0.1:5173';
const scene = process.argv[2] || 'living-room';
const results = [];
const check = (name, ok, detail = '') => {
  results.push({ name, ok });
  console.log(`${ok ? 'PASS' : 'FAIL'}  ${name}${detail ? '  — ' + detail : ''}`);
};

const browser = await chromium.launch({ args: ['--use-gl=angle', '--use-angle=swiftshader', '--enable-unsafe-swiftshader'] });
const page = await browser.newPage();
const errors = [];
page.on('pageerror', (e) => errors.push(e.message));
page.on('console', (m) => { if (m.type() === 'error') errors.push(m.text()); });

await page.goto(`${BASE}/?scene=${scene}&norender&test`);
await page.waitForFunction(() => window.ukemi?.world && window.ukemi.player.world, null, { timeout: 180000 });

const info = await page.evaluate(() => {
  const u = window.ukemi;
  u.pause();
  u.settings.follow = 'discrete';
  u.applySettings();
  return { spawn: u.spawn, stats: u.world.stats };
});
console.log(`scene ${scene}: ${info.stats.splats} splats, voxel ${info.stats.voxel.toFixed(3)}, spawn (${info.spawn.x.toFixed(2)}, ${info.spawn.y.toFixed(2)}, ${info.spawn.z.toFixed(2)})`);
check('spawn found on the floor', info.spawn.ok);

// Runs n frames with the given raw input and returns per-frame samples.
const run = (n, raw) => page.evaluate(({ n, raw }) => {
  const u = window.ukemi, T = u.THREE;
  u.sources.override = { move: { x: 0, y: 0 }, right: { x: 0, y: 0 }, a: false, b: false, ...raw };
  const out = [];
  const cam = new T.Vector3(), head = new T.Vector3(), q = new T.Quaternion();
  for (let i = 0; i < n; ++i) {
    u.step(1 / 60);
    u.camera.getWorldPosition(cam);
    u.camera.getWorldQuaternion(q);
    u.player.avatarHead(head);
    const f = new T.Vector3(0, 0, -1).applyQuaternion(q);
    out.push({
      state: u.player.state,
      cam: cam.toArray(), head: head.toArray(),
      yaw: Math.atan2(-f.x, -f.z),
      body: [u.player.body.x, u.player.body.y, u.player.body.z],
      avatarVisible: u.avatar.root.visible,
      hit: u.player.lastHit,
    });
  }
  return out;
}, { n, raw });

const dist = (a, b) => Math.hypot(a[0] - b[0], a[1] - b[1], a[2] - b[2]);
const angDiff = (a, b) => Math.abs(Math.atan2(Math.sin(a - b), Math.cos(a - b)));

// 1. Idle.
let s = await run(5, {});
let last = s.at(-1);
check('idle is first person', last.state === 'idle' && dist(last.cam, last.head) < 1e-3 && !last.avatarVisible,
  `camera-head ${dist(last.cam, last.head).toFixed(4)} m`);

// 2. Walk forward for 3 s.
s = await run(180, { move: { x: 0, y: 1 } });
const walking = s.filter((f) => f.state === 'locomotion');
check('stick enters third person', walking.length > 170 && walking.every((f) => f.avatarVisible));
let yawDrift = 0;
const cutFrames = [];
for (let i = 1; i < s.length; ++i) {
  if (dist(s[i].cam, s[i - 1].cam) > 1e-6) cutFrames.push(i);
  yawDrift = Math.max(yawDrift, angDiff(s[i].yaw, s[0].yaw));
}
// Cuts after the first (the start nudge fires on frame 0) must be a full
// catch-up interval apart: 0.25 s = 15 frames at 60 Hz.
const gaps = cutFrames.slice(1).map((f, i) => f - cutFrames[i]);
const minGap = gaps.length ? Math.min(...gaps) : Infinity;
check('camera moves only in discrete cuts, a catch-up interval apart', cutFrames.length >= 3 && minGap >= 15,
  `${cutFrames.length} cuts in 3 s, min gap ${minGap} frames`);
check('camera never rotates on its own while walking', yawDrift < 1e-6, `max yaw drift ${yawDrift.toExponential(2)} rad`);
const moved = Math.hypot(s.at(-1).body[0] - s[0].body[0], s.at(-1).body[2] - s[0].body[2]);
check('body walks forward', moved > 0.5, `${moved.toFixed(2)} m`);
const behind = s.at(-1);
const camToHead = Math.hypot(behind.cam[0] - behind.head[0], behind.cam[2] - behind.head[2]);
check('camera trails the avatar at the orbit distance', camToHead > 0.3 && camToHead < 2.6 + 0.8,
  `${camToHead.toFixed(2)} m behind`);

// 3. Keep pushing into whatever is ahead: the body must stop and stay put.
s = await run(600, { move: { x: 0, y: 1 } });
const tail = s.slice(-60);
const creep = dist(tail[0].body, tail.at(-1).body);
check('walking into furniture/walls stops the body', creep < 0.01 && s.some((f) => f.hit),
  `moved ${creep.toFixed(4)} m in the last second, stopped at (${tail.at(-1).body.map((v) => v.toFixed(2)).join(', ')})`);

// 4. Release: back into the head.
s = await run(10, {});
last = s.at(-1);
check('releasing the stick returns to first person', last.state === 'idle' && dist(last.cam, last.head) < 1e-3 && !last.avatarVisible);

// 5. Snap turn right.
const yaw0 = last.yaw;
await run(2, { right: { x: 1, y: 0 } });
s = await run(2, {});
const turned = Math.atan2(Math.sin(s.at(-1).yaw - yaw0), Math.cos(s.at(-1).yaw - yaw0)) * 180 / Math.PI;
check('right flick snaps 35° to the right', Math.abs(turned + 35) < 0.5, `${turned.toFixed(2)}°`);
check('snap turn keeps the eye in the head', dist(s.at(-1).cam, s.at(-1).head) < 1e-3);

// 6. Walk in 8 directions for 8 s each from the spawn: never leave, never fall.
let escapes = 0;
for (let k = 0; k < 8; ++k) {
  await page.evaluate(() => { window.ukemi.respawn(); });
  const a = (k / 8) * Math.PI * 2;
  s = await run(480, { move: { x: Math.sin(a), y: Math.cos(a) } });
  const bad = s.find((f) => f.body[1] < info.spawn.y - 0.7 || !Number.isFinite(f.body[0]));
  const inside = await page.evaluate(({ x, z }) => window.ukemi.world.insideXZ(x, z), { x: s.at(-1).body[0], z: s.at(-1).body[2] });
  if (bad || !inside) escapes++;
}
check('8 walks in all directions stay inside the scene on the floor', escapes === 0, `${escapes} escaped`);

// 7. Walking backwards (towards the camera): the start nudge and the
// jump-back must keep the avatar out of the lens the whole time.
await page.evaluate(() => { window.ukemi.respawn(); });
s = await run(2, {});
const before = s.at(-1);
s = await run(120, { move: { x: 0, y: -1 } });
const nudge = dist(s[0].cam, before.cam);
const closest = Math.min(...s.map((f) => Math.hypot(f.cam[0] - f.head[0], f.cam[2] - f.head[2])));
check('backward start cuts the camera back', nudge > 0.2 && nudge <= 2.5 + 1e-3, `${nudge.toFixed(2)} m on the first frame`);
check('walking backwards never puts the avatar in the lens', closest > 0.5, `closest ${closest.toFixed(2)} m`);
await run(10, {});

check('no page errors', errors.length === 0, errors.slice(0, 3).join(' | '));

await browser.close();
const failed = results.filter((r) => !r.ok).length;
console.log(`\n${results.length - failed}/${results.length} passed`);
process.exit(failed ? 1 : 0);
