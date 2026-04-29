"""Login and navigate to import page."""
from playwright.sync_api import sync_playwright
import os, json

EVIDENCE_DIR = os.path.dirname(os.path.abspath(__file__))
BASE = 'https://localhost:7071'

# Try these users in order
USERS = [
    ('dave@ortinau.com', 'Test1234!'),
    ('korean-test@sentencestudio.local', 'Test1234!'),
    ('testuser-ko@test.local', 'Test1234!'),
]

with sync_playwright() as p:
    browser = p.chromium.launch(headless=True)
    context = browser.new_context(ignore_https_errors=True)
    page = context.new_page()
    
    logged_in = False
    for email, password in USERS:
        print(f"Trying {email}...")
        page.goto(f'{BASE}/auth/login', wait_until='networkidle', timeout=30000)
        page.wait_for_timeout(1000)
        
        # Fill login form
        email_input = page.locator('input[type="email"], input[name="email"], input[placeholder*="email" i]').first
        pass_input = page.locator('input[type="password"]').first
        
        email_input.fill(email)
        pass_input.fill(password)
        
        # Submit
        submit = page.locator('button[type="submit"], button:has-text("Sign In")').first
        submit.click()
        page.wait_for_timeout(3000)
        
        # Check if we landed on a non-login page
        url = page.url
        print(f"  After login: {url}")
        if '/login' not in url.lower() and '/auth' not in url.lower():
            logged_in = True
            print(f"  SUCCESS: logged in as {email}")
            break
        else:
            # Check for error messages
            errors = page.locator('.alert-danger, .validation-summary-errors, .text-danger').all_text_contents()
            print(f"  Failed: {errors}")
    
    if not logged_in:
        print("All login attempts failed. Trying to register a new test user...")
        page.goto(f'{BASE}/auth/register', wait_until='networkidle', timeout=30000)
        page.wait_for_timeout(1000)
        page.screenshot(path=os.path.join(EVIDENCE_DIR, '00-register-page.png'), full_page=True)
        
        # Try registering
        email_input = page.locator('input[type="email"], input[name="email"]').first
        pass_input = page.locator('input[type="password"]').first
        
        email_input.fill('jayne-qa@test.local')
        pass_input.fill('Test1234!')
        
        # Look for confirm password field
        confirm = page.locator('input[name="confirmPassword"], input[placeholder*="confirm" i]')
        if confirm.count() > 0:
            confirm.first.fill('Test1234!')
        
        submit = page.locator('button[type="submit"]').first
        submit.click()
        page.wait_for_timeout(3000)
        url = page.url
        print(f"After register: {url}")
        page.screenshot(path=os.path.join(EVIDENCE_DIR, '00-after-register.png'), full_page=True)
    
    if logged_in:
        # Navigate to import page
        page.goto(f'{BASE}/import', wait_until='networkidle', timeout=30000)
        page.wait_for_timeout(3000)
        page.screenshot(path=os.path.join(EVIDENCE_DIR, '01-import-page.png'), full_page=True)
        
        # Save the full HTML for selector discovery
        with open(os.path.join(EVIDENCE_DIR, '01-import-page.html'), 'w') as f:
            f.write(page.content())
        
        print("Import page captured")
    
    browser.close()
