# Keibai

Personal-use archive + search for Japanese court real-estate auctions (競売). Ingests listings from
**BIT** (the Supreme Court's 不動産競売物件情報サイト), archives the 3点セット PDFs before the courts
delete them, and (Phase 3) lets you browse/search locally. **.NET 10 · Blazor · Wolverine · Marten ·
PostgreSQL.** No monetization, no public deployment — designed to later merge into `offmarket.deals`
as the `jp.` market.

> Phases 1, 2 **and 3** are done: recon + scaffold + nationwide listing ingestion (Phase 1); the
> 3点セット PDF archive, 売却結果 sale-results capture/backfill, monitoring/alerts, and durable queues
> (Phase 2); and the **Blazor search/browse UI** — search, property detail (with locally-streamed PDFs),
> past-results explorer, ops dashboard, and a watchlist + saved-search nightly digest (Phase 3). All three
> validated live: see `docs/validation-phase{1,2,3}.md`. `docs/agent-prompts.md` has the full plan and
> `docs/runbook.md` the operations.

## Layout

```
src/Keibai         thin ASP.NET Core host shell (composition root + shared-password gate + /healthz)
src/Keibai.Web     Blazor RCL: the /jp pages (search/detail/results/ops/watchlist) + their reader seam
src/Keibai.Core    domain documents, HTML parsers, the BIT client, the Wolverine ingestion pipeline
tests/Keibai.Tests       parser/rate-limit/kill-switch/idempotency + Alba host tests (xUnit v3, MTP)
tests/Keibai.Web.Tests   bUnit component tests
tests/fixtures/bit       raw BIT responses captured during recon (parser tests run against these)
docs/bit-api.md          the reverse-engineered BIT request flow (endpoint by endpoint)
docs/validation-phase1.md  Tokyo validation results
```

The merge into `offmarket.deals` is `copy the Keibai.* projects + ~6 lines`: the host calls
`AddKeibai(...)` + `ConfigureKeibaiMessaging(...)` (the two extension methods that ARE the merge
artifact), registers the ancillary `IKeibaiStore` (schema `keibai`, never the default Marten store),
and adds the RCL assembly to its existing `MapRazorComponents`.

## Run it

Postgres is required. Either use this repo's compose file:

```bash
docker compose up -d db          # imresamu/postgis on localhost:5432, creates the 'keibai' database
```

…or, if `docker compose` is unavailable (e.g. inside the PropertyPartner devcontainer), point at the
devcontainer's shared Postgres and a dedicated database:

```bash
# one-time: create the database on the devcontainer Postgres (host 'db')
PGPASSWORD=postgres psql -h db -U postgres -d postgres -c "CREATE DATABASE keibai"
export ConnectionStrings__Keibai="Host=db;Port=5432;Database=keibai;Username=postgres;Password=postgres"
```

Then:

```bash
dotnet run --project src/Keibai        # boots Marten (auto schema) + Wolverine (durable queues)
curl http://localhost:5199/healthz     # {"status":"ok"}
```

`appsettings.Development.json` already points at `localhost:5432/keibai` and sets
`Keibai:Ingestion:Enabled=true`; `appsettings.json` ships **ingestion disabled** (the kill-switch) and
blank secrets.

### Triggering ingestion

```bash
curl -X POST http://localhost:5199/sync/prefecture/13   # one prefecture (Tokyo) — good for testing
curl -X POST http://localhost:5199/sync/all             # nationwide sweep (hours at the rate limit)

# Phase 2 ops triggers
curl -X POST http://localhost:5199/archive/schedule     # drain the archive backlog (deadline-ordered) + re-checks
curl -X POST http://localhost:5199/results/backfill/31111   # one court's ~3yr 売却結果 history
curl -X POST http://localhost:5199/results/backfill-all     # nationwide results backfill (spread over nights)
curl -X POST http://localhost:5199/results/sync/31111       # one court's latest round results
curl -X POST http://localhost:5199/monitor/run          # anomaly alerts + storage watchdog now

# Derived docs + maintenance (no BIT traffic)
curl -X POST http://localhost:5199/admin/rebuild-derived    # rebuild AuctionCase/AuctionRound + link SaleResults
curl -X POST http://localhost:5199/admin/reparse-details    # re-parse stored detail captures onto existing rows
curl -X POST http://localhost:5199/admin/test-alert         # send a test alert (verify ntfy/SMTP delivery)
curl -X POST http://localhost:5199/admin/run-digest         # run the saved-search + watchlist digest now
```

### Browse it (Phase 3 UI, all under `/jp`)

Open `http://localhost:5199/` (redirects to `/jp`). Static server-rendered, stock Bootstrap, read-only —
the UI never triggers a crawl.

| route | what |
|---|---|
| `/jp` | **Search** — filter by prefecture / court / type / price / bidding status / 開札 date / address; sortable, paged. |
| `/jp/property/{courtId}/{saleUnitId}` | **Detail** — every captured field + per-物件 attribute tables, archived 3点セット PDFs (streamed locally via `/jp/doc/{court}/{unit}/{sha}`, never hotlinking BIT), version history, the case's other 物件, sale result, Google Maps / BIT links, watchlist star. |
| `/jp/results` | **Past results** — 売却結果 with winning-bid ratio + bid counts, and a per-prefecture sale-rate / median-ratio summary. |
| `/jp/ops` | **Ops dashboard** — storage vs threshold, queue depth, today's activity, per-prefecture traffic-light health + sparklines, disabled courts, recent alerts. |
| `/jp/watchlist` | **Watchlist + saved searches** — starred properties and named searches (run / delete); saved searches feed the nightly digest. |

A **07:00 JST** job also rebuilds the derived `AuctionCase`/`AuctionRound` documents nightly, an
**08:00 JST** job sends the saved-search + watchlist digest (one alert, only when something's new), and an
**18:00 JST** job syncs each 開札 day's `売却結果` the evening they're published.

A nightly sweep fires automatically at **01:00 JST**, and archive-backlog/results-sync/monitor at
**07:00 JST** (`NightlySweepScheduler`). New discoveries are archived the same night; sweeps + archives
run on one durable, strictly-sequential queue, so a restart never loses in-flight work.

## Crawling rules (non-negotiable — this is a court system)

Enforced in code, not by convention: **1 request / 3 s, single-threaded** (one global rate limiter in
a delegating handler), honest UA `keibai-personal-archive/0.1`, no proxies/UA-rotation, exponential
backoff (max 3 retries), every raw response stored before parsing, and a single kill-switch
(`Keibai:Ingestion:Enabled=false`) that stops all outbound traffic. A 403/429/block-page stops the
crawl and surfaces — it is never retried around.

## Tests

```bash
bash test.sh    # self-provisions the keibai db, builds with the style gate, runs both test projects
```

> `test.sh` runs the Microsoft.Testing.Platform test executables directly. The `dotnet test` CLI
> currently has a handshake bug with the MTP runner pinned in `global.json`, so invoke the gate via
> `test.sh` (or run the built `tests/*/bin/.../<Project>` binary) rather than `dotnet test`.

## Config keys (all under `Keibai:`)

| key | meaning |
|---|---|
| `ConnectionStrings:Keibai` | the `keibai` Postgres database |
| `Keibai:Ingestion:Enabled` | master kill-switch (default **false**) |
| `Keibai:Ingestion:MinRequestInterval` | rate floor (default `00:00:03`) |
| `Keibai:Ingestion:MaxRetries` | Polly retries (default 3) |
| `Keibai:Ingestion:ArchivePrefectures` | prefectures to archive PDFs for; empty = all (disk guard) |
| `Keibai:Ingestion:RecheckAfterDays` | days after first archive to re-check a 3点セット once (default 7) |
| `Keibai:BlobStore:Root` | local content-addressed blob root (raw captures + PDFs) |
| `Keibai:Alerts:Providers` | alert sinks: `ntfy` (default) / `smtp` / `log` |
| `Keibai:Alerts:Ntfy:Topic` | ntfy.sh topic to publish alerts to (**change the placeholder**) |
| `Keibai:Alerts:Smtp:*` | SMTP host/port/from/to/credentials (opt-in email alerts) |
| `Keibai:Storage:MaxGigabytes` | blob-storage watchdog threshold (0 disables) |
| `Keibai:Auth:SharedPassword` | single shared-password gate; blank = open (dev) |

> Durable Wolverine queues are Postgres-backed (schema `keibai_wolverine`) in the standalone host — wired
> in `Program.cs` only, so it stays out of the merge artifacts (offmarket.deals supplies durability from
> its own store at merge time).
```
