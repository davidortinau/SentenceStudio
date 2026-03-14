# Squad Decisions

## Active Decisions

### 1. GitHub Issues Created for Azure + Entra ID Plan (2026-03-13)

**Status:** DOCUMENTED  
**Date:** 2026-03-13  
**Author:** Zoe (Lead)  

27 GitHub issues decompose the Azure deployment + Entra ID authentication plan into actionable work items across 5 phases. All issues linked with dependency references and assigned to team members.

**Key Decisions:**
- **Reframed Issue #39:** User-secrets workflow as team best practice (not security emergency) — no secrets accidentally committed; `appsettings.json` already in `.gitignore`
- **Execution Order:** Phase 2 → Phase 1 → Phase 3 → Phase 4 → Phase 5 (security-first approach)
- **Phase 1 Testable Locally:** Auth flow fully validates on `localhost` without Azure deployment
- **Team Assignments:** Zoe (14 issues, architecture/infra), Kaylee (8 issues, UI/deploy), Captain (1 issue, Azure portal)
- **Critical Path:** CoreSync SQLite→PostgreSQL migration (Phase 3.7, XL complexity)

**Issue Mapping:**
| Phase | Count | Issues |
|-------|-------|--------|
| Phase 1 (Auth) | 7 | #42-47 |
| Phase 2 (Secrets) | 4 | #39-41, #54 |
| Phase 3 (Infrastructure) | 8 | #48-53, #55 |
| Phase 4 (Pipeline) | 4 | #56-59 |
| Phase 5 (Hardening) | 6 | #60-65 |

**Learnings:**
- Aspire-native provisioning (`azd`) generates Bicep — no manual templates needed
- Localhost testing of auth eliminates blocker for early validation
- DevAuthHandler alongside Entra ID maintains developer velocity
- User-secrets as team best practice enables secure local dev

**Next Steps:**
1. Captain: Register Entra ID app registrations (#42)
2. Zoe: Begin user-secrets setup (#39-40)
3. Kaylee: Begin CI workflow (#56)
4. All phases proceed in parallel where dependencies allow

---

### 2. Architecture Plan: Azure Deployment with Entra ID Authentication (2026-03-13)

**Status:** REFERENCE  
**Date:** 2026-03-13  
**Author:** Zoe (Lead)  

Comprehensive architecture plan for transitioning SentenceStudio from local-dev-only to production-ready Azure deployment with real authentication. Covers 5 phases from secret management through hardening, with technical decisions, risk register, and cost estimates.

**Key Technical Decisions:**
1. **Aspire-Native Provisioning over Raw Bicep** — AppHost defines resources; `azd` generates Bicep
2. **Keep DevAuthHandler Alongside Entra ID** — Developer velocity: use DevAuthHandler for local dev, Entra ID for production
3. **PostgreSQL over Azure SQL** — Aligns with AppHost declaration and CoreSync support
4. **Single-Tenant First** — Start with single Entra ID tenant; multi-tenant support added later
5. **Token Caching:** SecureStorage (MAUI) and Redis (WebApp)

**3 App Registrations Required:**
- SentenceStudio API (Web API — resource server)
- SentenceStudio WebApp (Web app, confidential — Blazor Server)
- SentenceStudio Native (Mobile/Desktop, public — MAUI clients)

**Scopes Exposed:**
- `api://sentencestudio/user.read` — user profile, vocabulary
- `api://sentencestudio/user.write` — modify user data, submit answers
- `api://sentencestudio/ai.access` — AI chat, speech synthesis, image analysis
- `api://sentencestudio/sync.readwrite` — CoreSync bi-directional sync

**Estimated Monthly Cost (Production):** ~$107-252

## Governance

- All meaningful changes require team consensus
- Document architectural decisions here
- Keep history focused on work, decisions focused on direction
