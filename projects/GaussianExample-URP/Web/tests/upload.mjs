// Opens local .ply / .spz files through the real file picker and checks each
// one renders a collision world and a spawn point.
//   BASE=http://127.0.0.1:5173 node tests/upload.mjs file1 [file2 ...]
import { chromium } from 'playwright';
const BASE = process.env.BASE || 'http://127.0.0.1:5173';
const files = process.argv.slice(2);
const browser = await chromium.launch({ args: ['--use-gl=angle', '--use-angle=swiftshader', '--enable-unsafe-swiftshader'] });
const page = await browser.newPage();
const errors = [];
page.on('pageerror', (e) => errors.push(e.message));
await page.goto(`${BASE}/?scene=none&norender`);
let failed = 0;
for (const f of files) {
  const t0 = Date.now();
  await page.setInputFiles('#file', f);
  await page.waitForFunction((name) => window.ukemi.world && document.getElementById('scene-info').textContent.startsWith(name),
    f.split('/').pop(), { timeout: 240000 });
  const r = await page.evaluate(() => ({ stats: window.ukemi.world.stats, spawn: window.ukemi.spawn }));
  const ok = r.stats.splats > 0 && r.spawn.ok;
  if (!ok) failed++;
  console.log(`${ok ? 'PASS' : 'FAIL'}  ${f.split('/').pop()}: ${r.stats.splats} splats, voxel ${r.stats.voxel.toFixed(3)}, ` +
    `spawn (${r.spawn.x.toFixed(2)}, ${r.spawn.y.toFixed(2)}, ${r.spawn.z.toFixed(2)}), ${((Date.now() - t0) / 1000).toFixed(1)} s`);
}
if (errors.length) { failed++; console.log('page errors:', errors.slice(0, 3)); }
await browser.close();
process.exit(failed ? 1 : 0);
