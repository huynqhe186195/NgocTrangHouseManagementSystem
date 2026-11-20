// P61 local-only invoice design preview capture. It authenticates against the
// disposable roster lane, but the preview itself never calls an invoice API.
import { chromium } from 'file:///C:/Users/Huynq/AppData/Local/npm-cache/_npx/e41f203b7505f1fb/node_modules/playwright/index.mjs';
import { mkdirSync } from 'node:fs';

const BASE = process.env.LANE_BASE ?? 'http://localhost:5273';
const PASS = process.env.LANE_ADMIN_PASSWORD;
const OUT = 'D:/Dev/NgocTrangBuilding/Project/NgocTrangHouseManagementSystem/etc/captures/p61';
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
    const context = await browser.newContext({
      viewport: { width: viewport.width, height: viewport.height },
      ignoreHTTPSErrors: true,
    });
    const page = await context.newPage();
    await login(page);
    await page.goto(`${BASE}/__design/invoices?state=data`);
    await page.waitForTimeout(500);
    const body = await page.locator('body').innerText();
    if (!body.includes('Bản thiết kế UI · Chưa tích hợp API · Dữ liệu minh họa')) {
      console.log(`${viewport.name}: URL=${page.url()} BODY=${body.slice(0, 800)}`);
    }
    assert(body.includes('Bản thiết kế UI · Chưa tích hợp API · Dữ liệu minh họa'), `${viewport.name}: preview banner missing`);
    assert(body.includes('Danh sách hóa đơn'), `${viewport.name}: listing missing`);
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
    await page.screenshot({ path: `${OUT}/invoice-preview-${viewport.name}.png`, fullPage: true });
    console.log(`${viewport.name}: layout=${layout.tableVisible ? 'table' : 'cards'}`);
    await context.close();
  }

  const stateNames = ['loading', 'empty', 'forbidden', 'error'];
  for (const state of stateNames) {
    const context = await browser.newContext({ viewport: { width: 1448, height: 1086 }, ignoreHTTPSErrors: true });
    const page = await context.newPage();
    await login(page);
    await page.goto(`${BASE}/__design/invoices?state=${state}`);
    await page.getByText('Bản thiết kế UI · Chưa tích hợp API · Dữ liệu minh họa').waitFor({ state: 'visible', timeout: 10000 });
    await page.waitForTimeout(300);
    const body = await page.locator('body').innerText();
    assert(body.includes('Bản thiết kế UI · Chưa tích hợp API · Dữ liệu minh họa'), `${state}: banner missing`);
    assert(!body.includes('Danh sách hóa đơn'), `${state}: data listing leaked into state`);
    await page.screenshot({ path: `${OUT}/invoice-preview-${state}-1448x1086.png`, fullPage: true });
    await context.close();
  }

  const context = await browser.newContext({ viewport: { width: 1448, height: 1086 }, ignoreHTTPSErrors: true });
  const page = await context.newPage();
  await login(page);
  await page.goto(`${BASE}/__design/invoices?state=data`);
  await page.getByRole('button', { name: 'Xem chi tiết' }).first().click();
  assert((await page.locator('body').innerText()).includes('Fixture UI cô lập'), 'detail preview missing');
  await page.screenshot({ path: `${OUT}/invoice-preview-detail-1448x1086.png`, fullPage: true });
  const buttonText = await page.locator('button').allTextContents();
  assert(!buttonText.some((text) => /phát hành|thanh toán|hoàn tiền|thu hồi|đối soát/i.test(text)), 'financial mutation control leaked');
  await page.goto(`${BASE}/invoices`);
  await page.waitForTimeout(150);
  assert(!new URL(page.url()).pathname.startsWith('/invoices'), 'production invoice route unexpectedly exists');
  await context.close();
  console.log('P61 invoice design preview capture complete');
} finally {
  await browser.close();
}
