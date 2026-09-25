// Screenshots of the landing page (hero over the live scene + sections).
//   BASE=http://127.0.0.1:5173 node tests/landing.mjs <outdir> [width height]
import { chromium } from 'playwright';
import fs from 'fs';
const BASE = process.env.BASE || 'http://127.0.0.1:5173';
const [,, outDir = 'scratch', w = '1440', h = '900', mobile] = process.argv;
fs.mkdirSync(outDir, { recursive: true });
const browser = await chromium.launch({ args: ['--use-gl=angle', '--use-angle=swiftshader', '--enable-unsafe-swiftshader'] });
const page = await browser.newPage({ viewport: { width: +w, height: +h }, deviceScaleFactor: mobile ? 2 : 1, isMobile: !!mobile, hasTouch: !!mobile });
page.on('pageerror', (e) => console.log('pageerror', e.message));
await page.goto(`${BASE}/?scene=living-room&test`);
await page.waitForFunction(() => window.ukemi?.world, null, { timeout: 300000 });
await page.evaluate(() => { const u = window.ukemi; u.setRender(false); u.step(2); u.pause(); });
for (let i = 0; i < 6; ++i) { await page.evaluate(() => window.ukemi.renderOnce()); await page.waitForTimeout(400); }
const tag = `${w}x${h}`;
await page.screenshot({ path: `${outDir}/landing-${tag}-hero.png`, timeout: 120000 });
for (const id of ['demo', 'value', 'industries', 'how', 'contact']) {
  await page.evaluate((id) => { const el = document.getElementById(id); document.getElementById('intro').scrollTo({ top: el.offsetTop, behavior: 'instant' }); }, id);
  await page.waitForTimeout(300);
  await page.screenshot({ path: `${outDir}/landing-${tag}-${id}.png`, timeout: 120000 });
}
console.log('done');
await browser.close();
