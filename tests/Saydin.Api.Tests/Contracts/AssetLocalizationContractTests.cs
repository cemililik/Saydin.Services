using System.Text.RegularExpressions;
using System.Xml.Linq;
using FluentAssertions;

namespace Saydin.Api.Tests.Contracts;

/// <summary>
/// SVCR-006: i18n kontrat testi. Migration seed'lerinde tanımlı HER asset sembolünün
/// hem TR (<c>ErrorMessages.resx</c>) hem EN (<c>ErrorMessages.en.resx</c>) display
/// name'i (<c>Asset_{SYMBOL}</c> key'i) bulunmalı. Yeni bir asset migration'a eklenir
/// ama resx güncellenmezse (CLAUDE.md add-asset checklist atlanırsa) bu test kırmızıya döner.
///
/// Test repo köküne (Saydin.Services.sln) çıkarak migration ve resx dosyalarını okur —
/// docker compose içinde repo mount'lu çalıştığı için dosyalar erişilebilir.
/// </summary>
public class AssetLocalizationContractTests
{
    private static readonly string RepoRoot = FindRepoRoot();
    private static readonly IReadOnlyList<string> SeededSymbols = ExtractSeededAssetSymbols();
    private static readonly IReadOnlySet<string> TrAssetKeys = ExtractAssetKeys("ErrorMessages.resx");
    private static readonly IReadOnlySet<string> EnAssetKeys = ExtractAssetKeys("ErrorMessages.en.resx");

    public static IEnumerable<object[]> Symbols() => SeededSymbols.Select(s => new object[] { s });

    [Theory]
    [MemberData(nameof(Symbols))]
    public void EverySeededAsset_HasTrAndEnDisplayName(string symbol)
    {
        var key = $"Asset_{symbol}";
        TrAssetKeys.Should().Contain(key, "TR resx '{0}' içermeli (migration'da seed'li asset)", key);
        EnAssetKeys.Should().Contain(key, "EN resx '{0}' içermeli (migration'da seed'li asset)", key);
    }

    [Fact]
    public void TrAndEn_AssetKeys_AreInParity()
    {
        // Bir dilde eklenip diğerinde unutulan asset'i yakalar.
        TrAssetKeys.Should().BeEquivalentTo(EnAssetKeys);
    }

    [Fact]
    public void SeededSymbols_AreDiscovered()
    {
        SeededSymbols.Should().NotBeEmpty("migration seed'lerinden asset sembolü çıkarılmalı");
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Saydin.Services.sln")))
            dir = dir.Parent;
        return dir?.FullName
            ?? throw new InvalidOperationException("Repo kökü (Saydin.Services.sln) bulunamadı.");
    }

    private static IReadOnlyList<string> ExtractSeededAssetSymbols()
    {
        var migrationsDir = Path.Combine(RepoRoot, "infrastructure", "postgres", "migrations");

        // Review follow-up: önce yalnız "INSERT INTO assets ... ;" bloklarını izole et, sonra
        // sembol regex'ini SADECE o bloklarda çalıştır. Tüm dosyayı taramak başka tablolardaki
        // ('UPPER','text') tuple'larını over-match edebilirdi (asset olmayan sembol → yanlış fail).
        // Singleline: '.' yeni satırları kapsar; non-greedy: ilk ';'de durur (asset seed
        // statement'ında gömülü ';' yoktur). \bassets\b: assets_* gibi tabloları dışlar.
        var insertBlock = new Regex(@"INSERT\s+INTO\s+assets\b.*?;",
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
        // Asset satırı: ('SYMBOL', 'DisplayName', ...) — sembol UPPER, ardından quoted metin.
        var symbolPattern = new Regex(@"\(\s*'([A-Z][A-Z0-9_]+)'\s*,\s*'", RegexOptions.Compiled);

        var symbols = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(migrationsDir, "*.sql"))
        {
            var sql = File.ReadAllText(file);
            foreach (Match block in insertBlock.Matches(sql))
                foreach (Match m in symbolPattern.Matches(block.Value))
                    symbols.Add(m.Groups[1].Value);
        }
        return symbols.ToList();
    }

    private static IReadOnlySet<string> ExtractAssetKeys(string resxFileName)
    {
        var path = Path.Combine(RepoRoot, "src", "Saydin.Api", "Resources", resxFileName);
        var doc = XDocument.Load(path);
        return doc.Root!
            .Elements("data")
            .Select(d => (string?)d.Attribute("name"))
            .Where(name => name is not null && name.StartsWith("Asset_", StringComparison.Ordinal))
            .Select(name => name!)
            .ToHashSet(StringComparer.Ordinal);
    }
}
