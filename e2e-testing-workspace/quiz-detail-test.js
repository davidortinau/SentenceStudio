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
  console.log('Authenticated at:', page.url());

  // Go to quiz
  await page.goto(`${BASE_URL}/vocab-quiz?resourceIds=${RESOURCE_ID}&skillId=${SKILL_ID}`, {
    waitUntil: 'networkidle', timeout: 30000,
  });
  await page.waitForTimeout(6000);

  // === ROUND COMPLETION TEST ===
  console.log('\n=== ROUND COMPLETION TEST ===');
  
  let questionsAnswered = 0;
  let roundComplete = false;
  
  for (let i = 0; i < 25; i++) {
    // Check progress counter
    const progress = await page.locator('#quiz-progress').innerText().catch(() => '');
    const correctBadge = await page.locator('#quiz-correct-count').innerText().catch(() => '');
    const bodyText = await page.innerText('body');
    
    console.log(`  Step ${i}: progress="${progress}" correct="${correctBadge}"`);
    
    // Check for round complete
    if (bodyText.includes('Round Complete') || bodyText.includes('Next Round') ||
        bodyText.includes('round-summary') || bodyText.includes('Start Next Round')) {
      roundComplete = true;
      console.log('  >>> ROUND COMPLETE DETECTED!');
      await page.screenshot({ path: 'round-summary.png', fullPage: true });
      break;
    }
    
    // Try MC option or text input
    const optA = page.locator('#quiz-option-a');
    const textIn = page.locator('#quiz-text-input');
    
    if (await optA.count() > 0 && await optA.isEnabled()) {
      await optA.click();
      questionsAnswered++;
      await page.waitForTimeout(4000);
    } else if (await textIn.count() > 0 && await textIn.isEnabled()) {
      await textIn.pressSequentially('test', { delay: 30 });
      await page.keyboard.press('Enter');
      questionsAnswered++;
      await page.waitForTimeout(4000);
    } else {
      // Check for any other buttons
      const btns = await page.locator('button').allInnerTexts();
      console.log(`  Available buttons: ${btns.filter(b => b.trim()).join(', ')}`);
      await page.waitForTimeout(2000);
    }
    
    // Capture state every 5 questions
    if (questionsAnswered > 0 && questionsAnswered % 5 === 0) {
      await page.screenshot({ path: `round-q${questionsAnswered}.png`, fullPage: true });
    }
  }
  
  console.log(`\nQuestions answered: ${questionsAnswered}`);
  console.log(`Round complete: ${roundComplete}`);
  await page.screenshot({ path: 'round-final-state.png', fullPage: true });
  
  // Get final page state
  const finalText = await page.innerText('body');
  console.log('\nFinal page text (first 500):');
  console.log(finalText.substring(0, 500));
  
  // === INFO PANEL DETAIL TEST ===
  console.log('\n=== INFO PANEL CONTENTS ===');
  
  // Navigate fresh
  await page.goto(`${BASE_URL}/vocab-quiz?resourceIds=${RESOURCE_ID}&skillId=${SKILL_ID}`, {
    waitUntil: 'networkidle', timeout: 30000,
  });
  await page.waitForTimeout(6000);
  
  const infoBtn = page.locator('#quiz-info-button');
  if (await infoBtn.count() > 0) {
    await infoBtn.click();
    await page.waitForTimeout(2000);
    
    // Get the full info panel content
    const infoHTML = await page.evaluate(() => {
      // Look for offcanvas, modal, or panel
      const panel = document.querySelector('.offcanvas-body, .modal-body, .info-panel, [class*="learning-detail"]');
      return panel ? panel.innerHTML : document.querySelector('body').innerHTML.substring(0, 5000);
    });
    
    console.log('Info panel HTML (first 2000):');
    console.log(infoHTML.substring(0, 2000));
    
    const panelText = await page.innerText('body');
    console.log('\nInfo panel text:');
    // Extract just the mastery-related text
    const lines = panelText.split('\n').filter(l => 
      l.match(/mastery|streak|difficulty|weight|score|progress|attempt|production|recognition|phase|mode|type/i)
    );
    for (const l of lines) {
      console.log(`  ${l.trim()}`);
    }
    
    await page.screenshot({ path: 'info-panel-detail.png', fullPage: true });
  }
  
  await browser.close();
})();
