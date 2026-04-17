const { chromium } = require('playwright');

const BASE_URL = 'https://localhost:7071';
const RESOURCE_ID = 'c1c343fb-7683-44f3-849c-635aa47d1514'; // Int1.1 Weather
const SKILL_ID = '2a281675-6855-4bf5-a98b-96148c9fa1f2'; // Int 1.1

const TEST_EMAIL = 'e2etest@sentencestudio.local';
const TEST_PASSWORD = 'TestPass123';
const TEST_DISPLAY = 'E2E Tester';

(async () => {
  const browser = await chromium.launch({ headless: true });
  const context = await browser.newContext({ ignoreHTTPSErrors: true });
  const page = await context.newPage();
  
  const results = { tests: [], pass: 0, fail: 0 };
  
  function record(name, passed, detail = '') {
    results.tests.push({ name, passed, detail });
    if (passed) results.pass++; else results.fail++;
    console.log(`${passed ? 'PASS' : 'FAIL'}: ${name}${detail ? ' — ' + detail : ''}`);
  }

  // Helper: type slowly for Blazor server-side binding
  async function blazorType(selector, text) {
    const loc = page.locator(selector);
    if (await loc.count() === 0) return false;
    await loc.click();
    await loc.fill('');
    await loc.pressSequentially(text, { delay: 30 });
    return true;
  }

  try {
    // ========== STEP 0: Sign in ==========
    console.log('\n=== STEP 0: Authentication ===');
    
    // Try to register first (idempotent - will fail if already exists)
    await page.goto(`${BASE_URL}/auth/register`, { waitUntil: 'networkidle', timeout: 30000 });
    await page.waitForTimeout(2000);
    
    let pageText = await page.innerText('body').catch(() => '');
    if (pageText.includes('Create Account')) {
      console.log('On register page, creating test account...');
      await blazorType('#displayName', TEST_DISPLAY);
      await blazorType('#email', TEST_EMAIL);
      await blazorType('#password', TEST_PASSWORD);
      await blazorType('#confirmPassword', TEST_PASSWORD);
      await page.waitForTimeout(500);
      
      const regBtn = await page.$('button:has-text("Create Account")');
      if (regBtn) {
        await regBtn.click();
        await page.waitForTimeout(3000);
        console.log('Registration submitted');
      }
    }
    
    // Now sign in
    await page.goto(`${BASE_URL}/auth/login`, { waitUntil: 'networkidle', timeout: 30000 });
    await page.waitForTimeout(2000);
    
    pageText = await page.innerText('body').catch(() => '');
    if (pageText.includes('Sign In')) {
      console.log('On login page, signing in...');
      await blazorType('#email', TEST_EMAIL);
      await blazorType('#password', TEST_PASSWORD);
      await page.waitForTimeout(500);
      
      const signInBtn = await page.$('button:has-text("Sign In")');
      if (signInBtn) {
        await signInBtn.click();
        await page.waitForTimeout(5000);
      }
    }
    
    // Check if we're now authenticated
    pageText = await page.innerText('body').catch(() => '');
    const url = page.url();
    console.log(`After login, URL: ${url}`);
    
    const isAuthenticated = !url.includes('/auth/login') && !url.includes('/auth/register');
    record('Authentication', isAuthenticated, 
      isAuthenticated ? `Landed on ${url}` : `Still on login page: ${pageText.substring(0, 100)}`);
    
    if (!isAuthenticated) {
      // Try with existing user dave@ortinau.com
      console.log('First attempt failed. Checking page state...');
      await page.screenshot({ path: 'auth-failed.png', fullPage: true });
      pageText = await page.innerText('body').catch(() => '');
      console.log('Page text:', pageText.substring(0, 300));
      throw new Error('Cannot authenticate - stopping tests');
    }
    
    // Take screenshot of authenticated state
    await page.screenshot({ path: 'quiz-authenticated.png', fullPage: true });
    
    // ========== TEST 1: Quiz loads and runs ==========
    console.log('\n=== TEST 1: Quiz loads and runs ===');
    
    await page.goto(`${BASE_URL}/vocab-quiz?resourceIds=${RESOURCE_ID}&skillId=${SKILL_ID}`, {
      waitUntil: 'networkidle', timeout: 30000,
    });
    await page.waitForTimeout(5000); // extra time for quiz to load words from API
    
    let content = await page.content();
    pageText = await page.innerText('body').catch(() => '');
    
    console.log('Quiz page text (first 500):', pageText.substring(0, 500));
    await page.screenshot({ path: 'quiz-initial-state.png', fullPage: true });
    
    const hasQuizContent = pageText.includes('Vocabulary Quiz') || 
                           content.includes('quiz-prompt') ||
                           content.includes('mc-option') ||
                           content.includes('answer-input') ||
                           content.includes('vocab-quiz');
    
    record('Quiz page loads', hasQuizContent,
      hasQuizContent ? 'Quiz content detected' : `Page content: ${pageText.substring(0, 200)}`);
    
    // Check for MC buttons or text input
    const mcButtons = await page.$$('button.mc-option, .mc-option, [class*="mc-option"]').catch(() => []);
    const textInput = await page.$('input.answer-input, input[type="text"], .answer-input').catch(() => null);
    
    if (mcButtons.length > 0) {
      record('Quiz mode detected', true, `MC mode with ${mcButtons.length} options`);
    } else if (textInput) {
      record('Quiz mode detected', true, 'Text entry mode');
    } else {
      // Maybe we need a profile selection first
      record('Quiz mode detected', false, 'No MC buttons or text input found');
      console.log('Full page content:');
      console.log(pageText.substring(0, 1000));
    }
    
    // ========== TEST 2: Answer questions and check feedback ==========
    console.log('\n=== TEST 2: Answer questions + feedback ===');
    
    let questionsAnswered = 0;
    let correctAnswers = [];
    let incorrectAnswers = [];
    const maxAttempts = 15;
    
    for (let i = 0; i < maxAttempts; i++) {
      pageText = await page.innerText('body').catch(() => '');
      
      // Check for round complete / summary 
      if (pageText.includes('Round Complete') || pageText.includes('round complete') ||
          pageText.match(/\d+\s*\/\s*\d+.*complete/i) ||
          (pageText.includes('Next Round') && questionsAnswered > 0)) {
        console.log(`Round ended after ${questionsAnswered} questions`);
        break;
      }
      
      // Try MC options
      const opts = await page.$$('button.mc-option, .mc-option').catch(() => []);
      const txtIn = await page.$('input.answer-input, input[type="text"]').catch(() => null);
      
      if (opts.length >= 2) {
        // Read options and click one
        const optTexts = [];
        for (const o of opts) {
          optTexts.push(await o.innerText().catch(() => ''));
        }
        console.log(`  Q${questionsAnswered + 1} (MC): Options: ${optTexts.join(' | ')}`);
        
        // Click first option
        await opts[0].click();
        await page.waitForTimeout(3000);
        
        pageText = await page.innerText('body').catch(() => '');
        if (pageText.includes('Correct') || pageText.includes('correct')) {
          correctAnswers.push(optTexts[0]);
          console.log('    -> Correct!');
        } else {
          incorrectAnswers.push(optTexts[0]);
          console.log('    -> Incorrect');
        }
        questionsAnswered++;
      } else if (txtIn) {
        console.log(`  Q${questionsAnswered + 1} (Text): Typing placeholder answer`);
        await txtIn.fill('');
        await txtIn.pressSequentially('hello', { delay: 50 });
        
        const checkBtn = await page.$('button:has-text("Check"), button[type="submit"], .btn-check-answer').catch(() => null);
        if (checkBtn) {
          await checkBtn.click();
        } else {
          await page.keyboard.press('Enter');
        }
        await page.waitForTimeout(3000);
        questionsAnswered++;
      } else {
        // Look for Next button  
        const nextBtn = await page.$('button:has-text("Next"), button:has-text("Continue")').catch(() => null);
        if (nextBtn) {
          await nextBtn.click();
          await page.waitForTimeout(2000);
        } else {
          // Check for override button or other elements
          await page.waitForTimeout(2000);
        }
      }
      
      // Take screenshot every few questions
      if (questionsAnswered > 0 && questionsAnswered % 3 === 0) {
        await page.screenshot({ path: `quiz-q${questionsAnswered}.png`, fullPage: true });
      }
    }
    
    record('Completed quiz questions', questionsAnswered >= 1,
      `Answered ${questionsAnswered} (${correctAnswers.length} correct, ${incorrectAnswers.length} incorrect)`);
    
    await page.screenshot({ path: 'quiz-after-round.png', fullPage: true });
    
    // ========== TEST 3: Session summary ==========
    console.log('\n=== TEST 3: Session summary ===');
    pageText = await page.innerText('body').catch(() => '');
    content = await page.content();
    
    const hasSummary = pageText.includes('Round') || pageText.includes('Summary') || 
                       pageText.includes('accuracy') || pageText.includes('Results') ||
                       content.includes('round-summary') || content.includes('session-summary');
    
    record('Session summary displayed', hasSummary,
      hasSummary ? 'Summary screen detected' : 'No summary found');
    
    const hasResultIcons = content.includes('bi-check') || content.includes('bi-x') || 
                           content.includes('check-circle') || content.includes('x-circle');
    record('Summary has result indicators', hasResultIcons,
      hasResultIcons ? 'Check/X icons found' : 'No result icons');
    
    // ========== TEST 4: Check Learning Details panel (info button) ==========
    console.log('\n=== TEST 4: Learning Details panel ===');
    
    // Navigate to quiz again to test info panel
    await page.goto(`${BASE_URL}/vocab-quiz?resourceIds=${RESOURCE_ID}&skillId=${SKILL_ID}`, {
      waitUntil: 'networkidle', timeout: 30000,
    });
    await page.waitForTimeout(5000);
    
    // Look for info button
    const infoBtn = await page.$('button .bi-info-circle, .bi-info-circle, [title*="info"], [title*="Info"], [aria-label*="info"]').catch(() => null);
    const infoBtnAlt = await page.$('button:has(.bi-info-circle)').catch(() => null);
    
    if (infoBtn || infoBtnAlt) {
      const btn = infoBtnAlt || infoBtn;
      console.log('Found info button, clicking...');
      await btn.click();
      await page.waitForTimeout(1500);
      await page.screenshot({ path: 'quiz-learning-details.png', fullPage: true });
      
      pageText = await page.innerText('body').catch(() => '');
      const hasMastery = pageText.match(/mastery|Mastery|streak|Streak|progress|Progress|accuracy|Accuracy/i);
      record('Learning details shows mastery data', !!hasMastery,
        hasMastery ? `Found: ${hasMastery[0]}` : 'No mastery info in panel');
      
      // Check for DifficultyWeight
      const hasDifficulty = pageText.match(/difficulty|weight|DifficultyWeight/i);
      record('DifficultyWeight visible', !!hasDifficulty,
        hasDifficulty ? `Found: ${hasDifficulty[0]}` : 'DifficultyWeight not shown in panel');
    } else {
      console.log('No info button found. Checking for inline progress display...');
      pageText = await page.innerText('body').catch(() => '');
      const hasInlineProgress = pageText.match(/mastery|streak|score/i);
      record('Learning details accessible', !!hasInlineProgress,
        hasInlineProgress ? 'Inline progress found' : 'No info button or inline progress');
    }
    
  } catch (err) {
    console.error('Test error:', err.message);
    record('Test execution', false, err.message);
    await page.screenshot({ path: 'quiz-error.png', fullPage: true }).catch(() => {});
  }
  
  // ========== RESULTS ==========
  console.log('\n========================================');
  console.log('=== E2E QUIZ MASTERY TEST RESULTS ===');
  console.log('========================================');
  console.log(`Total: ${results.pass + results.fail} | Pass: ${results.pass} | Fail: ${results.fail}`);
  for (const t of results.tests) {
    console.log(`  ${t.passed ? 'PASS' : 'FAIL'}: ${t.name}${t.detail ? ' — ' + t.detail : ''}`);
  }
  
  await browser.close();
})();
