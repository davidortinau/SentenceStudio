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

  // === IMMEDIATE MASTERY UPDATE TEST ===
  console.log('=== IMMEDIATE MASTERY UPDATE TEST ===');
  
  // Step 1: Read the prompt word
  const promptWord = await page.locator('.ss-display').first().innerText().catch(() => '');
  console.log(`Prompt word: "${promptWord}"`);
  
  // Step 2: Open info panel BEFORE answering
  const infoBtn = page.locator('#quiz-info-button');
  await infoBtn.click();
  await page.waitForTimeout(1500);
  
  // Extract mastery BEFORE
  const beforeText = await page.innerText('body');
  const masteryBefore = beforeText.match(/MasteryScore[\s\n]*([\d.]+%?)/)?.[1] || 'N/A';
  const streakBefore = beforeText.match(/CurrentStreak[\s\n]*([\d.]+)/)?.[1] || 'N/A';
  console.log(`BEFORE answer: mastery=${masteryBefore}, streak=${streakBefore}`);
  await page.screenshot({ path: 'mastery-before.png', fullPage: true });
  
  // Step 3: Close info panel
  const closeBtn = page.locator('button:has(.bi-x-lg), .btn-close, .offcanvas .btn-close');
  if (await closeBtn.count() > 0) {
    await closeBtn.first().click();
    await page.waitForTimeout(500);
  } else {
    // Click somewhere else to dismiss
    await page.locator('.activity-page-wrapper').click({ position: { x: 10, y: 10 } });
    await page.waitForTimeout(500);
  }
  
  // Step 4: Answer the question (click first option)
  const optA = page.locator('#quiz-option-a');
  const textIn = page.locator('#quiz-text-input');
  
  if (await optA.count() > 0 && await optA.isEnabled()) {
    console.log('Clicking MC option A...');
    await optA.click();
  } else if (await textIn.count() > 0) {
    console.log('Typing text answer...');
    await textIn.pressSequentially('test', { delay: 20 });
    await page.keyboard.press('Enter');
  }
  
  await page.waitForTimeout(2000);
  
  // Check the feedback
  const feedbackText = await page.innerText('body');
  const wasCorrect = feedbackText.includes('Correct') || feedbackText.includes('correct');
  console.log(`Answer was: ${wasCorrect ? 'CORRECT' : 'INCORRECT'}`);
  await page.screenshot({ path: 'mastery-after-answer.png', fullPage: true });
  
  // Step 5: While still on this word (before auto-advance), open info panel again
  // Info button should still be visible during feedback
  if (await infoBtn.count() > 0) {
    await infoBtn.click();
    await page.waitForTimeout(1500);
    
    const afterText = await page.innerText('body');
    const masteryAfter = afterText.match(/MasteryScore[\s\n]*([\d.]+%?)/)?.[1] || 'N/A';
    const streakAfter = afterText.match(/CurrentStreak[\s\n]*([\d.]+)/)?.[1] || 'N/A';
    console.log(`AFTER answer: mastery=${masteryAfter}, streak=${streakAfter}`);
    await page.screenshot({ path: 'mastery-after.png', fullPage: true });
    
    // Verify mastery updated
    if (wasCorrect) {
      if (masteryAfter !== masteryBefore) {
        console.log('PASS: Mastery IMMEDIATELY updated after correct answer!');
      } else {
        console.log('FAIL: Mastery did NOT update immediately (still stale)');
      }
    } else {
      console.log('INFO: Answer was incorrect, checking mastery behavior...');
      if (masteryAfter !== masteryBefore) {
        console.log('PASS: Mastery updated after incorrect answer');
      } else {
        console.log('INFO: Mastery stayed same (may be expected for incorrect)');
      }
    }
    
    // Print all mastery-related lines
    console.log('\nAll mastery-related info:');
    for (const line of afterText.split('\n')) {
      const l = line.trim();
      if (l && l.match(/mastery|streak|attempt|correct|production|mode|why|difficulty/i)) {
        console.log(`  ${l}`);
      }
    }
  } else {
    console.log('Info button not available during feedback phase');
  }
  
  await browser.close();
})();
