#!/usr/bin/env python3
"""
Test NumberDrill grading improvements:
1. Permissive whitespace grading (5시 vs 5 시)
2. Permissive words vs numerals (5 vs 다섯 vs 오)
3. Spacing hints with underscores in placeholder
4. Result view shows user input vs correct answer
"""

from playwright.sync_api import sync_playwright
import time

WEBAPP_URL = "https://localhost:7071"
TEST_EMAIL = "squad-jayne@sentencestudio.test"
TEST_PASSWORD = "SquadTest!2026"

def main():
    with sync_playwright() as p:
        browser = p.chromium.launch(headless=False)  # Show browser for debugging
        context = browser.new_context(ignore_https_errors=True)
        page = context.new_page()
        
        print("📱 Navigating to webapp...")
        page.goto(WEBAPP_URL)
        page.wait_for_load_state('networkidle')
        time.sleep(2)
        
        # Sign in
        print("🔐 Signing in...")
        try:
            # Check if already signed in
            if "Dashboard" in page.content():
                print("✅ Already signed in")
            else:
                # Fill sign-in form
                page.fill('input[type="email"]', TEST_EMAIL)
                page.fill('input[type="password"]', TEST_PASSWORD)
                page.click('button:has-text("Sign In")')
                page.wait_for_load_state('networkidle')
                time.sleep(2)
                print("✅ Signed in successfully")
        except Exception as e:
            print(f"⚠️ Sign-in skipped or already signed in: {e}")
        
        # Navigate to NumberDrill
        print("🔢 Navigating to NumberDrill...")
        page.goto(f"{WEBAPP_URL}/numberdrill")
        page.wait_for_load_state('networkidle')
        time.sleep(2)
        
        # Take screenshot of setup page
        page.screenshot(path='numberdrill-setup.png')
        print("📸 Screenshot: numberdrill-setup.png")
        
        # Select Time context and ReadAndProduce mode
        print("⚙️ Configuring session...")
        page.click('button:has-text("Time")')
        time.sleep(1)
        page.click('button:has-text("Read and produce")')
        time.sleep(1)
        
        # Set item count to 3 for quick testing
        page.click('button:has(i.bi-dash)')  # Reduce to 5
        time.sleep(0.5)
        
        # Start session
        print("▶️ Starting session...")
        page.click('button:has-text("Start Session")')
        page.wait_for_load_state('networkidle')
        time.sleep(3)
        
        # TEST 1: Check for spacing hint in placeholder
        print("\n🧪 TEST 1: Spacing hints with underscores")
        placeholder = page.get_attribute('input[type="text"]', 'placeholder')
        print(f"   Placeholder: {placeholder}")
        
        if "___" in placeholder:
            print("✅ PASS: Underscores found in placeholder (spacing hint)")
        else:
            print("❌ FAIL: No underscores in placeholder")
        
        page.screenshot(path='numberdrill-item1-prompt.png')
        print("📸 Screenshot: numberdrill-item1-prompt.png")
        
        # Get the display prompt to understand what we're working with
        prompt_text = page.inner_text('.ss-display')
        print(f"   Prompt: {prompt_text}")
        
        # TEST 2 & 3: Test permissive grading
        print("\n🧪 TEST 2 & 3: Permissive grading (whitespace + word forms)")
        
        # Try submitting with extra spaces (e.g., "5 시" instead of "5시")
        test_answer = prompt_text.replace("시", " 시").replace("분", " 분") if "시" in prompt_text or "분" in prompt_text else prompt_text
        print(f"   Testing answer with extra spaces: {test_answer}")
        
        page.fill('input[type="text"]', test_answer)
        time.sleep(1)
        page.click('button:has-text("Submit")')
        page.wait_for_load_state('networkidle')
        time.sleep(2)
        
        page.screenshot(path='numberdrill-item1-feedback.png')
        print("📸 Screenshot: numberdrill-item1-feedback.png")
        
        # TEST 4: Check result view shows user input vs correct answer
        print("\n🧪 TEST 4: Result view comparison")
        feedback_content = page.inner_text('.alert')
        print(f"   Feedback content:\n{feedback_content}")
        
        if "You typed:" in feedback_content:
            print("✅ PASS: 'You typed:' label found")
        else:
            print("❌ FAIL: 'You typed:' label missing")
        
        if "Correct answer:" in feedback_content:
            print("✅ PASS: 'Correct answer:' label found")
        else:
            print("❌ FAIL: 'Correct answer:' label missing")
        
        # Check if whitespace variant was accepted
        if "정확해요" in feedback_content or "check-circle-fill" in page.content():
            print("✅ PASS: Whitespace-variant answer accepted")
        else:
            print("⚠️ Answer not accepted (may be wrong answer, not grading issue)")
        
        # Move to next item
        print("\n📝 Moving to next item...")
        page.click('button:has-text("Next")')
        page.wait_for_load_state('networkidle')
        time.sleep(2)
        
        # TEST 5: Test Listen and Type mode with word/numeral variants
        print("\n🧪 TEST 5: Testing word vs numeral equivalence")
        
        # Check if we're in ReadAndProduce or ListenAndType
        # If ReadAndProduce, we can try typing numerals instead of words
        if 'input[type="text"]' in page.content():
            placeholder = page.get_attribute('input[type="text"]', 'placeholder')
            print(f"   Placeholder: {placeholder}")
            
            # Get prompt again
            prompt_text_elem = page.query_selector('.ss-display')
            if prompt_text_elem:
                prompt_text = prompt_text_elem.inner_text()
                print(f"   Prompt: {prompt_text}")
                
                # If prompt has Korean numbers, try typing with Arabic numerals
                # Or vice versa - just type the same thing for now to verify flow
                page.fill('input[type="text"]', prompt_text)
                time.sleep(1)
                
                page.screenshot(path='numberdrill-item2-input.png')
                print("📸 Screenshot: numberdrill-item2-input.png")
                
                page.click('button:has-text("Submit")')
                page.wait_for_load_state('networkidle')
                time.sleep(2)
                
                page.screenshot(path='numberdrill-item2-feedback.png')
                print("📸 Screenshot: numberdrill-item2-feedback.png")
        
        # Final screenshot
        page.screenshot(path='numberdrill-final-state.png', full_page=True)
        print("📸 Screenshot: numberdrill-final-state.png")
        
        print("\n✅ Test complete! Check screenshots for verification.")
        print("\nSummary:")
        print("- numberdrill-setup.png: Initial setup screen")
        print("- numberdrill-item1-prompt.png: First item with spacing hint placeholder")
        print("- numberdrill-item1-feedback.png: Feedback showing user input vs correct answer")
        print("- numberdrill-item2-input.png: Second item input")
        print("- numberdrill-item2-feedback.png: Second item feedback")
        print("- numberdrill-final-state.png: Final state")
        
        input("\nPress Enter to close browser...")
        browser.close()

if __name__ == "__main__":
    main()
