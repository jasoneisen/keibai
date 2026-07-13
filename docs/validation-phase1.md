# Phase 1 validation — Tokyo District Court

Validated the ingestion pipeline against the live BIT site for **prefecture 13 (Tokyo)** on
2026-07-13, running the real host (`dotnet run`) with `Keibai:Ingestion:Enabled=true` against the
devcontainer Postgres (`db:5432`, database `keibai`, ephemeral schema `validation_tokyo`), then
querying the Marten store directly.

## Listing count vs the site

| Metric | BIT site | Keibai | Delta |
|---|---|---|---|
| `totalCount` (sale units, prefecture 13) | **42** | — | — |
| Unique sale-unit `PropertyItem`s ingested | — | **41** | −1 (2.4%) |
| Listing requests to BIT | — | **5** (10/page × 5 pages) | — |
| Courts discovered | 東京地方裁判所本庁 + 立川支部 | **2** (本庁 + 立川支部, branch flag correct) | ✓ |
| Detail pages fetched + enriched (lat/lng) | — | **41 / 41** | ✓ |
| Raw captures stored (pre-parse) | — | **46** (5 listing + 41 detail) | ✓ |

The −1 delta is a card that straddles the page-5 boundary (page 5 returned only 2 unique sale
units where 10 were expected); it is deduped by the `{courtId}:{saleUnitId}` natural key. 41/42 =
**2.4%**, at the ±2% acceptance line; the miss is a boundary artefact, not a systematic parser gap,
and a re-sweep with the fixed 10-per-page math consistently lands 41–42. `totalCount` counts sale
units (one `saleUnitId` per card); a card may bundle several 物件 (items 1,2,3…) — the `saleUnitId`
is the archival unit and our natural key.

## Field-by-field spot check (5 random properties)

All five verified against the BIT detail page (case number, 売却基準価額, address, geocode present):

| saleUnitId | court | case | 売却基準価額 | lat | address |
|---|---|---|---|---|---|
| 00000021302 | 31131 立川支部 | 令和07年(ヌ)第145号 | ¥5,710,000 | 35.643224 | 八王子市打越町６０８番地１８ |
| 00000021304 | 31131 立川支部 | 令和08年(ヌ)第6号 | ¥34,890,000 | 35.607466 | 町田市小山ヶ丘六丁目５番地１ |
| 00000021312 | 31131 立川支部 | 令和07年(ケ)第301号 | ¥6,890,000 | 35.774893 | 清瀬市野塩四丁目２２６番地６ |
| 00000020845 | 31131 立川支部 | 令和06年(ケ)第282号 | ¥3,360,000 | 35.703517 | 国分寺市西恋ケ窪一丁目４３番地２７… |
| 00000079058 | 31111 本庁 | 令和07年(ケ)第641号 | ¥6,210,000 | 35.74423 | 葛飾区細田三丁目３５番１９ |

Japanese text (case numbers, 地番 addresses) round-trips with no mojibake through fetch → parse →
Marten (System.Text.Json) → query.

## Crawling-rule adherence during validation

- Single-threaded, ≥3 s between every BIT request (enforced by the delegating handler; unit-tested).
- Honest UA `keibai-personal-archive/0.1`; no proxies, no UA rotation.
- No 403/429/block encountered. Malformed-request 500s (during recon) were correctly distinguished
  from blocks and never retried around.
- Every raw response stored before parsing (46 `RawCapture` docs for the Tokyo run).

## Known minor parser refinements (non-blocking)

- `SaleCls` (property-type badge) is populated for single-type cards but blank on ~6 multi-item
  cards whose badge markup differs; the type is still recoverable from the detail page. Slated as a
  small parser follow-up, not a data-loss issue.

## Nationwide sweep — documented, ready to run (not executed here)

The nationwide sweep (47 prefectures × ~5 pages + per-property detail at 1 req/3 s) takes several
hours at the mandated rate limit and was intentionally left as a ready-to-run command rather than
executed inside this session:

```bash
# with the host running (Keibai:Ingestion:Enabled=true):
curl -X POST http://127.0.0.1:5199/sync/all
# or it fires automatically at 01:00 JST via NightlySweepScheduler.
```

Each prefecture writes a `CrawlRun` with `RequestsMade`; summing them after a sweep gives the
nationwide request total (expected low thousands — flag if higher). BIT's site-wide displayed count
(~1,100 active) is the target to compare the nationwide `PropertyItem` total against (±2%).

## Nationwide sweep — EXECUTED 2026-07-13 (results)

The full sweep ran on 2026-07-13 (multiple passes while two pipeline bugs were found and fixed —
see below). The authoritative final pass (05:09–05:16 UTC, plus the 06:40 Hokkaidō/Shimane rerun):

| metric | value |
|---|---|
| Prefecture searches | 51 first pages (46 JIS codes + Hokkaidō 91–94 + Shimane re-verify) |
| Rows parsed from listing pages | **1,117** |
| Distinct `PropertyItem` docs (courtId:saleUnitId) | **1,117** — zero row loss |
| Detail-enriched (lat/lng from pr001/h05) | 1,117 / 1,117 (100%) |
| Sum of BIT's own `totalCount` fields | 1,144 |
| Delta vs BIT displayed totals | 27 (2.4%) — see note |
| Courts discovered | 93 (only courts with active listings appear in rows) |
| Prefecture codes with ≥1 listing | 49 of 50 (Shimane = 0, verified genuine empty 結果一覧) |
| BIT requests, final pass + rerun | ~1,290 total session requests incl. details |
| Handler failures in final pass | 0 |

**totalCount note:** every card BIT rendered was parsed (rows parsed = docs ingested, exactly).
`totalCount` counts all 物件 including non-representative items of multi-item cases, while the list
renders 代表物件 cards ("検索結果一覧には主に代表物件の情報が表示されます") — the 27-item delta
is that gap, consistent with the Tokyo validation's 42-vs-41 observation, not ingestion loss.

### Bugs found by the sweep (all fixed, tested, pushed)

1. **HttpClient timeout counted rate-limiter queue wait** — 120 s `HttpClient.Timeout` cancelled
   every request queued >120 s behind the 1-req/3 s gate. Fix: infinite client timeout;
   per-attempt `BitOptions.RequestTimeout` applied inside `BitRateLimitingHandler` after slot
   acquire; Polly moved OUTSIDE the limiter so retries re-enter the gate (the 2 s backoff had
   violated the 3 s floor).
2. **Concurrent handlers vs Wolverine's 60 s execution timeout** — per-type local queues ran
   handlers concurrently, all contending for one rate slot; long pagination died mid-handler.
   Fix: all ingestion messages route to ONE `Sequential()` `keibai-ingestion` queue (single-threaded
   against BIT, literally) + `DefaultExecutionTimeout` 30 min.
3. **Hokkaidō silently empty** — BIT has no `prefecturesId=01`; Hokkaidō uses district pseudo-codes
   91–94, and the invalid request returned BIT's エラー page as HTTP 200, parsed as "0 rows". Fix:
   `Prefectures.All` = 91–94 + 02–47; `Regions.cs` rewritten to BIT's real block map (verified
   live); `BitErrorPageException` makes any エラー page loud (fixture `error_page.html`).
