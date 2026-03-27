const { chromium } = require('playwright');

(async () => {
  const browser = await chromium.launch();
  const context = await browser.newContext({
    viewport: { width: 393, height: 852 },
    deviceScaleFactor: 3,
    isMobile: true,
    ignoreHTTPSErrors: true,
  });
  const page = await context.newPage();

  // Login via the server-side form POST
  const resp = await page.request.post('https://localhost:7071/account-action/Login', {
    form: { Email: 'dave@ortinau.com', Password: 'Testing123!', RememberMe: 'false' },
    maxRedirects: 0,
  });
  console.log('Login response:', resp.status(), resp.headers()['location'] || '');

  // Get cookies from the response and set them
  const cookies = resp.headersArray().filter(h => h.name.toLowerCase() === 'set-cookie');
  console.log('Cookies received:', cookies.length);

  // Navigate using the context (cookies should be set via the request)
  await page.goto('https://localhost:7071/vocab-matching');
  await page.waitForLoadState('networkidle');
  await page.waitForTimeout(8000);

  console.log('URL:', page.url());
  await page.screenshot({ path: '/tmp/matching-viewport.png', fullPage: false });
  await page.screenshot({ path: '/tmp/matching-fullpage.png', fullPage: true });
  console.log('Screenshots saved');

  const info = await page.evaluate(() => {
    const grid = document.querySelector('.matching-tile-grid');
    if (!grid) return { error: 'No grid', title: document.title, url: location.href, snippet: document.body.innerText.substring(0, 200) };
    const rect = grid.getBoundingClientRect();
    const tiles = grid.querySelectorAll('.matching-tile');
    const last = tiles[tiles.length-1]?.getBoundingClientRect();
    const wrapper = document.querySelector('.matching-page-wrapper');
    const wRect = wrapper?.getBoundingClientRect();
    return {
      wrapperH: Math.round(wRect?.height || 0),
      gridTop: Math.round(rect.top),
      gridBot: Math.round(rect.bottom),
      gridH: Math.round(rect.height),
      vh: window.innerHeight,
      tiles: tiles.length,
      lastBot: last ? Math.round(last.bottom) : null,
      offScreen: last ? last.bottom > window.innerHeight : null,
      scrollH: document.documentElement.scrollHeight,
      overflows: document.documentElement.scrollHeight > window.innerHeight + 5,
    };
  });
  console.log(JSON.stringify(info, null, 2));

  await browser.close();
})();
