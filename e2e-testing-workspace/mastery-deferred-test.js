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

  console.log('=== DEFERRED vs POST-ADVANCE MASTERY TEST ===\n');

  // Step 1: Read the FIRST prompt word
  const word1 = await page.locator('.ss-display').first().innerText().catch(() => '');
  console.log(`Q1 word: "${word1}"`);

  // Step 2: Open info panel, capture mastery BEFORE answering
  await page.locator('#quiz-info-button').click();
  await page.waitForTimeout(1500);
  const beforeBody = await page.innerText('body');
  
  // Extract mastery info more carefully
  const extractMastery = (text) => {
    const lines = text.split('\n').map(l => l.trim()).filter(Boolean);
    const result = {};
    for (let i = 0; i < lines.length; i++) {
      if (lines[i] === 'MasteryScore' && i+1 < lines.length) result.mastery = lines[i+1];
      if (lines[i] === 'CurrentStreak' && i+1 < lines.length) result.streak = lines[i+1];
      if (lines[i] === 'TotalAttempts' && i+1 < lines.length) result.attempts = lines[i+1];
      if (lines[i] === 'CorrectAttempts' && i+1 < lines.length) result.correct = lines[i+1];
    }
    return result;
  };
  
  const before = extractMastery(beforeBody);
  console.log(`BEFORE answer: mastery=${before.mastery}, streak=${before.streak}, attempts=${before.attempts}, correct=${before.correct}`);

  // Close info panel
  await page.locator('button:has(.bi-x-lg)').first().click().catch(async () => {
    await page.keyboard.press('Escape');
  });
  await page.waitForTimeout(500);

  // Step 3: Answer the question
  const optA = page.locator('#quiz-option-a');
  if (await optA.count() > 0) {
    await optA.click();
    console.log('Answered via MC option A');
  }
  await page.waitForTimeout(1000);

  // Step 4: Check mastery during FEEDBACK phase (before auto-advance)
  const infoBtnFeedback = page.locator('#quiz-info-button');
  if (await infoBtnFeedback.count() > 0 && await infoBtnFeedback.isVisible()) {
    await infoBtnFeedback.click();
    await page.waitForTimeout(1500);
    const duringFeedback = extractMastery(await page.innerText('body'));
    console.log(`DURING FEEDBACK: mastery=${duringFeedback.mastery}, streak=${duringFeedback.streak}, attempts=${duringFeedback.attempts}`);
    
    // Close
    await page.locator('button:has(.bi-x-lg)').first().click().catch(async () => {
      await page.keyboard.press('Escape');
    });
    await page.waitForTimeout(500);
  }

  // Step 5: Wait for auto-advance to next question (or click next)
  console.log('Waiting for auto-advance...');
  const nextBtn = page.locator('button:has-text("Next")');
  if (await nextBtn.count() > 0) {
    await nextBtn.click();
  } else {
    await page.waitForTimeout(5000); // Wait for auto-advance timer
  }
  await page.waitForTimeout(2000);

  // Step 6: Check if we're on the second question now
  const progress = await page.locator('#quiz-progress').innerText().catch(() => 'N/A');
  const word2 = await page.locator('.ss-display').first().innerText().catch(() => '');
  console.log(`\nAfter advance: progress=${progress}, word="${word2}"`);

  // Step 7: Now answer Q2, then check Q1's mastery in the database
  console.log('\n=== DATABASE VERIFICATION ===');

  await browser.close();
  
  // Check DB for the word we just answered
  const { execSync } = require('child_process');
  try {
    const dbCmd = `docker exec -e PGPASSWORD='WsgZDs5sKWGFRC~SEMx5za' 9aaafcde1504 psql -U postgres -d sentencestudio -t -A -c "
      SELECT vw.\"TargetLanguageText\", vp.\"CurrentStreak\", vp.\"MasteryScore\", vp.\"TotalAttempts\", vp.\"CorrectAttempts\"
      FROM \"VocabularyProgress\" vp
      JOIN \"VocabularyWord\" vw ON vp.\"VocabularyWordId\" = vw.\"Id\"
      WHERE vw.\"NativeLanguageText\" ILIKE '%${word1.replace(/'/g, "''")}%'
      ORDER BY vp.\"LastAttemptDate\" DESC
      LIMIT 5;
    "`;
    const dbResult = execSync(dbCmd, { encoding: 'utf8' });
    console.log(`DB records for "${word1}":`);
    console.log(dbResult || '(no records)');
  } catch (e) {
    console.log('DB check failed:', e.message);
  }
  
  // Also check recent attempts
  try {
    const attCmd = `docker exec -e PGPASSWORD='WsgZDs5sKWGFRC~SEMx5za' 9aaafcde1504 psql -U postgres -d sentencestudio -t -A -c "
      SELECT vw.\"NativeLanguageText\", va.\"WasCorrect\", va.\"InputMode\", va.\"DifficultyWeight\", va.\"AttemptDate\"
      FROM \"VocabularyAttempt\" va
      JOIN \"VocabularyWord\" vw ON va.\"VocabularyWordId\" = vw.\"Id\"
      ORDER BY va.\"AttemptDate\" DESC
      LIMIT 5;
    "`;
    const attResult = execSync(attCmd, { encoding: 'utf8' });
    console.log('Recent attempts:');
    console.log(attResult);
  } catch (e) {
    console.log('Attempt check failed:', e.message);
  }
})();
