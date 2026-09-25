// The VR path end to end, on an emulated Meta Quest 3 (Meta's IWER runtime,
// loaded by ?xremu). Clicks "进入 VR", then drives the real WebXR gamepads:
// left stick walks, right stick snaps, release returns to first person.
//   BASE=http://127.0.0.1:5173 node tests/xr.mjs [scene]
import { chromium } from 'playwright';

const BASE = process.env.BASE || 'http://127.0.0.1:5173';
const scene = process.argv[2] || 'living-room';
const results = [];
const check = (name, ok, detail = '') => {
  results.push(ok);
  console.log(`${ok ? 'PASS' : 'FAIL'}  ${name}${detail ? '  — ' + detail : ''}`);
};

const browser = await chromium.launch({ args: ['--use-gl=angle', '--use-angle=swiftshader', '--enable-unsafe-swiftshader'] });
const page = await browser.newPage({ viewport: { width: 800, height: 500 } });
const errors = [];
page.on('pageerror', (e) => errors.push(e.message));
page.on('console', (m) => { if (m.type() === 'error') errors.push(m.text()); });
await page.goto(`${BASE}/?scene=${scene}&xremu&norender&test`);
await page.waitForFunction(() => window.ukemi?.world && window.ukemi.player.world, null, { timeout: 180000 });
// Splats off: SwiftShader cannot draw 500k splats twice per frame in real time,
// and this test is about input and camera, not pixels.
await page.evaluate(() => {
  const u = window.ukemi;
  u.splatMesh.visible = false;
  u.settings.follow = 'auto';
  u.applySettings();
  window.__rec = [];
  u.afterFrame = () => {
    const T = u.THREE, c = new T.Vector3(), h = new T.Vector3(), q = new T.Quaternion();
    u.camera.getWorldPosition(c); u.camera.getWorldQuaternion(q); u.player.avatarHead(h);
    const f = new T.Vector3(0, 0, -1).applyQuaternion(q);
    window.__rec.push({ t: performance.now(), state: u.player.state, cam: c.toArray(), head: h.toArray(),
      yaw: Math.atan2(-f.x, -f.z), body: [u.player.body.x, u.player.body.y, u.player.body.z],
      presenting: u.renderer.xr.isPresenting, follow: u.cameraRig.followMode });
  };
});

const label = await page.textContent('#vr-label');
check('VR button offered when a headset is present', label.includes('进入 VR') && !(await page.isDisabled('#vr')), label);
await page.click('#vr');
await page.waitForFunction(() => window.ukemi.renderer.xr.isPresenting, null, { timeout: 20000 });
await page.waitForTimeout(1500);
const take = () => page.evaluate(() => { const r = window.__rec; window.__rec = []; return r; });
const dist = (a, b) => Math.hypot(a[0] - b[0], a[1] - b[1], a[2] - b[2]);

let r = await take();
let last = r.at(-1);
check('entered immersive-vr', last.presenting && last.follow === 'discrete', `follow mode ${last.follow}, ${r.length} XR frames`);
check('headset is placed in the avatar head', dist(last.cam, last.head) < 0.02, `${dist(last.cam, last.head).toFixed(3)} m`);

const setStick = (hand, x, y) => page.evaluate(({ hand, x, y }) => window.__xrDevice.controllers[hand].updateAxes('thumbstick', x, y), { hand, x, y });

// Left stick forward (WebXR: -y is forward).
await setStick('left', 0, -1);
await page.waitForTimeout(2000);
r = await take();
const loco = r.filter((f) => f.state === 'locomotion');
check('left stick walks in third person', loco.length > r.length * 0.8, `${loco.length}/${r.length} frames in locomotion`);
const walked = Math.hypot(r.at(-1).body[0] - r[0].body[0], r.at(-1).body[2] - r[0].body[2]);
check('avatar moved', walked > 0.3, `${walked.toFixed(2)} m`);
// "No continuous motion": every camera move is a cut, and cuts are at least
// ~catchUpInterval apart (the only exception is the jump-back when the avatar
// walks at the lens, which this forward walk never triggers).
const cutTimes = [];
for (let i = 1; i < r.length; ++i) if (dist(r[i].cam, r[i - 1].cam) > 1e-5) cutTimes.push({ t: r[i].t, d: dist(r[i].cam, r[i - 1].cam) });
const gaps = cutTimes.slice(1).map((c, i) => (c.t - cutTimes[i].t) / 1000);
const minGap = gaps.length ? Math.min(...gaps) : Infinity;
console.log('      cuts:', cutTimes.map((c) => `${c.d.toFixed(2)}m`).join(' '), '| gaps:', gaps.map((g) => g.toFixed(2)).join(' '));
check('headset view moves only in discrete cuts, >= 0.2 s apart', cutTimes.length >= 3 && minGap >= 0.2,
  `${cutTimes.length} cuts, min gap ${minGap.toFixed(2)} s`);

await setStick('left', 0, 0);
await page.waitForTimeout(700);
r = await take();
last = r.at(-1);
check('release returns to first person in the head', last.state === 'idle' && dist(last.cam, last.head) < 0.02);

const yaw0 = last.yaw;
await setStick('right', 1, 0);
await page.waitForTimeout(400);
await setStick('right', 0, 0);
await page.waitForTimeout(400);
r = await take();
const turned = Math.atan2(Math.sin(r.at(-1).yaw - yaw0), Math.cos(r.at(-1).yaw - yaw0)) * 180 / Math.PI;
check('right stick flick = one 35° snap turn', Math.abs(turned + 35) < 1, `${turned.toFixed(1)}°`);

// IWER covers the page with its own view while presenting (a real headset has
// no page to click either), so exit through the same handler the button uses.
await page.evaluate(() => window.ukemi.toggleXr());
await page.waitForFunction(() => !window.ukemi.renderer.xr.isPresenting, null, { timeout: 10000 });
const back = await page.evaluate(() => ({ label: document.getElementById('vr-label').textContent, state: window.ukemi.player.state }));
check('exit VR returns to the desktop view', back.label.includes('进入 VR') && back.state === 'idle', back.label);
check('no page errors', errors.length === 0, errors.slice(0, 3).join(' | '));
await browser.close();
const failed = results.filter((x) => !x).length;
console.log(`\n${results.length - failed}/${results.length} passed`);
process.exit(failed ? 1 : 0);
