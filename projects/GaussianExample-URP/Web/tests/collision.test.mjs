// Walks a player through the sample captures without a browser and prints
// where each walk stops. Run: node tests/collision.test.mjs
import { readPly } from './plyReader.mjs';
import { VoxelWorld } from '../src/collision/VoxelWorld.js';

const dir = new URL('../SplatSamples/', import.meta.url).pathname;
const scenes = process.argv.slice(2).length ? process.argv.slice(2) : [
  'project-cozy-living-room-interior.ply',
  'project-ancient-chinese-bamboo-courtyard.ply',
];
let failures = 0;
for (const name of scenes) {
  const world = VoxelWorld.build(readPly(dir + name));
  console.log(`\n${name}: voxel ${world.voxel.toFixed(3)} dims ${world.stats.dims} used ${world.stats.used} in ${world.stats.buildMs} ms`);
  const spawn = world.findSpawn(0, 0);
  console.log(`spawn (${spawn.x.toFixed(2)}, ${spawn.y.toFixed(2)}, ${spawn.z.toFixed(2)}) floorY ${spawn.floorY?.toFixed(2)}`);
  if (!spawn.ok) { console.log('FAIL no spawn'); failures++; continue; }
  for (let k = 0; k < 16; ++k) {
    const a = (k / 16) * Math.PI * 2;
    const pos = { x: spawn.x, y: spawn.y, z: spawn.z };
    let dist = 0, minY = pos.y, maxY = pos.y, hitAt = null;
    // 30 s of walking at 1.5 m/s, 60 Hz.
    for (let f = 0; f < 1800; ++f) {
      const d = 1.5 / 60;
      const r = world.moveAndSlide(pos, Math.sin(a) * d, -Math.cos(a) * d);
      dist += r.moved;
      minY = Math.min(minY, pos.y); maxY = Math.max(maxY, pos.y);
      if (r.moved < 1e-4) { hitAt = f; break; }
    }
    const inside = world.insideXZ(pos.x, pos.z);
    const bad = !inside || minY < spawn.y - 1.0;
    if (bad) failures++;
    console.log(`${bad ? 'FAIL' : ' ok '} heading ${(k * 22.5).toFixed(1).padStart(5)}°  walked ${dist.toFixed(2).padStart(6)} m  ` +
      `end (${pos.x.toFixed(2)}, ${pos.y.toFixed(2)}, ${pos.z.toFixed(2)})  y ${minY.toFixed(2)}..${maxY.toFixed(2)}  ${hitAt === null ? 'still moving' : 'stopped'}`);
  }
}
process.exit(failures ? 1 : 0);
