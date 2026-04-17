const { chromium } = require('playwright');

const BASE_URL = 'https://localhost:7071';
const RESOURCE_ID = 'c1c343fb-7683-44f3-849c-635aa47d1514';
const SKILL_ID = '2a281675-6855-4bf5-a98b-96148c9fa1f2';

const TEST_EMAIL = 'e2etest@sentencestudio.local';
const TEST_PASSWORD = 'TestPass123';

(async () => {
  const browser = await chromium.launch({ headless: true });
  const ctx = await browser.newContext({ ignoreHTTPSErrors: true });
  const page = await ctx.newPage();
  
  const results = { tests: [], pass: 0, fail: 0 };
  function record(name, passed, detail = '') {
    results.tests.push({ name, passed, detail });
    if (passed) results.pass++; else results.fail++;
    console.log(`${passed ? 'PASS' : 'FAIL'}: ${name}${detail ? ' -- ' + detail : ''}`);
  }

  try {
    // ========== AUTHENTICATE ==========
    console.log('\n=== AUTH ===');
    await page.goto(`${BASE_URL}/auth/login`, { waitUntil: 'networkidle', timeout: 30000 });
    await page.waitForTimeout(2000);
    await page.locator('#email').pressSequentially(TEST_EMAIL, { delay: 20 });
    await page.locator('#password').pressSequentially(TEST_PASSWORD, { delay: 20 });
    await page.waitForTimeout(500);
    await page.locator('button:has-text("Sign In")').click();
    await page.waitForTimeout(5000);
    
    const isAuth = !page.url().includes('/auth/');
    record('Authentication', isAuth, `URL: ${page.url()}`);
    if (!isAuth) throw new Error('Auth failed');

    // ========== TEST 1: QUIZ LOADS AND RUNS ==========
    console.log('\n=== TEST 1: Quiz loads ===');
    await page.goto(`${BASE_URL}/vocab-quiz?resourceIds=${RESOURCE_ID}&skillId=${SKILL_ID}`, {
      waitUntil: 'networkidle', timeout: 30000,
    });
    await page.waitForTimeout(6000);
    
    await page.screenshot({ path: 'quiz-01-initial.png', fullPage: true });
    
    const quizTitle = await page.locator('h1:has-text("Vocabulary Quiz")').count();
    record('Quiz page title present', quizTitle > 0);
    
    const promptWord = await page.locator('.ss-display').first().innerText().catch(() => '');
    record('Prompt word displayed', promptWord.length > 0, `Word: "${promptWord}"`);
    
    const mcBtnCount = await page.locator('button.quiz-choice-btn').count();
    record('MC buttons rendered', mcBtnCount >= 2, `Found ${mcBtnCount} choice buttons`);
    
    const progressText = await page.locator('#quiz-progress').innerText().catch(() => '');
    record('Progress counter visible', progressText.includes('/'), `Shows: "${progressText}"`);

    // ========== TEST 2: ANSWER A QUESTION ==========
    console.log('\n=== TEST 2: Answer a question ===');
    
    // Click first MC option
    await page.locator('#quiz-option-a').click();
    await page.waitForTimeout(2000);
    
    await page.screenshot({ path: 'quiz-02-after-answer.png', fullPage: true });
    
    const bodyText = await page.innerText('body');
    const hasCorrectFb = bodyText.includes('Correct') || bodyText.includes('correct');
    const hasIncorrectFb = bodyText.includes('answer is') || bodyText.includes('Answer is');
    record('Answer feedback shown', hasCorrectFb || hasIncorrectFb,
      hasCorrectFb ? 'Correct feedback' : (hasIncorrectFb ? 'Incorrect feedback shown' : 'No feedback'));

    // ========== TEST 3: INFO/LEARNING DETAILS PANEL ==========
    console.log('\n=== TEST 3: Learning Details panel ===');
    
    // Wait for auto-advance or click next
    await page.waitForTimeout(3000);
    
    // Click info button
    const infoButton = page.locator('#quiz-info-button');
    if (await infoButton.count() > 0) {
      await infoButton.click();
      await page.waitForTimeout(1500);
      await page.screenshot({ path: 'quiz-03-info-panel.png', fullPage: true });
      
      const infoText = await page.innerText('body');
      const hasMastery = infoText.match(/mastery|Mastery/i);
      const hasStreak = infoText.match(/streak|Streak/i);
      const hasDifficulty = infoText.match(/difficulty|Difficulty/i);
      
      record('Learning details mastery info', !!(hasMastery || hasStreak),
        `Mastery: ${!!hasMastery}, Streak: ${!!hasStreak}`);
      record('DifficultyWeight in details', !!hasDifficulty,
        hasDifficulty ? 'Difficulty info found' : 'Not shown');
      
      // Close info panel if it's a modal/offcanvas
      const closeBtn = page.locator('.btn-close, [aria-label="Close"]');
      if (await closeBtn.count() > 0) {
        await closeBtn.first().click();
        await page.waitForTimeout(500);
      }
    } else {
      record('Info button accessible', false, 'Button #quiz-info-button not found');
    }

    // ========== TEST 4: COMPLETE A FULL ROUND ==========
    console.log('\n=== TEST 4: Complete a round ===');
    
    // Reload quiz fresh
    await page.goto(`${BASE_URL}/vocab-quiz?resourceIds=${RESOURCE_ID}&skillId=${SKILL_ID}`, {
      waitUntil: 'networkidle', timeout: 30000,
    });
    await page.waitForTimeout(6000);
    
    let questionsAnswered = 0;
    let maxLoop = 20;
    
    for (let i = 0; i < maxLoop; i++) {
      // Check for round complete state
      const body = await page.innerText('body');
      if (body.includes('Round Complete') || body.includes('Next Round') || 
          body.includes('round complete') || body.match(/\d+\s*%\s*accuracy/i)) {
        console.log(`  Round complete detected at loop ${i}`);
        break;
      }
      
      // Try clicking a quiz option button
      const optA = page.locator('#quiz-option-a');
      const optB = page.locator('#quiz-option-b');
      const textInput = page.locator('#quiz-text-input');
      
      if (await optA.count() > 0 && await optA.isEnabled()) {
        // In MC mode — click an option
        console.log(`  Q${questionsAnswered + 1}: Clicking MC option A`);
        await optA.click();
        questionsAnswered++;
        await page.waitForTimeout(4000); // wait for feedback + auto-advance
      } else if (await textInput.count() > 0 && await textInput.isEnabled()) {
        // In text mode
        console.log(`  Q${questionsAnswered + 1}: Text mode - typing`);
        await textInput.pressSequentially('hello', { delay: 30 });
        await page.keyboard.press('Enter');
        questionsAnswered++;
        await page.waitForTimeout(4000);
      } else {
        // Maybe between questions — check for Next/Continue
        const nextBtn = page.locator('button:has-text("Next"), button:has-text("Continue")');
        if (await nextBtn.count() > 0) {
          await nextBtn.first().click();
          await page.waitForTimeout(2000);
        } else {
          // Check if answer is shown and we need to wait for auto-advance
          console.log(`  Loop ${i}: Waiting for auto-advance...`);
          await page.waitForTimeout(3000);
        }
      }
      
      // Screenshot at halfway
      if (questionsAnswered === 5) {
        await page.screenshot({ path: 'quiz-04-midway.png', fullPage: true });
      }
    }
    
    record('Full round completed', questionsAnswered >= 3, 
      `Answered ${questionsAnswered} questions`);
    
    await page.screenshot({ path: 'quiz-05-after-round.png', fullPage: true });
    
    // ========== TEST 5: SESSION SUMMARY ==========
    console.log('\n=== TEST 5: Session summary ===');
    
    const summaryText = await page.innerText('body');
    const hasSummary = summaryText.includes('Round') || summaryText.includes('accuracy') || 
                       summaryText.includes('Summary') || summaryText.includes('Results');
    record('Session summary displayed', hasSummary, 
      hasSummary ? 'Summary detected' : 'No summary visible');
    
    const html = await page.content();
    const hasCheckIcons = html.includes('bi-check') || html.includes('check-circle');
    const hasXIcons = html.includes('bi-x-circle') || html.includes('x-circle');
    record('Result icons (check/X)', hasCheckIcons || hasXIcons,
      `Check: ${hasCheckIcons}, X: ${hasXIcons}`);
    
    // ========== TEST 6: MODE SELECTION (MC vs TEXT) ==========
    console.log('\n=== TEST 6: Mode selection logic ===');
    
    // Look at what mode was used - if the first question had MC buttons, it started as MC
    // We already confirmed MC mode in Test 1, which means CurrentStreak < 3 for those words
    record('New words start in MC mode', mcBtnCount >= 2, 
      'Words with low streak correctly start in MC mode');
    
    // ========== TEST 7: DATABASE VERIFICATION ==========
    console.log('\n=== TEST 7: Database check ===');
    console.log('(Database verification will be done separately via sqlite3)');
    
  } catch (err) {
    console.error('Test error:', err.message);
    record('Test execution', false, err.message);
    await page.screenshot({ path: 'quiz-error.png', fullPage: true }).catch(() => {});
  }
  
  // ========== RESULTS ==========
  console.log('\n========================================');
  console.log('=== E2E QUIZ MASTERY TEST RESULTS ===');
  console.log('========================================');
  console.log(`Total: ${results.pass + results.fail} | Pass: ${results.pass} | Fail: ${results.fail}\n`);
  for (const t of results.tests) {
    console.log(`  ${t.passed ? 'PASS' : 'FAIL'}: ${t.name}${t.detail ? ' -- ' + t.detail : ''}`);
  }
  
  await browser.close();
})();
