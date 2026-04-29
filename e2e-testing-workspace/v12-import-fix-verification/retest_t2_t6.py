"""Retest Test 2 (Sentences) and Test 6 (Words) with UNIQUE content."""
from playwright.sync_api import sync_playwright
import time, subprocess, os

EVIDENCE = os.path.dirname(os.path.abspath(__file__))
BASE = 'https://localhost:7071'

# UNIQUE content for Test 2 (Sentences)
SENTENCE_CONTENT = """오늘 날씨가 정말 좋아서 공원에서 산책했어요.|Today the weather was really nice so I took a walk in the park.
서울에서 부산까지 KTX로 두 시간 반 걸려요.|It takes two and a half hours from Seoul to Busan by KTX.
주말에 친구들과 같이 영화를 봤는데 정말 재미있었어요.|I watched a movie with friends on the weekend and it was really fun."""

# UNIQUE content for Test 6 (Words)
WORD_CONTENT = "산책,walk/stroll\n공원,park\n주말,weekend\n영화관,movie theater\n기차역,train station"

def db(sql):
    r = subprocess.run(
        ['docker', 'exec', 'db-84833ad0', 'env',
         'PGPASSWORD=WsgZDs5sKWGFRC~SEMx5za',
         'psql', '-U', 'dbadmin', '-d', 'sentencestudio', '-t', '-A', '-c', sql],
        capture_output=True, text=True)
    return r.stdout.strip()

def counts():
    c = {}
    for line in db('SELECT "LexicalUnitType", COUNT(*) FROM "VocabularyWord" GROUP BY "LexicalUnitType" ORDER BY "LexicalUnitType"').split('\n'):
        if '|' in line:
            parts = line.split('|')
            try: c[int(parts[0])] = int(parts[1])
            except: pass
    return c

def total():
    return int(db('SELECT COUNT(*) FROM "VocabularyWord"') or '0')

with sync_playwright() as p:
    browser = p.chromium.launch(headless=True)
    ctx = browser.new_context(ignore_https_errors=True, viewport={'width': 1280, 'height': 1000})
    page = ctx.new_page()
    
    # Login
    page.goto(f'{BASE}/auth/login', wait_until='networkidle', timeout=30000)
    page.wait_for_timeout(3000)
    page.locator('#email').fill('testuser-ko@test.local')
    page.locator('#password').fill('Test1234!')
    page.wait_for_timeout(500)
    for _i in range(30):
        btn = page.query_selector('button.btn-primary.w-100')
        if btn and btn.get_attribute('disabled') is None:
            btn.click()
            break
        page.wait_for_timeout(500)
    page.wait_for_timeout(5000)
    
    baseline = counts()
    print(f"Baseline: {baseline} total={total()}")
    
    # ===== TEST 2 RETEST: Sentences with UNIQUE content =====
    print("\n=== TEST 2 RETEST: Sentences (unique content) ===")
    pre_c = counts()
    pre_t = total()
    
    page.goto(f'{BASE}/import-content', wait_until='networkidle', timeout=30000)
    page.wait_for_timeout(2000)
    
    page.locator('textarea').first.fill(SENTENCE_CONTENT)
    selects = page.locator('select').all()
    selects[0].select_option(label='Sentences')
    page.wait_for_timeout(300)
    selects[1].select_option(label='파이프 (|)')
    
    page.locator('button:has-text("미리보기")').click()
    print("  Clicked Preview...")
    
    # Wait for harvest checkboxes
    for _ in range(30):
        page.wait_for_timeout(2000)
        if page.locator('#harvestWords').count() > 0 and page.locator('table tbody tr').count() > 0:
            break
    page.wait_for_timeout(5000)
    
    # Verify harvest defaults
    for cb_id in ['#harvestSentences', '#harvestWords']:
        cb = page.locator(cb_id)
        if cb.count() > 0:
            print(f"  {cb_id}: checked={cb.is_checked()}")
    
    page.screenshot(path=os.path.join(EVIDENCE, 't2r-01-preview.png'), full_page=True)
    
    # Fill resource title
    title_input = page.locator('input[placeholder*="예:"]')
    if title_input.count() > 0:
        title_input.first.fill("QA-T2-Sentences-Retest")
        print("  Filled resource title")
    
    # Click commit
    page.locator('button:has-text("가져오기")').click()
    print("  Clicked commit...")
    page.wait_for_timeout(15000)
    page.screenshot(path=os.path.join(EVIDENCE, 't2r-02-after-commit.png'), full_page=True)
    
    post_c = counts()
    post_t = total()
    delta = post_t - pre_t
    print(f"  Before: {pre_c} total={pre_t}")
    print(f"  After:  {post_c} total={post_t}")
    print(f"  Delta: +{delta}")
    
    # Check for type=3 entries
    sent3 = db('SELECT "TargetLanguageTerm", "LexicalUnitType" FROM "VocabularyWord" WHERE "LexicalUnitType" = 3 ORDER BY "CreatedAt" DESC LIMIT 10')
    print(f"  Sentence (type=3) entries:")
    for line in sent3.split('\n'):
        if line.strip():
            print(f"    {line}")
    
    if delta > 0:
        # Check if sentences got type=3
        new_sents = db('''SELECT "TargetLanguageTerm", "LexicalUnitType" FROM "VocabularyWord" WHERE "TargetLanguageTerm" LIKE '%날씨%' OR "TargetLanguageTerm" LIKE '%부산%' OR "TargetLanguageTerm" LIKE '%영화를 봤%' ORDER BY "LexicalUnitType"''')
        print(f"  Entries matching test content:")
        for line in new_sents.split('\n'):
            if line.strip():
                print(f"    {line}")
        print("  TEST 2 RETEST: PASS" if delta > 0 else "  TEST 2 RETEST: FAIL")
    else:
        print("  TEST 2 RETEST: FAIL - 0 new entries")
    
    # ===== TEST 6 RETEST: Words with UNIQUE content =====
    print("\n=== TEST 6 RETEST: Words-only (unique content) ===")
    pre_c = counts()
    pre_t = total()
    
    page.goto(f'{BASE}/import-content', wait_until='networkidle', timeout=30000)
    page.wait_for_timeout(2000)
    
    page.locator('textarea').first.fill(WORD_CONTENT)
    selects = page.locator('select').all()
    selects[0].select_option(label='어휘')
    page.wait_for_timeout(300)
    selects[1].select_option(label='쉼표')
    
    page.locator('button:has-text("미리보기")').click()
    print("  Clicked Preview...")
    
    for _ in range(30):
        page.wait_for_timeout(2000)
        if page.locator('#harvestWords').count() > 0 and page.locator('table tbody tr').count() > 0:
            break
    page.wait_for_timeout(5000)
    
    for cb_id in ['#harvestPhrases', '#harvestWords']:
        cb = page.locator(cb_id)
        if cb.count() > 0:
            print(f"  {cb_id}: checked={cb.is_checked()}")
    
    page.screenshot(path=os.path.join(EVIDENCE, 't6r-01-preview.png'), full_page=True)
    
    title_input = page.locator('input[placeholder*="예:"]')
    if title_input.count() > 0:
        title_input.first.fill("QA-T6-Words-Retest")
    
    page.locator('button:has-text("가져오기")').click()
    print("  Clicked commit...")
    page.wait_for_timeout(15000)
    page.screenshot(path=os.path.join(EVIDENCE, 't6r-02-after-commit.png'), full_page=True)
    
    post_c = counts()
    post_t = total()
    delta = post_t - pre_t
    print(f"  Before: {pre_c} total={pre_t}")
    print(f"  After:  {post_c} total={post_t}")
    print(f"  Delta: +{delta}")
    
    if delta > 0:
        new_words = db('''SELECT "TargetLanguageTerm", "LexicalUnitType" FROM "VocabularyWord" WHERE "TargetLanguageTerm" IN ('산책','공원','주말','영화관','기차역') ORDER BY "LexicalUnitType"''')
        non_word = any('|2' in l or '|3' in l for l in new_words.split('\n'))
        print(f"  New word entries: {new_words}")
        print(f"  TEST 6 RETEST: {'FAIL - non-word types found' if non_word else 'PASS - only word entries'}")
    else:
        print("  TEST 6 RETEST: FAIL - 0 new entries")
    
    # Final counts
    print(f"\nFinal: {counts()} total={total()}")
    
    browser.close()
