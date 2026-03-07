# Project Context

- **Owner:** David Ortinau
- **Project:** SentenceStudio — a .NET MAUI Blazor Hybrid language learning app
- **Stack:** .NET 10, MAUI, Blazor Hybrid, MauiReactor (MVU), .NET Aspire, EF Core, SQLite, OpenAI
- **Created:** 2026-03-07

## Learnings

<!-- Append new learnings below. Each entry is something lasting about the project. -->

- AI prompts are Scriban templates in `src/SentenceStudio.AppLib/Resources/Raw/*.scriban-txt`
- AI grading uses `AiService.SendPrompt<T>()` with structured JSON responses
- Grading philosophy: VERY permissive — accept associations, contrasts, feelings, moods, cultural links
- Only mark related=false if truly random with no possible link
- Never penalize spelling — provide corrected_text field instead
- When in doubt, ALWAYS give credit (err on side of related=true)
- Word Association prompt at `GradeWordAssociation.scriban-txt` — latest activity
- Response models in `src/SentenceStudio.Shared/Models/` — use JsonPropertyName attributes
- Support both target language and native language clues as valid input
