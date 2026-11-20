// P59 capture — the admin "Khách thuê" roster and the tenant detail page.
//
// No Figma mockup exists for this screen, so the acceptance evidence is: the
// server returns exactly the people this script seeded, the roster prints them
// with where they rent, and the four viewports render without a sideways
// scrollbar or a broken card.
//
// The admin password arrives through LANE_ADMIN_PASSWORD, never written down.

import { chromium } from 'file:///C:/Users/Huynq/AppData/Local/npm-cache/_npx/e41f203b7505f1fb/node_modules/playwright/index.mjs';
import { mkdirSync } from 'node:fs';
import { execFileSync } from 'node:child_process';

const BASE = process.env.LANE_BASE ?? 'http://localhost:5273';
const API = `${BASE}/api/building-management`;
const OUT =
  'D:/Dev/NgocTrangBuilding/Project/NgocTrangHouseManagementSystem/etc/captures/p59';
const PASS = process.env.LANE_ADMIN_PASSWORD;
const USER = 'admin';
const DB_CONTAINER = process.env.LANE_DB_CONTAINER ?? 'bm-p21-pg-15432';
const BUILDING_DB = process.env.LANE_BUILDING_DB;

mkdirSync(OUT, { recursive: true });
if (!PASS) {
  console.error('LANE_ADMIN_PASSWORD is not set');
  process.exit(1);
}
if (!BUILDING_DB) {
  console.error('LANE_BUILDING_DB is not set');
  process.exit(1);
}

const VIEWPORTS = [
  { name: '1448x1086', width: 1448, height: 1086 },
  { name: '1280x800', width: 1280, height: 800 },
  { name: '1024x768', width: 1024, height: 768 },
  { name: '390x844', width: 390, height: 844 },
];

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
  if (!res.ok) {
    throw new Error(`${method} ${path} -> ${res.status} ${text.slice(0, 400)}`);
  }
  return text ? JSON.parse(text).data : null;
}

function psql(sql) {
  const out = execFileSync(
    'docker',
    ['exec', '-i', DB_CONTAINER, 'psql', '-U', 'postgres', '-d', BUILDING_DB,
      '-v', 'ON_ERROR_STOP=1', '-t', '-A'],
    { input: sql, encoding: 'utf8' },
  );
  return out.split(/\r?\n/).map((line) => line.trim()).filter(Boolean)[0] ?? '';
}

// ---- 1. One building, two floors, four rooms ----------------------------
const t = await token();
const building = await api(t, 'POST', '/building', {
  name: 'Ngọc Trang Building',
  address: '12 Nguyễn Trãi, Quận 5',
  description: 'Tòa nhà dùng cho ảnh chụp P59.',
});

async function makeFloor(number, name, description) {
  return api(t, 'POST', '/floor', {
    buildingId: building.id,
    floorNumber: number,
    name,
    description,
    status: 1,
  });
}

const floor1 = await makeFloor(1, 'Tầng 1', null);
const floor2 = await makeFloor(2, 'Tầng 2', null);

async function makeRoom(floor, number, status) {
  const room = await api(t, 'POST', '/room', {
    floorId: floor.id,
    roomNumber: number,
    name: `Phòng ${number}`,
    area: 22.5,
    capacity: 2,
    status,
    description: null,
    roomType: 'Phòng tiêu chuẩn',
    locationNote: null,
    direction: 4,
  });
  await api(t, 'PUT', `/room/${room.id}/rent`, { monthlyRent: 3500000 });
  return room;
}

const room101 = await makeRoom(floor1, '101', 2);
const room102 = await makeRoom(floor1, '102', 2);
const room201 = await makeRoom(floor2, '201', 2);
const room202 = await makeRoom(floor2, '202', 1);

// ---- 2. Seed people and links ------------------------------------------
// Gender: 1 Male, 2 Female, 3 Other. Null is allowed and printed as "—".
const TENANTS = [
  {
    id: '7a2d3c6f-2222-4b02-8b02-000000000001',
    name: 'Nguyễn Văn An', phone: '0901234501', email: 'an.nguyen@example.com',
    gender: 1, address: '12 Lê Lợi, Phường Bến Nghé, Quận 1',
    notes: 'Khách thuê lâu năm, đóng tiền đúng hạn.',
  },
  {
    id: '7a2d3c6f-2222-4b02-8b02-000000000002',
    name: 'Trần Thị Bình', phone: '0901234502', email: null,
    gender: 2, address: '45 Trần Hưng Đạo, Quận 5',
    notes: null,
  },
  {
    id: '7a2d3c6f-2222-4b02-8b02-000000000003',
    name: 'Lê Hoàng Cường', phone: '0901234503', email: 'cuong.le@example.com',
    gender: 1, address: null,
    notes: 'Thanh toán chuyển khoản.',
  },
  {
    id: '7a2d3c6f-2222-4b02-8b02-000000000004',
    name: 'Phạm Thu Dung', phone: '0901234504', email: null,
    gender: 2, address: '88 Nguyễn Trãi, Quận 5',
    notes: 'Đã trả phòng, còn giữ liên hệ.',
  },
  {
    id: '7a2d3c6f-2222-4b02-8b02-000000000005',
    name: 'Đỗ Minh Khôi', phone: '0901234505', email: 'khoi.do@example.com',
    gender: 1, address: null,
    notes: 'Thuê hai phòng ở hai tầng khác nhau.',
  },
  {
    id: '7a2d3c6f-2222-4b02-8b02-000000000006',
    name: 'Vũ Thị Hạnh', phone: '0901234506', email: null,
    gender: null, address: null,
    notes: null,
  },
];

for (const tenant of TENANTS) {
  psql(
    `INSERT INTO "BuildingManagementTenants" (
       "Id", "FullName", "PhoneNumber", "Email", "DateOfBirth", "Gender",
       "IdentityType", "IdentityNumber", "PermanentAddress",
       "EmergencyContactName", "EmergencyContactPhone", "Notes",
       "ExtraProperties", "ConcurrencyStamp", "CreationTime", "IsDeleted")
     VALUES (
       '${tenant.id}'::uuid, '${tenant.name}', '${tenant.phone}',
       ${tenant.email ? `'${tenant.email}'` : 'NULL'},
       NULL, ${tenant.gender ?? 'NULL'},
       1, '0792010010${TENANTS.indexOf(tenant) + 1}',
       ${tenant.address ? `'${tenant.address}'` : 'NULL'},
       NULL, NULL,
       ${tenant.notes ? `'${tenant.notes}'` : 'NULL'},
       '{}', gen_random_uuid()::text, now()::timestamp, false);`,
  );
}

// status: 1 Draft, 2 Active, 3 Ended, 4 Cancelled, 5 Signed
// The roster highlights the live contract per person, so each status has to be
// somebody's *highlight* or it never reaches the screen:
//   An      -> two live contracts (101 and 302)  (Đang hiệu lực)
//   Bình    -> only a signed contract            (Đã ký)
//   Cường   -> a live contract ending soon        (Đang hiệu lực, sắp hết hạn)
//   Dung    -> one ended contract                 (Đã kết thúc)
//   Khôi    -> three live contracts               (thuê nhiều phòng)
//   Hạnh    -> no contract at all                 (must NOT appear)
const TODAY = new Date();
const soon = new Date(TODAY.getTime() + 14 * 24 * 60 * 60 * 1000)
  .toISOString().slice(0, 10);

const CONTRACTS = [
  {
    number: 'HD-2026-101', room: room101, status: 2,
    start: '2026-01-01', end: '2026-12-31',
    rent: 3500000, deposit: 7000000,
    tenants: [{ ...TENANTS[0], role: 1 }, { ...TENANTS[4], role: 2 }],
    notes: null,
  },
  {
    number: 'HD-2026-102', room: room102, status: 5,
    start: '2026-03-01', end: '2027-02-28',
    rent: 4200000, deposit: 8400000,
    tenants: [{ ...TENANTS[1], role: 1 }],
    notes: null,
  },
  {
    number: 'HD-2026-201', room: room201, status: 2,
    start: '2026-05-01', end: soon,
    rent: 3900000, deposit: 7800000,
    tenants: [{ ...TENANTS[2], role: 1 }],
    notes: null,
  },
  {
    number: 'HD-2025-088', room: room202, status: 3,
    start: '2025-01-01', end: '2025-12-31', actualEnd: '2025-11-30', endReason: 2,
    rent: 3100000, deposit: 6200000,
    tenants: [{ ...TENANTS[3], role: 1 }],
    notes: 'Khách đã trả phòng, tất toán tiền cọc.',
  },
  {
    number: 'HD-2026-301', room: room201, status: 2,
    start: '2026-02-01', end: '2027-01-31',
    rent: 3600000, deposit: 7200000,
    tenants: [{ ...TENANTS[4], role: 1 }],
    notes: null,
  },
  {
    number: 'HD-2026-302', room: room101, status: 2,
    start: '2026-04-01', end: '2027-03-31',
    rent: 3700000, deposit: 7400000,
    tenants: [{ ...TENANTS[4], role: 2 }, { ...TENANTS[0], role: 1 }],
    notes: null,
  },
];

const contractIds = {};
for (const spec of CONTRACTS) {
  contractIds[spec.number] = psql(
    `INSERT INTO "BuildingManagementContracts" (
       "Id", "ContractNumber", "RoomId", "StartDate", "EndDate",
       "ActualEndedAt", "EndReason", "MonthlyRent", "DepositAmount",
       "Status", "Notes", "ExtraProperties", "ConcurrencyStamp",
       "CreationTime", "IsDeleted")
     VALUES (
       gen_random_uuid()::uuid, '${spec.number}', '${spec.room.id}',
       DATE '${spec.start}', ${spec.end ? `DATE '${spec.end}'` : 'NULL'},
       ${spec.actualEnd ? `DATE '${spec.actualEnd}'` : 'NULL'},
       ${spec.endReason ?? 'NULL'},
       ${spec.rent}, ${spec.deposit}, ${spec.status},
       ${spec.notes ? `'${spec.notes.replace(/'/g, "''")}'` : 'NULL'}, '{}',
       gen_random_uuid()::text, now()::timestamp, false)
     RETURNING "Id"::text;`,
  );
}
for (const spec of CONTRACTS) {
  for (const link of spec.tenants) {
    psql(
      `INSERT INTO "BuildingManagementContractTenants"
         ("Id", "ContractId", "TenantId", "Role", "CreationTime", "IsDeleted")
       VALUES (gen_random_uuid()::uuid, '${contractIds[spec.number]}',
         '${link.id}', ${link.role}, now()::timestamp, false);`,
    );
  }
}

const EXPECTED_PEOPLE = TENANTS.filter((x) => x.name !== 'Vũ Thị Hạnh');

// ---- 3. The read must answer before any pixel is trusted ---------------
const roster = await api(t, 'GET', '/tenants');
if (!Array.isArray(roster)) throw new Error('GET /tenants did not return an array');
if (roster.length !== EXPECTED_PEOPLE.length) {
  throw new Error(
    `GET /tenants returned ${roster.length}, expected ${EXPECTED_PEOPLE.length}` +
      ` (${roster.map((x) => x.fullName).join(', ')})`,
  );
}
if (roster.some((x) => x.fullName === 'Vũ Thị Hạnh')) {
  throw new Error('a person with no contract was listed');
}
for (const person of EXPECTED_PEOPLE) {
  const row = roster.find((x) => x.id === person.id);
  if (!row) throw new Error(`${person.name} was not read back`);
  if (row.phoneNumber !== person.phone) {
    throw new Error(`${person.name} phone ${row.phoneNumber}, expected ${person.phone}`);
  }
  if (!row.contracts.length) throw new Error(`${person.name} came back with no contract`);
  console.log(
    `  ${person.name}: ${row.contracts.length} hợp đồng |` +
      ` ${row.contracts.map((c) => `${c.contractNumber} (${c.roomNumber}, status ${c.status})`).join(', ')}`,
  );
}
console.log(`GET /tenants lists ${roster.length} people, one building`);

const anId = TENANTS[0].id;
const detail = await api(t, 'GET', `/tenants/${anId}`);
if (detail.fullName !== 'Nguyễn Văn An') {
  throw new Error(`detail is ${detail.fullName}, expected Nguyễn Văn An`);
}
if (detail.contracts.length !== 2) {
  throw new Error(`An should hold 2 contracts in this building, got ${detail.contracts.length}`);
}
console.log(
  `detail Nguyễn Văn An: ${detail.contracts.map((c) => c.contractNumber).join(', ')}` +
    ` | address ${detail.permanentAddress}`,
);

// A person outside the caller's buildings must not be probeable. There is
// no second building on this lane, so an unknown id stands in for one: the
// server has to answer 404, not 200 with an empty shape.
const unknown = '11111111-2222-3333-4444-555555555555';
const unknownRes = await fetch(`${API}/tenants/${unknown}`, {
  headers: { Authorization: `Bearer ${t}` },
});
if (unknownRes.status !== 404) {
  throw new Error(`GET /tenants/{unknown} -> ${unknownRes.status}, expected 404`);
}
console.log(`unknown tenant id -> ${unknownRes.status}`);

// ---- 4. Screenshots -----------------------------------------------------
const browser = await chromium.launch({
  executablePath: 'C:/Program Files/Google/Chrome/Application/chrome.exe',
  args: ['--ignore-certificate-errors'],
});

async function login(page) {
  await page.goto(`${BASE}/login`);
  await page.fill('input[name="username"], input[type="text"]', USER);
  await page.fill('input[type="password"]', PASS);
  const remember = page.getByLabel(/ghi nhớ|remember/i);
  if (await remember.count()) await remember.first().check();
  await page.getByRole('button', { name: /đăng nhập|login/i }).click();
  await page.waitForURL((url) => !url.pathname.includes('/login'), { timeout: 20000 });
}

for (const viewport of VIEWPORTS) {
  const ctx = await browser.newContext({
    viewport: { width: viewport.width, height: viewport.height },
    ignoreHTTPSErrors: true,
  });
  const page = await ctx.newPage();
  page.on('console', (message) => {
    if (message.type() === 'error') console.log(`  [console] ${message.text()}`);
  });
  page.on('response', (res) => {
    if (res.status() >= 400) console.log(`  [${res.status()}] ${res.url()}`);
  });

  await login(page);

  // (a) The roster.
  await page.goto(`${BASE}/tenants`);
  await page.waitForTimeout(1200);

  const listBody = await page.locator('body').innerText();
  const missing = EXPECTED_PEOPLE.filter((p) => !listBody.includes(p.name));
  if (missing.length) {
    throw new Error(`roster is missing: ${missing.map((p) => p.name).join(', ')}`);
  }
  const checks = {
    notTheContractlessPerson: !listBody.includes('Vũ Thị Hạnh'),
    phones: listBody.includes('0901234501') && listBody.includes('0901234504'),
    locations:
      listBody.includes('101 · Tầng 1 · Ngọc Trang Building') &&
      listBody.includes('201 · Tầng 2 · Ngọc Trang Building'),
    contracts: listBody.includes('HD-2026-101') && listBody.includes('HD-2025-088'),
    statuses: ['Đang hiệu lực', 'Đã ký', 'Đã kết thúc'].every((s) => listBody.includes(s)),
    kpi: /Tổng số khách thuê/.test(listBody),
    notUnavailable: !listBody.includes('chưa được xây dựng trên hệ thống'),
  };
  const failed = Object.entries(checks).filter(([, ok]) => !ok).map(([key]) => key);
  console.log(`  ${viewport.name} roster: ${JSON.stringify(checks)}`);
  if (failed.length) throw new Error(`roster checks failed at ${viewport.name}: ${failed.join(', ')}`);

  const listOverflow = await page.evaluate(() => {
    const doc = document.documentElement;
    const scroller = document.querySelector('table')?.parentElement ?? null;
    return {
      pageScrollsSideways: doc.scrollWidth > doc.clientWidth + 1,
      pageWidth: doc.scrollWidth,
      viewport: doc.clientWidth,
      tableWidth: scroller ? Math.round(scroller.getBoundingClientRect().width) : 0,
      tableScrolls: scroller ? scroller.scrollWidth > scroller.clientWidth + 1 : false,
    };
  });
  console.log(
    `  ${viewport.name} roster layout: page ${listOverflow.pageWidth}/${listOverflow.viewport}` +
      `${listOverflow.pageScrollsSideways ? ' PAGE SCROLLS SIDEWAYS' : ''}` +
      ` | table panel ${listOverflow.tableWidth}` +
      `${listOverflow.tableScrolls ? ' (scrolls inside its panel)' : ''}`,
  );
  if (listOverflow.pageScrollsSideways) {
    throw new Error(`the roster page scrolls sideways at ${viewport.name}`);
  }
  if (listOverflow.tableWidth > 0) {
    const lastColumn = await page.evaluate(() => {
      const scroller = document.querySelector('table')?.parentElement ?? null;
      const cells = document.querySelectorAll('table thead th');
      const last = cells[cells.length - 1];
      if (!scroller || !last) return null;
      const panel = scroller.getBoundingClientRect();
      const box = last.getBoundingClientRect();
      return {
        header: (last.textContent ?? '').trim(),
        clippedBy: Math.round(box.right - panel.right),
        width: Math.round(box.width),
      };
    });
    console.log(
      `  ${viewport.name} last column "${lastColumn.header}" ${lastColumn.width} px,` +
        ` overflow ${lastColumn.clippedBy}`,
    );
    if (lastColumn.clippedBy > 1) {
      throw new Error(
        `at ${viewport.name} the "${lastColumn.header}" column is clipped` +
          ` by ${lastColumn.clippedBy} px`,
      );
    }
  }

  const cards = await page.locator('ul.xl\\:hidden > li').count();
  const tableRows = await page.locator('table tbody tr').count();
  const layouts = await page.evaluate(() => {
    const visible = (el) => !!el && el.getBoundingClientRect().width > 0;
    return {
      cards: visible(document.querySelector('ul.xl\\:hidden')),
      table: visible(document.querySelector('table')),
    };
  });
  console.log(
    `  ${viewport.name} roster: ${tableRows} table rows + ${cards} cards in the DOM` +
      ` | visible: ${layouts.cards ? 'cards' : ''}${layouts.cards && layouts.table ? '+' : ''}${layouts.table ? 'table' : ''}`,
  );
  if (tableRows !== EXPECTED_PEOPLE.length || cards !== EXPECTED_PEOPLE.length) {
    throw new Error(
      `${viewport.name}: expected ${EXPECTED_PEOPLE.length} rows in both layouts,` +
        ` got ${tableRows}/${cards}`,
    );
  }
  if (layouts.cards === layouts.table) {
    throw new Error(
      `${viewport.name}: expected one visible layout, got cards=${layouts.cards} table=${layouts.table}`,
    );
  }
  const expectTable = viewport.width >= 1280;
  if (layouts.table !== expectTable) {
    throw new Error(
      `${viewport.name}: expected the ${expectTable ? 'table' : 'card'} layout`,
    );
  }

  await page.screenshot({ path: `${OUT}/tenants-list-${viewport.name}.png`, fullPage: true });
  console.log(`captured tenants-list-${viewport.name}.png`);

  // (b) "Đã kết thúc" narrows the roster to the one departed tenant.
  await page.getByRole('button', { name: 'Đã kết thúc', exact: true }).click();
  await page.waitForTimeout(500);
  const filteredBody = await page.locator('body').innerText();
  if (!filteredBody.includes('Phạm Thu Dung') || filteredBody.includes('Nguyễn Văn An')) {
    throw new Error(`${viewport.name}: the "Đã kết thúc" filter did not narrow the roster`);
  }
  console.log(`  ${viewport.name} filter "Đã kết thúc" -> only Phạm Thu Dung`);

  await page.getByRole('button', { name: 'Đang thuê', exact: true }).click();
  await page.waitForTimeout(400);
  const rentingBody = await page.locator('body').innerText();
  if (rentingBody.includes('Phạm Thu Dung') || !rentingBody.includes('Nguyễn Văn An')) {
    throw new Error(`${viewport.name}: the "Đang thuê" filter is wrong`);
  }
  console.log(`  ${viewport.name} filter "Đang thuê" -> drops the departed tenant`);

  await page.getByRole('button', { name: 'Tất cả', exact: true }).click();
  await page.waitForTimeout(400);

  // (c) Detail of the multi-room tenant.
  await page.goto(`${BASE}/tenants/${anId}`);
  await page.waitForTimeout(1100);
  const detailBody = await page.locator('body').innerText();
  const detailChecks = {
    name: detailBody.includes('Nguyễn Văn An'),
    phone: detailBody.includes('0901234501'),
    email: detailBody.includes('an.nguyen@example.com'),
    gender: detailBody.includes('Nam'),
    address: detailBody.includes('12 Lê Lợi, Phường Bến Nghé, Quận 1'),
    notes: detailBody.includes('Khách thuê lâu năm'),
    twoContracts: detailBody.includes('HD-2026-101') && detailBody.includes('HD-2026-302'),
    location: detailBody.includes('Phòng 101'),
    // The scope rule has to be stated on screen, otherwise a person who rents
    // elsewhere looks like they rent nowhere.
    scopeNote: detailBody.includes('Chỉ những hợp đồng thuộc tòa nhà tài khoản này được xem'),
  };
  const detailFailed = Object.entries(detailChecks)
    .filter(([, ok]) => !ok)
    .map(([key]) => key);
  console.log(`  ${viewport.name} detail: ${JSON.stringify(detailChecks)}`);
  if (detailFailed.length) {
    throw new Error(`detail checks failed at ${viewport.name}: ${detailFailed.join(', ')}`);
  }

  const detailOverflow = await page.evaluate(() => {
    const doc = document.documentElement;
    return {
      pageScrollsSideways: doc.scrollWidth > doc.clientWidth + 1,
      pageWidth: doc.scrollWidth,
      viewport: doc.clientWidth,
    };
  });
  console.log(
    `  ${viewport.name} detail layout: page ${detailOverflow.pageWidth}/${detailOverflow.viewport}` +
      `${detailOverflow.pageScrollsSideways ? ' PAGE SCROLLS SIDEWAYS' : ''}`,
  );
  if (detailOverflow.pageScrollsSideways) {
    throw new Error(`the tenant detail page scrolls sideways at ${viewport.name}`);
  }

  await page.screenshot({ path: `${OUT}/tenants-detail-${viewport.name}.png`, fullPage: true });
  console.log(`captured tenants-detail-${viewport.name}.png`);

  // (d) Clicking a roster row must reach that person's page.
  await page.goto(`${BASE}/tenants`);
  await page.waitForTimeout(900);
  await page.getByRole('link', { name: 'Phạm Thu Dung' }).first().click();
  await page.waitForURL(/\/tenants\/[0-9a-f-]{36}$/, { timeout: 10000 });
  await page.waitForTimeout(900);
  const clickedBody = await page.locator('body').innerText();
  if (!clickedBody.includes('Phạm Thu Dung') || !clickedBody.includes('HD-2025-088')) {
    throw new Error(`${viewport.name}: clicking a row did not open that tenant`);
  }
  console.log(`  ${viewport.name} row click -> ${new URL(page.url()).pathname}`);

  await ctx.close();
}

await browser.close();
console.log(`\nOPEN: ${BASE}/tenants`);
