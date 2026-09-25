// Top-down diagnostic of a capture: colour = splat colour seen from above
// (floor band only), green tint = reachable from spawn, red = blocked body,
// blue = no floor, 1 m grid with coordinates. Run:
//   node tests/floorMap.mjs <file.ply> <out.png-basename> [scale]
import fs from 'fs';
import { execFileSync } from 'child_process';
import { readPly } from './plyReader.mjs';
import { VoxelWorld } from '../src/collision/VoxelWorld.js';

const [,, file, out, scaleArg] = process.argv;
const S = +(scaleArg || 6);
const splats = readPly(file);
const world = VoxelWorld.build(splats);
const spawn = world.findSpawnAnywhere();
const cs = world.voxel, W = world.nx, H = world.nz;
// Floor-level colour: brightest-weighted colour of splats within 0.5 m of the spawn floor.
const col = new Float32Array(W * H * 4);
const buf = fs.readFileSync(file); const end = buf.indexOf('end_header\n') + 11;
const f = new Float32Array(buf.buffer.slice(buf.byteOffset + end));
const n = f.length / 17;
for (let i = 0; i < n; ++i) {
  const x = f[i * 17], y = f[i * 17 + 1], z = f[i * 17 + 2];
  if (y > spawn.y + 1.0) continue;
  const ix = world.ix(x), iz = world.iz(z);
  if (ix < 0 || iz < 0 || ix >= W || iz >= H) continue;
  const o = (iz * W + ix) * 4, SH = 0.2820948;
  col[o] += 0.5 + SH * f[i * 17 + 6]; col[o + 1] += 0.5 + SH * f[i * 17 + 7]; col[o + 2] += 0.5 + SH * f[i * 17 + 8]; col[o + 3] += 1;
}
// Classify each column at floor level relative to the spawn floor.
const cls = new Uint8Array(W * H); // 0 none,1 reach,2 blocked,3 nofloor
const reach = new Set();
const q = [[world.ix(spawn.x), world.iz(spawn.z), spawn.y]];
reach.add(q[0][1] * W + q[0][0]);
while (q.length) {
  const [i, k, y] = q.pop();
  for (const [di, dk] of [[1, 0], [-1, 0], [0, 1], [0, -1]]) {
    const ni = i + di, nk = k + dk, key = nk * W + ni;
    if (ni < 0 || nk < 0 || ni >= W || nk >= H || reach.has(key)) continue;
    const pos = { x: world.ox + (i + 0.5) * cs, y, z: world.oz + (k + 0.5) * cs };
    const r = world.moveAndSlide(pos, di * cs, dk * cs);
    if (r.hit || r.moved < cs * 0.9) continue;
    reach.add(key);
    q.push([ni, nk, pos.y]);
  }
}
for (let k = 0; k < H; ++k) for (let i = 0; i < W; ++i) {
  const x = world.ox + (i + 0.5) * cs, z = world.oz + (k + 0.5) * cs;
  if (reach.has(k * W + i)) { cls[k * W + i] = 1; continue; }
  const g = world.support(x, z, spawn.y);
  cls[k * W + i] = Number.isNaN(g) ? 3 : world.blocked(x, z, g) ? 2 : 0;
}
const img = Buffer.alloc(W * S * H * S * 3);
for (let k = 0; k < H * S; ++k) for (let i = 0; i < W * S; ++i) {
  const ci = Math.floor(i / S), ck = Math.floor(k / S), c = ck * W + ci, o = (k * W * S + i) * 3;
  const cnt = col[c * 4 + 3];
  let r = cnt ? col[c * 4] / cnt : 0, g = cnt ? col[c * 4 + 1] / cnt : 0, b = cnt ? col[c * 4 + 2] / cnt : 0;
  const t = process.env.NOTINT ? 0 : cls[c];
  if (t === 1) { r = r * 0.55; g = g * 0.55 + 0.35; b = b * 0.55; }
  else if (t === 2) { r = r * 0.5 + 0.45; g *= 0.5; b *= 0.5; }
  else if (t === 3) { r *= 0.35; g *= 0.35; b = b * 0.35 + 0.3; }
  const x = world.ox + (ci + 0.5) * cs, z = world.oz + (ck + 0.5) * cs;
  const onGrid = Math.abs(x - Math.round(x)) < cs / 2 && i % S === 0 || Math.abs(z - Math.round(z)) < cs / 2 && k % S === 0;
  if (onGrid) { r = r * 0.5 + 0.5; g = g * 0.5 + 0.5; b = b * 0.5 + 0.5; }
  img[o] = Math.max(0, Math.min(255, r * 255)); img[o + 1] = Math.max(0, Math.min(255, g * 255)); img[o + 2] = Math.max(0, Math.min(255, b * 255));
}
fs.writeFileSync(`${out}.ppm`, Buffer.concat([Buffer.from(`P6 ${W * S} ${H * S} 255\n`), img]));
const labels = [];
for (let x = Math.ceil(world.ox); x < world.ox + W * cs; ++x) labels.push(['x', x, Math.round((x - world.ox) / cs * S)]);
for (let z = Math.ceil(world.oz); z < world.oz + H * cs; ++z) labels.push(['z', z, Math.round((z - world.oz) / cs * S)]);
fs.writeFileSync(`${out}.labels.json`, JSON.stringify({ labels, spawn: [Math.round((spawn.x - world.ox) / cs * S), Math.round((spawn.z - world.oz) / cs * S)] }));
execFileSync('python3', ['-c', `
from PIL import Image, ImageDraw
import json
im=Image.open('${out}.ppm'); d=ImageDraw.Draw(im); L=json.load(open('${out}.labels.json'))
for a,v,p in L['labels']:
    d.text((p+2,2) if a=='x' else (2,p+2), f"{a}{v}", fill=(255,255,255))
sx,sz=L['spawn']; d.ellipse((sx-5,sz-5,sx+5,sz+5),outline=(255,255,0),width=2)
im.save('${out}.png')`]);
console.log(`reachable ${(reach.size * cs * cs).toFixed(1)} m², spawn (${spawn.x.toFixed(2)}, ${spawn.y.toFixed(2)}, ${spawn.z.toFixed(2)}) → ${out}.png`);
