# Squad History — Publish Log

## Publish #8: NumberDrill Layout Parity (Footer Pin + Card Wrapper Removal)

**Date:** 2026-05-07  
**Ship Agent:** Wash (DevOps/Release Engineering)  
**Status:** ✅ SHIPPED

### Commits Shipped
- `577852ff` — fix(css): PageHeader `flex-shrink: 0` so footer pins to bottom edge
- `d09c233c` — fix(numberdrill): Remove card wrapper to match VocabQuiz flat layout
- `28aaca6e` — Kaylee bookkeeping (activity log + dashboard integration)
- `17209ec3` — Wash build fix (dangling `</div>` in NumberDrill.razor)

### Azure Deployment
- **Result:** ✅ SUCCESS (1m 57s)
- **API Revision:** api--0000094 (new)
- **WebApp Revision:** webapp--0000080 (new)
- **Post-Deploy Validation:** ✅ PASS (16/16 checks)

### iOS Deployment to DX24
- **Build:** ✅ SUCCESS (net10 SDK, ValidateXcodeVersion=false)
- **Install:** ✅ SUCCESS (2nd attempt after device wake)
- **Launch:** ✅ SUCCESS (bundle ID com.simplyprofound.sentencestudio)

### Friction Encountered
**HTML Build Issue (Unbalanced Tags):** Kaylee's card wrapper removal commit `d09c233c` removed opening `<div class="card card-ss p-4">` but left closing `</div>`. Razorblade caught unbalanced tags (33 close vs 32 open). Fixed in `17209ec3` before final deploy. **Lesson:** Post-structural removals in HTML, always verify balanced tag counts. Candidate for pre-commit Razor build check.

### Manual Validation (Captain)
- App running on DX24 post-launch
- Ready for NumberDrill footer pin + layout parity visual confirmation
