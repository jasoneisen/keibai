using Keibai.Web.Components.Pages;
using Xunit;

namespace Keibai.Web.Tests;

public class HomeTests
{
    [Fact]
    public void Home_renders_the_heading()
    {
        using var ctx = new Bunit.BunitContext();

        var cut = ctx.Render<Home>();

        Assert.Contains("Keibai", cut.Markup);
    }
}
