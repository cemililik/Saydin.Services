namespace Saydin.Shared.Constants;

/// <summary>
/// `users.tier` için kanonik değerler. DB CHECK constraint (`chk_users_tier`) ile
/// hizalıdır. Tier karşılaştırması case-insensitive yapılmalıdır
/// (kullanıcı kayıtlarında geçmişe yönelik karışık casing olabilir).
/// </summary>
public static class UserTiers
{
    public const string Free    = "free";
    public const string Premium = "premium";

    /// <summary>F13 follow-up: sıralı sabit liste — CHECK SQL deterministic.</summary>
    public static readonly IReadOnlyList<string> All = new[]
    {
        Free,
        Premium,
    };

    /// <summary>Case-insensitive membership kontrolü (kullanıcı kayıtlarındaki mixed-case).</summary>
    public static readonly IReadOnlySet<string> Lookup =
        new HashSet<string>(All, StringComparer.OrdinalIgnoreCase);
}
