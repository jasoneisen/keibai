using Alba;
using Xunit;

namespace Keibai.Tests;

[Collection("host")]
public class HealthTests(HostFixture fixture)
{
    [Fact]
    public async Task Healthz_is_green()
    {
        await fixture.Host.Scenario(s =>
        {
            s.Get.Url("/healthz");
            s.StatusCodeShouldBeOk();
            s.ContentShouldContain("ok");
        });
    }

    [Fact]
    public async Task Root_redirects_to_jp()
    {
        await fixture.Host.Scenario(s =>
        {
            s.Get.Url("/");
            s.StatusCodeShouldBe(System.Net.HttpStatusCode.Redirect);
            s.Header("Location").SingleValueShouldEqual("/jp");
        });
    }
}

/// <summary>xUnit collection so the Alba host boots once for all integration tests.</summary>
[CollectionDefinition("host")]
public sealed class HostCollection : ICollectionFixture<HostFixture>;
