# BIT reverse-engineered request flow

> Source: https://www.bit.courts.go.jp — the Supreme Court's official auction-property
> site (不動産競売物件情報サイト), operated by 株式会社日立社会情報サービス.
> Backend is **Spring Boot** (server-rendered Thymeleaf pages + a few AJAX/JSON endpoints),
> fronted by a CDN (`Via: JSTCDN`). Front end is jQuery 3.5.1 + Bootstrap.
>
> **All observations below were captured live on 2026-07-13 at ≤1 request / 3 s with the
> honest User-Agent `keibai-personal-archive/0.1`.** Raw captures are committed under
> `tests/fixtures/bit/`. 16 requests total were made during recon.

## TL;DR — the whole flow

```
GET  /                                 301 → /app/
GET  /app/                             302 → /app/top/pt001/h01
GET  /app/top/pt001/h01                200  home page (region map, sets Counter cookie)
POST /app/area/pk001/h01               200  region-block map (blocks 01–09)
POST /app/area/pk001/h02               200  search-condition page for a block (競売物件検索)
POST /app/areaselect/ps002/h05         200  RESULT LISTING (searchType=1, prefecturesId=NN)
POST /app/search                       200  pagination of the result listing
POST /app/propertyresult/pr001/h05     200  PROPERTY DETAIL (saleUnitId + detailCourtId)
POST /app/detail/pd001/h03             200  "success" — 3点セット availability check (AJAX)
GET  /app/detail/pd001/h04?courtId=&saleUnitId=   200  the 3点セット PDF (application/pdf)
```

The URL scheme is Spring controller-style: `/app/{module}/{ctrl}{nnn}/h{nn}`, where `hNN`
is the handler/screen-transition number. Almost everything is **POST of a full form** — the
backend rejects (HTTP 500 "エラーが発生しました") a POST that is missing fields it expects,
so the client must submit the *entire* form field-set, not a hand-picked subset. There is no
CSRF token and no auth; the only cookies are `Counter`/`lastTime`/`lastHours` (a visit
counter, `Max-Age=3600`, not required for the flow to work — the flow replays fine with an
empty cookie jar).

## Session / cookies / tokens

- No login, no CSRF token, no bearer/session token in any request body.
- On the home page the server sets `Counter`, `lastTime`, `lastHours` (HttpOnly, 1 h TTL).
  These are **not** required — every downstream POST succeeds with an empty cookie jar. We
  still carry the cookie jar to look like a normal browser and to avoid re-counting.
- `X-Requested-With: XMLHttpRequest` is sent on the AJAX availability check (`h03`) but is
  not enforced.

## Step 1 — Home / entry (`GET /app/top/pt001/h01`)

`tests/fixtures/bit/top_pt001_h01.html`. Contains:
- A national region map. Each region is an `<a onclick="tranAreaMap('NN','property')">`
  where `NN` ∈ `01..09` is a **block** (地域ブロック): 01 北海道, 02 東北, 03 関東, 04 中部,
  05 近畿, 06 中国, 07 四国, 08 九州, 09 沖縄 (ordering inferred from map position).
- Two forms: `topForm` → `POST /app/top/pt001/h02` (region drill-in from the map),
  `headerForm` → `POST /app/area/pk001/h01` (the "検索" nav entry).
- `top.js` (`../../top/top.js` → `/app/top/top.js`) defines `tranAreaMap(blockCls, tabId)`,
  `tranResult(prefecturesId, courtId, saleScdId, saleCls, tabId)` — all just set hidden
  inputs and submit `#topForm`.

## Step 2 — Region → prefecture/court (`POST /app/area/pk001/h01` → `h02`)

`tests/fixtures/bit/area_pk001_h01.html`. `/app/area/pk001/h01` renders the block map;
each block is `<a onclick="transProperty('NN')">` which sets `#blockCls` and POSTs
`#areaForm` → `/app/area/pk001/h02`.

`POST /app/area/pk001/h02` (body: `blockCls=03&tabId=property`) returns the **search-condition
page** (`tests/fixtures/bit/search_condition_ps002.html`, title 競売物件検索). This is the big
form (`searchareaselectForm`, ~70 top-level inputs + a `detailAreaInfoDto.*` sub-object with
~2900 fields once the per-type panels are counted). Relevant hidden fields:

| field | meaning |
|---|---|
| `blockCls` | region block 01–09 |
| `prefecturesId` | JIS prefecture code `01`–`47` (Tokyo = `13`) |
| `searchType` | **`1` = prefecture-level search, `2` = block/all-area** |
| `saleCls` (checkbox ×4) | property type: 1 土地 / 2 戸建 / 3 マンション / 4 その他 |
| `saleClsList` | comma-joined selected types, e.g. `1,2,3,4` |
| `municipalityId`, `areaIdList` | optional narrowing to city/ward |
| `saleStandardAmountCls` + `...TextMin/Max` | 売却基準価額 range filter |

The inline JS defines the submit targets:
- `tranAllAreaSearch()` → sets action `/app/areaselect/ps002/h05`, submits (**this is the search**).
- `areaLinkSearch(municipalityId, municipalityNm)` → action `/app/areaselect/ps002/h10` (city drill-in).
- `tranResultSearch('2')` → 売却結果 (sale results) search (Phase 2).

## Step 3 — Result listing (`POST /app/areaselect/ps002/h05`)

`tests/fixtures/bit/results_ps002_h05_tokyo.html` (title 競売物件検索：結果一覧, 610 KB for Tokyo).
Body = the **entire** `searchareaselectForm`, with `searchType=1`, `prefecturesId=13`,
`saleCls` ×4 checked, `saleClsList=1,2,3,4`.

> ⚠️ A POST that omits the `detailAreaInfoDto.*` field-set returns HTTP 500. Send every field.

The listing carries a `propertyResultForm` (`action=/app/search`) whose hidden fields ARE the
pagination + search-state envelope. Observed values for Tokyo:

| field | value | meaning |
|---|---|---|
| `totalCount` | `42` | total 物件 for prefecture 13 |
| `pageSize` | `10` | rows per page (also selectable 10/20/30) |
| `currentPage` | `1` | 1-based page index |
| `pageListChangeFlg`, `navigationFlg` | | paging control flags |
| `conditionShowFlag` | `H28` | opaque server state token echoed back |

**Pagination** (CORRECTED after live testing — the results-page `<form action="/app/search">`
default is a decoy; `/app/search` returns a Spring **404 JSON** on POST):

- **Page 1** comes from the search itself (`POST /app/areaselect/ps002/h05`).
- **Pages ≥2** come from **`POST /app/propertyresult/pr001/h39`**, replaying the *previous* page's
  full `propertyResultForm` envelope with `currentPage` set (and `pageListChangeFlg=0`,
  `resultListSearchButtonFlag=0`). Verified: page 2 returns `currentPage=2` and a distinct set
  of sale units.
- **BIT returns a FIXED 10 results per page and IGNORES the requested `pageSize`** (10/20/30 all
  return 10). So the loop must use `pageSize = 10` in its page math, else pages are silently
  skipped. Continue while `(currentPage-1)*10 < totalCount`.
- `totalCount` is the **sale-unit** count (one `saleUnitId` per card), NOT the finer 物件-item
  count. A card can bundle several 物件 (numbered 1,2,3…) sold as a set under one `saleUnitId`;
  the `saleUnitId` is the archival unit and the natural key. (Tokyo: totalCount 42 → 5 pages →
  41–42 unique sale units; the ~1 slack is a card that straddles a page boundary, deduped by key.)

Each result row is:
```html
<a href="#" onclick="tranPropertyDetail(&quot;00000021309&quot;,&quot;31131&quot;,&#39;1&#39;);">
  東京地方裁判所立川支部　令和08年(ヌ)第12号<br></a>
```
i.e. `tranPropertyDetail(saleUnitId, courtId, transitionTabId)`. The visible text gives the
court name + case number (令和08年(ヌ)第12号 → era 令和, year 08, type ヌ=強制競売, serial 12).

**Natural keys learned here:**
- `saleUnitId` — 11-digit zero-padded id of a *sale unit* (a 物件, possibly a set). Stable per property.
- `courtId` — 5-digit BIT court code (`31131` = 東京地方裁判所立川支部; `31111` = 東京地方裁判所本庁 seen in other rows).

## Step 4 — Property detail (`POST /app/propertyresult/pr001/h05`)

`tests/fixtures/bit/detail_pr001_h05.html` (title 競売物件検索：詳細). `tranPropertyDetail` sets
`#saleUnitId`, `#detailCourtId`, `#transitionTabId` on `propertyResultForm`, switches its
action to `/app/propertyresult/pr001/h05`, and submits. **The full form body (all ~3000 fields
carried over from the listing) must be posted** — posting only the 3 changed fields returns 500.

The detail page exposes:
- Case number, court, 物件 type badges.
- `latitude` / `longitude` hidden inputs (BIT's own geocode — feed a Mapion map). Use as
  best-effort lat/lng; confidence unknown, never trusted.
- The **3点セット download button** `#threeSetPDF` (label shows the combined PDF size, e.g. 2.24MB).
- Detail JS is `/app/property/syosai.js` (syosai = 詳細).

## Step 5 — 3点セット PDF download (the core value)

From `syosai.js`, the `#threeSetPDF` click handler:
```js
$.ajax({ url:'/app/detail/pd001/h03', type:'POST',
         data:{ courtId, saleUnitId }, dataType:'text' })
  .done(d => { if (d.match(/success/)) location.href =
     '/app/detail/pd001/h04?courtId='+courtId+'&saleUnitId='+saleUnitId; });
```

Two-step:
1. `POST /app/detail/pd001/h03` body `courtId=31131&saleUnitId=00000021309` → **plain-text `success`**
   (7 bytes). This is the availability gate — when the bidding period has ended and the PDF is
   deleted, expect a non-`success` body (drives our "archive before deletion" urgency).
2. `GET /app/detail/pd001/h04?courtId=31131&saleUnitId=00000021309` →
   `Content-Type: application/pdf`, `Content-Disposition: attachment; filename=TAC_R08N00012_1.pdf`,
   **2,346,213 bytes** verified downloaded, magic bytes `%PDF`.

The server-supplied filename encodes court+case+item: `TAC` (Tachikawa) `_R08` (令和8) `N00012`
(case 12) `_1` (物件/item 1). Treat as advisory; our archive is content-addressed by sha256.

## Court enumeration strategy (for `SyncCourts`)

Courts are **not** returned as a clean JSON list; they appear:
1. as `courtId` values embedded in listing rows (harvested during a sweep), and
2. implicitly via the block→prefecture structure (blocks 01–09, prefectures 01–47).

Codes observed: `31111` 東京地方裁判所, `31131` 東京地方裁判所立川支部. The `311xx` prefix maps to
prefecture 13 (Tokyo). **Phase-1 plan:** enumerate the 47 prefectures via `searchType=1`,
`prefecturesId=01..47`; every listing row yields `(courtId, courtName)` which we upsert into
`Court` (prefecture inferred from the driving `prefecturesId`, branch flag from the name
containing 支部). This discovers all ~147 courts/branches organically from the sweep without a
separate court index. (A dedicated court-list endpoint was probed — `/app/schedule/...` returns
a Spring JSON 404 — none found; harvest-from-listings is the robust path.)

## Endpoints NOT to hit / dead ends probed

- `/app/schedule/pk002/h01` → JSON `404` (guessed path; the schedule directory referenced in
  the brief is a human `schedule/index.html`, not part of the search JSON flow — deferred).
- `/app/areaselect/ps002/h17` and `/app/areaselect/ps002/h05` with a partial body → HTTP 500
  (application error, **not** a block — distinguish these from a real block page).

## Rate-limit & block-detection notes for the client

- No `robots.txt` (404), no `Retry-After` seen. Enforce **1 req / 3 s single-threaded** ourselves.
- A genuine application error is `<title>エラー | BIT…</title>` + "エラーが発生しました。しばらくして
  から、アクセスしてください。" with HTTP 500 — retryable-ish but usually means a malformed request.
- Treat **HTTP 403/429, or any body matching a WAF/block page, as STOP-AND-ALERT** (kill that
  court, do not retry around it). None were encountered during recon.
