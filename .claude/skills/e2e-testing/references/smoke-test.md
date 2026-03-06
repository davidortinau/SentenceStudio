# Smoke Test & Cross-Cutting Checks

## Global Setup

```
cd src/SentenceStudio.AppHost && aspire run
# Wait for webapp: curl -sk -o /dev/null -w "%{http_code}" https://localhost:7071/

# Test users:
# David  (Korean)  f452438c-b0ac-4770-afea-0803e2670df5
# Jose   (Spanish) 8d5f7b4a-7710-4882-af45-a550145dad4b
# Gunther(German)  c3bb57f7-e371-43d4-b91f-32902a9f9844
```

**Prerequisite:** Each user needs ≥1 LearningResource with vocabulary:
```sql
SELECT lr.Id, lr.Title, COUNT(rvm.VocabularyWordId) as words
FROM LearningResource lr
JOIN ResourceVocabularyMapping rvm ON lr.Id = rvm.ResourceId
WHERE lr.UserProfileId = '<userId>'
GROUP BY lr.Id;
```

## Quick Smoke Test (5 min)

1. **Dashboard** → Confirm resources loaded, vocab stats display
2. **Vocab Quiz** → Answer 2 questions → verify auto-advance + dashboard Learning count updates
3. **Import** → Paste a YouTube URL → Fetch → verify transcript appears
4. **Resource Edit** → Open existing resource → Generate vocabulary → verify words appear
5. **Audio** → Click any 🔊 button → verify no errors

## Cross-Cutting Verifications

Run after **any** activity change:

| Concern | How to verify |
|---------|---------------|
| **Correct UserId** | `SELECT DISTINCT UserId FROM VocabularyProgress ORDER BY LastPracticedAt DESC LIMIT 5` — must be GUID, never `"1"` |
| **Cache freshness** | Dashboard Learning/Known counts update immediately after activity |
| **Audio on webapp** | Browser console has no `audioInterop` errors; speaker button cycles spinner→idle |
| **Audio on native** | `maui-devflow MAUI logs --limit 10` shows no NPE from AudioManager |
| **AI calls via Aspire** | Aspire structured logs show no 503/401 on `/api/v1/ai/chat` |
| **Auto-advance (quiz)** | After answering, next question loads in ~2s without clicking Next |
