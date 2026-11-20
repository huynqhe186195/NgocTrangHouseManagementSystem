// P60 roster capture. The lane is roster-only: no bootstrap or mutation route.
// The API preflight runs before trusting any screenshot. Password is supplied
// through LANE_ADMIN_PASSWORD and never written to disk.
import { chromium } from 'file:///C:/Users/Huynq/AppData/Local/npm-cache/_npx/e41f203b7505f1fb/node_modules/playwright/index.mjs';
import { mkdirSync } from 'node:fs';

const BASE = process.env.LANE_BASE ?? 'http://localhost:5273';
const API = `${BASE}/api/building-management`;
const OUT = 'D:/Dev/NgocTrangBuilding/Project/NgocTrangHouseManagementSystem/etc/captures/p60';
const PASS = process.env.LANE_ADMIN_PASSWORD;
const ROUTE = '**/api/building-management/operator-memberships**';
const VIEWPORTS = [
  { name: '1448x1086', width: 1448, height: 1086 },
  { name: '1280x800', width: 1280, height: 800 },
  { name: '1024x768', width: 1024, height: 768 },
  { name: '390x844', width: 390, height: 844 },
];
mkdirSync(OUT, { recursive: true });
if (!PASS) throw new Error('LANE_ADMIN_PASSWORD is not set');

async function token() {
  const body = new URLSearchParams({
    grant_type: 'password',
    client_id: 'NgocTrangHouseManagementSystem_App',
    username: 'admin',
    password: PASS,
    scope: 'openid profile email roles NgocTrangHouseManagementSystem',
  });
  const response = await fetch(`${BASE}/connect/token`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
    body,
  });
  if (!response.ok) throw new Error(`token ${response.status}: ${await response.text()}`);
  return (await response.json()).access_token;
}

async function api(accessToken, path) {
  const response = await fetch(`${API}${path}`, {
    headers: { Authorization: `Bearer ${accessToken}` },
    redirect: 'manual',
  });
  const text = await response.text();
  let body = null;
  try { body = text ? JSON.parse(text) : null; } catch { /* diagnostic below */ }
  if (!response.ok) throw new Error(`GET ${path} -> ${response.status} ${text.slice(0, 300)}`);
  if (!body?.success || !body.data) throw new Error(`GET ${path} did not return an ABP success envelope`);
  return body.data;
}

function assert(condition, message) {
  if (!condition) throw new Error(message);
}

const accessToken = await token();
const roster = await api(accessToken, '/operator-memberships?skipCount=0&maxResultCount=100');
assert(Array.isArray(roster.items), 'roster.items is not an array');
assert(roster.items.length === 7, `expected 7 seeded history rows, got ${roster.items.length}`);
const buildings = new Map(roster.items.map((row) => [row.buildingId, row.buildingName]));
assert(buildings.size === 2, `expected two scoped buildings, got ${buildings.size}`);
const statuses = new Set(roster.items.map((row) => row.status));
for (const status of [1, 2, 3, 4]) assert(statuses.has(status), `missing seeded status ${status}`);
const buildingIds = [...buildings.keys()];
const filtered = await api(accessToken, `/operator-memberships?buildingId=${buildingIds[0]}&skipCount=0&maxResultCount=100`);
assert(filtered.items.length === 4, `expected four rows for first building, got ${filtered.items.length}`);
assert(filtered.items.every((row) => row.buildingId === buildingIds[0]), 'building filter escaped its scope');
console.log(`API PREFLIGHT PASS: ${roster.items.length} rows, ${buildings.size} buildings, statuses active/expired/revoked/scheduled`);
console.log(`API FILTER PASS: ${filtered.items.length} rows belong only to ${buildings.get(buildingIds[0])}`);

async function login(page) {
  await page.goto(`${BASE}/login`);
  await page.fill('input[name="username"], input[type="text"]', 'admin');
  await page.fill('input[type="password"]', PASS);
  const remember = page.getByLabel(/ghi nhớ|remember/i);
  if (await remember.count()) await remember.first().check();
  await page.getByRole('button', { name: /đăng nhập|login/i }).click();
  await page.waitForURL((url) => !url.pathname.includes('/login'), { timeout: 20000 });
}

const browser = await chromium.launch({
  executablePath: 'C:/Program Files/Google/Chrome/Application/chrome.exe',
  args: ['--ignore-certificate-errors'],
});

for (const viewport of VIEWPORTS) {
  const context = await browser.newContext({
    viewport: { width: viewport.width, height: viewport.height },
    ignoreHTTPSErrors: true,
  });
  const page = await context.newPage();
  page.on('response', (response) => {
    if (response.status() >= 400) console.log(`  [${viewport.name}] ${response.status()} ${response.url()}`);
  });
  await login(page);
  await page.goto(`${BASE}/users`);
  await page.waitForTimeout(1200);
  const body = await page.locator('body').innerText();
  for (const text of ['Người dùng', 'Tòa nhà A', 'Tòa nhà B', 'Đang hiệu lực', 'Đã hết hạn', 'Đã thu hồi', 'Sắp có hiệu lực']) {
    assert(body.includes(text), `${viewport.name}: missing ${text}`);
  }
  const mutationControls = await page.locator('button, a').allTextContents();
  assert(!mutationControls.some((text) => /cấp quyền|thu hồi quyền|đổi chủ|assign|revoke/i.test(text)), `${viewport.name}: mutation control leaked into roster UI`);
  const layout = await page.evaluate(() => {
    const visible = (element) => !!element && element.getBoundingClientRect().width > 0;
    const documentElement = document.documentElement;
    const panel = document.querySelector('table')?.parentElement;
    return {
      pageScrollsSideways: documentElement.scrollWidth > documentElement.clientWidth + 1,
      pageWidth: documentElement.scrollWidth,
      viewport: documentElement.clientWidth,
      tableVisible: visible(document.querySelector('table')),
      cardsVisible: visible(document.querySelector('ul.xl\\:hidden')),
      tablePanelScrolls: !!panel && panel.scrollWidth > panel.clientWidth + 1,
    };
  });
  assert(!layout.pageScrollsSideways, `${viewport.name}: page scrolls sideways`);
  assert(layout.tableVisible !== layout.cardsVisible, `${viewport.name}: both roster layouts visible`);
  const expectTable = viewport.width >= 1280;
  assert(layout.tableVisible === expectTable, `${viewport.name}: wrong roster layout at breakpoint`);
  console.log(`  ${viewport.name}: layout=${layout.tableVisible ? 'table' : 'cards'} page=${layout.pageWidth}/${layout.viewport}`);
  await page.screenshot({ path: `${OUT}/roster-${viewport.name}.png`, fullPage: true });
  await context.close();
}

async function captureState(name, handler) {
  const context = await browser.newContext({ viewport: { width: 1448, height: 1086 }, ignoreHTTPSErrors: true });
  const page = await context.newPage();
  await login(page);
  await page.route(ROUTE, handler);
  await page.goto(`${BASE}/users`);
  await page.waitForTimeout(name === 'loading' ? 250 : 900);
  const body = await page.locator('body').innerText();
  if (name === 'loading') assert(body.includes('Đang tải danh sách người dùng'), 'loading state was not rendered');
  if (name === 'empty') assert(body.includes('Chưa có membership nào') && !body.includes('Đang hiệu lực'), 'empty state was not isolated');
  if (name === 'forbidden') assert(body.includes('403') && body.includes('từ chối truy cập'), '403 state was not rendered');
  await page.screenshot({ path: `${OUT}/roster-${name}-1448x1086.png`, fullPage: true });
  console.log(`captured roster-${name}-1448x1086.png`);
  await context.close();
}

await captureState('loading', async (route) => {
  await new Promise((resolve) => setTimeout(resolve, 5000));
  await route.continue();
});
await captureState('empty', (route) => route.fulfill({
  status: 200,
  contentType: 'application/json',
  body: JSON.stringify({ success: true, data: { totalCount: 0, items: [] }, error: null, meta: { traceId: 'capture', timestamp: new Date().toISOString() } }),
}));
await captureState('forbidden', (route) => route.fulfill({
  status: 403,
  contentType: 'application/json',
  body: JSON.stringify({ success: false, data: null, error: { code: 'Forbidden', message: 'Từ chối truy cập', details: null, validationErrors: [] }, meta: { traceId: 'capture', timestamp: new Date().toISOString() } }),
}));
await browser.close();
console.log('P60 roster capture complete');
