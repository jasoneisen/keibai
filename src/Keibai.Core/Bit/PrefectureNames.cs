namespace Keibai.Core.Bit;

/// <summary>
/// BIT prefecture search-code → Japanese display name. Codes are BIT's own: 02–47 are the zero-padded JIS
/// prefecture codes, and Hokkaidō is split into the four district-court pseudo-codes 91–94 (BIT never uses
/// 01 for a search — see <see cref="Ingestion.Prefectures"/>). Display-only; the sweep drives off the codes.
/// </summary>
public static class PrefectureNames
{
    private static readonly IReadOnlyDictionary<string, string> Names = new Dictionary<string, string>
    {
        ["01"] = "北海道",
        ["91"] = "北海道（札幌）",
        ["92"] = "北海道（函館）",
        ["93"] = "北海道（旭川）",
        ["94"] = "北海道（釧路）",
        ["02"] = "青森県",
        ["03"] = "岩手県",
        ["04"] = "宮城県",
        ["05"] = "秋田県",
        ["06"] = "山形県",
        ["07"] = "福島県",
        ["08"] = "茨城県",
        ["09"] = "栃木県",
        ["10"] = "群馬県",
        ["11"] = "埼玉県",
        ["12"] = "千葉県",
        ["13"] = "東京都",
        ["14"] = "神奈川県",
        ["15"] = "新潟県",
        ["16"] = "富山県",
        ["17"] = "石川県",
        ["18"] = "福井県",
        ["19"] = "山梨県",
        ["20"] = "長野県",
        ["21"] = "岐阜県",
        ["22"] = "静岡県",
        ["23"] = "愛知県",
        ["24"] = "三重県",
        ["25"] = "滋賀県",
        ["26"] = "京都府",
        ["27"] = "大阪府",
        ["28"] = "兵庫県",
        ["29"] = "奈良県",
        ["30"] = "和歌山県",
        ["31"] = "鳥取県",
        ["32"] = "島根県",
        ["33"] = "岡山県",
        ["34"] = "広島県",
        ["35"] = "山口県",
        ["36"] = "徳島県",
        ["37"] = "香川県",
        ["38"] = "愛媛県",
        ["39"] = "高知県",
        ["40"] = "福岡県",
        ["41"] = "佐賀県",
        ["42"] = "長崎県",
        ["43"] = "熊本県",
        ["44"] = "大分県",
        ["45"] = "宮崎県",
        ["46"] = "鹿児島県",
        ["47"] = "沖縄県",
    };

    /// <summary>All (code, name) pairs in BIT sweep order (Hokkaidō district codes first, then 02–47).</summary>
    public static IReadOnlyList<(string Code, string Name)> Ordered { get; } =
        Ingestion.Prefectures.All.Select(code => (code, Of(code))).ToList();

    /// <summary>The Japanese name for a BIT prefecture code, or the code itself when unknown.</summary>
    public static string Of(string? prefectureId) =>
        prefectureId is not null && Names.TryGetValue(prefectureId, out var name) ? name : prefectureId ?? "?";
}
