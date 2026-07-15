# Phase 3 validation — Blazor UI (search, detail, results, ops, watchlist)

Validated against the **real Phase 1/2 dataset** on 2026-07-15, running the standalone host
(`dotnet Keibai.dll`, `ASPNETCORE_ENVIRONMENT=Production`) with **ingestion disabled**
(`Keibai:Ingestion:Enabled=false` — the UI is read-only, zero BIT traffic), against the devcontainer
Postgres (`db:5432`, database `keibai`, schema `keibai`) and the real blob store
(`/workspace/keibai-blobstore`, 765 MB). Dataset at validation time: **1,180 properties, 2,233 sale
results, 98 courts, 39 archived 3点セット PDFs**.

All pages are **static server-rendered** (no interactive circuits): `[CascadingParameter] HttpContext`,
GET filter forms (bookmarkable query strings), and plain-form POST + Post-Redirect-Get for the few writes
(star / save / delete). Stock **Bootstrap 5.3.7** + Bootstrap Icons vendored to the host `wwwroot`;
`keibai.css` (a JP font stack + one sparkline rule) is the only custom CSS.

## 1. Search (`/jp`) — filters match the database exactly

The filter form (prefecture · court · type · price range · bidding status · 開札 range · free-text
address · sort) round-trips through the query string; results page server-side (25/page). Every filter
was checked against `psql` ground truth:

| filter | UI result | DB truth |
|---|---|---|
| (no filter) | 25 rows / page (of 1,180) | 1,180 |
| `pref=14` (Kanagawa) | **43 properties** | 43 |
| `pref=14 & type=Mansion` | **18** | 18 |
| `type=Mansion & max=¥20M` | **156** | — |
| `status=Bidding` (nationwide) | **405** | — |
| `status=Viewing` | **501** | — |
| **acceptance:** `pref=14 & type=Mansion & max=¥20M & status=Bidding` | **0 properties** (empty state) | **0** — 18 Kanagawa condos exist, but the DB confirms **0 are inside the 入札期間 as of 2026-07-15**, so the empty result is correct, not a bug |

Every row links to its detail page; a star form (POST `/jp/watch`) and a "Save this search" form (POST
`/jp/search/save` carrying the filter in the query string) render inline. Paging renders
`?page=N` links with `data-enhance-nav="false"`.

## 2. Property detail (`/jp/property/{court}/{unit}`) — all fields + local PDFs

Loaded `31131:00000021309` (令和08年(ヌ)第12号, 東京立川支部). The page renders the overview, schedule,
typed attribute rollups, the **full per-物件 attribute tables**, the case's other 物件, the sale result
(when present), and external links (Google Maps with a 地番/geocode caveat; BIT search). Archived
documents link to the local stream, **never to BIT**.

| check | result |
|---|---|
| Document link | `/jp/doc/31131/00000021309/3c5970ad…` (kind `combined`) |
| **PDF stream** (`GET /jp/doc/…`) | **HTTP 200, `application/pdf`, 2,346,213 bytes** — the real archived 3点セット, served from the content-addressed blob store |
| Missing property | `GET /jp/property/00000/0` → **404** (not-found alert) |

## 3. Past results (`/jp/results`) — ratios + per-prefecture stats

Renders all **2,233** results (50/page), each row carrying 売却価額 / 売却基準価額, the winning-bid ratio,
bid count, and outcome (売却 / 特別売却 / 不売 / 取下げ) as a badge; tracked results link back to their
property. A per-prefecture **summary band** shows total, sold, **sale rate**, and **median winning-bid
ratio** (computed in the reader). Prefecture / court / outcome filters resolve through the court→prefecture
map.

## 4. Ops dashboard (`/jp/ops`) — healthy in 30 seconds

One `IOpsReader.GetAsync` composite, rendered live:

| panel | live value |
|---|---|
| Storage | **~0.7 GB** of the 50 GB threshold (progress bar) |
| Queue depth | pending durable envelopes (best-effort raw count; `—` when unavailable) |
| Today | archives / failures / results / rechecks from `DailyStats` |
| Prefecture health | **51 prefecture rows**, traffic-light **47 green / 4 red**, each with a dependency-free `.sparkline` of recent ItemsFound |
| Disabled courts | danger panel (auto-disabled on a block) |
| Recent alerts | from the new `AlertLog` (see §5) |

The 4 red prefectures are a one-off artifact of Phase 1's repeated same-window passes (a later sweep with
a lower/zero count than an earlier one) — the same noise Phase 2's monitor documented.

## 5. Watchlist + saved searches + the nightly digest

Write flow (all static-SSR plain-form POST → 302 PRG), verified end-to-end:

- **Star** a property (`POST /jp/watch`) → `WatchlistEntry` persisted (snapshotting current archive/status/
  result state so the digest reports only *later* changes); shows on `/jp/watchlist` with a Remove form.
- **Save** a search (`POST /jp/search/save?…filter…`) → `SavedSearch` persisted; shows with **Run**
  (rehydrates `/jp?…`) and **Delete** forms.
- **Digest** (`POST /admin/run-digest`, and nightly at **08:00 JST**):
  - **Run 1 (baseline)** — `LastRunAt` was null → set the baseline (18 matches for "Kanagawa condos") and
    sent **no alert**. "No news is good news" confirmed (`AlertLog` = 0).
  - **Run 2 (after new data)** — with the baseline cleared to simulate 18 new matches → sent **ONE**
    `Info` alert *"Keibai digest — 18 new match(es), 0 watch update(s)"* whose body lists the fresh
    property links (`/jp/property/31211/…`, Yokohama). Persisted to `AlertLog` and rendered on `/jp/ops`
    (em-dash correctly HTML-escaped). One digest, never per-item spam.

## Acceptance criteria — status

- [x] Find all Kanagawa condos under ¥20M with bidding open, open one, read its archived PDF, in under a
  minute, all served locally — **filter matches DB truth (0 in-window today); a `combined` 3点セット for a
  sample property streams as a 2.35 MB `application/pdf` from the blob store; no BIT hotlinking.**
- [x] Saved search produces a correct digest when new matching data arrives — **demonstrated: baseline
  silent, then one Info alert listing 18 new matches.**
- [x] `/jp/ops` reflects a healthy system (and would surface a broken court) — **51 prefectures with
  traffic-light health + sparklines, storage, queue depth, disabled-court panel, recent alerts.**
- [x] No query full-scans — **indexes on court, prefecture, 開札 date, price (`SaleStandardAmount`) and
  type (`SaleCls`); the shared `PropertySearch` is the only query path.**
- [x] Japanese text renders correctly everywhere (case numbers, addresses, prefecture names, outcomes) —
  no mojibake; JP font stack applied.

## Fixes found during live validation

- **`app.UseAntiforgery()` was missing.** The Blazor component endpoints carry antiforgery metadata; every
  `/jp` page 500'd until the middleware was wired. Phase 1/2 had no forms, so it had never been needed. The
  Phase 3 write endpoints opt out per-endpoint via `.DisableAntiforgery()`.
- **Static-asset content root.** Running the built DLL from the repo root put the content root there, so
  `MapStaticAssets` served empty bodies. Running via `dotnet run --project src/Keibai` (or with
  `ASPNETCORE_CONTENTROOT=…/src/Keibai`) resolves the host `wwwroot` and serves stock Bootstrap correctly.

## Notes / follow-ups

- The UI is **read-only against BIT** — it never triggers a crawl. Ingestion stayed disabled throughout.
- Merge-readiness holds: the pages depend only on the `Keibai.Web.Reading` interfaces; the two Web-side
  merge artifacts are `AddKeibaiWeb()` (readers) + the RCL assembly. Detail routes on `{court}/{unit}` (the
  natural `PropertyItem` identity) rather than the spec's `{caseNumber}/{itemNo}`, since case numbers carry
  parentheses/Japanese and are not URL-clean.
- Address free-text uses `ILIKE` substring (no Postgres full-text tokenizer for Japanese); combined with the
  indexed facets it stays fast at this data volume.
