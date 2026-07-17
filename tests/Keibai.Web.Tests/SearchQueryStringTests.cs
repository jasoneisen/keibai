using Keibai.Core.Search;
using Keibai.Web.Reading;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Xunit;

namespace Keibai.Web.Tests;

/// <summary>
/// Unit tests for the multi-select status grammar in <see cref="SearchQueryString"/> — the tri-state
/// (explicit set / deliberate none / default) and its round-trip stability, plus tolerance of the old
/// single-value URLs.
/// </summary>
public sealed class SearchQueryStringTests
{
    private static IQueryCollection Query(params (string Key, string[] Values)[] pairs) =>
        new QueryCollection(pairs.ToDictionary(p => p.Key, p => new StringValues(p.Values)));

    [Fact]
    public void No_status_params_default_to_everything_except_Closed()
    {
        var q = SearchQueryString.Parse(Query());

        Assert.Equal(
            [BiddingStatus.Upcoming, BiddingStatus.Viewing, BiddingStatus.Bidding, BiddingStatus.Opened],
            q.Statuses);
        Assert.DoesNotContain(BiddingStatus.Closed, q.Statuses);
    }

    [Fact]
    public void Repeated_status_params_parse_into_the_selected_set()
    {
        var q = SearchQueryString.Parse(Query(("status", ["Bidding", "Viewing"])));

        Assert.Equal([BiddingStatus.Bidding, BiddingStatus.Viewing], q.Statuses);
    }

    [Fact]
    public void Old_single_status_url_still_parses_to_a_one_element_list()
    {
        var q = SearchQueryString.Parse(Query(("status", ["Closed"])));

        Assert.Equal([BiddingStatus.Closed], q.Statuses);
    }

    [Fact]
    public void Sentinel_with_no_checked_boxes_means_no_filter_show_everything()
    {
        // The form always submits statusSet=1; with no status values the operator cleared every box.
        var q = SearchQueryString.Parse(Query(("statusSet", ["1"])));

        Assert.Empty(q.Statuses);
    }

    [Fact]
    public void Unparseable_and_removed_Any_values_are_ignored()
    {
        var q = SearchQueryString.Parse(Query(("status", ["Any", "bogus", "Bidding"])));

        Assert.Equal([BiddingStatus.Bidding], q.Statuses);
    }

    [Fact]
    public void Roundtrip_preserves_an_explicit_status_set()
    {
        var original = new PropertyQuery { Statuses = [BiddingStatus.Viewing, BiddingStatus.Bidding] };

        var reparsed = ReParse(original);

        Assert.Equal(original.Statuses, reparsed.Statuses);
    }

    [Fact]
    public void Roundtrip_preserves_deliberate_none_via_the_sentinel()
    {
        // Empty Statuses ⇒ no constraint. It must NOT re-parse to the all-but-Closed default.
        var original = new PropertyQuery { Statuses = [] };

        var qs = SearchQueryString.ToQueryString(original);
        var reparsed = ReParse(original);

        Assert.Contains("statusSet=1", qs);
        Assert.Empty(reparsed.Statuses);
    }

    [Fact]
    public void Roundtrip_of_the_default_is_stable()
    {
        // The default may serialize as explicit statuses; re-parsing must yield the same set (bookmark stability).
        var original = new PropertyQuery { Statuses = SearchQueryString.DefaultStatuses };

        var reparsed = ReParse(original);

        Assert.Equal(SearchQueryString.DefaultStatuses, reparsed.Statuses);
    }

    private static PropertyQuery ReParse(PropertyQuery query)
    {
        var http = TestHttp.Get(SearchQueryString.ToQueryString(query));
        return SearchQueryString.Parse(http.Request.Query);
    }
}
