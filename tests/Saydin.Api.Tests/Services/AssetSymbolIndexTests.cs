using FluentAssertions;
using Saydin.Api.Repositories;
using Saydin.Api.Services;
using Saydin.Shared.Entities;

namespace Saydin.Api.Tests.Services;

public sealed class AssetSymbolIndexTests
{
    [Fact]
    public async Task ConcurrentDifferentCatalogs_NeverReturnAnotherCallsSnapshot()
    {
        var index = new AssetSymbolIndex();
        var oldAsset = Asset("old");
        var newAsset = Asset("new");
        var oldCatalog = Catalog(1, 0x11);
        var newCatalog = Catalog(2, 0x22);
        using var start = new ManualResetEventSlim();

        var calls = Enumerable.Range(0, 256).Select(async call =>
        {
            await Task.Yield();
            start.Wait();
            for (var iteration = 0; iteration < 100; iteration++)
            {
                var useOld = ((call + iteration) & 1) == 0;
                var expected = useOld ? oldAsset : newAsset;
                var actual = index.Lookup(
                    [expected],
                    "CATALOG_RACE",
                    useOld ? oldCatalog : newCatalog);
                actual.Should().BeSameAs(expected);
            }
        }).ToArray();

        start.Set();
        await Task.WhenAll(calls);
    }

    private static Asset Asset(string displayName) => new()
    {
        Id = Guid.NewGuid(),
        Symbol = "CATALOG_RACE",
        DisplayName = displayName,
        Category = AssetCategory.Stock,
        IsActive = true,
        Source = "test",
    };

    private static AssetCatalogVersion Catalog(long revision, byte value) => new()
    {
        Revision = revision,
        CatalogSha256 = Enumerable.Repeat(value, 32).ToArray(),
    };
}
