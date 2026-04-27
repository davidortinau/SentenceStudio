from playwright.sync_api import sync_playwright
import time, subprocess

DB_CMD = "docker exec db-84833ad0 env PGPASSWORD='WsgZDs5sKWGFRC~SEMx5za' psql -U dbadmin -d sentencestudio -t -A -c"

def db_counts():
    out = subprocess.check_output(f'''{DB_CMD} "SELECT COALESCE(\\\"LexicalUnitType\\\",1), COUNT(*) FROM \\\"VocabularyWord\\\" GROUP BY COALESCE(\\\"LexicalUnitType\\\",1) ORDER BY 1"''', shell=True, text=True)
    counts = {}
    total = 0
    for line in out.strip().split('\n'):
        if '|' in line:
            parts = line.split('|')
            t, c = int(parts[0]), int(parts[1])
            counts[t] = c
            total += c
    return counts, total

with sync_playwright() as p:
    browser = p.chromium.launch(headless=True)
    ctx = browser.new_context(ignore_https_errors=True)
    page = ctx.new_page()
    
    # Login
    page.goto('https://localhost:7071/auth/login')
    page.wait_for_selector('#email', timeout=15000)
    page.fill('#email', 'testuser-ko@test.local')
    page.fill('#password', 'Test1234!')
    
    # Wait for Blazor hydration and click Sign In
    for i in range(30):
        btn = page.query_selector('button.btn-primary.w-100')
        if btn and btn.get_attribute('disabled') is None:
            btn.click()
            break
        time.sleep(0.5)
    
    time.sleep(3)
    print(f"After login URL: {page.url}")
    
    # Navigate to import
    page.goto('https://localhost:7071/import-content')
    page.wait_for_load_state('networkidle')
    time.sleep(2)
    
    # Select Phrases content type
    selects = page.query_selector_all('select')
    if selects:
        selects[0].select_option(label='문구')
    time.sleep(1)
    
    # Fill content
    page.fill('textarea', '안녕하세요\n감사합니다\n실례합니다')
    time.sleep(0.5)
    
    # Click Preview
    for b in page.query_selector_all('button'):
        if '미리보기' in (b.text_content() or ''):
            b.click()
            break
    
    # Wait for harvest checkboxes
    page.wait_for_selector('#harvestPhrases', timeout=60000)
    time.sleep(12)  # Wait for AI processing to finish fully
    print("Preview ready")
    
    # FILL RESOURCE TITLE — use the correct placeholder
    title_input = page.query_selector('input[placeholder*="일반 한국어"]')
    if not title_input:
        title_input = page.query_selector('input[placeholder*="예:"]')
    if title_input:
        title_input.fill("E2E Debug Test")
        print(f"Filled resource title: '{title_input.input_value()}'")
    else:
        print("RESOURCE TITLE INPUT NOT FOUND!")
    
    time.sleep(1)
    page.screenshot(path='debug2-before-commit.png', full_page=True)
    
    # Get DB baseline
    before_counts, before_total = db_counts()
    print(f"DB before: {before_counts} total={before_total}")
    
    # Monitor network for API calls
    responses = []
    def on_resp(resp):
        if resp.url != 'https://localhost:7071/import-content':
            return
        responses.append(f"{resp.request.method} {resp.url} -> {resp.status}")
    page.on("response", on_resp)
    
    # Click commit
    for b in page.query_selector_all('button'):
        t = (b.text_content() or '').strip()
        if '가져오기' in t:
            disabled = b.get_attribute('disabled')
            print(f"Commit button: text='{t}' disabled={disabled}")
            b.click()
            print("Clicked commit!")
            break
    
    # Wait for commit to process
    time.sleep(8)
    page.screenshot(path='debug2-after-commit.png', full_page=True)
    
    # Check DB
    after_counts, after_total = db_counts()
    print(f"DB after: {after_counts} total={after_total}")
    print(f"Delta: +{after_total - before_total}")
    
    # Check what page looks like now
    print(f"URL after commit: {page.url}")
    
    # Check for toast or error messages
    toasts = page.query_selector_all('.toast, .alert, [role=alert], .toast-body')
    for t in toasts:
        print(f"Toast/Alert: {t.text_content()}")
    
    # Network responses
    print(f"\nNetwork responses: {len(responses)}")
    for r in responses:
        print(f"  {r}")
    
    browser.close()
