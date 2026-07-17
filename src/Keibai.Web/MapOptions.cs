namespace Keibai.Web;

/// <summary>
/// Client-side map configuration. Bound from <c>Keibai:Maps</c> so appsettings merge into the OMD
/// host by concatenation.
/// </summary>
public sealed class MapOptions
{
    /// <summary>Config section name.</summary>
    public const string SectionName = "Keibai:Maps";

    /// <summary>
    /// Google Maps API key for the Street View embed in the map-pin popups (Maps Embed API — no-charge,
    /// but a key is still required; restrict it by HTTP referrer). Empty = no embed; popups fall back to
    /// a plain "open Street View" link, which needs no key. The key is necessarily visible in the page
    /// HTML — that is how the Embed API is designed to be used.
    /// </summary>
    public string GoogleMapsApiKey { get; set; } = "";
}
