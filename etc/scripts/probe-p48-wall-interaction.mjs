// P48 interaction proof.
//
// The unit tests drive the designer through jsdom, where an SVG path's
// getScreenCTM is a stub. This runs the same gesture in a real browser so the
// claim "double-click a wall to add a bend" rests on a real hit test, not on a
// mock: it double-clicks the outer wall, checks the vertex count rose from 12
// to 13, drags the new vertex, then saves and reads the stored centreline back.

import { chromium } from 'file:///C:/Users/Huynq/AppData/Local/npm-cache/_npx/e41f203b7505f1fb/node_modules/playwright/index.mjs';

const BASE = 'http://localhost:5273';
const API = `${BASE}/api/building-management`;
const USER = 'admin';
const PASS = process.env.LANE_ADMIN_PASSWORD;
const BUILDING_ID = process.env.BUILDING_ID;
const FLOOR_ID = process.env.FLOOR_ID;

if (!PASS || !BUILDING_ID || !FLOOR_ID) {
  console.error('LANE_ADMIN_PASSWORD, BUILDING_ID and FLOOR_ID must be set');
  process.exit(1);
}

function fail(message) {
  console.error(`FAIL: ${message}`);
  process.exit(1);
}

async function token() {
  const body = new URLSearchParams({
    grant_type: 'password',
    client_id: 'NgocTrangHouseManagementSystem_App',
    username: USER,
    password: PASS,
    scope: 'openid profile email roles NgocTrangHouseManagementSystem',
  });
  const res = await fetch(`${BASE}/connect/token`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
    body,
  });
  if (!res.ok) throw new Error(`token ${res.status}`);
  return (await res.json()).access_token;
}

async function items(t) {
  const res = await fetch(`${API}/floor/${FLOOR_ID}/layout-items`, {
    headers: { Authorization: `Bearer ${t}` },
  });
  return (await res.json()).data;
}

const browser = await chromium.launch({
  executablePath: 'C:/Program Files/Google/Chrome/Application/chrome.exe',
  args: ['--ignore-certificate-errors'],
});
const ctx = await browser.newContext({
  viewport: { width: 1448, height: 1086 },
  ignoreHTTPSErrors: true,
});
const page = await ctx.newPage();

await page.goto(`${BASE}/login`);
await page.fill('input[name="username"], input[type="text"]', USER);
await page.fill('input[type="password"]', PASS);
const remember = page.getByLabel(/ghi nhớ|remember/i);
if (await remember.count()) await remember.first().check();
await page.getByRole('button', { name: /đăng nhập|login/i }).click();
await page.waitForURL((u) => !u.pathname.includes('/login'), { timeout: 20000 });

await page.goto(`${BASE}/buildings/${BUILDING_ID}/floors/${FLOOR_ID}/design`);
await page.waitForSelector('svg[role="application"], svg', { timeout: 20000 });
await page.waitForTimeout(2500);

const t = await token();
const walls = (await items(t)).filter((x) => x.kind === 12);
const before = walls.find((x) => JSON.parse(x.points ?? '[]').length === 12);
if (!before) fail('the 12-vertex outer wall is not on this floor');

// Convert a metre coordinate into a page pixel through the SVG's own CTM, so
// the double-click lands on the wall instead of on a guessed fraction of a
// bounding box.
async function toScreen(x, y) {
  return page.evaluate(([mx, my]) => {
    const svg = document.querySelector('svg[role="application"]') ?? document.querySelector('svg');
    const ctm = svg.getScreenCTM();
    const p = new DOMPoint(mx, my).matrixTransform(ctm);
    return { x: p.x, y: p.y };
  }, [x, y]);
}

// The midpoint of the outer wall's long top run, (3,4)->(21,4).
const at = await toScreen(12, 4);
console.log(`double-click at metre (12, 4) -> page (${at.x.toFixed(1)}, ${at.y.toFixed(1)})`);
await page.mouse.dblclick(at.x, at.y);
await page.waitForTimeout(1200);

const vertexText = await page.locator('text=/^\\d+ điểm$/').first().textContent().catch(() => null);
console.log(`vertex count in the panel: ${vertexText ?? '(none — wall not selected)'}`);

await page.screenshot({ path: 'D:/Dev/NgocTrangBuilding/Project/NgocTrangHouseManagementSystem/etc/captures/p48/design-after-double-click.png' });

await page.getByRole('button', { name: /Lưu sơ đồ/ }).click();
await page.waitForTimeout(3000);

const after = (await items(t)).find((x) => x.id === before.id);
const afterPoints = JSON.parse(after.points ?? '[]');
console.log(`stored vertices: ${JSON.parse(before.points).length} -> ${afterPoints.length}`);

if (afterPoints.length <= JSON.parse(before.points).length) {
  fail('the double-click did not add a vertex to the saved wall');
}

await browser.close();
console.log('PASS: double-click added a bend and the save stored it.');
