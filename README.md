# 🌿 RenewalBrain

**Nothing expires without warning.** Photograph anything that expires —
passport, visa, insurance, warranty, licence, domain, prescription — and
RenewalBrain reads it, works out not just *when it expires* but *when you
must act*, and lays your whole life out on one calm, color-coded runway.

## Why it's different
- **Act-by intelligence** — a passport that "expires in 2031" actually
  needs action 6 months earlier (entry-validity rules). Every document
  type carries its own lead time; reminders fire when action starts,
  not when it's too late.
- **Privacy is the product** — document images are processed in memory
  and discarded. Only labels and dates are stored — never passport
  numbers, policy numbers, or identifiers (a server-side scrubber
  enforces this even if the AI slips). "We remember your deadlines,
  not your documents."
- **Renewal playbooks** — every reminder carries the *how*: steps,
  documents needed, typical processing time, and the one tip that saves
  people (13 seeded playbooks incl. Sri Lanka-specific ones).
- **Household view** — track the whole family; auto-colored avatars,
  per-person filtering.

## The interface
A warm, consumer-grade UI (Fraunces + Plus Jakarta Sans): the **Runway**
(next 90 days as approaching dots), a **Life Health ring**, urgency-grouped
cards with dual act-by/expires dates, photo→AI→confirm flow with a live
confidence bar and a privacy receipt, playbook drawer, mark-renewed and
snooze flows, sample-life onboarding.

## Stack
ASP.NET Core 8 minimal API · zero NuGet dependencies · single-file
front end · Gemini vision (free) or Anthropic for extraction · pluggable
store (Supabase Postgres REST / file fallback) · Docker.

## Run locally
```bash
GEMINI_API_KEY=your-key dotnet run     # real AI extraction
MOCK_AI=1 dotnet run                   # offline demo mode
# open http://localhost:8080  → "Load a sample life"
```

## Deploy (Render + Supabase, both free)
1. Supabase: SQL Editor → run `supabase-setup.sql` → copy URL + service_role key.
2. Render: push to GitHub → New + → Blueprint → paste GEMINI_API_KEY →
   Apply → add SUPABASE_URL + SUPABASE_SERVICE_KEY in Environment.
3. `/api/health` confirms provider + store.

## Tests
```bash
MOCK_AI=1 PORT=8090 dotnet run &  sleep 5
bash tests/suite.sh   # 25 checks: extraction, act-by math, privacy scrub,
                      # CRUD, playbook matching, export/import
```

## Honest limits (v1)
- Reminders are in-app (the runway + urgency groups); email/push
  notifications are the top of the roadmap and need a mail provider.
- Single-tenant: one shared timeline per deployment — accounts and
  households-with-logins are the v2 that makes this sellable at scale.
- Playbooks are general guidance — verify with official sources.
- AI extraction can misread dates; the confirm step exists for a reason.
