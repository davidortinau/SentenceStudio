"""Reconnaissance: screenshot the import page to see current UI state."""
from playwright.sync_api import sync_playwright
import os

EVIDENCE_DIR = os.path.dirname(os.path.abspath(__file__))

with sync_playwright() as p:
    browser = p.chromium.launch(headless=True)
    context = browser.new_context(ignore_https_errors=True)
    page = context.new_page()
    
    # Navigate to import page
    page.goto('https://localhost:7071/import', wait_until='networkidle', timeout=30000)
    page.wait_for_timeout(3000)
    page.screenshot(path=os.path.join(EVIDENCE_DIR, '00-import-page-initial.png'), full_page=True)
    
    # Also check what the page HTML looks like for selectors
    content = page.content()
    with open(os.path.join(EVIDENCE_DIR, '00-import-page-dom.html'), 'w') as f:
        f.write(content)
    
    # Navigate to vocabulary page too
    page.goto('https://localhost:7071/vocabulary', wait_until='networkidle', timeout=30000)
    page.wait_for_timeout(3000)
    page.screenshot(path=os.path.join(EVIDENCE_DIR, '00-vocabulary-page-initial.png'), full_page=True)
    
    browser.close()
    print("Recon complete")
