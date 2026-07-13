namespace Keibai.Core.Ingestion;

/// <summary>Kick off a nationwide sweep: enqueue a prefecture sync for all 47 prefectures.</summary>
public sealed record SyncCourts;

/// <summary>Sync one prefecture's active listings (discovers its courts organically from the rows).</summary>
public sealed record SyncPrefectureListings(string PrefectureId);

/// <summary>Fetch and upsert a single property's detail (idempotent on <c>{CourtId}:{SaleUnitId}</c>).</summary>
public sealed record SyncPropertyDetail(
    string PrefectureId, string CourtId, string SaleUnitId, string ResultsHtmlBlobPath);
