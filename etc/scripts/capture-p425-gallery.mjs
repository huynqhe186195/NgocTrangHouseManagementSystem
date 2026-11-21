import { chromium } from 'file:///C:/Users/Huynq/AppData/Local/npm-cache/_npx/e41f203b7505f1fb/node_modules/playwright/index.mjs';
import { mkdirSync } from 'node:fs';

const BASE = process.env.LANE_BASE ?? 'http://localhost:5273';
const OUT = 'D:/Dev/NgocTrangBuilding/Project/NgocTrangHouseManagementSystem/etc/captures/p425';
const VIEWPORTS = [
  { name: '1448x1086', width: 1448, height: 1086 },
  { name: '1280x800', width: 1280, height: 800 },
  { name: '1024x768', width: 1024, height: 768 },
];
mkdirSync(OUT, { recursive: true });

function assert(condition, message) {
  if (!condition) throw new Error(message);
}

async function screenshotPreview(page, path) {
  const width = page.viewportSize()?.width ?? 1448;
  await page.screenshot({ path, fullPage: width >= 1024 });
}

async function inspectPreview(page, url, fileName, expectedText) {
  const before = page.url();
  await page.goto(`${BASE}${url}`);
  try {
    await page.getByText('Bản thiết kế UI · Chưa tích hợp API · Dữ liệu minh họa').waitFor({ state: 'visible', timeout: 15000 });
  } catch (error) {
    await page.screenshot({ path: `${OUT}/${fileName.replace(/\.png$/, '')}-failure.png`, fullPage: true });
    console.error(`Preview load failed at ${page.url()}: ${await page.locator('body').innerText()}`);
    throw error;
  }
  const body = await page.locator('body').innerText();
  assert(body.includes(expectedText), `${url}: expected text missing`);
  assert(body.includes('Không có thao tác tài chính'), `${url}: boundary copy missing`);
  assert(!page.url().includes('/login'), `${url}: redirected to login`);
  await screenshotPreview(page, `${OUT}/${fileName}`);
  return { before, after: page.url() };
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
    requests.length = 0;
    const screens = [
      ['owner-staff', '07-thu-chi', 'Quản lý thu chi'],
      ['owner-staff', '08-dat-phong', 'Đặt phòng'],
      ['owner-staff', '09-phan-quyen', 'Danh sách người dùng vận hành'],
      ['owner-staff', '10-hop-dong', 'Thông tin hợp đồng'],
      ['owner-staff', '11-dien-nuoc', 'Bảng ghi chỉ số kỳ 11/2026'],
      ['tenant', 'TEN-01', 'Phòng minh họa đang chờ bạn'],
      ['tenant', 'TEN-02', 'PHÒNG ĐANG THUÊ'],
      ['tenant', 'TEN-03', 'Thông tin hợp đồng'],
      ['tenant', 'TEN-04', 'Danh sách hóa đơn'],
      ['tenant', 'TEN-05', 'Chi tiết các khoản'],
      ['tenant', 'TEN-06', 'Điện năng tiêu thụ'],
      ['tenant', 'TEN-07', 'Quy trình xác minh'],
      ['tenant', 'TEN-08', 'Tạo yêu cầu trả phòng'],
      ['tenant', 'TEN-09', 'Phòng phù hợp với bạn'],
      ['tenant', 'TEN-10', 'Trung tâm hỗ trợ'],
    ];

    for (const [group, screen, expectedText] of screens) {
      requests.length = 0;
      await inspectPreview(
        page,
        `/__design/figma?group=${group}&screen=${screen}&state=data`,
        `${group}-${screen}-${viewport.name}.png`,
        expectedText,
      );
      const layout = await page.evaluate(() => ({
        sideways: document.documentElement.scrollWidth > document.documentElement.clientWidth + 1,
      }));
      assert(!layout.sideways, `${group}/${screen}/${viewport.name}: horizontal overflow`);
      assert(!requests.some((url) => /:\d+\/api\//i.test(url)), `${group}/${screen}/${viewport.name}: preview called an API: ${requests.join(' | ')}`);
    }
    console.log(`${viewport.name}: PASS 15 desktop frames overflow=false api=false`);
    await context.close();
  }

  const context = await browser.newContext({ viewport: { width: 1448, height: 1086 }, ignoreHTTPSErrors: true });
  const page = await context.newPage();
  await inspectPreview(page, '/__design/figma?state=forbidden', 'state-forbidden-1448x1086.png', 'Máy chủ chưa mở quyền cho flow này (403).');
  await inspectPreview(page, '/__design/figma?state=error', 'state-error-1448x1086.png', 'Không tải được dữ liệu trong preview.');
  await page.goto(`${BASE}/payments`);
  await page.waitForTimeout(200);
  assert(!page.url().includes('/payments'), 'production payments route unexpectedly exists');
  assert(!(await page.locator('body').innerText()).includes('Bản thiết kế UI · Chưa tích hợp API'), 'fixture leaked into production route');
  await context.close();
  console.log('P425 gallery capture complete');
} finally {
  await browser.close();
}
