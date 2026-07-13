namespace Keibai.Core.Bit;

/// <summary>
/// BIT region blocks (地域ブロック). A prefecture-level search REQUIRES the correct <c>blockCls</c> +
/// <c>blockName</c> for the prefecture (a blank block returns HTTP 500). Standard Japanese region
/// grouping; verified live for Tokyo (13 → block 03 関東).
/// </summary>
public static class Regions
{
    private static readonly (string Block, string Name, string[] Prefectures)[] Blocks =
    [
        ("01", "北海道", ["01"]),
        ("02", "東北", ["02", "03", "04", "05", "06", "07"]),
        ("03", "関東", ["08", "09", "10", "11", "12", "13", "14"]),
        ("04", "中部", ["15", "16", "17", "18", "19", "20", "21", "22", "23"]),
        ("05", "近畿", ["24", "25", "26", "27", "28", "29", "30"]),
        ("06", "中国", ["31", "32", "33", "34", "35"]),
        ("07", "四国", ["36", "37", "38", "39"]),
        ("08", "九州", ["40", "41", "42", "43", "44", "45", "46"]),
        ("09", "沖縄", ["47"]),
    ];

    /// <summary>Return the (blockCls, blockName) for a JIS prefecture code.</summary>
    public static (string BlockCls, string BlockName) BlockFor(string prefectureId)
    {
        foreach (var (block, name, prefectures) in Blocks)
        {
            if (Array.IndexOf(prefectures, prefectureId) >= 0)
            {
                return (block, name);
            }
        }

        throw new ArgumentOutOfRangeException(
            nameof(prefectureId), prefectureId, "Unknown JIS prefecture code (expected 01–47).");
    }
}
