### 2026-05-02: Vocab Quiz Stream A shipped as single PR

**By:** Kaylee
**What:** PR #196 ships four UI-only fixes (#190, #192, #193, #194) for VocabQuiz.razor as one PR per Captain's Stream A scope. MC distractor pool now uses a new `distractorScope: List<VocabularyWord>` field captured in `LoadVocabulary`. Audio for the prompt now routes through `GetPromptAudioText` / `GetPromptAudioLanguage` helpers that switch on `promptUsesNativeLanguage`. Learning Details info panel no longer renders TargetLanguageTerm or NativeLanguageTerm. Submit button added to text-entry form (new resource key VocabQuiz_SubmitAnswer).
**Why:** Single-file Razor changes cluster naturally; verifying once and shipping once is faster than four PRs. Anti-cheat fixes (#193, #194) are most user-impactful — they were leaking the answer in two separate code paths.
**Follow-up:** Stream B (Wash) handles #189 (obsolete progress fields audit). `GetTargetAudioText` / `GetTargetAudioLanguage` are now dead code — candidate for removal in a follow-up cleanup. Full e2e for #192 requires a word past `MasteryScore >= 0.50` to trigger text-entry mode — not exercised in this PR's verification, only build-verified.
