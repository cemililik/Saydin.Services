namespace Saydin.Api.Services;

/// <summary>
/// Consumer-policy revision for every cache entry derived from price/CPI rows.
/// Prefixing, rather than deleting, makes the final-only cutover atomic across replicas.
/// </summary>
internal static class AuthorityCacheNamespace
{
    internal const string Revision = "authority-final-v1";

    internal static string Key(string key) => $"{Revision}:{key}";
}
