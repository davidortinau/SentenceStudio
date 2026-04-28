"""
v1.2 Import Bug Fix — E2E Ship-Gate Validation (Corrected Flow)
Properly handles the Blazor SSR import workflow:
  1. Fill content + options → Click Preview
  2. Harvest defaults applied, preview runs
  3. Wait for preview table to appear
  4. Fill resource name
  5. Click commit (가져오기) 
"""
from playwright.sync_api import sync_playwright, expect
import os, subprocess, time

EVIDENCE = os.path.dirname(os.path.abspath(__file__))
BASE = 'https://localhost:7071'

TEST_FIXTURE = """저는 맥주를 많이 안 마시지만, 앤지하고 맥주집에 갔어요.|I don't drink beer much but went with Angie to a beer house (brewery).
앤지는 맥주를 많이 안 마시지만, 단 음료를 마셔요.|Angie doesn't drink much beer but she drinks sweet drinks.
그 웨이터는 동료가 한국어로 주문했는데 이해 못 했어요.|The waiter didn't understand (when) my colleague ordered in Korean."""

WORD_FIXTURE = "사과,apple\n바나나,banana\n맥주,beer\n물,water\n커피,coffee"

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

def recent(n=30):
    entries = []
    for line in db(f'SELECT "TargetLanguageTerm", "LexicalUnitType" FROM "VocabularyWord" ORDER BY "CreatedAt" DESC LIMIT {n}').split('\n'):
        if '|' in line:
            p = line.split('|')
            entries.append((p[0], int(p[1])))
    return entries

def shot(page, name):
    page.screenshot(path=os.path.join(EVIDENCE, f'{name}.png'), full_page=True)

LOG = []
def log(msg):
    print(msg)
    LOG.append(msg)

def save_log():
    with open(os.path.join(EVIDENCE, 'test-log.txt'), 'w') as f:
        f.write('\n'.join(LOG))

results = {}

with sync_playwright() as p:
    browser = p.chromium.launch(headless=True)
    ctx = browser.new_context(ignore_https_errors=True, viewport={'width': 1280, 'height': 1000})
    page = ctx.new_page()
    
    # LOGIN
    log("=== LOGIN ===")
    page.goto(f'{BASE}/auth/login', wait_until='networkidle', timeout=30000)
    page.wait_for_timeout(3000)
    page.locator('#email').fill('testuser-ko@test.local')
    page.locator('#password').fill('Test1234!')
    page.wait_for_timeout(500)
    # Wait for Blazor hydration — poll until Sign In button is enabled
    for _i in range(30):
        btn = page.query_selector('button.btn-primary.w-100')
        if btn and btn.get_attribute('disabled') is None:
            btn.click()
            break
        page.wait_for_timeout(500)
    page.wait_for_timeout(5000)
    # Blazor SSR may not change URL — verify by navigating to a protected page
    page.goto(f'{BASE}/import-content', wait_until='networkidle', timeout=30000)
    page.wait_for_timeout(2000)
    assert '/login' not in page.url.lower(), f"Login failed: {page.url}"
    page.goto(f'{BASE}/', wait_until='networkidle', timeout=15000)
    log(f"  Logged in: {page.url}")
    
    baseline_c = counts()
    baseline_t = total()
    log(f"  Baseline: {baseline_c} total={baseline_t}")
    
    def do_import(test_name, content, content_type_label, delimiter_label, 
                  harvest_sentences=False, harvest_phrases=False, harvest_words=True,
                  resource_title=None, expect_commit=True):
        """Run a full import cycle and return (pre_total, post_total, recent_entries)."""
        pre_t = total()
        pre_c = counts()
        
        log(f"\n  Navigating to import page...")
        page.goto(f'{BASE}/import-content', wait_until='networkidle', timeout=30000)
        page.wait_for_timeout(2000)
        
        # Step 1: Fill content
        page.locator('textarea').first.fill(content)
        
        # Step 2: Set content type and delimiter
        selects = page.locator('select').all()
        selects[0].select_option(label=content_type_label)
        page.wait_for_timeout(300)
        if delimiter_label:
            selects[1].select_option(label=delimiter_label)
        
        shot(page, f'{test_name}-01-filled')
        
        # Click preview
        page.locator('button:has-text("미리보기")').click()
        log(f"  Clicked Preview, waiting for AI processing...")
        
        # Wait for harvest section or preview table (up to 60s for AI calls)
        harvest_visible = False
        preview_visible = False
        for _ in range(30):
            page.wait_for_timeout(2000)
            if page.locator('#harvestWords').count() > 0:
                harvest_visible = True
            if page.locator('table tbody tr').count() > 0:
                preview_visible = True
                break
        
        shot(page, f'{test_name}-02-after-parse')
        log(f"  Harvest visible: {harvest_visible}, Preview visible: {preview_visible}")
        
        if harvest_visible:
            # Check current checkbox states
            for cb_id in ['#harvestTranscript', '#harvestSentences', '#harvestPhrases', '#harvestWords']:
                cb = page.locator(cb_id)
                if cb.count() > 0:
                    log(f"    {cb_id}: checked={cb.is_checked()}")
        
        # Scroll to see full page
        page.evaluate("window.scrollTo(0, document.body.scrollHeight)")
        page.wait_for_timeout(1000)
        shot(page, f'{test_name}-03-scrolled')
        
        # Wait more for preview if not yet visible
        if not preview_visible:
            for _ in range(15):
                page.wait_for_timeout(2000)
                if page.locator('table tbody tr').count() > 0:
                    preview_visible = True
                    break
            shot(page, f'{test_name}-04-waited-more')
        
        if preview_visible:
            row_count = page.locator('table tbody tr').count()
            log(f"  Preview rows: {row_count}")
            
            # Check row types
            cells = page.locator('table tbody tr td').all_text_contents()
            log(f"  First few cells: {cells[:12]}")
        else:
            log("  WARNING: Preview table never appeared")
            shot(page, f'{test_name}-05-no-preview')
        
        if not expect_commit:
            return pre_t, total(), recent(20)
        
        # Fill resource title for the commit (correct selector for Korean UI)
        title = resource_title or f"QA-{test_name}-{int(time.time())}"
        title_input = page.locator('input[placeholder*="예:"]')
        if title_input.count() > 0:
            title_input.first.fill(title)
            log(f"  Set resource title: {title}")
        else:
            # Fallback: try other selectors
            alt = page.locator('input#newResourceTitle, input[placeholder*="resource" i], input[placeholder*="리소스" i]')
            if alt.count() > 0:
                alt.first.fill(title)
                log(f"  Set resource title (alt): {title}")
            else:
                log(f"  WARNING: resource title input not found!")
        
        page.evaluate("window.scrollTo(0, 0)")
        page.wait_for_timeout(500)
        shot(page, f'{test_name}-06-before-commit')
        
        # Click commit button — use text match which is most reliable
        commit = page.locator('button:has-text("가져오기")')
        if commit.count() == 0:
            commit = page.locator('.btn-ss-primary')
        
        if commit.count() > 0 and commit.first.is_visible():
            log("  Clicking commit...")
            commit.first.click()
            page.wait_for_timeout(15000)
            shot(page, f'{test_name}-07-after-commit')
            
            # Check for success/error indicators
            success = page.locator('.alert-success, :has-text("Success"), :has-text("성공")')
            if success.count() > 0:
                log("  Commit success indicator found")
            
            error = page.locator('.alert-danger')
            if error.count() > 0:
                err_text = error.first.text_content()
                log(f"  ERROR: {err_text}")
        else:
            log("  Commit button not visible/found")
        
        post_t = total()
        post_c = counts()
        log(f"  DB: before={pre_c} total={pre_t}")
        log(f"  DB: after={post_c} total={post_t}")
        log(f"  Delta: +{post_t - pre_t}")
        
        return pre_t, post_t, recent(30)
    
    # ================================================================
    # TEST 1: Captain's exact reproduction — Phrases + Words
    # ================================================================
    log("\n" + "="*60)
    log("TEST 1: Captain's Bug Repro (Content Type=Phrases, Harvest=Phrases+Words)")
    log("="*60)
    
    pre, post, rec = do_import('t1', TEST_FIXTURE, '문구', '파이프 (|)',
                                harvest_phrases=True, harvest_words=True)
    
    delta = post - pre
    phrase_entries = [(t,tp) for t,tp in rec if tp in (2,3) and any(k in t for k in ['맥주', '앤지', '웨이터', '동료', '마시'])]
    word_entries = [(t,tp) for t,tp in rec if tp == 1]
    
    log(f"  Phrase/Sentence entries with test content: {len(phrase_entries)}")
    for t, tp in phrase_entries[:5]:
        log(f"    [{tp}] {t[:80]}")
    log(f"  Word entries (recent 10): {len(word_entries[:10])}")
    for t, tp in word_entries[:5]:
        log(f"    [{tp}] {t}")
    
    if delta > 0 and len(phrase_entries) >= 3:
        results['test1'] = 'PASS'
        log("  RESULT: PASS")
    elif delta > 0:
        results['test1'] = f'PARTIAL - {delta} entries but only {len(phrase_entries)} phrases'
        log(f"  RESULT: {results['test1']}")
    else:
        results['test1'] = f'FAIL - 0 new entries (commit may have failed)'
        log(f"  RESULT: {results['test1']}")
    
    # ================================================================
    # TEST 2: Sentences content type
    # ================================================================
    log("\n" + "="*60)
    log("TEST 2: Sentences Content Type (Sentences + Words)")
    log("="*60)
    
    pre, post, rec = do_import('t2', TEST_FIXTURE, 'Sentences', '파이프 (|)',
                                harvest_sentences=True, harvest_words=True)
    
    delta = post - pre
    sent_entries = [(t,tp) for t,tp in rec if tp == 3]
    
    log(f"  Sentence (type=3) entries: {len(sent_entries)}")
    for t, tp in sent_entries[:5]:
        log(f"    [{tp}] {t[:80]}")
    
    if delta > 0 and len(sent_entries) >= 3:
        results['test2'] = 'PASS'
    elif delta > 0:
        results['test2'] = f'PARTIAL - {delta} entries but only {len(sent_entries)} sentences'
    else:
        results['test2'] = f'FAIL - 0 new entries'
    log(f"  RESULT: {results['test2']}")
    
    # ================================================================
    # TEST 3: Vocabulary type filter + Add button
    # ================================================================
    log("\n" + "="*60)
    log("TEST 3: Vocabulary Type Filter + Add Button")
    log("="*60)
    
    page.goto(f'{BASE}/vocabulary', wait_until='networkidle', timeout=30000)
    page.wait_for_timeout(3000)
    shot(page, 't3-01-vocabulary')
    
    # Save HTML for analysis
    with open(os.path.join(EVIDENCE, 't3-vocabulary.html'), 'w') as f:
        f.write(page.content())
    
    # Check all select elements
    all_selects = page.locator('select').all()
    for i, sel in enumerate(all_selects):
        opts = [o.text_content().strip() for o in sel.locator('option').all()]
        log(f"  Select[{i}]: {opts}")
    
    # Look for type filter - might be buttons or a different UI element
    type_btns = page.locator('button:has-text("Word"), button:has-text("Phrase"), button:has-text("Sentence")')
    if type_btns.count() > 0:
        log(f"  Type filter buttons found: {type_btns.count()}")
        results['test3_filter'] = 'PASS'
    else:
        # Check for dropdown with Word/Phrase/Sentence
        found_type_filter = False
        for sel in all_selects:
            opts = [o.text_content().strip() for o in sel.locator('option').all()]
            if any('Word' in o or 'Phrase' in o or 'Sentence' in o for o in opts):
                found_type_filter = True
                log(f"  Type filter dropdown found: {opts}")
                break
        
        if not found_type_filter:
            results['test3_filter'] = 'FAIL - No Word/Phrase/Sentence filter found'
            log(f"  RESULT: {results['test3_filter']}")
        else:
            results['test3_filter'] = 'PASS'
    
    # Check Add button
    add_btns = page.locator('button, a').all()
    for btn in add_btns:
        txt = btn.text_content().strip()
        if 'Add' in txt or '추가' in txt:
            log(f"  Found button: '{txt}'")
            if 'Word' in txt:
                results['test3_add'] = 'FAIL - Still says "Add Word"'
            else:
                results['test3_add'] = 'PASS'
            break
    else:
        results['test3_add'] = 'INVESTIGATING - No Add button found'
    
    log(f"  Filter: {results.get('test3_filter', 'N/A')}")
    log(f"  Add btn: {results.get('test3_add', 'N/A')}")
    
    # Mobile test
    page.set_viewport_size({'width': 375, 'height': 812})
    page.wait_for_timeout(1500)
    shot(page, 't3-02-mobile')
    page.set_viewport_size({'width': 1280, 'height': 1000})
    page.wait_for_timeout(1000)
    
    # ================================================================
    # TEST 4: Auto-detect
    # ================================================================
    log("\n" + "="*60)
    log("TEST 4: Auto-Detect Classifier")
    log("="*60)
    
    page.goto(f'{BASE}/import-content', wait_until='networkidle', timeout=30000)
    page.wait_for_timeout(2000)
    page.locator('textarea').first.fill(TEST_FIXTURE)
    page.locator('select').first.select_option(label='자동 감지')
    page.locator('select').nth(1).select_option(label='파이프 (|)')
    page.locator('button:has-text("미리보기")').click()
    
    # Wait for auto-detect result (AI classifier call)
    for _ in range(20):
        page.wait_for_timeout(2000)
        html = page.content()
        if 'Auto-detected' in html or 'auto-detect' in html.lower() or 'confidence' in html.lower():
            break
    
    shot(page, 't4-01-auto-detect')
    page.evaluate("window.scrollTo(0, document.body.scrollHeight)")
    page.wait_for_timeout(500)
    shot(page, 't4-02-auto-detect-full')
    
    html = page.content()
    with open(os.path.join(EVIDENCE, 't4-auto-detect.html'), 'w') as f:
        f.write(html)
    
    if 'Sentence' in html and ('Auto-detected' in html or 'Confirmed' in html):
        results['test4'] = 'PASS - Classified as Sentences'
        log(f"  RESULT: {results['test4']}")
    elif 'Phrase' in html and ('Auto-detected' in html or 'Confirmed' in html):
        results['test4'] = 'PASS - Classified as Phrases (acceptable)'
        log(f"  RESULT: {results['test4']}")
    elif 'Vocabulary' in html and 'Auto-detected' in html:
        results['test4'] = 'FAIL - Classified as Vocabulary (should be Sentence/Phrase)'
        log(f"  RESULT: {results['test4']}")
    else:
        results['test4'] = 'INVESTIGATING - Could not determine classification'
        log(f"  RESULT: {results['test4']}")
    
    # ================================================================
    # TEST 5: Validation gate
    # ================================================================
    log("\n" + "="*60)
    log("TEST 5: Validation Gate (all harvest unchecked)")
    log("="*60)
    
    page.goto(f'{BASE}/import-content', wait_until='networkidle', timeout=30000)
    page.wait_for_timeout(2000)
    page.locator('textarea').first.fill(TEST_FIXTURE)
    page.locator('select').first.select_option(label='문구')
    page.locator('select').nth(1).select_option(label='파이프 (|)')
    page.locator('button:has-text("미리보기")').click()
    
    # Wait for harvest checkboxes
    for _ in range(15):
        page.wait_for_timeout(2000)
        if page.locator('#harvestWords').count() > 0:
            break
    
    # Uncheck all harvest options
    for cb_id in ['#harvestTranscript', '#harvestSentences', '#harvestPhrases', '#harvestWords']:
        cb = page.locator(cb_id)
        if cb.count() > 0 and cb.is_checked():
            cb.click()
            page.wait_for_timeout(300)
    
    page.wait_for_timeout(1000)
    shot(page, 't5-01-all-unchecked')
    
    # Check for validation message
    page.evaluate("window.scrollTo(0, document.body.scrollHeight)")
    page.wait_for_timeout(500)
    shot(page, 't5-02-scrolled')
    
    validation = page.locator('.alert-danger:has-text("at least one")')
    if validation.count() > 0:
        results['test5'] = 'PASS - Validation message shown'
    else:
        # Try clicking commit to see if it's blocked
        commit = page.locator('button:has-text("가져오기")')
        if commit.count() > 0 and commit.first.is_visible():
            pre_t = total()
            commit.first.click()
            page.wait_for_timeout(5000)
            post_t = total()
            if post_t == pre_t:
                results['test5'] = 'PASS - Commit blocked (0 new entries)'
            else:
                results['test5'] = 'FAIL - Commit succeeded with no harvest options'
        else:
            results['test5'] = 'PASS - Commit button not available'
    log(f"  RESULT: {results['test5']}")
    
    # ================================================================
    # TEST 6: Vocab words-only regression
    # ================================================================
    log("\n" + "="*60)
    log("TEST 6: Regression — Vocabulary (Words only)")
    log("="*60)
    
    pre, post, rec = do_import('t6', WORD_FIXTURE, '어휘', '쉼표',
                                harvest_words=True)
    
    delta = post - pre
    new_phrase = [e for e in rec[:delta] if e[1] in (2, 3)] if delta > 0 else []
    
    if delta > 0 and len(new_phrase) == 0:
        results['test6'] = 'PASS - Only word entries'
    elif delta > 0:
        results['test6'] = f'FAIL - {len(new_phrase)} non-word entries found'
    else:
        results['test6'] = 'FAIL - 0 new entries'
    log(f"  RESULT: {results['test6']}")
    
    # ================================================================
    # TEST 7: Heuristic check
    # ================================================================
    log("\n" + "="*60)
    log("TEST 7: Heuristic Verification (via DB)")
    log("="*60)
    
    all_recent = recent(50)
    test_sentences = [e for e in all_recent if '맥주를 많이' in e[0] or '동료가 한국어' in e[0] or '단 음료' in e[0]]
    
    for term, typ in test_sentences:
        type_name = {1:'Word', 2:'Phrase', 3:'Sentence'}.get(typ, f'?{typ}')
        log(f"  [{type_name}] {term[:80]}")
    
    if test_sentences:
        types = set(tp for _, tp in test_sentences)
        if types <= {2, 3}:
            results['test7'] = 'PASS - All sentences classified as Phrase/Sentence'
        elif 1 in types:
            results['test7'] = 'FAIL - Some full sentences classified as Word'
        else:
            results['test7'] = f'INVESTIGATING - Types found: {types}'
    else:
        results['test7'] = 'INVESTIGATING - No test sentences found in DB'
    log(f"  RESULT: {results['test7']}")
    
    # ================================================================
    # FINAL SUMMARY
    # ================================================================
    log("\n" + "="*60)
    log("FINAL SUMMARY")
    log("="*60)
    
    final_c = counts()
    final_t = total()
    log(f"  Baseline: {baseline_c} total={baseline_t}")
    log(f"  Final:    {final_c} total={final_t}")
    log(f"  Net new:  +{final_t - baseline_t}")
    
    log("\n  RESULTS:")
    for k, v in results.items():
        status = 'PASS' if 'PASS' in str(v) else 'FAIL' if 'FAIL' in str(v) else 'INVESTIGATING'
        log(f"    {k}: [{status}] {v}")
    
    browser.close()

save_log()
print(f"\nEvidence saved to: {EVIDENCE}")
print(f"Log: {os.path.join(EVIDENCE, 'test-log.txt')}")
