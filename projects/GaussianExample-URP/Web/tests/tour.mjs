// A walking tour of a sample scene (living room by default) through the real page and the real
// locomotion rig: the stick is steered towards each waypoint every frame
// (view-relative, exactly as a player would push it), the discrete camera
// runs as in VR, and collision decides where the body ends up.
//
//   BASE=http://127.0.0.1:5173 node tests/tour.mjs
//
// Checks:
//   - every waypoint on the route is reached (the room is walkable end to end,
//     including the corridor)
//   - the feet stay on the floor the whole way (no climbing onto furniture)
//   - walking straight at the coffee table, the sofa and an armchair stops
//     short of them, on the floor
//   - the body never ends up outside the room

import { chromium } from 'playwright';

const BASE = process.env.BASE || 'http://127.0.0.1:5173';
const SCENE = process.argv[2] || 'living-room';
const results = [];
const check = (name, ok, detail = '') => {
  results.push(ok);
  console.log(`${ok ? 'PASS' : 'FAIL'}  ${name}${detail ? '  — ' + detail : ''}`);
};

const browser = await chromium.launch({ args: ['--use-gl=angle', '--use-angle=swiftshader', '--enable-unsafe-swiftshader'] });
const page = await browser.newPage();
const errors = [];
page.on('pageerror', (e) => errors.push(e.message));
await page.goto(`${BASE}/?scene=${SCENE}&walk&norender&test`);
console.log(`tour of ${SCENE}`);
await page.waitForFunction(() => window.ukemi?.world && window.ukemi.mode === 'walk', null, { timeout: 180000 });
const floorY = await page.evaluate(() => {
  const u = window.ukemi;
  u.pause();
  u.settings.follow = 'discrete';
  u.applySettings();
  return u.spawn.y;
});

// Walk towards (x, z) for up to `seconds`; stop within `tol`. Returns a log.
const walkTo = (x, z, seconds = 20, tol = 0.25) => page.evaluate(({ x, z, seconds, tol }) => {
  const u = window.ukemi, T = u.THREE;
  const q = new T.Quaternion();
  let minY = Infinity, maxY = -Infinity, stuckFrames = 0, last = null;
  const frames = Math.round(seconds * 60);
  for (let i = 0; i < frames; ++i) {
    const b = u.player.body;
    const dx = x - b.x, dz = z - b.z, d = Math.hypot(dx, dz);
    if (d < tol) break;
    u.camera.getWorldQuaternion(q);
    const f = new T.Vector3(0, 0, -1).applyQuaternion(q).setY(0).normalize();
    const r = new T.Vector3(-f.z, 0, f.x);
    // Ease in near the goal, but never below the 0.2 stick start threshold.
    const s = Math.max(0.3, Math.min(1, d / 0.6));
    u.sources.override = { move: { x: ((dx * r.x + dz * r.z) / d) * s, y: ((dx * f.x + dz * f.z) / d) * s }, right: { x: 0, y: 0 }, a: false, b: false };
    const prev = { x: b.x, y: b.y, z: b.z, state: u.player.state };
    u.step(1 / 60);
    minY = Math.min(minY, b.y); maxY = Math.max(maxY, b.y);
    // Invariant: the body only ever stands where the collision world allows.
    if (!window.__violation && Number.isNaN(u.world._standAt(b.x, b.z, b.y))) {
      window.__violation = { from: prev, to: { x: b.x, y: b.y, z: b.z, state: u.player.state },
        hitsTo: u.world.bodyHits(b.x, b.z, b.y), support: u.world.support(b.x, b.z, b.y) };
    }
    if (last && Math.hypot(b.x - last.x, b.z - last.z) < 1e-4) stuckFrames++; else stuckFrames = 0;
    last = { x: b.x, z: b.z };
    if (stuckFrames > 60) break;
  }
  u.sources.override = { move: { x: 0, y: 0 }, right: { x: 0, y: 0 }, a: false, b: false };
  for (let i = 0; i < 20; ++i) u.step(1 / 60);
  const b = u.player.body;
  return { x: b.x, y: b.y, z: b.z, minY, maxY, dist: Math.hypot(x - b.x, z - b.z),
    inside: u.world.insideXZ(b.x, b.z), state: u.player.state };
}, { x, z, seconds, tol });

const fmt = (p) => `(${p.x.toFixed(2)}, ${p.y.toFixed(2)}, ${p.z.toFixed(2)})`;
// Indoors the feet must stay on the one floor (no climbing onto furniture);
// outdoors the ground itself steps and slopes, so only big jumps count.
const floorTol = SCENE === 'living-room' ? 0.15 : 0.5;
const onFloor = (r) => r.minY > floorY - floorTol && r.maxY < floorY + floorTol;

// Plan on the collision grid (BFS over the same moves the player makes) and
// hand back waypoints every ~0.5 m. Planning only says where a path should
// exist; walking it with the stick is what the test checks.
await page.evaluate(() => {
  const u = window.ukemi, w = u.world, v = w.voxel;
  window.__plan = (from, to) => {
    const key = (i, k) => i * 100003 + k;
    // Start on the nearest grid cell the body could stand on.
    let start = null;
    for (let r = 0; r <= 3 && !start; ++r) for (let di = -r; di <= r && !start; ++di) for (let dk = -r; dk <= r && !start; ++dk) {
      const c = [Math.round(from.x / v) + di, Math.round(from.z / v) + dk];
      if (!Number.isNaN(w._standAt(c[0] * v, c[1] * v, from.y))) start = c;
    }
    start ||= [Math.round(from.x / v), Math.round(from.z / v)];
    const seen = new Map([[key(...start), null]]);
    const ys = new Map([[key(...start), from.y]]);
    const queue = [start];
    let best = start, bestD = Infinity;
    while (queue.length) {
      const [i, k] = queue.shift();
      const d = Math.hypot(i * v - to.x, k * v - to.z);
      if (d < bestD) { bestD = d; best = [i, k]; }
      if (d < v) break;
      for (const [di, dk] of [[1, 0], [-1, 0], [0, 1], [0, -1], [1, 1], [1, -1], [-1, 1], [-1, -1]]) {
        const ni = i + di, nk = k + dk, kk = key(ni, nk);
        if (seen.has(kk)) continue;
        const pos = { x: i * v, y: ys.get(key(i, k)), z: k * v };
        const r = w.moveAndSlide(pos, di * v, dk * v);
        if (r.hit || r.moved < Math.hypot(di, dk) * v * 0.9) continue;
        seen.set(kk, 'x');
        seen.set(kk, [i, k]);
        ys.set(kk, pos.y);
        queue.push([ni, nk]);
      }
    }
    const path = [];
    for (let c = best; c; c = seen.get(key(...c)) === 'x' ? null : seen.get(key(...c))) path.push({ x: c[0] * v, z: c[1] * v });
    path.reverse();
    // Waypoints every other cell: straight chords between sparser points cut
    // corners (around a pool edge, say) that the grid path goes round.
    return { points: path.filter((_, j) => j % 2 === 0 || j === path.length - 1), reachable: bestD < v * 1.5, end: path.at(-1) };
  };
  // Targets: the furthest reachable spot in 8 directions from the spawn.
  const s = u.spawn, targets = [];
  for (let k = 0; k < 8; ++k) {
    const a = (k / 8) * Math.PI * 2, dx = Math.sin(a), dz = Math.cos(a);
    let far = null;
    for (let t = 0.5; t < 12; t += v) {
      const x = s.x + dx * t, z = s.z + dz * t;
      if (!w.insideXZ(x, z)) break;
      const g = w.support(x, z, s.y);
      if (Number.isNaN(g) || Math.abs(g - s.y) >= 0.12 || w.blocked(x, z, g)) continue;
      // Real floor, not a sliver of ledge: standable 0.3 m around too.
      const roomy = [[0.3, 0], [-0.3, 0], [0, 0.3], [0, -0.3]].every(([ox, oz]) => {
        const g2 = w.support(x + ox, z + oz, g);
        return !Number.isNaN(g2) && !w.blocked(x + ox, z + oz, g2);
      });
      if (roomy) far = { x, z };
    }
    if (far) targets.push(far);
  }
  window.__targets = targets;
});

const follow = async (to, partial = false) => {
  const from = await page.evaluate(() => ({ ...window.ukemi.player.body }));
  const plan = await page.evaluate(({ from, to }) => window.__plan(from, to), { from, to });
  if (!plan.reachable && !partial) return { planned: false, end: plan.end };
  let r = null, minY = Infinity, maxY = -Infinity;
  for (const p of plan.points) {
    r = await walkTo(p.x, p.z, 10, 0.12);
    minY = Math.min(minY, r.minY); maxY = Math.max(maxY, r.maxY);
  }
  return { planned: true, ...r, minY, maxY, dist: Math.hypot(r.x - to.x, r.z - to.z) };
};

const targets = await page.evaluate(() => window.__targets);
let visited = 0;
for (const t of targets) {
  const r = await follow(t);
  if (!r.planned) { console.log(`      skip (${t.x.toFixed(2)}, ${t.z.toFixed(2)}): standable but not connected to the spawn`); continue; }
  visited++;
  check(`walk to the far side (${t.x.toFixed(1)}, ${t.z.toFixed(1)})`, r.dist < 0.35 && onFloor(r) && r.inside,
    `ended ${fmt(r)}, ${r.dist.toFixed(2)} m short, feet y ${r.minY.toFixed(2)}..${r.maxY.toFixed(2)}`);
}
check('the room is walkable in every direction', visited >= 6, `${visited} of ${targets.length} directions`);

// Push into things. From a few spots on the floor, find directions where
// something blocks the body within 1.5 m (not a missing floor - an actual
// obstacle), walk straight at it for 3 s and check the body stops in front.
// The obstacle is classed by how high its blocking voxels reach.
const pushes = await page.evaluate(() => {
  const u = window.ukemi, w = u.world, s = u.spawn, out = [];
  // Open floor around the spawn, in two rings, so the middle of the room is covered.
  const spots = [{ x: s.x, z: s.z }];
  for (const rad of [1.2, 2.0]) for (let k = 0; k < 12; ++k) spots.push({ x: s.x + Math.sin(k * Math.PI / 6) * rad, z: s.z + Math.cos(k * Math.PI / 6) * rad });
  for (const p of spots) {
    const g0 = w.support(p.x, p.z, s.y);
    if (Number.isNaN(g0) || w.blocked(p.x, p.z, g0)) continue;
    for (let k = 0; k < 24; ++k) {
      const a = (k / 24) * Math.PI * 2, dx = Math.sin(a), dz = Math.cos(a);
      for (let t = 0.1; t < 2.5; t += 0.05) {
        const x = p.x + dx * t, z = p.z + dz * t, g = w.support(x, z, g0);
        if (Number.isNaN(g)) break; // edge of the floor, not an obstacle
        if (!w.blocked(x, z, g)) continue;
        // Height of the obstacle: highest solid voxel in the body band here.
        // (measured one body radius ahead, where the blocking voxels are)
        let top = 0;
        for (let e = 0; e <= 0.3; e += 0.05) {
          const ox = x + dx * e, oz = z + dz * e;
          for (let y = g + 0.3; y <= g + 1.7; y += w.voxel) if (w.solidAt(ox, y, oz)) top = Math.max(top, y - g);
        }
        // Wide obstacles (blocked 0.4 m to either side too) must stop the body;
        // narrow ones may be slid around, like a capsule slides past a pole.
        const side = (o) => { const sx = x - dz * o, sz = z + dx * o, sg = w.support(sx, sz, g0); return Number.isNaN(sg) || w.blocked(sx, sz, sg); };
        if (t < 0.3 || !side(0.4) || !side(-0.4)) break;
        out.push({ from: { x: p.x, z: p.z }, dir: { x: dx, z: dz }, dist: t, kind: top < 0.6 ? 'low (table)' : top < 1.1 ? 'mid (seat, sofa, bed)' : 'tall (wall, shelf, door)' });
        break;
      }
    }
  }
  // One of each kind, nearest first, plus a few extra.
  out.sort((a, b) => a.dist - b.dist);
  const pick = [];
  for (const kind of ['low (table)', 'mid (seat, sofa, bed)', 'tall (wall, shelf, door)']) {
    pick.push(...out.filter((o) => o.kind === kind).slice(0, 2));
  }
  return pick;
});
const kinds = new Set();
for (const p of pushes) {
  // Each push starts from the spawn, so one odd corner cannot strand the rest.
  await page.evaluate(() => window.ukemi.respawn());
  await follow(p.from, true);
  await walkTo(p.from.x, p.from.z, 4, 0.03);
  const b0 = await page.evaluate(() => ({ ...window.ukemi.player.body }));
  if (Math.hypot(b0.x - p.from.x, b0.z - p.from.z) > 0.15) {
    console.log(`      skip push from (${p.from.x.toFixed(2)}, ${p.from.z.toFixed(2)}): body is at (${b0.x.toFixed(2)}, ${b0.z.toFixed(2)})`);
    continue;
  }
  const tx = b0.x + p.dir.x * (p.dist + 1.0), tz = b0.z + p.dir.z * (p.dist + 1.0);
  const r = await walkTo(tx, tz, 3, 0.05);
  // Forward progress only: sliding along the obstacle is allowed.
  const went = (r.x - b0.x) * p.dir.x + (r.z - b0.z) * p.dir.z;
  // It must walk right up to the obstacle, then stop in front of it. (A few
  // centimetres past first contact is sliding along an oblique edge; the
  // per-frame invariant below is what proves it never goes inside.)
  const ok = went > p.dist - 0.3 && went < p.dist + 0.15 && onFloor(r);
  if (ok) kinds.add(p.kind);
  check(`walking into a wide ${p.kind} obstacle stops the body`, ok,
    `obstacle ${p.dist.toFixed(2)} m ahead, body advanced ${went.toFixed(2)} m towards it, feet y ${r.minY.toFixed(2)}..${r.maxY.toFixed(2)}`);
}
check('obstacles were tested', SCENE === 'living-room' ? kinds.size === 3 : kinds.size >= 1, [...kinds].join(', '));

const violation = await page.evaluate(() => window.__violation || null);
check('the body never stands inside geometry, on any frame', !violation, JSON.stringify(violation));
check('no page errors', errors.length === 0, errors.slice(0, 2).join(' | '));
await browser.close();
const failed = results.filter((x) => !x).length;
console.log(`\n${results.length - failed}/${results.length} passed`);
process.exit(failed ? 1 : 0);
