# Phase 2 validation — archive, sale results, monitoring, durability

Validated against the **live BIT site** on 2026-07-15, running the real host (`dotnet Keibai.dll`,
`ASPNETCORE_ENVIRONMENT=Production`) with `Keibai:Ingestion:Enabled=true`,
`Keibai:Ingestion:ArchivePrefectures=[13]` (Tokyo, disk guard), blob root `/workspace/keibai-blobstore`,
against the devcontainer Postgres (`db:5432`, database `keibai`). All BIT traffic single-threaded at
≥3 s spacing with the honest UA `keibai-personal-archive/0.1`; **0 blocks (403/429/block-page)
encountered**, 0 retries-around-a-block.

## 1. PDF archive — same-night, on a real court

Triggered a live Tokyo sweep (`POST /sync/prefecture/13`). Property detail sync parsed the bidding
window (39 of 41 Tokyo properties carried a full 入札期間/開札期日 — the 2 without were skipped as
out-of-window), then archived each property's 3点セット **the same run it was discovered**.

| metric | value |
|---|---|
| 3点セット PDFs archived | **39** |
| Archive attempts / failures | 39 / **0** (100 % success) |
| 3点セット found already-deleted | 0 |
| Total archived bytes | **91.0 MB** (blob store incl. raw captures: 103 MB) |
| Sample PDF magic bytes | `%PDF-1.6` (verified real PDF; e.g. `31131:00000021309` = 2,346,213 bytes) |
| Archive download requests (`pd001/h04`) | 78 (availability `h03` + download `h04` per property) |

Content-addressed storage (`{sha[..2]}/{sha}.pdf`) with a per-property `ArchivedDocument`
(`{PropertyItemId}:{sha}`). `DailyStats` for 2026-07-15: 39 attempts, 39 archived, 0 failures.

## 2. Durability — restart mid-crawl loses no queued work

Wolverine durable local queues are backed by Postgres (`PersistMessagesWithPostgresql`, schema
`keibai_wolverine`). Mid-archive, the host was **`kill -9`'d**:

| checkpoint | ArchivedDocuments | unhandled durable envelopes |
|---|---|---|
| at crash (`kill -9`) | 13 | **26** (persisted in Postgres) |
| host down, +5 s | 13 (frozen — no processing) | 26 |
| after restart (no re-trigger) | 13 → 24 → **39** | 26 → 0 (drained) |

The 26 in-flight messages survived the crash and **resumed automatically on restart** (26 more archives
completed post-restart, clean — no Wolverine recovery errors). No queued work was lost.

## 3. Sale results — backfill for 3 courts across 3 prefectures

Reverse-engineered the 売却結果 flow (see `docs/bit-api.md`) and backfilled each court's full retained
history (`POST /results/backfill/{courtId}`, chunked per-page via `resultlist/pr002/h03`):

| court | name | prefecture | results | sold (売却, w/ winning bid) |
|---|---|---|---|---|
| 31111 | 東京地方裁判所本庁 | 13 東京 | **148** (15 pages) | 116 |
| 31211 | 横浜地方裁判所本庁 | 14 神奈川 | **101** | 72 |
| 31311 | さいたま地方裁判所本庁 | 11 埼玉 | **61** | 49 |
| **total** | | | **310** | 237 |

Every 売却 outcome carried a parsed winning bid (116/116, 72/72, 49/49). Idempotent on
court+case+物件番号 (311 rows processed → 310 distinct stored; the 1 duplicate upserted, not
re-inserted). Outcomes observed: 売却 / 特別売却 / 取下げ / 不売.

## 4. Monitoring, alerts, storage watchdog

`POST /monitor/run` summarized the run: **50 prefectures, blob store 0.1 GB** (under the 20 GB
threshold → no storage alert). It raised 6 actionable **Warning** alerts of the form "Prefecture NN
listings dropped >50%" — these are correct detections against Phase 1's noisy CrawlRun history (multiple
buggy-then-fixed passes on 2026-07-13 left several prefectures with a higher earlier count than a later
one). No CRITICAL false positives (no zero-nationwide, no block). In steady-state nightly operation the
whole nation sweeps once per night, so the night-over-night comparison is clean; the noise here is a
one-off artifact of Phase 1's repeated same-window passes.

Block handling (403/429 → immediate critical alert + auto-disable that court, rest unaffected, never
retried) is covered by unit test `ArchiveHandlerTests.A_block_disables_the_court_alerts_and_does_not_retry`
(no block was encountered live, so it was not exercised against BIT — as intended).

## Acceptance criteria — status

- [x] New property discovered in a sweep has all its PDFs archived within the same night's run — **39/39 Tokyo, logged, valid PDFs.**
- [x] Sale results captured; backfill executed for ≥3 courts with counts documented — **3 courts / 3 prefectures, 310 results.**
- [x] Fake 403 → correct alert fires, court auto-disabled, rest unaffected — **unit-tested** (`A_block_disables_the_court...`).
- [x] Restarting the host mid-crawl loses no queued work — **`kill -9` at 13 archives; 26 durable messages resumed to 39.**
- [x] `docs/runbook.md` exists and matches reality.

## Gap closures (2026-07-15, follow-up pass)

The remaining Phase-1/2 gaps were closed and validated live:

- **AuctionCase + AuctionRound documents** (the missing domain model). `RebuildDerivedDocuments`
  (`POST /admin/rebuild-derived`, non-BIT) materializes them from the property store. Live:
  **1,015 AuctionCases** (67 multi-物件, e.g. 令和07年(ケ)第5号 = 18 items) and **128 AuctionRounds**
  keyed on court + 開札期日 with a derived lifecycle (viewing 80 / bidding 33 / closed 14 / opened 1).
- **SaleResult → PropertyItem linkage + 開札-date inheritance**. The rebuild links each result on
  court + normalized case + 物件番号. Live: **43 results linked, 32 inherited an 開札 date** (e.g.
  令和07年(ケ)第600号 → property `31111:00000079036` → 2026-07-29). The rest are historical results whose
  property is long delisted — no property to link to (the documented, unavoidable limit).
- **Round-keyed, evening results scheduling**. `ScheduleResultsSync` now enqueues one
  `SyncRoundResults(court, 開札 date)` per distinct round (`DueRounds`), and a new **18:00 JST** scheduler
  job makes it the primary trigger (evening of 開札, after BIT publishes), with 07:00 as morning catch-up.
- **Real alert delivery**. `POST /admin/test-alert` sent a live alert through `NtfyAlerter` and it was
  **confirmed received on ntfy.sh** (title, priority 3). (SMTP stays config-gated; not exercised.)
- **Re-check loop, live**. Backdated an archived Tokyo property's `LastArchivedAt` to 8 days ago;
  `ScheduleArchiveWork` found "1 to re-check", re-fetched the 3点セット, and — hash unchanged — recorded
  `LastRecheckedAt` + `RechecksPerformed=1`, `AmendmentsCaptured=0` (no spurious new version).
- **Nationwide results backfill executed**. `POST /results/backfill-all` fanned out to **98 courts** and
  ran the full per-court/per-page crawl at 1 req/3s (durable — it survived a `kill -9` restart mid-run and
  resumed). Reached **95 courts / ~1,380 sale results (855 sold)** and finishing the tail in the
  background — the "biggest crawl the system does," spread over time as designed. A re-run of the rebuild
  then linked **82** of those results to tracked properties (up from 43 as coverage grew).
- **Attribute enrichment** (separate pass, `docs/data-profile.md`): every property now captures the full
  per-物件 detail attribute set (37 labels) + typed rollups; a 戸建て mis-classification was fixed
  (748 properties re-typed, classification 96 % → 100 %).

## Notes / follow-ups

- Backfilled `SaleResult.OpeningDate` is still null for results with no surviving property (historical
  auctions); linkage fills it only where the property is still tracked. Case re-auctions collapse under
  the court+case+item key — acceptable for the MVP (the full-history view lists final outcomes).
- Nationwide PDF archiving remains disk-gated to `ArchivePrefectures` (Tokyo here). Tokyo's 3点セット
  averaged ~2.3 MB each (39 → 91 MB), far below the 10–50 MB/property worst case — measure other
  prefectures before widening.
- Genuinely unavailable in BIT (need a geocoder, not a crawl): normalized/住居表示 address and geocode
  confidence. Everything else the specs named is now captured.
