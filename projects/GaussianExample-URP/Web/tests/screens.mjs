// Renders a few viewpoints of the real page to PNG (slow under SwiftShader).
//   BASE=http://127.0.0.1:5173 node tests/screens.mjs <scene> <outdir> [debug]
import { chromium } from 'playwright';
import fs from 'fs';
const BASE = process.env.BASE || 'http://127.0.0.1:5173';
const [,, scene = 'living-room', outDir = 'scratch', debug] = process.argv;
fs.mkdirSync(outDir, { recursive: true });
const browser = await chromium.launch({ args: ['--use-gl=angle', '--use-angle=swiftshader', '--enable-unsafe-swiftshader'] });
const page = await browser.newPage({ viewport: { width: 960, height: 600 } });
page.on('pageerror', (e) => console.log('pageerror', e.message));
await page.goto(`${BASE}/?scene=${scene}&test${debug ? '&debug' : ''}`);
await page.waitForFunction(() => window.ukemi?.world && window.ukemi.player.world, null, { timeout: 300000 });
await page.evaluate(() => { const u = window.ukemi; u.pause(); u.setRender(false); u.settings.follow = 'discrete'; u.applySettings(); });
const shoot = async (name) => {
  // Let Spark finish sorting for this viewpoint, then grab the canvas.
  for (let i = 0; i < 6; ++i) { await page.evaluate(() => window.ukemi.renderOnce()); await page.waitForTimeout(400); }
  await page.screenshot({ path: `${outDir}/${scene}-${name}.png`, timeout: 120000 });
  console.log('wrote', `${outDir}/${scene}-${name}.png`);
};
const run = (n, raw) => page.evaluate(({ n, raw }) => {
  const u = window.ukemi;
  u.sources.override = { move: { x: 0, y: 0 }, right: { x: 0, y: 0 }, a: false, b: false, ...raw };
  for (let i = 0; i < n; ++i) u.step(1 / 60);
}, { n, raw });
await run(3, {});
await shoot('1-first-person');
await run(45, { move: { x: 0.2, y: 1 } });
await shoot('2-third-person');
await run(20, { move: { x: 1, y: 0 } });
await shoot('3-strafe');
await browser.close();
