// P48 wall-polyline capture.
//
// Seeds a floor whose outer wall bends the way Anh_5_nguyen_goc.png does
// (chamfered top-right, chamfered bottom-right, a projecting bay along the
// bottom), then screenshots the designer and the plan view at the four
// widths the master prompt requires. Evidence, not a build log: the images
// are what says the wall is drawn.

import { chromium } from 'file:///C:/Users/Huynq/AppData/Local/npm-cache/_npx/e41f203b7505f1fb/node_modules/playwright/index.mjs';
import { mkdirSync } from 'node:fs';

const BASE = 'http://localhost:5273';
const API = `${BASE}/api/building-management`;
const OUT = 'D:/Dev/NgocTrangBuilding/Project/NgocTrangHouseManagementSystem/etc/captures/p48';
const USER = 'admin';
const PASS = process.env.LANE_ADMIN_PASSWORD;

mkdirSync(OUT, { recursive: true });

if (!PASS) {
  console.error('LANE_ADMIN_PASSWORD is not set');
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
  if (!res.ok) throw new Error(`token ${res.status}: ${await res.text()}`);
  return (await res.json()).access_token;
}

async function api(t, method, path, payload) {
  const res = await fetch(`${API}${path}`, {
    method,
    headers: {
      Authorization: `Bearer ${t}`,
      ...(payload ? { 'Content-Type': 'application/json' } : {}),
    },
    ...(payload ? { body: JSON.stringify(payload) } : {}),
  });
  const text = await res.text();
  if (!res.ok) throw new Error(`${method} ${path} -> ${res.status}: ${text}`);
  return text ? JSON.parse(text).data : null;
}

// The storey outline from Anh_5, in metres. A 26 x 19.5 m envelope: the
// top-left corner is cut back a little, the top-right corner is cut back hard,
// the bottom-right corner is cut back, and the stair bay projects 3.5 m south
// from the bottom edge. A box could not be any of those four things.
const OUTER_WALL = [
  { x: 0, y: 7 },
  { x: 3, y: 4 },
  { x: 21, y: 4 },
  { x: 26, y: 9 },
  { x: 26, y: 17 },
  { x: 23, y: 20 },
  { x: 15, y: 20 },
  { x: 15, y: 23.5 },
  { x: 10.5, y: 23.5 },
  { x: 10.5, y: 20 },
  { x: 0, y: 20 },
  { x: 0, y: 7 },
];

// Interior partitions. The second one is the case a box could never draw: it
// runs along the corridor, then turns south to enclose the stair bay.
const PARTITIONS = [
  [
    { x: 9.2, y: 4 },
    { x: 9.2, y: 10.2 },
    { x: 14.2, y: 10.2 },
    { x: 14.2, y: 4 },
  ],
  [
    { x: 0.4, y: 13.6 },
    { x: 11, y: 13.6 },
    { x: 11, y: 23.2 },
  ],
  [
    { x: 11, y: 13.6 },
    { x: 22.6, y: 13.6 },
  ],
];

const ROOMS = [
  { number: '101', x: 4, y: 5, w: 4.8, h: 5 },
  { number: '102', x: 9.6, y: 5, w: 4.2, h: 5 },
  { number: '103', x: 14.6, y: 5, w: 2.4, h: 5 },
  { number: '104', x: 17.4, y: 5, w: 4.1, h: 5 },
  { number: '105', x: 1, y: 14, w: 5.4, h: 5.2 },
  { number: '106', x: 6.8, y: 14, w: 4.2, h: 5.2 },
  { number: '107', x: 11.4, y: 14, w: 3, h: 5.2 },
  { number: '108', x: 17.6, y: 14, w: 4.9, h: 5.2 },
];

const VIEWPORTS = [
  { name: '1448x1086', width: 1448, height: 1086 },
  { name: '1280x800', width: 1280, height: 800 },
  { name: '1024x768', width: 1024, height: 768 },
  { name: '390x844', width: 390, height: 844 },
];

function boxOf(points, thickness) {
  const xs = points.map((p) => p.x);
  const ys = points.map((p) => p.y);
  const pad = thickness / 2;
  const minX = Math.min(...xs) - pad;
  const minY = Math.min(...ys) - pad;
  return {
    x: minX,
    y: minY,
    width: Math.max(0.5, Math.max(...xs) - Math.min(...xs) + pad * 2),
    height: Math.max(0.5, Math.max(...ys) - Math.min(...ys) + pad * 2),
  };
}

async function main() {
  const t = await token();
  console.log('token ok');

  const building = await api(t, 'POST', '/building', {
    name: 'Ngoc Trang - P48',
    address: 'So 1 Duong Kiem Thu',
    description: 'P48 wall polyline evidence',
  });
  const floor = await api(t, 'POST', '/floor', {
    buildingId: building.id,
    floorNumber: 1,
    name: 'Tang 1',
    description: null,
    status: 2,
  });
  console.log(`building ${building.id} floor ${floor.id}`);

  for (const r of ROOMS) {
    const room = await api(t, 'POST', '/room', {
      floorId: floor.id,
      roomNumber: r.number,
      name: `Phong ${r.number}`,
      area: Math.round(r.w * r.h),
      capacity: 2,
      status: 1,
      description: null,
    });
    await api(t, 'PUT', `/room/${room.id}/layout`, {
      x: r.x, y: r.y, width: r.w, height: r.h,
      shape: 1, rotationDegrees: 0, points: null,
    });
  }
  console.log(`${ROOMS.length} rooms placed`);

  const outerThickness = 0.32;
  const partitionThickness = 0.16;

  const outerBox = boxOf(OUTER_WALL, outerThickness);
  const outer = await api(t, 'POST', `/floor/${floor.id}/layout-item`, {
    kind: 12,
    label: null,
    ...outerBox,
    points: JSON.stringify(OUTER_WALL),
    thickness: outerThickness,
    cornerRadius: 0,
  });

  const partitions = [];
  for (const points of PARTITIONS) {
    partitions.push(
      await api(t, 'POST', `/floor/${floor.id}/layout-item`, {
        kind: 12,
        label: null,
        ...boxOf(points, partitionThickness),
        points: JSON.stringify(points),
        thickness: partitionThickness,
        cornerRadius: 0,
      }),
    );
  }
  console.log(`outer ${outer.id} partitions ${partitions.length}`);

  // Read back through the API: a wall that saved but lost its centreline would
  // screenshot as a plain box, and the images would be the lie.
  const saved = await api(t, 'GET', `/floor/${floor.id}/layout-items`);
  for (const item of saved.filter((x) => x.kind === 12)) {
    const pts = JSON.parse(item.points ?? '[]');
    console.log(
      `  ${item.label}: ${pts.length} vertices, ` +
        `thickness ${item.thickness}, radius ${item.cornerRadius}`,
    );
    if (pts.length < 2) throw new Error(`${item.id} saved without a centreline`);
  }

  const browser = await chromium.launch({
    executablePath: 'C:/Program Files/Google/Chrome/Application/chrome.exe',
    args: ['--ignore-certificate-errors'],
  });

  for (const vp of VIEWPORTS) {
    const ctx = await browser.newContext({
      viewport: { width: vp.width, height: vp.height },
      ignoreHTTPSErrors: true,
    });
    const page = await ctx.newPage();
    page.on('console', (m) => {
      if (m.type() === 'error') console.log(`  [console] ${m.text()}`);
    });

    await page.goto(`${BASE}/login`);
    await page.fill('input[name="username"], input[type="text"]', USER);
    await page.fill('input[type="password"]', PASS);
    // Playwright's fresh context has no storage, so without this the session
    // lives only in memory and the next goto lands back on the login screen.
    const remember = page.getByLabel(/ghi nhớ|remember/i);
    if (await remember.count()) await remember.first().check();
    await page.getByRole('button', { name: /đăng nhập|login/i }).click();
    await page.waitForURL((u) => !u.pathname.includes('/login'), { timeout: 20000 });

    for (const [label, path] of [
      ['design', `/buildings/${building.id}/floors/${floor.id}/design`],
      ['plan', `/buildings/${building.id}/floors/${floor.id}`],
    ]) {
      await page.goto(`${BASE}${path}`);
      await page.waitForSelector('svg', { timeout: 20000 });
      // Never networkidle under Vite HMR: it never settles.
      await page.waitForTimeout(2500);
      const file = `${OUT}/${label}-${vp.name}.png`;
      await page.screenshot({ path: file, fullPage: false });
      console.log(`shot ${file}`);
    }

    await ctx.close();
  }

  // The Owner asked for rounded corners as well as the chamfered ones, so the
  // radius has to be proved on screen too, not just accepted by the API.
  await api(t, 'PUT', `/floor/${floor.id}/layout-item/${outer.id}`, {
    kind: 12,
    label: null,
    ...outerBox,
    points: JSON.stringify(OUTER_WALL),
    thickness: outerThickness,
    cornerRadius: 0.9,
  });
  {
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
    await page.goto(`${BASE}/buildings/${building.id}/floors/${floor.id}`);
    await page.waitForSelector('svg', { timeout: 20000 });
    await page.waitForTimeout(2500);
    await page.screenshot({ path: `${OUT}/plan-rounded-1448x1086.png` });
    console.log(`shot ${OUT}/plan-rounded-1448x1086.png`);
    await ctx.close();
  }

  await browser.close();
  console.log(`OUTPUT_DIR=${OUT}`);
  console.log(`BUILDING_ID=${building.id}`);
  console.log(`FLOOR_ID=${floor.id}`);
}

main().catch((err) => {
  console.error(err);
  process.exit(1);
});
