### 2026-02-17: Captain's answers to port plan open questions
**By:** David Ortinau (via Copilot)
**What:** Decisions on all 7 open questions from the Bootstrap port plan:
1. **Custom themes:** All 10 themes wanted (5 custom + 5 Bootswatch). The library should have the CSS to make this work.
2. **Priority page:** Dashboard confirmed as first validation page.
3. **Syncfusion controls:** Replace — do not keep Syncfusion.
4. **PageHeader:** Use Shell TitleView approach.
5. **Font scale slider:** Must-have — implement it, don't defer.
6. **Localization:** Same .resx approach — no reason to reinvent.
7. **Data layer cherry-picks:** Only cherry-pick what's needed that isn't already in the main branch.
**Why:** Captain's call — these unblock all infrastructure and page porting work.
