// Minimal reader for 3DGS binary PLY (float properties), for Node tests.
import fs from 'fs';
export function readPly(path) {
  const buf = fs.readFileSync(path);
  const end = buf.indexOf('end_header\n') + 11;
  const header = buf.subarray(0, end).toString();
  const count = +header.match(/element vertex (\d+)/)[1];
  const props = [...header.matchAll(/property float (\w+)/g)].map(m => m[1]);
  const f = new Float32Array(buf.buffer.slice(buf.byteOffset + end, buf.byteOffset + end + count * props.length * 4));
  const P = Object.fromEntries(props.map((p, i) => [p, i]));
  const st = props.length;
  return {
    count,
    forEach(cb) {
      for (let i = 0; i < count; ++i) {
        const o = i * st;
        let qw = f[o + P.rot_0], qx = f[o + P.rot_1], qy = f[o + P.rot_2], qz = f[o + P.rot_3];
        const ql = Math.hypot(qw, qx, qy, qz) || 1;
        cb(f[o + P.x], f[o + P.y], f[o + P.z],
          Math.exp(f[o + P.scale_0]), Math.exp(f[o + P.scale_1]), Math.exp(f[o + P.scale_2]),
          qx / ql, qy / ql, qz / ql, qw / ql, 1 / (1 + Math.exp(-f[o + P.opacity])));
      }
    },
  };
}
