"""
v1.2 Import Bug Fix — Full E2E Ship-Gate Validation
Jayne (QA Tester)
Tests all 7 critical scenarios via Playwright + Postgres verification.
"""
from playwright.sync_api import sync_playwright
import os, json, subprocess, time

EVIDENCE_DIR = os.path.dirname(os.path.abspath(__file__))
BASE = 'https://localhost:7071'
DB_EXEC = "docker exec db-84833ad0 env PGPASSWORD='WsgZDs5sKWGFRC~SEMx5za' psql -U dbadmin -d sentencestudio -t -A"

TEST_FIXTURE = """저는 맥주를 많이 안 마시지만, 앤지하고 맥주집에 갔어요.|I don't drink beer much but went with Angie to a beer house (brewery).
앤지는 맥주를 많이 안 마시지만, 단 음료를 마셔요.|Angie doesn't drink much beer but she drinks sweet drinks.
그 웨이터는 동료가 한국어로 주문했는데 이해 못 했어요.|The waiter didn't understand (when) my colleague ordered in Korean."""

def db_query(sql):
    """Run a SQL query against Postgres and return output."""
    cmd = f'''{DB_EXEC} -c "{sql}"'''
    result = subprocess.run(cmd, shell=True, capture_output=True, text=True)
    return result.stdout.strip()

def get_type_counts():
    """Get LexicalUnitType counts from DB."""
    raw = db_query('SELECT "LexicalUnitType", COUNT(*) FROM "VocabularyWord" GROUP BY "LexicalUnitType" ORDER BY "LexicalUnitType"')
    counts = {}
    for line in raw.split('\n'):
        if '|' in line:
            parts = line.split('|')
            counts[int(parts[0].strip())] = int(parts[1].strip())
    return counts

def get_recent_entries(since_count):
    """Get entries added after a known total count."""
    return db_query(f'''SELECT "TargetLanguageTerm", "LexicalUnitType" FROM "VocabularyWord" ORDER BY "CreatedAt" DESC LIMIT 50''')

def screenshot(page, name):
    path = os.path.join(EVIDENCE_DIR, f'{name}.png')
    page.screenshot(path=path, full_page=True)
    print(f"  Screenshot: {name}.png")
    return path

results = {}

with sync_playwright() as p:
    browser = p.chromium.launch(headless=True)
    context = browser.new_context(ignore_https_errors=True, viewport={'width': 1280, 'height': 900})
    page = context.new_page()
    
    # === LOGIN ===
    print("=== LOGGING IN ===")
    page.goto(f'{BASE}/auth/login', wait_until='networkidle', timeout=30000)
    page.wait_for_timeout(1000)
    page.locator('input[type="email"], input[name="email"], input[placeholder*="email" i]').first.fill('testuser-ko@test.local')
    page.locator('input[type="password"]').first.fill('Test1234!')
    page.locator('button[type="submit"], button:has-text("Sign In")').first.click()
    page.wait_for_timeout(3000)
    
    if '/login' in page.url.lower() or '/auth' in page.url.lower():
        print("FATAL: Login failed!")
        screenshot(page, 'FATAL-login-failed')
        browser.close()
        exit(1)
    print(f"Logged in. URL: {page.url}")
    
    # === BASELINE ===
    baseline = get_type_counts()
    baseline_total = sum(baseline.values())
    print(f"Baseline counts: {baseline} (total={baseline_total})")
    
    # ================================================================
    # TEST 1: Captain's exact reproduction case
    # Content Type = Phrases, Harvest = Phrases + Words
    # ================================================================
    print("\n=== TEST 1: Captain's Bug Repro (Phrases + Words) ===")
    page.goto(f'{BASE}/import-content', wait_until='networkidle', timeout=30000)
    page.wait_for_timeout(2000)
    screenshot(page, '01-import-content-initial')
    
    # Dump HTML for selector analysis
    html = page.content()
    with open(os.path.join(EVIDENCE_DIR, '01-import-content.html'), 'w') as f:
        f.write(html)
    
    # Find the content type selector  
    # Look for select/dropdown for content type
    selectors_info = []
    for sel in ['select', 'input[type="radio"]', '.btn-group button', '[data-content-type]']:
        elements = page.locator(sel).all()
        for el in elements:
            try:
                text = el.text_content()
                val = el.get_attribute('value') or ''
                selectors_info.append(f"{sel}: text='{text}' value='{val}'")
            except:
                pass
    
    print("  Found selectors:")
    for s in selectors_info[:20]:
        print(f"    {s}")
    
    # Find textarea for content paste
    textarea = page.locator('textarea').first
    if textarea.count() > 0:
        print("  Found textarea for content")
    
    # Take a full snapshot of form elements
    # Let's look at what's actually on the page
    all_buttons = page.locator('button').all()
    for b in all_buttons[:15]:
        try:
            txt = b.text_content().strip()
            if txt:
                print(f"  Button: '{txt}'")
        except:
            pass
    
    all_inputs = page.locator('input').all()
    for inp in all_inputs[:15]:
        try:
            t = inp.get_attribute('type') or 'text'
            n = inp.get_attribute('name') or inp.get_attribute('id') or ''
            v = inp.get_attribute('value') or ''
            print(f"  Input: type={t} name={n} value={v}")
        except:
            pass
    
    all_selects = page.locator('select').all()
    for sel in all_selects[:10]:
        try:
            n = sel.get_attribute('name') or sel.get_attribute('id') or ''
            options = sel.locator('option').all()
            opts = [o.text_content().strip() for o in options]
            print(f"  Select: name={n} options={opts}")
        except:
            pass
    
    browser.close()
    print("\n=== RECON COMPLETE - Need to analyze HTML for proper selectors ===")
