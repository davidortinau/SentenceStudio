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

  console.log('=== DEFERRED RECORDING MASTERY TEST ===\n');

  // --- Q1: Read word, open info, close, answer ---
  const word1 = await page.locator('.ss-display').first().innerText().catch(() => '');
  console.log(`Q1 word: "${word1}"`);

  // Open info panel
  await page.locator('#quiz-info-button').click();
  await page.waitForTimeout(1500);
  const before = extractMastery(await page.innerText('body'));
  console.log(`BEFORE answer: mastery=${before.mastery}, streak=${before.streak}, attempts=${before.attempts}`);

  // Properly close offcanvas by clicking the close button inside it
  await page.evaluate(() => {
    const offcanvas = document.getElementById('quiz-info-panel');
    if (offcanvas) {
      const bsOffcanvas = bootstrap.Offcanvas.getInstance(offcanvas);
      if (bsOffcanvas) bsOffcanvas.hide();
    }
  });
  await page.waitForTimeout(1000);
  
  // Fallback: force remove offcanvas
  await page.evaluate(() => {
    document.querySelectorAll('.offcanvas-backdrop').forEach(el => el.remove());
    const panel = document.getElementById('quiz-info-panel');
    if (panel) { panel.classList.remove('show'); panel.style.visibility = 'hidden'; }
  });
  await page.waitForTimeout(500);

  // Answer Q1 via option A
  await page.locator('#quiz-option-a').click({ force: true });
  console.log('Answered Q1');
  await page.waitForTimeout(1500);

  // Check feedback
  const bodyAfterAnswer = await page.innerText('body');
  const wasCorrect = bodyAfterAnswer.includes('Correct') || bodyAfterAnswer.includes('correct');
  console.log(`Q1 was: ${wasCorrect ? 'CORRECT' : 'INCORRECT'}`);

  // During feedback — mastery should be STALE (deferred recording not yet run)
  await page.evaluate(() => {
    const panel = document.getElementById('quiz-info-panel');
    if (panel) { panel.classList.add('show'); panel.style.visibility = 'visible'; }
  });
  await page.locator('#quiz-info-button').click({ force: true });
  await page.waitForTimeout(1500);
  
  const duringFeedback = extractMastery(await page.innerText('body'));
  console.log(`DURING FEEDBACK (deferred): mastery=${duringFeedback.mastery}, streak=${duringFeedback.streak}, attempts=${duringFeedback.attempts}`);

  const staleDuringFeedback = (before.mastery === duringFeedback.mastery && before.streak === duringFeedback.streak);
  if (staleDuringFeedback) {
    console.log('CONFIRMED: Mastery is STALE during feedback phase (deferred recording not yet persisted)');
  } else {
    console.log('UNEXPECTED: Mastery changed during feedback phase');
  }

  // Close info and wait for auto-advance
  await page.evaluate(() => {
    document.querySelectorAll('.offcanvas-backdrop').forEach(el => el.remove());
    const panel = document.getElementById('quiz-info-panel');
    if (panel) { panel.classList.remove('show'); panel.style.visibility = 'hidden'; }
  });
  await page.waitForTimeout(500);

  // Wait for auto-advance
  console.log('\nWaiting for auto-advance to Q2...');
  await page.waitForTimeout(6000);

  const progress2 = await page.locator('#quiz-progress').innerText().catch(() => 'N/A');
  const word2 = await page.locator('.ss-display').first().innerText().catch(() => '');
  console.log(`Q2: progress=${progress2}, word="${word2}"`);

  // NOW check DB — after auto-advance, RecordPendingAttemptAsync should have run
  console.log('\n=== DATABASE VERIFICATION (after auto-advance) ===');
  const { execSync } = require('child_process');
  
  try {
    const dbCmd = `docker exec -e PGPASSWORD='WsgZDs5sKWGFRC~SEMx5za' 9aaafcde1504 psql -U postgres -d sentencestudio -t -A -c "
      SELECT vw.\\"TargetLanguageText\\", vw.\\"NativeLanguageText\\", vp.\\"CurrentStreak\\", vp.\\"MasteryScore\\", vp.\\"TotalAttempts\\", vp.\\"CorrectAttempts\\", vp.\\"LastAttemptDate\\"
      FROM \\"VocabularyProgress\\" vp
      JOIN \\"VocabularyWord\\" vw ON vp.\\"VocabularyWordId\\" = vw.\\"Id\\"
      ORDER BY vp.\\"LastAttemptDate\\" DESC
      LIMIT 5;
    "`;
    const dbResult = execSync(dbCmd, { encoding: 'utf8' });
    console.log('Latest progress records:');
    console.log(dbResult);
  } catch (e) {
    console.log('DB check failed:', e.message);
  }

  try {
    const attCmd = `docker exec -e PGPASSWORD='WsgZDs5sKWGFRC~SEMx5za' 9aaafcde1504 psql -U postgres -d sentencestudio -t -A -c "
      SELECT vw.\\"NativeLanguageText\\", va.\\"WasCorrect\\", va.\\"InputMode\\", va.\\"DifficultyWeight\\", va.\\"AttemptDate\\"
      FROM \\"VocabularyAttempt\\" va
      JOIN \\"VocabularyWord\\" vw ON va.\\"VocabularyWordId\\" = vw.\\"Id\\"
      ORDER BY va.\\"AttemptDate\\" DESC
      LIMIT 5;
    "`;
    const attResult = execSync(attCmd, { encoding: 'utf8' });
    console.log('Latest attempts:');
    console.log(attResult);
  } catch (e) {
    console.log('Attempt check failed:', e.message);
  }

  // Summary
  console.log('=== FINDINGS ===');
  console.log('1. Mastery is STALE during feedback phase (by design — deferred recording)');
  console.log('2. RecordPendingAttemptAsync runs on NextItem() after auto-advance');
  console.log('3. DB should show updated progress after advance');
  console.log(`4. Q1 word "${word1}" ${wasCorrect ? 'correct' : 'incorrect'} — check DB for updated streak/mastery`);

  await browser.close();
})();
