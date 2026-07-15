# Keibai operations runbook

The system is built to need **< 1 hr/week** of human attention: no news is good news. Alerts fire only
on actionable anomalies. This runbook is what to do when one does.

## Daily rhythm (automatic)

`NightlySweepScheduler` fires two JST jobs (BIT night hours):

- **01:00 JST** — `SyncCourts`: nationwide listing sweep. New discoveries are detail-enriched and their
  3点セット archived **the same night** (bounded by `Keibai:Ingestion:ArchivePrefectures`).
- **07:00 JST** — `ScheduleArchiveWork` (deadline-ordered archive backlog drain + due mid-window
  re-checks), `ScheduleResultsSync` (results for courts with a 開札 in the last 2 days), then
  `SummarizeSweep` (the monitor).

Everything runs on the single `Sequential()` `keibai-ingestion` local queue — one BIT request at a time,
≥3 s apart. Durable (Postgres, schema `keibai_wolverine`), so a restart resumes in-flight work.

## Alerts — what each means, what to do

Alerts go to the providers in `Keibai:Alerts:Providers` (default `ntfy`; `smtp` and `log` also
available). **Every alert is actionable.**

| Alert | Meaning | Action |
|---|---|---|
| **BIT blocked — court NNNNN / prefecture NN** (Critical) | BIT returned 403/429/block-page. The court is auto-disabled (`Court.CrawlDisabled=true`); it is NOT retried. | Stop. Investigate rate/UA/IP. Do **not** work around it. When resolved, clear the flag (below) to resume. If prefecture-level (no single court), consider `Keibai:Ingestion:Enabled=false` until sorted. |
| **Prefecture NN sweep had errors** (Warning) | A court fetch failed after retries; the work item was parked. | Re-run: `POST /sync/prefecture/NN`. Check `/tmp` host log for the exception. |
| **Prefecture NN listings dropped >50%** (Warning) | Listing count fell to <50 % of the previous non-zero sweep. | Could be a real takedown, a BIT change, or a parser regression. Verify the count on bit.courts.go.jp by hand before trusting it. |
| **PDF archive failure rate high** (Warning) | >5 % of archive attempts failed (≥20 attempts). | Check disk space, network, and whether BIT changed the download flow (`docs/bit-api.md`). |
| **Nationwide sweep found zero listings** (Critical) | Every prefecture returned zero. Almost certainly silent breakage (auth/endpoint/parser), not a real empty result. | Check the last `CrawlRun`s and BIT by hand; the search flow likely changed. |
| **Blob storage over threshold** (Warning) | Blob store exceeded `Keibai:Storage:MaxGigabytes`. | Add disk, prune old raw captures, or narrow `Keibai:Ingestion:ArchivePrefectures`. |

## Common tasks

All manual triggers are `POST` (no body). The host listens on `:5199` by default.

```bash
# Re-run one prefecture's listing sweep (also re-archives new discoveries)
curl -X POST http://localhost:5199/sync/prefecture/13

# Nationwide sweep now (hours at the rate limit)
curl -X POST http://localhost:5199/sync/all

# Drain the archive backlog (deadline-ordered) + due re-checks
curl -X POST http://localhost:5199/archive/schedule

# Sale results: backfill one court's ~3-year history / all courts / sync one court's latest round
curl -X POST http://localhost:5199/results/backfill/31111
curl -X POST http://localhost:5199/results/backfill-all
curl -X POST http://localhost:5199/results/sync/31111

# Run the monitor now (anomaly alerts + storage watchdog)
curl -X POST http://localhost:5199/monitor/run
```

### Re-enable a court that was auto-disabled after a block

A block sets `Court.CrawlDisabled=true`; detail/archive/results handlers skip disabled courts. Once the
underlying issue is fixed, clear it in the `keibai` Marten store:

```sql
UPDATE keibai.mt_doc_court
SET data = jsonb_set(jsonb_set(data, '{CrawlDisabled}', 'false'),
                     '{CrawlDisabledReason}', 'null')
WHERE data->>'Id' = '31111';
```

Then re-run the court's prefecture: `POST /sync/prefecture/{prefectureId}`.

### Replay a parse from a raw capture (never re-fetch to fix a parser bug)

Every BIT response is stored raw **before** parsing (`RawCapture` doc + blob), keyed by URL + content
hash. To debug/replay a parser without touching BIT:

```sql
-- find the capture
SELECT data->>'Url', data->>'BlobPath', data->>'FetchedAt'
FROM keibai.mt_doc_rawcapture
WHERE data->>'Url' LIKE '%propertyresult%' ORDER BY data->>'FetchedAt' DESC LIMIT 5;
```

The blob lives at `{Keibai:BlobStore:Root}/{BlobPath}`. Feed its bytes to the relevant parser
(`ListingParser` / `DetailParser` / `SaleResultParser`) — the parsers are pure and fixture-tested, so
fixing one is edit + `bash test.sh`, no crawl.

### Restore a wedged queue

Durable envelopes live in schema `keibai_wolverine`. Inspect:

```sql
SELECT status, count(*) FROM keibai_wolverine.wolverine_incoming_envelopes GROUP BY status;
SELECT message_type, count(*) FROM keibai_wolverine.wolverine_dead_letters GROUP BY message_type;
```

- **Backed up but moving** — normal; the sequential queue is 1 req/3 s, so a nationwide sweep is slow by
  design. Leave it.
- **Stuck / not draining** — restart the host. Durable messages resume automatically (proven in
  `docs/validation-phase2.md`).
- **Dead-lettered** — handlers are idempotent, so replaying is safe. Use Wolverine's CLI
  (`dotnet Keibai.dll storage ...`) or move rows from `wolverine_dead_letters` back to
  `wolverine_incoming_envelopes`. Investigate the recurring exception first (host log).

### Kill switch

`Keibai:Ingestion:Enabled=false` stops **all** outbound BIT traffic immediately (enforced in the
delegating handler, not by convention). The monitor still runs. Use it if BIT blocks the whole site or
anything looks wrong.

## Where things are

| thing | location |
|---|---|
| Documents (Court/PropertyItem/ArchivedDocument/SaleResult/CrawlRun/DailyStats/RawCapture) | Postgres schema `keibai` (Marten) |
| Durable message envelopes | Postgres schema `keibai_wolverine` |
| Blobs (raw captures + archived PDFs) | `Keibai:BlobStore:Root` (dev: `/workspace/keibai-blobstore`) |
| Reverse-engineered BIT flow | `docs/bit-api.md` |
| Config keys | `README.md` (all under `Keibai:`) |
