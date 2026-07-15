# Keibai MVP — Agent Handoff Prompts

> Moved from `offmarket.deals/todo/keibai-mvp-agent-prompts.md` (2026-07-15). **Status: Phase 1
> completed and validated 2026-07-13** (`validation-phase1.md`); **Phase 2 completed and validated
> live 2026-07-15** (`validation-phase2.md`, `runbook.md`). Phase 3 (Blazor UI) not started.

Personal-use MVP of a Japanese court-auction (競売) search platform: scrape BIT, archive documents before courts delete them, browse/search locally. **.NET / Blazor / Wolverine / Marten.** No monetization, no public deployment, no entity — that comes later.

Keibai lives in its own repo now, but it is designed to be merged into the **offmarket.deals** codebase later (as the jp.offmarket.deals market). Every rule in "Merge-readiness" below exists so that merge is "copy the `Keibai.*` projects + add ~6 composition lines to the OMD host" — treat those rules as acceptance criteria, not suggestions.

Usage: give every agent the **Shared Context** block below, plus exactly one Phase prompt. Phases are sequential; each ends with acceptance criteria the agent must demonstrate before stopping.

---

## Shared Context (prepend to every phase prompt)

You are building **Keibai**, a personal-use tool that ingests Japanese court real-estate auction data and archives auction documents. Read this context fully before writing code.

### The data source

- **BIT** (https://www.bit.courts.go.jp) is the Supreme Court's official auction-property site, operated by 株式会社日立社会情報サービス. It lists ~1,100 active properties at any time across **~147 district courts and branches** (all 47 prefectures).
- Structure: a schedule directory (https://www.bit.courts.go.jp/schedule/index.html) lists each court's 期間入札 (bidding-period) rounds. Each round has: 公告日 (announcement, ≥2 weeks before bidding), 閲覧開始日 (documents viewable), 入札期間 (bidding window, typically ~8 days), 開札期日 (bid opening).
- Per property, BIT provides the **3点セット** — three PDFs: 物件明細書 (property statement), 現況調査報告書 (site investigation report), 評価書 (appraisal) — often scanned images, 50–150 pages total, with the 期間入札公告 prepended.
- **Critical constraint: 3点セット PDFs are deleted when each property's bidding period ends.** Even winning bidders can't re-download them. Archiving them the moment they're discovered is the core value of this system. 売却結果 (sale results: winning bid, bid count) are published on BIT from ~15:00–16:00 JST on the 開札 day and retained ~3 years (structured data only, not PDFs).
- The front end is a jQuery/Bootstrap app; the search flows call **undocumented internal JSON endpoints under `/app/...`** behind a CDN. There is **no API, no bulk export, and no robots.txt** (returns 404). Expect to reverse-engineer the request flow (POST bodies, session/token handling, pagination) from browser devtools or Playwright network capture, then replay with plain HTTP. Use Playwright only as a fallback if the HTTP flow can't be replayed. **(Phase 1 did this — see `bit-api.md` for the mapped flow, incl. the Hokkaidō 91–94 pseudo-codes and エラー-page semantics.)**
- Case numbers look like 令和7年(ケ)第123号 (ケ = 担保不動産競売, ヌ = 強制競売). One case can contain multiple 物件 (property items, numbered 物件1, 物件2…) sold as a set or separately. Addresses are often 地番 (registry lot number), not 住居表示 (postal address) — geocoding is best-effort, never trusted.

### Non-negotiable crawling rules (this is a COURT system)

1. Global rate limit: **max 1 request per 3 seconds**, single-threaded against BIT. No parallel fetching against their origin, ever.
2. Run bulk crawls during Japan night hours (01:00–06:00 JST) where scheduling allows; incremental checks may run during the day at the same rate limit.
3. Honest, stable User-Agent string (e.g. `keibai-personal-archive/0.1`); do not rotate UAs, do not use proxies, do not evade any blocking. If BIT ever returns 403/429 or a block page, **stop and alert** — do not retry around it.
4. Exponential backoff on errors (Polly), max 3 retries, then park the work item and alert.
5. A single config kill-switch (`Keibai:Ingestion:Enabled=false`) must stop all outbound traffic to BIT.
6. **Store every raw response** (JSON/HTML bytes) before parsing, keyed by URL+timestamp, so parser bugs never require re-fetching. Parsing failures must never lose raw data.
7. Personal use only: no re-publishing, no public hosting of documents. Court PDFs are stored as-is with their redactions intact; never attempt to de-anonymize.

### Stack (mandatory — versions pinned to offmarket.deals)

- **.NET 10** (`global.json`: SDK `10.0.300`, `rollForward: latestMinor`, test runner `Microsoft.Testing.Platform`), C#, nullable enabled, single ASP.NET Core host.
- **Marten 9.12.0** on **PostgreSQL** as document store — documents not EF. Registered as an **ancillary store** (`AddMartenStore<IKeibaiStore>(...)`) with `DatabaseSchemaName("keibai")`, NEVER the default `AddMarten` store — the OMD host owns the default store, and the ancillary registration is byte-identical before and after the merge. Inject `IKeibaiStore` everywhere; no bare `IDocumentSession`. Use the `imresamu/postgis` image in docker-compose (arm64 compatibility; PostGIS unused for now but the image future-proofs geo search).
- **WolverineFx 6.16.0** (+ `WolverineFx.Marten`, `WolverineFx.Http` as needed) for all background work: the crawl pipeline is Wolverine message handlers with durable local queues (`UseDurableLocalQueues`), so restarts don't lose in-flight work. All handlers live in `Keibai.Core` (never the host project); local queue names carry a `keibai-` prefix. Scheduling via Wolverine scheduled messages or a slim `BackgroundService` that enqueues messages on a cron cadence — your choice, but all real work happens in handlers.
- Keep Marten/Wolverine versions in lockstep with offmarket.deals (`/workspace/offmarket.deals`) — check its csproj before bumping majors. Version divergence is the #1 merge-friction risk.
- **Blazor** (interactive server) for the UI (Phase 3). All pages live in the `Keibai.Web` Razor class library with routes prefixed **`/jp/...`** (the standalone host redirects `/` → `/jp`), and use the RCL's own layout component — never assume a host layout. Components depend on in-process service **interfaces** defined in `Keibai.Web` (mirror OMD's `IDealReader`-style seam), never on `HttpClient` calls to the own API.
- PDF/blob storage behind an `IDocumentBlobStore` abstraction; default implementation = local filesystem with content-addressed paths (`{sha256[..2]}/{sha256}.pdf`). Keep it swappable for S3-compatible/Azure blob storage later.
- xUnit for tests. Parsers must be tested against **fixture files** (raw captured responses committed under `tests/fixtures/`), never against the live site.

### Merge-readiness rules (offmarket.deals)

The OMD host (`/workspace/offmarket.deals/src/OffMarket/Program.cs`) has ONE Wolverine runtime, ONE default Marten store, and ONE `MapRazorComponents<App>()` root. Keibai must slot in beside all three without touching them:

1. **Composition seam.** The standalone host `Program.cs` is a thin shell (~20 lines) over extension methods that ARE the merge artifact: `AddKeibai(this IServiceCollection, IConfiguration)` (ancillary Marten store, blob store, BIT client, options) and `ConfigureKeibaiMessaging(this WolverineOptions)` (handler assembly discovery via a `KeibaiMarker` type, queue policies). Merging = OMD calls these + `opts.Discovery.IncludeAssembly(typeof(KeibaiMarker).Assembly)` + `.AddAdditionalAssemblies(...)` on its existing `MapRazorComponents`.
2. **Config namespace.** Every setting lives under a `Keibai:` section (`Keibai:Ingestion:Enabled`, `Keibai:Ingestion:ArchivePrefectures`, `Keibai:Alerts:...`) so appsettings merge by concatenation.
3. **Auth stays in the host.** The single-shared-password gate is standalone-host middleware only — no auth logic inside `Keibai.Web` components (OMD's cookie auth takes over at merge time).
4. **Route namespace.** `/jp/...` prefix on every RCL page — no route may collide with an OMD page.
5. **Style/build parity.** Copy OMD's `global.json` and `.editorconfig`; replicate its `Directory.Build.props` pattern (EnforceCodeStyleInBuild + GenerateDocumentationFile, conditioned on the `Keibai` project-name prefix). Provide a `test.sh` gate mirroring OMD's (build + test, self-provisioning against the compose Postgres).

### Domain model (Marten documents — refine as reality demands, keep names)

- `Court` — BIT court code, name (JP), prefecture, branch flag.
- `AuctionRound` — court ref, round dates (公告/閲覧開始/入札期間 start-end/開札期日), status.
- `AuctionCase` — case number (structured: era-year, case-type ケ/ヌ, serial), court ref, round ref.
- `PropertyItem` — case ref, 物件番号, type (土地/建物/マンション/一括), raw address, normalized address (best-effort), lat/lng + geocode confidence, 売却基準価額, 買受可能価額, headline attributes (floor area, land area, build year where present), first-seen/last-seen timestamps, `Delisted` flag + reason (takedown-ready even though private).
- `ArchivedDocument` — property/case ref, kind (明細書/調査報告書/評価書/公告/combined), sha256, byte size, source URL, fetched-at, blob path.
- `SaleResult` — case/property ref, 開札 date, 売却価額 (winning bid), bid count, outcome (sold/不売/取下げ/特別売却).
- `CrawlRun` — per-court per-run stats: started/finished, requests made, items found/new/changed, PDFs archived, errors. This document powers monitoring.
- `RawCapture` — url, fetched-at, content hash, blob path of raw bytes.

### Repo conventions

- Repo root: `/workspace/keibai`. Solution `Keibai.slnx`, mirroring OMD's topology:
  - `src/Keibai` — thin host shell (composition root only: calls the `AddKeibai`/`ConfigureKeibaiMessaging` extensions, maps the RCL pages, hosts the shared-password middleware).
  - `src/Keibai.Web` — Razor class library: Blazor pages/components + the in-process service interfaces they consume.
  - `src/Keibai.Core` — domain documents, parsers, BIT client, Wolverine message handlers. No host concerns.
  - `tests/Keibai.Tests` — host/integration tests (Alba boots the real `Program`); `tests/Keibai.Web.Tests` — bUnit component tests (no host reference).
- `docker-compose.yml` for Postgres; `README.md` with run instructions; `.editorconfig` (copied from OMD); secrets/config via appsettings + user-secrets, never committed.
- Conventional commits, small and frequent.

---

## Phase 1 prompt — Recon, scaffold, and listing ingestion ✅ DONE (2026-07-13)

> **Goal:** a running host that discovers all courts and auction rounds, ingests every active listing nationwide into Marten, and can prove its data matches the BIT website.
>
> Steps, in order:
>
> 1. **Recon (do this before writing production code).** Using Playwright (or careful manual HTTP), walk BIT's search flow: top page → region → court → listing → property detail. Capture every request: method, URL, headers, body, response shape. Document the flow in `docs/bit-api.md` — endpoint by endpoint, with real (redacted-if-needed) request/response samples saved as fixture files. Identify: how courts are enumerated, how a court's current listings are paged, how property detail (incl. document download URLs) is fetched, and any session/cookie/token requirements. Deliverable: `docs/bit-api.md` complete enough that a stranger could re-implement the client from it.
> 2. **Scaffold** the solution per Repo conventions: host boots with Marten (auto schema), Wolverine (durable local queues), health endpoint `/healthz`, docker-compose Postgres.
> 3. **BIT client** in `Keibai.Core`: typed methods over the recon'd endpoints, built on `IHttpClientFactory` + Polly, with the global 1-req/3s rate limiter enforced in ONE place (a delegating handler), raw-capture-before-parse, and the kill-switch check.
> 4. **Ingestion pipeline** as Wolverine messages: `SyncCourts` → `SyncCourtSchedule(courtCode)` → `SyncRoundListings(roundId)` → `SyncPropertyDetail(caseId, itemNo)`. Handlers upsert Marten documents idempotently (natural keys: court code, case number + 物件番号). Re-running any message must be safe.
> 5. **Scheduler**: nightly full sweep (schedules + listings for all courts), enqueued at 01:00 JST. A manual trigger endpoint or console command for one court (`sync-court 3110` style) for testing.
> 6. **Validate on ONE court first** (Tokyo District Court), compare ingested listing count and 5 random properties field-by-field against the website manually, record the comparison in `docs/validation-phase1.md`. Only then run the nationwide sweep once, and append nationwide totals vs. BIT's own displayed counts.
>
> Acceptance criteria:
> - `docker compose up` + `dotnet run` boots clean; `/healthz` green.
> - `docs/bit-api.md` documents the reverse-engineered flow with fixtures.
> - After one nationwide sweep: every court present, active-listing total within ±2% of BIT's site-wide count, spot-check documented.
> - Parser unit tests pass against fixtures; ingestion messages are idempotent (test proves double-handling causes no duplicates).
> - All crawling rules verifiably enforced (rate limiter unit-tested; kill-switch works).
> - Total requests for the nationwide sweep logged in `CrawlRun` docs — expect low thousands, flag if higher.

---

## Phase 2 prompt — Document archive, sale results, monitoring ✅ DONE (2026-07-15)

> Validated live — see `validation-phase2.md`. 3点セット archiver (same-night, deadline-priority,
> re-check), 売却結果 capture + nationwide backfill, monitoring/alerts (ntfy + SMTP), storage watchdog,
> per-court block auto-disable, and Postgres-durable queues are all implemented and demonstrated.

> **Goal:** never lose a 3点セット again, capture sale results, and make the system tell the operator when something breaks (target: <1 hr/week human attention).
>
> Builds on Phase 1 (read `docs/bit-api.md` first).
>
> 1. **PDF archiver**: new Wolverine message `ArchiveDocuments(caseId, itemNo)` emitted whenever `SyncPropertyDetail` sees a property whose documents aren't yet archived. Download each 3点セット PDF (rate limits apply — PDFs count as requests), verify it's a real PDF (magic bytes, non-trivial size), store via `IDocumentBlobStore` content-addressed, record `ArchivedDocument`. **Priority rule:** properties whose 入札期間 ends soonest are archived first (they're closest to deletion) — implement as queue ordering or scheduled priority, and archive new discoveries same-night, not next sweep.
> 2. **Re-check loop**: documents are sometimes replaced/amended during the viewing window. Re-fetch document metadata mid-window once (config: `RecheckAfterDays`, default 7); if hash differs, archive as a new version (keep both).
> 3. **Sale results**: on each round's 開札期日, schedule `SyncRoundResults(roundId)` for that evening (~18:00 JST, after the 15–16:00 publication). Parse 売却結果 into `SaleResult` (sold / 不売 / 取下げ, winning bid, bid count). Backfill: BIT retains ~3 years of past results — implement `BackfillResults(courtCode)` and run it once nationwide (respecting rate limits; this is the biggest crawl the system will ever do — spread it over multiple nights).
> 4. **Monitoring & anomaly alerts** (the passivity requirement): after each nightly run, a summarizer compares `CrawlRun` stats to trailing norms per court. Alert conditions: court fetch failed after retries; a court's listing count dropped >50% versus its previous non-zero sweep; PDF archive failure rate >5%; any 403/429/block-page response (immediate alert + auto-disable that court's crawling); zero new data nationwide (possible silent breakage). Alerts go to a pluggable `IAlerter` — implement email (SMTP config) and ntfy.sh push; default config = ntfy. **No news = all good; every alert must be actionable.**
> 5. **Storage watchdog**: alert when blob storage exceeds a configured GB threshold (deployment target may be disk-constrained).
> 6. **Ops docs**: `docs/runbook.md` — what each alert means, how to re-run a court, how to replay from `RawCapture`, how to restore a wedged queue.
>
> Acceptance criteria:
> - New property discovered in a sweep has all its PDFs archived within the same night's run (demonstrate with logs on a real court).
> - Sale results captured for at least one real 開札 day; backfill executed for ≥3 courts with counts documented.
> - Kill a court's DNS / return a fake 403 in a test: correct alert fires, court auto-disabled, rest of system unaffected.
> - Restarting the host mid-crawl loses no queued work (durable queues demonstrated).
> - `docs/runbook.md` exists and matches reality.

---

## Phase 3 prompt — Blazor UI: search, detail, alerts

> **Goal:** the owner (a technical user, bilingual EN with JP real-estate context) can find, evaluate, and track auction properties faster than on BIT or 981.jp — locally.
>
> Builds on Phases 1–2. UI language: English labels, Japanese data as-is (case numbers, addresses in original form). No auth beyond a single shared password (personal use), configured via appsettings.
>
> 1. **Search page** (`/jp`, with the host redirecting `/` → `/jp`): filters — prefecture, court, property type, price range (売却基準価額), bidding status (upcoming 閲覧中 / bidding open / past), 開札 date range, free-text over address. Results as a sortable table: case no., type, address, 売却基準価額, 買受可能価額, bid window, days-until-deadline badge. Server-side paging via Marten (compiled queries where hot). Target <200 ms for typical filters.
> 2. **Property detail** (`/jp/case/{caseNumber}/{itemNo}`): all captured fields; document list linking to **locally archived PDFs** (streamed from blob store, never hotlinking BIT); version history if re-check captured amendments; the case's other 物件; sale result once known; external links out to BIT and Google Maps (raw address query — flag geocode confidence).
> 3. **Past results explorer** (`/jp/results`): filter like search; show 売却価額 vs 売却基準価額 ratio, bid counts; per-prefecture summary stats (median ratio, sale rate). Simple tables/sparklines, no chart framework needed.
> 4. **Watchlist + saved searches**: star properties; save any search-filter combo with a name. A nightly Wolverine job diffs saved searches against new data and sends ONE digest notification (via Phase 2 `IAlerter`) listing new matches and watched-property status changes (documents archived / bidding opens in N days / result published). One digest, never per-item spam.
> 5. **Ops dashboard** (`/jp/ops`): last sweep per court (green/amber/red), queue depth, blob storage usage, recent alerts, per-court listing-count sparkline. This page is how the owner confirms in 30 seconds that the system is healthy.
> 6. Polish constraints: fast initial render (server-side), works on mobile Safari (checking a property from the street), Japanese text rendered correctly everywhere (no mojibake in PDFs' filenames or addresses).
>
> Acceptance criteria:
> - From cold start: find all Kanagawa condos under ¥20M with bidding open, open one, read its archived 評価書 PDF — in under a minute, all served locally.
> - Saved search produces a correct digest when new matching data arrives (demonstrate with a real sweep or seeded fixture data).
> - `/jp/ops` accurately reflects a healthy system and a deliberately broken court.
> - Lighthouse/perf sanity: search page interactive fast on a small VPS; no query does a full-collection scan (verify Marten indexes: court code, case number, prefecture, 開札 date, price).

---

## Deliberately out of scope (do not build yet)

- Payments/subscriptions, public deployment, user accounts, SEO pages
- LLM summarization/translation of 3点セット (design the schema so parsed fields can attach to `ArchivedDocument` later)
- PostGIS geo search / map UI (image ready in compose; schema keeps lat/lng)
- 公売 (KSI kankocho / NTA) ingestion
- Any takedown-request workflow beyond the `Delisted` flag (private system)

## Notes for the human operator

- Phase 1's recon step is the riskiest unknown (undocumented JSON flow, possible session tokens). If the HTTP replay proves brittle, the fallback is Playwright-driven crawling at the same rate limits — slower but acceptable at this data volume. *(Resolved: the HTTP replay works — see `bit-api.md`.)*
- Run Phase 2's results **backfill** early; it's ~3 years of history that also eventually expires.
- Storage math: ~11k properties/yr × ~10–50 MB of scanned PDFs ≈ **100–500 GB/yr** if you archive *everything* nationwide (measure the real average during Phase 2's first weeks — it drives the hosting decision). On constrained hardware, either mount external storage, point `IDocumentBlobStore` at S3/B2, or config-limit archiving to selected prefectures initially (`Keibai:Ingestion:ArchivePrefectures`).
