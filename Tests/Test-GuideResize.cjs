// Run with Node and Playwright available; pass an external evidence directory.
const { chromium } = require('playwright');
const fs = require('fs');
const path = require('path');
const { pathToFileURL } = require('url');

(async () => {
  const output = process.argv[2];
  if (!output) throw new Error('Pass an external evidence directory.');
  fs.mkdirSync(output, { recursive: true });
  const browser = await chromium.launch({ channel: 'msedge', headless: true });
  try {
    const page = await browser.newPage({ viewport: { width: 1440, height: 900 } });
    await page.goto(pathToFileURL(path.resolve(__dirname, '../shot-landing/help.html')).href);
    await page.evaluate(() => document.fonts.ready);
    const index = await page.locator('.acc-body').evaluateAll(bodies => bodies.reduce(
      (best, body, i) => body.scrollHeight > bodies[best].scrollHeight ? i : best, 0));
    await page.locator('.acc-head').nth(index).click();
    await page.waitForTimeout(400);
    const measure = () => page.locator('.acc-body').nth(index).evaluate(body => ({
      client: body.clientHeight, scroll: body.scrollHeight, max: body.style.maxHeight,
      inert: body.inert, expanded: body.previousElementSibling.getAttribute('aria-expanded')
    }));
    const wide = await measure();
    await page.setViewportSize({ width: 390, height: 844 });
    await page.waitForTimeout(400);
    const narrow = await measure();
    await page.screenshot({ path: path.join(output, 'guide-narrow.png') });
    await page.locator('.acc-body-inner').nth(index).evaluate(inner => {
      inner.querySelectorAll('p,code,td,th').forEach(el => el.style.fontSize = '20px');
    });
    await page.waitForTimeout(400);
    const font = await measure();
    await page.locator('.acc-body-inner').nth(index).evaluate(inner => {
      const paragraph = document.createElement('p');
      paragraph.textContent = 'Longer translated text for dynamic layout coverage. '.repeat(12);
      inner.append(paragraph);
      document.dispatchEvent(new Event('kshot-languagechange'));
    });
    await page.waitForTimeout(400);
    const language = await measure();
    await page.locator('.acc-head').nth(index).click();
    await page.waitForTimeout(400);
    const closed = await measure();
    const results = { index, wide, narrow, font, language, closed };
    fs.writeFileSync(path.join(output, 'guide-resize.json'), JSON.stringify(results, null, 2));
    console.log(JSON.stringify(results));
    if ([wide, narrow, font, language].some(m => m.scroll > m.client + 2 || m.inert || m.expanded !== 'true')
      || !closed.inert || closed.expanded !== 'false' || closed.client !== 0) {
      throw new Error('Expanded guide content clips, or a closed panel remains accessible.');
    }
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
