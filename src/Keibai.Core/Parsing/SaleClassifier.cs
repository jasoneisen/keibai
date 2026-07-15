using Keibai.Core.Domain;

namespace Keibai.Core.Parsing;

/// <summary>
/// Maps BIT's property-type labels to <see cref="SaleCls"/>. Handles the several spellings BIT uses for
/// the same type across the listing badge, the detail badge, and the 種別 table — notably <c>戸建て</c>
/// (with the trailing て) and the detail table's <c>区分所有建物</c> / <c>建物</c> for a mansion.
/// </summary>
public static class SaleClassifier
{
    /// <summary>Classify a type label; null when unrecognized/empty.</summary>
    public static SaleCls? Parse(string? label)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            return null;
        }

        var t = label.Trim();
        return t switch
        {
            _ when t.Contains("マンション") || t.Contains("区分所有") => SaleCls.Mansion,
            _ when t.Contains("戸建") => SaleCls.Detached,
            _ when t.Contains("土地") => SaleCls.Land,
            _ when t.Contains("その他") => SaleCls.Other,
            _ => null,
        };
    }
}
