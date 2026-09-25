// Opening awkward user files must never leave the viewer broken.
//   node tests/makeFixtures.mjs scratch/fixtures && BASE=... node tests/robustness.mjs scratch/fixtures
import { chromium } from 'playwright';
const BASE = process.env.BASE || 'http://127.0.0.1:5173';
const dir = process.argv[2] || 'scratch/fixtures';
const results = [];
const check = (name, ok, detail = '') => { results.push(ok); console.log(`${ok ? 'PASS' : 'FAIL'}  ${name}${detail ? '  — ' + detail : ''}`); };
const browser = await chromium.launch({ args: ['--use-gl=angle', '--use-angle=swiftshader', '--enable-unsafe-swiftshader'] });
const page = await browser.newPage();
const pageErrors = [];
page.on('pageerror', (e) => pageErrors.push(e.message));
await page.goto(`${BASE}/?scene=living-room&norender`);
await page.waitForFunction(() => window.ukemi?.world, null, { timeout: 120000 });

const open = async (file) => {
  await page.evaluate(() => { window.__loaded = window.ukemi.world; });
  await page.setInputFiles('#file', file);
  await page.waitForFunction(() => (window.ukemi.world !== window.__loaded) || !document.getElementById('toast').hidden, null, { timeout: 120000 });
  await page.waitForTimeout(300);
  return page.evaluate(() => ({
    mode: window.ukemi.mode, current: window.ukemi.current?.name, spawn: window.ukemi.spawn,
    toast: document.getElementById('toast').hidden ? '' : document.getElementById('toast').textContent,
    error: document.getElementById('toast').classList.contains('error'),
  }));
};

let r = await open(`${dir}/upside-down.ply`);
check('upside-down PLY is turned the right way up and walkable', r.mode === 'walk' && r.spawn?.ok && Math.abs(r.spawn.y) < 0.4,
  `mode ${r.mode}, floor y ${r.spawn?.y?.toFixed(2)}`);

r = await open(`${dir}/object.ply`);
check('a capture with no floor opens in object mode', r.mode === 'object' && r.current === 'object.ply', `mode ${r.mode}; ${r.toast}`);

r = await open(`${dir}/broken.ply`);
check('a broken file shows an error and keeps the current scene', r.error && r.current === 'object.ply' && r.mode === 'object', r.toast);

await page.setInputFiles('#file', { name: 'notes.txt', mimeType: 'text/plain', buffer: Buffer.from('hello') });
await page.waitForTimeout(300);
const t = await page.textContent('#toast');
check('an unsupported extension is refused up front', t.includes('not supported'), t);

// Back to a sample from the scene switcher.
await page.selectOption('#sample', 'bamboo-courtyard');
await page.waitForFunction(() => window.ukemi.current?.id === 'bamboo-courtyard' && window.ukemi.mode === 'walk', null, { timeout: 120000 });
check('switching back to a sample walks again', true);
check('no uncaught page errors', pageErrors.length === 0, pageErrors.slice(0, 2).join(' | '));
await browser.close();
const failed = results.filter((x) => !x).length;
console.log(`\n${results.length - failed}/${results.length} passed`);
process.exit(failed ? 1 : 0);
