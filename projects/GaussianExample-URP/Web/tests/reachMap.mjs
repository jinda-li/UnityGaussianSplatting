// Top-down map of where the player can walk from the spawn point.
// Grey = splat density, green = reachable floor, red = spawn.
// Run: node tests/reachMap.mjs <file.ply> <out.ppm>
import fs from 'fs';
import { readPly } from './plyReader.mjs';
import { VoxelWorld } from '../src/collision/VoxelWorld.js';

const [,, file, out] = process.argv;
const splats = readPly(file);
const world = VoxelWorld.build(splats);
const spawn = world.findSpawn(0, 0);
const cs = world.voxel;
const W = world.nx, H = world.nz;
const dens = new Float32Array(W * H);
splats.forEach((x, y, z, sx, sy, sz, qx, qy, qz, qw, o) => {
  if (y < spawn.y - 0.5 || y > spawn.y + 2.2) return;
  const i = world.ix(x), k = world.iz(z);
  if (i >= 0 && k >= 0 && i < W && k < H) dens[k * W + i] += o;
});
const reach = new Float32Array(W * H).fill(NaN);
const q = [[world.ix(spawn.x), world.iz(spawn.z), spawn.y]];
reach[q[0][1] * W + q[0][0]] = spawn.y;
let count = 0;
while (q.length) {
  const [i, k, y] = q.pop();
  ++count;
  for (const [di, dk] of [[1, 0], [-1, 0], [0, 1], [0, -1]]) {
    const ni = i + di, nk = k + dk;
    if (ni < 0 || nk < 0 || ni >= W || nk >= H || !Number.isNaN(reach[nk * W + ni])) continue;
    const pos = { x: world.ox + (i + 0.5) * cs, y, z: world.oz + (k + 0.5) * cs };
    const r = world.moveAndSlide(pos, di * cs, dk * cs);
    if (r.hit) continue;
    reach[nk * W + ni] = pos.y;
    q.push([ni, nk, pos.y]);
  }
}
const img = Buffer.alloc(W * H * 3);
for (let k = 0; k < H; ++k) for (let i = 0; i < W; ++i) {
  const p = k * W + i, g = Math.min(255, Math.sqrt(dens[p]) * 40);
  const o = p * 3;
  if (!Number.isNaN(reach[p])) { img[o] = g * 0.4; img[o + 1] = 90 + g * 0.6; img[o + 2] = g * 0.4; }
  else { img[o] = img[o + 1] = img[o + 2] = g; }
}
const si = world.ix(spawn.x), sk = world.iz(spawn.z);
for (let a = -2; a <= 2; ++a) for (let b = -2; b <= 2; ++b) { const o = ((sk + a) * W + si + b) * 3; img[o] = 255; img[o + 1] = 0; img[o + 2] = 0; }
fs.writeFileSync(out, Buffer.concat([Buffer.from(`P6 ${W} ${H} 255\n`), img]));
console.log(`reachable ${(count * cs * cs).toFixed(1)} m² from spawn (${spawn.x.toFixed(2)}, ${spawn.y.toFixed(2)}, ${spawn.z.toFixed(2)}); map x→right, z→down; ${W}x${H} @ ${cs.toFixed(3)} m`);
