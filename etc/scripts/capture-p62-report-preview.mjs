// P62 local-only report design preview capture. The preview never calls a report API.
import { chromium } from 'file:///C:/Users/Huynq/AppData/Local/npm-cache/_npx/e41f203b7505f1fb/node_modules/playwright/index.mjs';
import { mkdirSync } from 'node:fs';

const BASE = process.env.LANE_BASE ?? 'http://localhost:5273';
const PASS = process.env.LANE_ADMIN_PASSWORD;
const OUT = 'D:/Dev/NgocTrangBuilding/Project/NgocTrangHouseManagementSystem/etc/captures/p62';
const VIEWPORTS = [
  { name: '1448x1086', width: 1448, height: 1086 },
  { name: '1280x800', width: 1280, height: 800 },
  { name: '1024x768', width: 1024, height: 768 },
  { name: '390x844', width: 390, height: 844 },
];
if (!PASS) throw new Error('LANE_ADMIN_PASSWORD is not set');
mkdirSync(OUT, { recursive: true });

function assert(condition, message) {
  if (!condition) throw new Error(message);
}

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

try {
  for (const viewport of VIEWPORTS) {
    const context = await browser.newContext({ viewport: { width: viewport.width, height: viewport.height }, ignoreHTTPSErrors: true });
    const page = await context.newPage();
    const requests = [];
    page.on('request', (request) => requests.push(request.url()));
    await login(page);
    await page.goto(`${BASE}/__design/reports?state=data`);
    await page.getByText('Bản thiết kế UI · Chưa tích hợp API · Dữ liệu minh họa').waitFor({ state: 'visible', timeout: 10000 });
    const body = await page.locator('body').innerText();
    assert(body.includes('Tổng quan theo tòa nhà'), `${viewport.name}: report listing missing`);
    assert(body.includes('Tòa mẫu A'), `${viewport.name}: fixture missing`);
    assert(!requests.some((url) => /\/api\//i.test(url) && /report|payment|utility|invoice/i.test(url)), `${viewport.name}: preview made a report/financial backend request`);
    const layout = await page.evaluate(() => {
      const visible = (element) => !!element && element.getBoundingClientRect().width > 0;
      const root = document.documentElement;
      return {
        pageScrollsSideways: root.scrollWidth > root.clientWidth + 1,
        tableVisible: visible(document.querySelector('table')),
        cardsVisible: visible(document.querySelector('ul.xl\\:hidden')),
      };
    });
    assert(!layout.pageScrollsSideways, `${viewport.name}: page scrolls sideways`);
    assert(layout.tableVisible !== layout.cardsVisible, `${viewport.name}: both layouts visible`);
    assert(layout.tableVisible === viewport.width >= 1280, `${viewport.name}: wrong breakpoint layout`);
    await page.screenshot({ path: `${OUT}/report-preview-${viewport.name}.png`, fullPage: true });
    console.log(`${viewport.name}: layout=${layout.tableVisible ? 'table' : 'cards'}`);
    await context.close();
  }

  for (const state of ['loading', 'empty', 'forbidden', 'error']) {
    const context = await browser.newContext({ viewport: { width: 1448, height: 1086 }, ignoreHTTPSErrors: true });
    const page = await context.newPage();
    await login(page);
    await page.goto(`${BASE}/__design/reports?state=${state}`);
    await page.getByText('Bản thiết kế UI · Chưa tích hợp API · Dữ liệu minh họa').waitFor({ state: 'visible', timeout: 10000 });
    const body = await page.locator('body').innerText();
    assert(!body.includes('Tổng quan theo tòa nhà'), `${state}: data listing leaked into state`);
    await page.screenshot({ path: `${OUT}/report-preview-${state}-1448x1086.png`, fullPage: true });
    await context.close();
  }

  const context = await browser.newContext({ viewport: { width: 1448, height: 1086 }, ignoreHTTPSErrors: true });
  const page = await context.newPage();
  await login(page);
  await page.goto(`${BASE}/__design/reports?state=data`);
  await page.getByRole('button', { name: 'Xem tóm tắt' }).first().click();
  assert((await page.locator('body').innerText()).includes('Fixture UI cô lập'), 'detail preview missing');
  await page.screenshot({ path: `${OUT}/report-preview-detail-1448x1086.png`, fullPage: true });
  const buttonText = await page.locator('button').allTextContents();
  assert(!buttonText.some((text) => /(^|\s)(thu tiền|phát hành|đối soát|chỉnh sửa)(\s|$)/i.test(text) || /^xuất(\s|$)/i.test(text)), `mutation control leaked: ${buttonText.join(' | ')}`);
  await page.goto(`${BASE}/reports`);
  await page.waitForTimeout(150);
  assert(!new URL(page.url()).pathname.startsWith('/reports'), 'production report route unexpectedly exists');
  await context.close();
  console.log('P62 report design preview capture complete');
} finally {
  await browser.close();
}
