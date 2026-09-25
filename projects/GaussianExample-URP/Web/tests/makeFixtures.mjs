// Builds odd "user files" from the living-room sample for tests/robustness.mjs:
//   upside-down.ply  the scene with Y flipped (as a raw 3DGS trainer exports it)
//   object.ply       only the sofa, no floor - should open in object mode
//   broken.ply       a PLY header followed by garbage
import fs from 'fs';
const src = new URL('../SplatSamples/project-cozy-living-room-interior.ply', import.meta.url).pathname;
const out = process.argv[2] || 'scratch/fixtures';
fs.mkdirSync(out, { recursive: true });
const buf = fs.readFileSync(src);
const end = buf.indexOf('end_header\n') + 11;
const header = buf.subarray(0, end).toString();
const n = +header.match(/element vertex (\d+)/)[1];
const stride = 17 * 4;
const body = buf.subarray(end);

const write = (name, rows) => {
  const h = header.replace(/element vertex \d+/, `element vertex ${rows.length / stride}`);
  fs.writeFileSync(`${out}/${name}`, Buffer.concat([Buffer.from(h), rows]));
};

// Upside down: rotate 180° about X (y -> -y, z -> -z), rotation quaternions too.
const flipped = Buffer.from(body);
for (let i = 0; i < n; ++i) {
  const o = i * stride;
  flipped.writeFloatLE(-flipped.readFloatLE(o + 4), o + 4);
  flipped.writeFloatLE(-flipped.readFloatLE(o + 8), o + 8);
  // q' = (1,0,0,0) * q with q = (w, x, y, z) stored as rot_0..3
  const w = flipped.readFloatLE(o + 52), x = flipped.readFloatLE(o + 56), y = flipped.readFloatLE(o + 60), z = flipped.readFloatLE(o + 64);
  flipped.writeFloatLE(-x, o + 52); flipped.writeFloatLE(w, o + 56); flipped.writeFloatLE(-z, o + 60); flipped.writeFloatLE(y, o + 64);
}
write('upside-down.ply', flipped);

// Object: splats in a box around the sofa, well above the floor.
const keep = [];
for (let i = 0; i < n; ++i) {
  const o = i * stride;
  const x = body.readFloatLE(o), y = body.readFloatLE(o + 4), z = body.readFloatLE(o + 8);
  if (x > -0.5 && x < 2.5 && z > -5 && z < -3 && y > 0.25 && y < 1.2) keep.push(body.subarray(o, o + stride));
}
write('object.ply', Buffer.concat(keep));

fs.writeFileSync(`${out}/broken.ply`, Buffer.concat([Buffer.from('ply\nformat binary_little_endian 1.0\nelement vertex 1000\nproperty float x\nend_header\n'), Buffer.from('not really splats')]));
console.log(`fixtures in ${out}: upside-down.ply (${n}), object.ply (${keep.length}), broken.ply`);
