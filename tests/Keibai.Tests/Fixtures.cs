namespace Keibai.Tests;

/// <summary>Locates the BIT fixture files copied next to the test binary.</summary>
public static class Fixtures
{
    private static readonly string Root = Path.Combine(AppContext.BaseDirectory, "fixtures", "bit");

    /// <summary>Read a fixture file's text.</summary>
    public static string Read(string name) => File.ReadAllText(Path.Combine(Root, name));

    /// <summary>The Tokyo (prefecture 13) result-listing page.</summary>
    public static string TokyoResults() => Read("results_ps002_h05_tokyo.html");

    /// <summary>A property-detail page.</summary>
    public static string Detail() => Read("detail_pr001_h05.html");
}
