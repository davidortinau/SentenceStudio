const { chromium } = require('playwright');

(async () => {
  const browser = await chromium.launch({ headless: true });
  const ctx = await browser.newContext({ ignoreHTTPSErrors: true });
  const page = await ctx.newPage();
  
  // Sign in first
  await page.goto('https://localhost:7071/auth/login', { waitUntil: 'networkidle', timeout: 30000 });
  await page.waitForTimeout(2000);
  await page.locator('#email').fill('');
  await page.locator('#email').pressSequentially('e2etest@sentencestudio.local', { delay: 20 });
  await page.locator('#password').fill('');
  await page.locator('#password').pressSequentially('TestPass123', { delay: 20 });
  await page.waitForTimeout(500);
  await page.locator('button:has-text("Sign In")').click();
  await page.waitForTimeout(5000);
  
  // Go to quiz
  await page.goto('https://localhost:7071/vocab-quiz?resourceIds=c1c343fb-7683-44f3-849c-635aa47d1514&skillId=2a281675-6855-4bf5-a98b-96148c9fa1f2', {
    waitUntil: 'networkidle', timeout: 30000,
  });
  await page.waitForTimeout(8000);
  
  // Get all buttons
  const buttons = await page.locator('button').all();
  console.log('All buttons:');
  for (const b of buttons) {
    const text = (await b.innerText().catch(() => '')).trim();
    const cls = await b.getAttribute('class').catch(() => '');
    if (text) console.log(`  Button: "${text.substring(0,80)}" class: ${cls}`);
  }
  
  // Get the quiz section HTML
  const quizHtml = await page.evaluate(() => {
    const main = document.querySelector('.activity-page-wrapper') || document.querySelector('main');
    return main ? main.innerHTML.substring(0, 5000) : 'NO MAIN FOUND';
  });
  console.log('\nQuiz HTML (first 5000):');
  console.log(quizHtml);
  
  // Also check page text
  const text = await page.innerText('body');
  console.log('\nPage text:');
  console.log(text.substring(0, 800));
  
  await browser.close();
})();
