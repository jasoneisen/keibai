namespace Keibai.Core.Bit;

/// <summary>
/// BIT region blocks (地域ブロック). A prefecture-level search REQUIRES the correct <c>blockCls</c> +
/// <c>blockName</c> for the prefecture. This is BIT's OWN grouping — NOT the standard 8-region 地方
/// scheme: 中部 does not exist (it is split into 北陸・甲信越 and 東海), 九州 and 沖縄 are one block,
/// and Hokkaidō uses four district-court pseudo-prefecture codes (91 札幌 / 92 函館 / 93 旭川 /
/// 94 釧路 — see <see cref="Ingestion.Prefectures"/>). Source of truth: the top-page map's
/// <c>tranAreaMap('NN')</c> areas (fixture <c>top_pt001_h01.html</c>) and each block's
/// search-condition <c>prefecturesCondition</c> options, verified live 2026-07-13 for blocks
/// 01/04/05/06.
/// </summary>
public static class Regions
{
    private static readonly (string Block, string Name, string[] Prefectures)[] Blocks =
    [
        ("01", "北海道", ["91", "92", "93", "94"]),
        ("02", "東北", ["02", "03", "04", "05", "06", "07"]),
        ("03", "関東", ["08", "09", "10", "11", "12", "13", "14"]),
        ("04", "北陸・甲信越", ["15", "16", "17", "18", "19", "20"]),
        ("05", "東海", ["21", "22", "23", "24"]),
        ("06", "近畿", ["25", "26", "27", "28", "29", "30"]),
        ("07", "中国", ["31", "32", "33", "34", "35"]),
        ("08", "四国", ["36", "37", "38", "39"]),
        ("09", "九州・沖縄", ["40", "41", "42", "43", "44", "45", "46", "47"]),
    ];

    /// <summary>Return the (blockCls, blockName) for a BIT prefecture search code.</summary>
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
            nameof(prefectureId), prefectureId,
            "Unknown BIT prefecture search code (expected 02–47 or 91–94; Hokkaidō is 91–94, never 01).");
    }
}
