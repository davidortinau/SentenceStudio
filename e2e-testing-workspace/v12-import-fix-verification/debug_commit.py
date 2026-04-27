from playwright.sync_api import sync_playwright
import time, json

with sync_playwright() as p:
    browser = p.chromium.launch(headless=True)
    ctx = browser.new_context(ignore_https_errors=True)
    page = ctx.new_page()
    
    # Capture console messages and network
    console_msgs = []
    page.on("console", lambda msg: console_msgs.append(f"[{msg.type}] {msg.text}"))
    
    api_calls = []
    def on_response(resp):
        if '/api/' in resp.url or 'import' in resp.url.lower():
            api_calls.append(f"{resp.request.method} {resp.url} -> {resp.status}")
    page.on("response", on_response)
    
    # Login with retry for Blazor hydration
    page.goto('https://localhost:7071/auth/login')
    page.wait_for_selector('#email', timeout=15000)
    page.fill('#email', 'testuser-ko@test.local')
    page.fill('#password', 'Test1234!')
    
    # Wait for Sign In button to become enabled
    for i in range(20):
        btn = page.query_selector('button.btn-primary.w-100')
        if btn:
            disabled = btn.get_attribute('disabled')
            if disabled is None:
                btn.click()
                break
        time.sleep(0.5)
    else:
        # Force click anyway
        page.click('button.btn-primary.w-100', force=True)
    
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
    btns = page.query_selector_all('button')
    for b in btns:
        t = (b.text_content() or '').strip()
        if '미리보기' in t:
            b.click()
            print("Clicked preview")
            break
    
    # Wait for harvest checkboxes
    page.wait_for_selector('#harvestPhrases', timeout=60000)
    print("Harvest visible")
    time.sleep(10)  # Wait for AI processing
    
    # Screenshot preview
    page.screenshot(path='debug-commit-preview.png', full_page=True)
    
    # Dump all inputs at this stage
    print("\n=== ALL INPUTS AFTER PREVIEW ===")
    inputs = page.query_selector_all('input')
    for inp in inputs:
        itype = inp.get_attribute('type') or 'text'
        iid = inp.get_attribute('id') or ''
        iname = inp.get_attribute('name') or ''
        iplaceholder = inp.get_attribute('placeholder') or ''
        if itype in ('checkbox','radio'):
            ival = str(inp.is_checked())
        else:
            try: ival = inp.input_value()
            except: ival = '?'
        print(f'  type={itype} id={iid} name={iname} placeholder="{iplaceholder}" value={ival}')
    
    # Look for resource title section HTML
    html_dump = page.evaluate('''() => {
        // Find the import target / resource section
        const allDivs = document.querySelectorAll('div');
        for (const div of allDivs) {
            const text = div.textContent || '';
            if (text.includes('새 리소스') || text.includes('New Resource') || text.includes('리소스 이름') || text.includes('Resource Title') || text.includes('가져오기')) {
                if (div.querySelector('input[type=text], input[type=radio]')) {
                    return div.outerHTML.substring(0, 3000);
                }
            }
        }
        // Fallback: find commit button area
        const btns = document.querySelectorAll('button');
        for (const btn of btns) {
            if ((btn.textContent || '').includes('가져오기')) {
                return btn.parentElement?.parentElement?.outerHTML?.substring(0, 3000) || 'no parent';
            }
        }
        return 'nothing found';
    }''')
    print("\n=== RESOURCE/COMMIT SECTION HTML ===")
    print(html_dump[:3000])
    
    # Try to fill resource title - various selectors
    print("\n=== ATTEMPTING RESOURCE TITLE FILL ===")
    # Try by placeholder
    selectors = [
        'input#newResourceTitle',
        'input[placeholder*="title"]',
        'input[placeholder*="제목"]',
        'input[placeholder*="이름"]',
        'input[placeholder*="name"]',
        'input[placeholder*="Title"]',
        'input[placeholder*="리소스"]',
    ]
    filled = False
    for sel in selectors:
        el = page.query_selector(sel)
        if el:
            print(f"  Found with selector: {sel}")
            el.fill("E2E Test Import")
            filled = True
            break
    
    if not filled:
        # Try text inputs that aren't email/password
        text_inputs = page.query_selector_all('input[type=text], input:not([type])')
        for ti in text_inputs:
            tid = ti.get_attribute('id') or ''
            ttype = ti.get_attribute('type') or ''
            tplaceholder = ti.get_attribute('placeholder') or ''
            if tid not in ('email',) and ttype != 'checkbox' and ttype != 'radio' and ttype != 'email':
                print(f"  Filling text input: id={tid} placeholder={tplaceholder}")
                ti.fill("E2E Test Import")
                filled = True
                break
    
    if not filled:
        print("  NO TEXT INPUT FOUND FOR RESOURCE TITLE!")
    
    time.sleep(1)
    page.screenshot(path='debug-commit-before-click.png', full_page=True)
    
    # Click commit button
    api_calls.clear()
    btns = page.query_selector_all('button')
    commit_clicked = False
    for b in btns:
        t = (b.text_content() or '').strip()
        if '가져오기' in t:
            print(f"\nClicking commit button: '{t}'")
            b.click()
            commit_clicked = True
            break
    
    if not commit_clicked:
        print("COMMIT BUTTON NOT FOUND!")
    
    time.sleep(5)
    page.screenshot(path='debug-commit-after-click.png', full_page=True)
    
    # Check API calls
    print("\n=== API CALLS ===")
    for c in api_calls:
        print(f"  {c}")
    
    # Console messages
    print("\n=== CONSOLE MESSAGES (last 20) ===")
    for m in console_msgs[-20:]:
        print(f"  {m}")
    
    browser.close()
