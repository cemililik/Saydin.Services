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

    /// <summary>Tüm geçerli değerlerin sırasız kümesi (case-insensitive eşleşme).</summary>
    public static readonly IReadOnlySet<string> All =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Free, Premium };
}
