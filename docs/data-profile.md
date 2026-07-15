# Keibai data profile — property attributes

Snapshot of the Marten store on 2026-07-15 after enriching every property with the **full detail-page
attribute set** and backfilling existing rows. The backfill was an **offline reparse** (`POST
/admin/reparse-details` → `ReparseDetailCaptures`): it re-parsed **3,316 stored detail `RawCapture`s**
through the current parser and re-enriched all matching properties — **0 BIT requests, 0 unmatched, 0
missing-blob, ~12 s**.

## Corpus

| metric | value |
|---|---|
| Properties (sale units) | **1,180** |
| 物件 items captured (across all sale units) | **3,051** |
| Distinct attribute labels captured (generic map) | **37** |
| Multi-物件 cards | 862 of 1,180 (up to **34** items in one sale unit) |

## Property type (種別) — corrected

The profile surfaced a real classification bug: BIT's badge reads **戸建て** (trailing て) and the parsers
matched `戸建` exactly, so 戸建 cards fell through and were mis-typed as Land. Fixed with a shared
`SaleClassifier` (reads the card badge, tolerant of spelling). Before → after the fix:

| type | before | **after** |
|---|---|---|
| 戸建 Detached | 0 | **748** |
| 土地 Land | 956 | **253** |
| マンション Mansion | 178 | **178** |
| その他 Other | 1 | **1** |
| unclassified | 45 | **0** |

**748 properties were re-typed** and classification went from 96 % → **100 %**.

## Attribute coverage

Coverage is **type-appropriate**: "missing" values are almost always a type that doesn't have the field
(land has no build year; only mansions have 専有面積 / 管理費). This is correct data, not a gap.

| attribute | coverage | note |
|---|---|---|
| 所在地 DetailAddress · 売却基準価額 · 買受可能価額 · lat/lng | **100 %** | always present |
| 開札 OpeningDate · 入札締切 BiddingEnd | 98 % | the bid window (from the 39-court-scale re-sweep + captures) |
| 土地面積 · 地目 · 用途地域 LandArea/Category/Zoning | 85 % | present for land-bearing items |
| 構造 · 間取り · 敷地利用権 · 占有者 · 家屋番号 Structure/Layout/Rights/Occupant/HouseNo | 79 % | building items |
| 建ぺい率 · 容積率 Coverage/FloorArea ratio | 78 % | |
| 築年月 BuildYear | 75 % overall → **93 % of Detached, 99 % of Mansion, ~2 % of Land** | land has no build year |
| 床面積 BuildingArea (戸建) | 57 % | detached houses |
| 専有面積 · 階 · 管理費 · 総戸数 Exclusive/Floor/AdminFee/TotalUnits | 14–15 % overall → **~100 % of Mansion** | mansion-only fields |

## Distributions

- **Build year:** 1929 – 2025, **median 1992** (881 dated properties).
- **Land area:** 2 – 61,376 m², **median 235 m²**.
- **Mansion exclusive area:** 12 – 868 m², **median 60 m²**.
- **Occupancy (占有者):** 750 債務者・所有者 (debtor/owner-occupied), 182 あり (occupied) — a bidder-relevant risk signal.
- **Sale unit size:** 318 single-物件; the rest bundle 2–34 物件 (a 戸建 card is typically land + building; large land cards bundle many parcels).

## Everything captured (the 37 generic-map labels)

Beyond the ~18 typed rollups, the per-物件 `Attributes` map keeps **every** label BIT renders, so nothing
is lost — including rare ones never promoted to a field:

```
種別, 所在地, 物件番号, 土地面積（登記）, 地目（登記）, 用途地域, 容積率, 建ぺい率, 家屋番号,
種類（登記）, 占有者, 築年月, 構造（登記）, 敷地利用権, 間取り, 床面積（登記）, 地目（現況）,
床面積（現況）, 持分, 階, バルコニー面積, 専有面積（登記）, 種類（現況）, 土地の利用状況,
構造（現況）, 管理費等, 総戸数, 建物番号, 符号, 土地面積（現況）, 専有面積（現況）,
地上権表示, 登記, 主登記, 付記登記, 地積, 地番
```

(The last handful — 地上権表示 / 主登記 / 付記登記 / 地積 / 地番 — appear on only a few properties and
are captured **only** because the generic map keeps unrecognized labels; they'd be lost by a typed-fields-
only parser.)

## Genuinely unavailable (not in BIT)

Only two Phase-1-spec attributes truly don't exist in BIT and would need a geocoder we don't have:
**normalized/住居表示 address** (BIT publishes only the 地番) and **geocode confidence** (lat/lng are
"best-effort, never trusted"). Everything else the spec named — build year, floor/land/exclusive area,
structure, layout, occupancy — is present and now captured.

## How to refresh

- New sweeps enrich automatically (`SyncPropertyDetail` → `DetailEnrichment`).
- To backfill new parser fields onto existing rows without crawling: `POST /admin/reparse-details`
  (replays stored detail captures; idempotent; no BIT traffic).
