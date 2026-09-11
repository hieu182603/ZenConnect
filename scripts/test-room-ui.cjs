// Runs only against the isolated server started by test-room-actions.py --browser.
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'playwright');
const assert = require('node:assert/strict');
const path = require('node:path');

(async () => {
  const base = process.env.TEST_BASE_URL;
  assert.match(base || '', /^http:\/\/127\.0\.0\.1:\d+$/);
  const key = process.env.TEST_ROOM_KEY;
  const output = process.env.TEST_OUTPUT_DIR;
  const browser = await chromium.launch({ headless: true,
    ...(process.env.TEST_CHROMIUM_PATH ? { executablePath: process.env.TEST_CHROMIUM_PATH } : {}) });
  const context = await browser.newContext({ viewport: { width: 1440, height: 1000 }, acceptDownloads: true });
  const page = await context.newPage();
  const errors = [];
  page.on('pageerror', error => errors.push(error.message));
  const headers = { 'X-Screen-Review-Key': key };
  const messages = async () => (await context.request.get(base + '/api/messages', { headers })).json();
  const waitEnabled = selector => page.waitForFunction(selector => !document.querySelector(selector).disabled, selector);
  try {
    await page.goto(base);
    await page.locator('#access-key').fill(key);
    await page.locator('#toggle-key').click();
    assert.equal(await page.locator('#access-key').getAttribute('type'), 'text');
    assert.equal(await page.locator('#toggle-key').getAttribute('aria-pressed'), 'true');
    await page.screenshot({ path: path.join(output, 'entry-desktop.png'), fullPage: true });
    for (const width of [1440, 900, 390, 320]) {
      await page.setViewportSize({ width, height: 1000 });
      const field = await page.locator('.password-field').boundingBox();
      const input = await page.locator('#access-key').boundingBox();
      const button = await page.locator('#toggle-key').boundingBox();
      assert(button.x >= input.x + input.width - 1, 'Key and toggle overlap');
      assert(button.x + button.width <= field.x + field.width, 'Toggle exceeds field');
      assert(Math.abs(button.y + button.height / 2 - (field.y + field.height / 2)) < 2, 'Toggle not centered');
      assert(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), 'Horizontal overflow');
      if (width === 390) await page.screenshot({ path: path.join(output, 'entry-mobile.png'), fullPage: true });
    }
    await page.setViewportSize({ width: 1440, height: 1000 });
    await page.locator('#toggle-key').click();
    assert.equal(await page.locator('#access-key').getAttribute('type'), 'password');
    await page.locator('#connect-button').click();
    await page.locator('#room-screen').waitFor({ state: 'visible' });
    await waitEnabled('#send-button');
    await page.locator('#message-mode').selectOption('choice');
    await page.locator('#message').fill('không đúng định dạng');
    await page.locator('#send-button').click();
    assert.match(await page.locator('#message-status').innerText(), /1 A \| 2 B/);
    assert.equal((await messages()).length, 0);
    await page.locator('#message').fill('1 A | 2 C');
    await page.screenshot({ path: path.join(output, 'room-desktop.png'), fullPage: true });
    for (const width of [390, 320]) {
      await page.setViewportSize({ width, height: 844 });
      const heading = await page.locator('.filmstrip-heading').boundingBox();
      const thumbnails = await page.locator('#thumbnail-list').boundingBox();
      const buttons = await page.locator('.collection-actions').boundingBox();
      const imageStage = await page.locator('#image-stage').boundingBox();
      assert(buttons.y + buttons.height <= imageStage.y, 'Collection actions are not above the image');
      assert(buttons.y + buttons.height <= thumbnails.y, 'Collection actions overlap thumbnails');
      assert(buttons.x + buttons.width <= width, 'Collection actions overflow');
      assert(thumbnails.y >= heading.y + heading.height, 'Filmstrip overlap');
      assert(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth));
      if (width === 390) await page.screenshot({ path: path.join(output, 'room-mobile.png'), fullPage: true });
    }
    await page.setViewportSize({ width: 1440, height: 1000 });
    let sent = 0;
    await page.route('**/api/messages', async route => {
      if (route.request().method() !== 'POST') return route.continue();
      sent++;
      await new Promise(resolve => setTimeout(resolve, 400));
      await route.continue();
    });
    await waitEnabled('#send-button');
    // Two synchronously submitted events must still issue exactly one request.
    await page.locator('#message-form').evaluate(form => {
      form.dispatchEvent(new Event('submit', { cancelable: true }));
      form.dispatchEvent(new Event('submit', { cancelable: true }));
    });
    await waitEnabled('#send-button');
    assert.equal(sent, 1);
    assert.equal((await messages())[0].text, 'Đáp án trắc nghiệm\n1. A | 2. C');
    assert.equal((await messages())[0].screenshotId, null);
    // A single numbered answer is valid in the explicit answer mode.
    await page.locator('#message').fill('4 B');
    await page.locator('#send-button').click();
    await waitEnabled('#send-button');
    assert.equal((await messages()).at(-1).text, 'Đáp án trắc nghiệm\n4. B');
    await page.locator('#message-mode').selectOption('text');
    await page.locator('#message').fill('5 D | 6 A');
    await page.locator('#send-button').click();
    await waitEnabled('#send-button');
    assert.equal((await messages()).at(-1).text, 'Đáp án trắc nghiệm\n5. D | 6. A');
    assert.equal((await messages()).at(-1).screenshotId, null);
    await page.locator('#message').fill('Phản hồi thường');
    await page.locator('#send-button').click();
    await waitEnabled('#send-button');
    assert.equal((await messages()).at(-1).text, 'Phản hồi thường');
    await waitEnabled('#export-button');
    const downloadPromise = page.waitForEvent('download');
    await page.locator('#export-button').click();
    const download = await downloadPromise;
    assert.match(download.suggestedFilename(), /^de-zenconnect-\d{8}-\d{6}\.zip$/);
    await download.saveAs(path.join(output, 'download.zip'));
    assert.equal(await download.failure(), null);
    await waitEnabled('#export-button');
    // A failed export must report the server error without downloading anything.
    await page.route('**/api/screenshots/export', route => route.fulfill({ status: 409, contentType: 'application/json', body: JSON.stringify({ error: 'Ảnh bị thiếu' }) }));
    let extraDownload = false;
    page.once('download', () => { extraDownload = true; });
    await page.locator('#export-button').click();
    await page.waitForFunction(() => document.querySelector('#status').textContent === 'Ảnh bị thiếu');
    assert.equal(extraDownload, false);
    await page.unroute('**/api/screenshots/export');
    await waitEnabled('#delete-all-button');
    page.once('dialog', dialog => dialog.dismiss());
    await page.locator('#delete-all-button').click();
    assert.equal(await page.locator('.thumbnail-button').count(), 2);
    page.once('dialog', dialog => dialog.accept());
    await page.locator('#delete-all-button').click();
    await page.waitForFunction(() => document.querySelectorAll('.thumbnail-button').length === 0);
    assert.equal((await messages()).length, 3);
    assert(await page.locator('#delete-all-button').isDisabled());
    assert(await page.locator('#export-button').isDisabled());
    assert.equal(await page.locator('#send-button').isDisabled(), false);
    await page.locator('#leave-button').click();
    assert.equal(await page.locator('#access-key').getAttribute('type'), 'password');
    assert.equal(await page.locator('#toggle-key').getAttribute('aria-pressed'), 'false');
    assert.deepEqual(errors, []);
    console.log('PASS: responsive key control, room-level multi-answer parsing, normal-chat auto-detection, duplicate-send guard, ZIP download, export errors, cancel/confirm delete and key reset');
  } finally {
    await browser.close();
  }
})().catch(error => { console.error(error); process.exitCode = 1; });
