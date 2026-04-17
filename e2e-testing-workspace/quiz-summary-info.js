const { chromium } = require('playwright');

const BASE_URL = 'https://localhost:7071';
const RESOURCE_ID = 'c1c343fb-7683-44f3-849c-635aa47d1514';
const SKILL_ID = '2a281675-6855-4bf5-a98b-96148c9fa1f2';

(async () => {
  const browser = await chromium.launch({ headless: true });
  const ctx = await browser.newContext({ ignoreHTTPSErrors: true });
  const page = await ctx.newPage();

  // Authenticate
  await page.goto(`${BASE_URL}/auth/login`, { waitUntil: 'networkidle', timeout: 30000 });
  await page.waitForTimeout(2000);
  await page.locator('#email').pressSequentially('e2etest@sentencestudio.local', { delay: 20 });
  await page.locator('#password').pressSequentially('TestPass123', { delay: 20 });
  await page.waitForTimeout(500);
  await page.locator('button:has-text("Sign In")').click();
  await page.waitForTimeout(5000);

  // Go to quiz
  await page.goto(`${BASE_URL}/vocab-quiz?resourceIds=${RESOURCE_ID}&skillId=${SKILL_ID}`, {
    waitUntil: 'networkidle', timeout: 30000,
  });
  await page.waitForTimeout(6000);

  // Answer 10 questions
  console.log('=== Answering 10 questions ===');
  for (let i = 0; i < 10; i++) {
    const optA = page.locator('#quiz-option-a');
    const textIn = page.locator('#quiz-text-input');
    
    if (await optA.count() > 0 && await optA.isEnabled()) {
      await optA.click();
    } else if (await textIn.count() > 0 && await textIn.isEnabled()) {
      await textIn.pressSequentially('test', { delay: 20 });
      await page.keyboard.press('Enter');
    }
    console.log(`  Q${i+1} answered`);
    await page.waitForTimeout(4000);
  }
  
  // Now we should be on summary screen
  await page.waitForTimeout(2000);
  console.log('\n=== SUMMARY SCREEN ===');
  
  const bodyText = await page.innerText('body');
  console.log('Summary text:');
  console.log(bodyText.substring(0, 1500));
  
  // Get summary HTML
  const summaryHTML = await page.evaluate(() => {
    const main = document.querySelector('.activity-page-wrapper') || document.querySelector('main');
    return main ? main.innerHTML : 'NO MAIN';
  });
  
  await page.screenshot({ path: 'summary-screen.png', fullPage: true });
  console.log('\nSummary HTML (first 3000):');
  console.log(summaryHTML.substring(0, 3000));
  
  // === INFO PANEL TEST ===
  console.log('\n\n=== INFO PANEL TEST ===');
  
  // Start new round or navigate fresh
  await page.goto(`${BASE_URL}/vocab-quiz?resourceIds=${RESOURCE_ID}&skillId=${SKILL_ID}`, {
    waitUntil: 'networkidle', timeout: 30000,
  });
  await page.waitForTimeout(6000);
  
  // Click info button
  const infoBtn = page.locator('#quiz-info-button');
  if (await infoBtn.count() > 0) {
    await infoBtn.click();
    await page.waitForTimeout(2000);
    
    await page.screenshot({ path: 'info-panel-open.png', fullPage: true });
    
    // Get panel content
    const panelText = await page.innerText('body');
    const panelLines = panelText.split('\n');
    
    console.log('Info panel full text:');
    let inPanel = false;
    for (const line of panelLines) {
      const l = line.trim();
      if (l.match(/learning detail|word detail|info/i)) inPanel = true;
      if (inPanel && l) console.log(`  ${l}`);
      if (l.match(/close|done|back/i) && inPanel) break;
    }
    
    // Print ALL lines with relevant keywords
    console.log('\nRelevant lines from info panel:');
    for (const line of panelLines) {
      const l = line.trim();
      if (l && l.match(/mastery|streak|difficulty|weight|score|progress|attempt|production|recognition|mode|phase|mc|text/i)) {
        console.log(`  "${l}"`);
      }
    }
  }
  
  await browser.close();
})();
