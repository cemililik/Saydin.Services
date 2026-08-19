using FluentAssertions;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Saydin.Api.Models;
using Saydin.Api.Models.Requests;
using Saydin.Api.Options;
using Saydin.Api.Repositories;
using Saydin.Api.Services;
using Saydin.Shared.Entities;
using Saydin.Shared.Exceptions;

namespace Saydin.Api.Tests.Services;

public class SavedScenarioServiceTests
{
    private readonly ISavedScenarioRepository _repository = Substitute.For<ISavedScenarioRepository>();
    private readonly IStringLocalizer<ErrorMessages> _localizer = Substitute.For<IStringLocalizer<ErrorMessages>>();
    private readonly IInstallationPrincipalContext _principalContext = Substitute.For<IInstallationPrincipalContext>();
    private readonly FakeTimeProvider _timeProvider = new();
    // F2.2-12: last_seen throttle gerçek implementasyonla beslenir (basit in-memory map).
    // Throttle ilk çağrıda true döner; test'lerin çoğu tek çağrı ile çalışır. F3.1-5:
    // TimeProvider ile beslenir (ctor'da _timeProvider erişilebilir olduğu için orada kurulur).
    private readonly ILastSeenThrottle _lastSeenThrottle;
    private readonly SavedScenarioService _sut;

    private static readonly PlanOptions PlanOptions = new()
    {
        Free    = new TierOptions { MaxSavedScenarios = 5 },
        Premium = new TierOptions { MaxSavedScenarios = 0 }
    };

    private static readonly Asset BtcAsset = new()
    {
        Id          = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"),
        Symbol      = "BTC",
        DisplayName = "Bitcoin",
        Category    = AssetCategory.Crypto,
        Source      = "coingecko",
        IsActive    = true
    };

    private static readonly User FreeUser = new()
    {
        Id       = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001"),
        DeviceId = null,
        Tier     = "free",
        CreatedAt = DateTimeOffset.UtcNow
    };

    private static readonly User PremiumUser = new()
    {
        Id       = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002"),
        DeviceId = null,
        Tier     = "premium",
        CreatedAt = DateTimeOffset.UtcNow
    };

    public SavedScenarioServiceTests()
    {
        var options = Microsoft.Extensions.Options.Options.Create(PlanOptions);

        _localizer[Arg.Any<string>()]
            .Returns(ci => new LocalizedString((string)ci[0], (string)ci[0]));
        _localizer[Arg.Any<string>(), Arg.Any<object[]>()]
            .Returns(ci => new LocalizedString((string)ci[0], (string)ci[0]));

        // Her test doğrulanmış installation principal'inin UUID'siyle başlar.
        _principalContext.PrincipalId.Returns(FreeUser.Id);
        _lastSeenThrottle = new LastSeenThrottle(_timeProvider);

        _sut = new SavedScenarioService(_repository, _lastSeenThrottle, _principalContext, _timeProvider, options, _localizer, NullLogger<SavedScenarioService>.Instance);
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(5, 5)]
    [InlineData(100, 100)]
    [InlineData(101, 100)]
    [InlineData(500, 100)]
    public void ScenarioLimits_ConfiguredPlanLimit_ReturnsEffectiveSystemBound(
        int configured,
        int expected)
    {
        ScenarioLimits.GetEffectiveSaveLimit(configured).Should().Be(expected);
    }

    // ── GetScenariosAsync ────────────────────────────────────────────────────

    [Fact]
    public async Task GetScenariosAsync_ExistingUserWithScenarios_ReturnsMappedList()
    {
        var scenarioId = Guid.NewGuid();
        var scenarios = new List<SavedScenario>
        {
            new()
            {
                Id               = scenarioId,
                UserId           = FreeUser.Id,
                AssetId          = BtcAsset.Id,
                AssetSymbol      = "BTC",
                AssetDisplayName = "Bitcoin",
                Type             = "what_if",
                BuyDate          = new DateOnly(2020, 1, 1),
                SellDate         = new DateOnly(2021, 1, 1),
                Quantity         = 10000m,
                QuantityUnit     = "try",
                Label            = "Test senaryosu",
                CreatedAt        = DateTimeOffset.UtcNow
            }
        };

        _repository.GetUserByIdAsync(FreeUser.Id, Arg.Any<CancellationToken>()).Returns(FreeUser);
        _repository.GetByUserIdAsync(
                FreeUser.Id,
                SavedScenarioService.LegacyListHardLimit,
                Arg.Any<CancellationToken>())
            .Returns(scenarios.AsReadOnly());

        var result = await _sut.GetScenariosAsync(CancellationToken.None);

        result.Should().HaveCount(1);
        result[0].Id.Should().Be(scenarioId);
        result[0].AssetSymbol.Should().Be("BTC");
        result[0].AssetDisplayName.Should().Be("Bitcoin");
        result[0].Amount.Should().Be(10000m);
        result[0].AmountType.Should().Be("try");
        result[0].Label.Should().Be("Test senaryosu");
        result[0].Type.Should().Be("what_if");
    }

    [Fact]
    public async Task GetScenariosAsync_MissingAuthenticatedPrincipal_ThrowsWithoutLegacyClaim()
    {
        _repository.GetUserByIdAsync(FreeUser.Id, Arg.Any<CancellationToken>()).Returns((User?)null);

        var act = () => _sut.GetScenariosAsync(CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Authenticated installation principal is missing.");
        await _repository.DidNotReceive().GetByUserIdAsync(
            Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetScenariosAsync_ExistingUser_UpdatesLastSeenOnceThrottled()
    {
        // F2.2-12: ilk çağrıda last_seen UPDATE atılır; aynı pencere içindeki ikinci
        // çağrıda atılmaz.
        _repository.GetUserByIdAsync(FreeUser.Id, Arg.Any<CancellationToken>()).Returns(FreeUser);
        _repository.GetByUserIdAsync(
                       FreeUser.Id,
                       SavedScenarioService.LegacyListHardLimit,
                       Arg.Any<CancellationToken>())
                   .Returns(new List<SavedScenario>().AsReadOnly());

        await _sut.GetScenariosAsync(CancellationToken.None);
        await _sut.GetScenariosAsync(CancellationToken.None);

        await _repository.Received(1).UpdateUserLastSeenAsync(FreeUser, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetScenariosAsync_ExistingUser_UsesLegacyHardCap()
    {
        _repository.GetUserByIdAsync(FreeUser.Id, Arg.Any<CancellationToken>()).Returns(FreeUser);
        _repository.GetByUserIdAsync(
                FreeUser.Id,
                SavedScenarioService.LegacyListHardLimit,
                Arg.Any<CancellationToken>())
            .Returns(Array.Empty<SavedScenario>());

        await _sut.GetScenariosAsync(CancellationToken.None);

        await _repository.Received(1).GetByUserIdAsync(
            FreeUser.Id,
            100,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetScenarioPageAsync_DefaultLimit_FetchesOneExtraAndReturnsStableNextCursor()
    {
        var createdAt = new DateTimeOffset(2026, 8, 18, 12, 0, 0, TimeSpan.Zero);
        var scenarios = Enumerable.Range(1, 21)
            .Select(i => CreateScenario(
                Guid.Parse($"00000000-0000-0000-0000-{i:D12}"),
                createdAt.AddMinutes(-i)))
            .ToList();
        _repository.GetUserByIdAsync(FreeUser.Id, Arg.Any<CancellationToken>()).Returns(FreeUser);
        _repository.GetPageByUserIdAsync(
                FreeUser.Id,
                null,
                SavedScenarioService.DefaultPageSize + 1,
                Arg.Any<CancellationToken>())
            .Returns(scenarios);

        var result = await _sut.GetScenarioPageAsync(null, null, CancellationToken.None);

        result.Items.Should().HaveCount(20);
        result.NextCursor.Should().NotBeNull();
        ScenarioCursorCodec.TryDecode(result.NextCursor, out var cursor).Should().BeTrue();
        cursor.Should().Be(new ScenarioCursor(scenarios[19].CreatedAt, scenarios[19].Id));
    }

    [Fact]
    public async Task GetScenarioPageAsync_ValidCursor_PassesExactTupleAndLimitPlusOne()
    {
        var boundary = new ScenarioCursor(
            new DateTimeOffset(2026, 8, 18, 12, 0, 0, TimeSpan.Zero),
            Guid.Parse("0198beef-0000-7000-8000-000000000001"));
        var token = ScenarioCursorCodec.Encode(boundary);
        _repository.GetUserByIdAsync(FreeUser.Id, Arg.Any<CancellationToken>()).Returns(FreeUser);
        _repository.GetPageByUserIdAsync(
                FreeUser.Id,
                boundary,
                51,
                Arg.Any<CancellationToken>())
            .Returns(Array.Empty<SavedScenario>());

        var result = await _sut.GetScenarioPageAsync(50, token, CancellationToken.None);

        result.Items.Should().BeEmpty();
        result.NextCursor.Should().BeNull();
        await _repository.Received(1).GetPageByUserIdAsync(
            FreeUser.Id, boundary, 51, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(51)]
    public async Task GetScenarioPageAsync_InvalidLimit_FailsBeforeRepository(int limit)
    {
        var act = () => _sut.GetScenarioPageAsync(limit, null, CancellationToken.None);

        await act.Should().ThrowAsync<ValidationException>()
            .Where(ex => ex.Field == "limit");
        await _repository.DidNotReceive().GetUserByIdAsync(
            Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetScenarioPageAsync_InvalidCursor_FailsBeforeRepository()
    {
        var act = () => _sut.GetScenarioPageAsync(20, "not-a-cursor", CancellationToken.None);

        await act.Should().ThrowAsync<ValidationException>()
            .Where(ex => ex.Field == "cursor");
        await _repository.DidNotReceive().GetUserByIdAsync(
            Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetScenarioPageAsync_MissingAuthenticatedPrincipal_ThrowsWithoutLegacyClaim()
    {
        _repository.GetUserByIdAsync(FreeUser.Id, Arg.Any<CancellationToken>()).Returns((User?)null);

        var act = () => _sut.GetScenarioPageAsync(null, null, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Authenticated installation principal is missing.");
        await _repository.DidNotReceive().GetPageByUserIdAsync(
            Arg.Any<Guid>(), Arg.Any<ScenarioCursor?>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // ── SaveScenarioAsync ────────────────────────────────────────────────────

    [Fact]
    public async Task SaveScenarioAsync_WhatIfValidRequest_ReturnsCreatedScenario()
    {
        var request = new SaveScenarioRequest("BTC", "Bitcoin", new DateOnly(2020, 1, 1),
            new DateOnly(2021, 1, 1), 10000m, "try", "Bitcoin yatırımım");

        _repository.GetUserByIdAsync(FreeUser.Id, Arg.Any<CancellationToken>()).Returns(FreeUser);
        _repository.GetActiveAssetBySymbolAsync("BTC", Arg.Any<CancellationToken>()).Returns(BtcAsset);
        _repository.CreateWithinLimitAsync(Arg.Any<SavedScenario>(), 5, Arg.Any<CancellationToken>())
                   .Returns(callInfo => callInfo.Arg<SavedScenario>());

        var result = await _sut.SaveScenarioAsync(request, CancellationToken.None);

        result.AssetSymbol.Should().Be("BTC");
        result.AssetDisplayName.Should().Be("Bitcoin");
        result.BuyDate.Should().Be(new DateOnly(2020, 1, 1));
        result.SellDate.Should().Be(new DateOnly(2021, 1, 1));
        result.Amount.Should().Be(10000m);
        result.AmountType.Should().Be("try");
        result.Label.Should().Be("Bitcoin yatırımım");
        result.Type.Should().Be("what_if");
    }

    [Fact]
    public async Task SaveScenarioAsync_WhatIfWithNullSellDate_SavesSuccessfully()
    {
        var request = new SaveScenarioRequest("BTC", "Bitcoin", new DateOnly(2020, 1, 1),
            null, 10000m, "try", null);

        _repository.GetUserByIdAsync(FreeUser.Id, Arg.Any<CancellationToken>()).Returns(FreeUser);
        _repository.GetActiveAssetBySymbolAsync("BTC", Arg.Any<CancellationToken>()).Returns(BtcAsset);
        _repository.CreateWithinLimitAsync(Arg.Any<SavedScenario>(), 5, Arg.Any<CancellationToken>())
                   .Returns(callInfo => callInfo.Arg<SavedScenario>());

        var result = await _sut.SaveScenarioAsync(request, CancellationToken.None);

        result.SellDate.Should().BeNull();
        result.Label.Should().BeNull();
    }

    [Fact]
    public async Task SaveScenarioAsync_WhatIfAssetNotFound_ThrowsAssetNotFoundException()
    {
        var request = new SaveScenarioRequest("YOKASSET", "Bilinmeyen", new DateOnly(2020, 1, 1),
            null, 100m, "try", null);

        _repository.GetUserByIdAsync(FreeUser.Id, Arg.Any<CancellationToken>()).Returns(FreeUser);
        _repository.GetActiveAssetBySymbolAsync("YOKASSET", Arg.Any<CancellationToken>())
                   .Returns((Asset?)null);

        var act = () => _sut.SaveScenarioAsync(request, CancellationToken.None);

        await act.Should().ThrowAsync<AssetNotFoundException>()
                 .Where(ex => ex.Symbol == "YOKASSET");
    }

    [Fact]
    public async Task SaveScenarioAsync_DcaTypeWithUnknownAsset_ThrowsAssetNotFoundException()
    {
        // F2.6-9 ([C-F-27/28]): DCA scenario type için de FK kontrolü zorunlu.
        var request = new SaveScenarioRequest("UNKNOWN", "Yok", new DateOnly(2020, 1, 1),
            null, 100m, "try", null, Type: "dca");

        _repository.GetUserByIdAsync(FreeUser.Id, Arg.Any<CancellationToken>()).Returns(FreeUser);
        _repository.GetActiveAssetBySymbolAsync("UNKNOWN", Arg.Any<CancellationToken>())
                   .Returns((Asset?)null);

        var act = () => _sut.SaveScenarioAsync(request, CancellationToken.None);

        await act.Should().ThrowAsync<AssetNotFoundException>()
                 .Where(ex => ex.Symbol == "UNKNOWN");
    }

    [Fact]
    public async Task SaveScenarioAsync_InvalidType_ThrowsValidationException()
    {
        // F2.6-9: bilinmeyen tip ScenarioTypes.All listesi dışında → 400.
        var request = new SaveScenarioRequest("BTC", "Bitcoin", new DateOnly(2020, 1, 1),
            null, 100m, "try", null, Type: "unknown_type");

        _repository.GetUserByIdAsync(FreeUser.Id, Arg.Any<CancellationToken>()).Returns(FreeUser);
        var act = () => _sut.SaveScenarioAsync(request, CancellationToken.None);

        await act.Should().ThrowAsync<ValidationException>()
                 .Where(ex => ex.Field == "Type");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task SaveScenarioAsync_BlankType_FailsBeforeAnyUserWrite(string type)
    {
        var request = new SaveScenarioRequest(
            "BTC", "Bitcoin", new DateOnly(2020, 1, 1), null,
            100m, "try", null, Type: type);

        var act = () => _sut.SaveScenarioAsync(request, CancellationToken.None);

        await act.Should().ThrowAsync<ValidationException>()
            .Where(exception => exception.Field == "Type");
    }

    [Fact]
    public async Task SaveScenarioAsync_InvalidExtraData_FailsBeforeAnyUserWrite()
    {
        var extraData = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(
            "{\"unexpected\":\"sentinel\"}");
        var request = new SaveScenarioRequest("BTC", "Bitcoin", new DateOnly(2020, 1, 1),
            null, 100m, "try", null, ExtraData: extraData);

        var act = () => _sut.SaveScenarioAsync(request, CancellationToken.None);

        await act.Should().ThrowAsync<ValidationException>()
            .Where(ex => ex.Field == "ExtraData");
        await _repository.DidNotReceive().CreateWithinLimitAsync(
            Arg.Any<SavedScenario>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SaveScenarioAsync_ComparisonType_SkipsAssetLookupAndSaves()
    {
        // F2.3-6: comparison/portfolio tipinde server AssetSymbol'ü olduğu gibi alır
        // (asset FK doğrulaması yok) ancak AssetDisplayName client'tan değil
        // sembolün kendisinden türetilir — buradan istek satırından "Bitcoin, Ethereum"
        // gibi keyfi metin sızmaz.
        var request = new SaveScenarioRequest("BTC,ETH", "ClientTextIgnored",
            new DateOnly(2020, 1, 1), new DateOnly(2021, 1, 1),
            10000m, "try", "Kripto karşılaştırması",
            Type: "comparison");

        _repository.GetUserByIdAsync(FreeUser.Id, Arg.Any<CancellationToken>()).Returns(FreeUser);
        _repository.CreateWithinLimitAsync(Arg.Any<SavedScenario>(), 5, Arg.Any<CancellationToken>())
                   .Returns(callInfo => callInfo.Arg<SavedScenario>());

        var result = await _sut.SaveScenarioAsync(request, CancellationToken.None);

        result.Type.Should().Be("comparison");
        result.AssetSymbol.Should().Be("BTC,ETH");
        // F2.3-6: server-side display name = symbol (asset FK yoksa client metnine güvenilmez).
        result.AssetDisplayName.Should().Be("BTC,ETH");

        // Asset FK araması yapılmamalı
        await _repository.DidNotReceive()
                         .GetActiveAssetBySymbolAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SaveScenarioAsync_PortfolioType_SkipsAssetLookupAndSaves()
    {
        var request = new SaveScenarioRequest("PORTFOLIO", "ClientTextIgnored",
            new DateOnly(2020, 1, 1), null,
            50000m, "try", "Karışık portföy",
            Type: "portfolio");

        _repository.GetUserByIdAsync(FreeUser.Id, Arg.Any<CancellationToken>()).Returns(FreeUser);
        _repository.CreateWithinLimitAsync(Arg.Any<SavedScenario>(), 5, Arg.Any<CancellationToken>())
                   .Returns(callInfo => callInfo.Arg<SavedScenario>());

        var result = await _sut.SaveScenarioAsync(request, CancellationToken.None);

        result.Type.Should().Be("portfolio");
        result.AssetSymbol.Should().Be("PORTFOLIO");
        // F2.3-6: server-side display name = symbol.
        result.AssetDisplayName.Should().Be("PORTFOLIO");

        // Asset FK araması yapılmamalı
        await _repository.DidNotReceive()
                         .GetActiveAssetBySymbolAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SaveScenarioAsync_FreeUserAtLimit_ThrowsScenarioLimitExceededException()
    {
        var request = new SaveScenarioRequest("BTC", "Bitcoin", new DateOnly(2020, 1, 1),
            null, 100m, "try", null);

        _repository.GetUserByIdAsync(FreeUser.Id, Arg.Any<CancellationToken>()).Returns(FreeUser);
        _repository.GetActiveAssetBySymbolAsync("BTC", Arg.Any<CancellationToken>()).Returns(BtcAsset);
        _repository.CreateWithinLimitAsync(Arg.Any<SavedScenario>(), 5, Arg.Any<CancellationToken>())
            .Returns<Task<SavedScenario>>(_ => throw new ScenarioLimitExceededException(5));

        var act = () => _sut.SaveScenarioAsync(request, CancellationToken.None);

        await act.Should().ThrowAsync<ScenarioLimitExceededException>()
                 .Where(ex => ex.Limit == 5);
    }

    [Fact]
    public async Task SaveScenarioAsync_FreeUserUnderLimit_SavesSuccessfully()
    {
        var request = new SaveScenarioRequest("BTC", "Bitcoin", new DateOnly(2020, 1, 1),
            null, 100m, "try", null);

        _repository.GetUserByIdAsync(FreeUser.Id, Arg.Any<CancellationToken>()).Returns(FreeUser);
        _repository.GetActiveAssetBySymbolAsync("BTC", Arg.Any<CancellationToken>()).Returns(BtcAsset);
        _repository.CreateWithinLimitAsync(Arg.Any<SavedScenario>(), 5, Arg.Any<CancellationToken>())
                   .Returns(callInfo => callInfo.Arg<SavedScenario>());

        var act = () => _sut.SaveScenarioAsync(request, CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task SaveScenarioAsync_PremiumUser_UnderSystemHardCap_SavesSuccessfully()
    {
        var request = new SaveScenarioRequest("BTC", "Bitcoin", new DateOnly(2020, 1, 1),
            null, 100m, "try", null);

        _repository.GetUserByIdAsync(PremiumUser.Id, Arg.Any<CancellationToken>()).Returns(PremiumUser);
        _repository.GetActiveAssetBySymbolAsync("BTC", Arg.Any<CancellationToken>()).Returns(BtcAsset);
        _repository.CreateWithinLimitAsync(Arg.Any<SavedScenario>(), 100, Arg.Any<CancellationToken>())
                   .Returns(callInfo => callInfo.Arg<SavedScenario>());

        _principalContext.PrincipalId.Returns(PremiumUser.Id);
        await _sut.SaveScenarioAsync(request, CancellationToken.None);

        await _repository.Received(1).CreateWithinLimitAsync(
            Arg.Is<SavedScenario>(saved => saved.UserId == PremiumUser.Id),
            SavedScenarioService.SystemSaveHardLimit,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SaveScenarioAsync_PremiumUserAtSystemHardCap_RejectsNewSave()
    {
        var request = new SaveScenarioRequest("BTC", "Bitcoin", new DateOnly(2020, 1, 1),
            null, 100m, "try", null);

        _repository.GetUserByIdAsync(PremiumUser.Id, Arg.Any<CancellationToken>()).Returns(PremiumUser);
        _repository.GetActiveAssetBySymbolAsync("BTC", Arg.Any<CancellationToken>()).Returns(BtcAsset);
        _repository.CreateWithinLimitAsync(Arg.Any<SavedScenario>(), 100, Arg.Any<CancellationToken>())
            .Returns<Task<SavedScenario>>(_ => throw new ScenarioLimitExceededException(100));
        _principalContext.PrincipalId.Returns(PremiumUser.Id);

        var act = () => _sut.SaveScenarioAsync(request, CancellationToken.None);

        await act.Should().ThrowAsync<ScenarioLimitExceededException>()
            .Where(ex => ex.Limit == SavedScenarioService.SystemSaveHardLimit);
        await _repository.Received(1).CreateWithinLimitAsync(
            Arg.Any<SavedScenario>(), 100, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SaveScenarioAsync_MissingAuthenticatedPrincipal_NeverClaimsLegacyDevice()
    {
        var request = new SaveScenarioRequest("BTC", "Bitcoin", new DateOnly(2020, 1, 1),
            null, 100m, "try", null);
        _repository.GetActiveAssetBySymbolAsync("BTC", Arg.Any<CancellationToken>()).Returns(BtcAsset);
        _repository.GetUserByIdAsync(FreeUser.Id, Arg.Any<CancellationToken>()).Returns((User?)null);

        var act = () => _sut.SaveScenarioAsync(request, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Authenticated installation principal is missing.");
        await _repository.DidNotReceive().CreateWithinLimitAsync(
            Arg.Any<SavedScenario>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SaveScenarioAsync_TrimmedMixedCaseTypeAndUnit_PersistsCanonicalValues()
    {
        var request = new SaveScenarioRequest(
            " btc ", "ignored", new DateOnly(2020, 1, 1), null,
            100m, " TRY ", null, Type: " DCA ");
        _repository.GetActiveAssetBySymbolAsync("BTC", Arg.Any<CancellationToken>()).Returns(BtcAsset);
        _repository.GetUserByIdAsync(FreeUser.Id, Arg.Any<CancellationToken>()).Returns(FreeUser);
        _repository.CreateWithinLimitAsync(Arg.Any<SavedScenario>(), 5, Arg.Any<CancellationToken>())
            .Returns(callInfo => callInfo.Arg<SavedScenario>());

        var result = await _sut.SaveScenarioAsync(request, CancellationToken.None);

        result.Type.Should().Be("dca");
        result.AmountType.Should().Be("try");
        await _repository.Received(1).CreateWithinLimitAsync(
            Arg.Is<SavedScenario>(saved =>
                saved.Type == "dca" && saved.QuantityUnit == "try" && saved.AssetSymbol == "BTC"),
            5,
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("bitcoin")]
    public async Task SaveScenarioAsync_BlankOrUnknownUnit_FailsBeforeAnyUserWrite(string? amountType)
    {
        var request = new SaveScenarioRequest(
            "BTC", "Bitcoin", new DateOnly(2020, 1, 1), null,
            100m, amountType!, null);

        var act = () => _sut.SaveScenarioAsync(request, CancellationToken.None);

        await act.Should().ThrowAsync<ValidationException>()
            .Where(exception => exception.Field == "AmountType");
    }

    [Theory]
    [InlineData("units")]
    [InlineData("grams")]
    public async Task SaveScenarioAsync_DcaNonTryUnit_FailsBeforeAnyUserWrite(string amountType)
    {
        var request = new SaveScenarioRequest(
            "BTC", "Bitcoin", new DateOnly(2020, 1, 1), null,
            100m, amountType, null, Type: "dca");

        var act = () => _sut.SaveScenarioAsync(request, CancellationToken.None);

        await act.Should().ThrowAsync<ValidationException>()
            .Where(exception => exception.Field == "AmountType");
    }

    [Theory]
    [InlineData("what_if", "units")]
    [InlineData("comparison", "grams")]
    [InlineData("portfolio", "try")]
    public async Task SaveScenarioAsync_NonDcaCanonicalUnit_IsAccepted(string type, string amountType)
    {
        var requiresAsset = type == "what_if";
        var request = new SaveScenarioRequest(
            requiresAsset ? "BTC" : "PORTFOLIO", "ignored",
            new DateOnly(2020, 1, 1), null, 100m, amountType, null, Type: type);
        if (requiresAsset)
            _repository.GetActiveAssetBySymbolAsync("BTC", Arg.Any<CancellationToken>()).Returns(BtcAsset);
        _repository.GetUserByIdAsync(FreeUser.Id, Arg.Any<CancellationToken>()).Returns(FreeUser);
        _repository.CreateWithinLimitAsync(Arg.Any<SavedScenario>(), 5, Arg.Any<CancellationToken>())
            .Returns(callInfo => callInfo.Arg<SavedScenario>());

        var result = await _sut.SaveScenarioAsync(request, CancellationToken.None);

        result.AmountType.Should().Be(amountType);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task SaveScenarioAsync_SellDateNotStrictlyAfterBuyDate_FailsBeforeAnyUserWrite(
        int sellDateOffsetDays)
    {
        var buyDate = new DateOnly(2020, 1, 2);
        var request = new SaveScenarioRequest(
            "BTC", "Bitcoin", buyDate, buyDate.AddDays(sellDateOffsetDays),
            100m, "try", null);

        var act = () => _sut.SaveScenarioAsync(request, CancellationToken.None);

        await act.Should().ThrowAsync<ValidationException>()
            .Where(exception => exception.Field == "SellDate");
    }

    // ── DeleteScenarioAsync ──────────────────────────────────────────────────

    [Fact]
    public async Task DeleteScenarioAsync_OwnScenario_DeletesSuccessfully()
    {
        var scenarioId = Guid.NewGuid();
        var scenario = new SavedScenario
        {
            Id = scenarioId, UserId = FreeUser.Id, AssetId = BtcAsset.Id,
            AssetSymbol = "BTC", AssetDisplayName = "Bitcoin",
            BuyDate = new DateOnly(2020, 1, 1), Quantity = 100m, QuantityUnit = "try",
            CreatedAt = DateTimeOffset.UtcNow
        };

        _repository.GetUserByIdAsync(FreeUser.Id, Arg.Any<CancellationToken>()).Returns(FreeUser);
        _repository.GetByIdAndUserIdAsync(scenarioId, FreeUser.Id, Arg.Any<CancellationToken>())
                   .Returns(scenario);

        await _sut.DeleteScenarioAsync(scenarioId, CancellationToken.None);

        await _repository.Received(1).DeleteAsync(scenario, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteScenarioAsync_ScenarioNotFound_ThrowsScenarioNotFoundException()
    {
        var scenarioId = Guid.NewGuid();

        _repository.GetUserByIdAsync(FreeUser.Id, Arg.Any<CancellationToken>()).Returns(FreeUser);
        _repository.GetByIdAndUserIdAsync(scenarioId, FreeUser.Id, Arg.Any<CancellationToken>())
                   .Returns((SavedScenario?)null);

        var act = () => _sut.DeleteScenarioAsync(scenarioId, CancellationToken.None);

        await act.Should().ThrowAsync<ScenarioNotFoundException>()
                 .Where(ex => ex.ScenarioId == scenarioId);
    }

    [Fact]
    public async Task DeleteScenarioAsync_ScenarioBelongsToOtherUser_ThrowsScenarioNotFoundException()
    {
        var scenarioId = Guid.NewGuid();

        // Başka kullanıcının senaryosu → repository null döner (ownership filtresi)
        _repository.GetUserByIdAsync(FreeUser.Id, Arg.Any<CancellationToken>()).Returns(FreeUser);
        _repository.GetByIdAndUserIdAsync(scenarioId, FreeUser.Id, Arg.Any<CancellationToken>())
                   .Returns((SavedScenario?)null);

        var act = () => _sut.DeleteScenarioAsync(scenarioId, CancellationToken.None);

        await act.Should().ThrowAsync<ScenarioNotFoundException>();
        await _repository.DidNotReceive().DeleteAsync(Arg.Any<SavedScenario>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteScenarioAsync_MissingAuthenticatedPrincipal_DoesNotClaimLegacyDevice()
    {
        // F2.2-13: DELETE path'i de user yaratma side-effect'i taşımaz.
        // Kullanıcı kaydı yoksa senaryo da yoktur → 404 ScenarioNotFound.
        var scenarioId = Guid.NewGuid();

        _repository.GetUserByIdAsync(FreeUser.Id, Arg.Any<CancellationToken>()).Returns((User?)null);

        var act = () => _sut.DeleteScenarioAsync(scenarioId, CancellationToken.None);

        await act.Should().ThrowAsync<ScenarioNotFoundException>();
        await _repository.DidNotReceive().GetByIdAndUserIdAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    private static SavedScenario CreateScenario(Guid id, DateTimeOffset createdAt) => new()
    {
        Id = id,
        UserId = FreeUser.Id,
        AssetId = BtcAsset.Id,
        AssetSymbol = BtcAsset.Symbol,
        AssetDisplayName = BtcAsset.DisplayName,
        Type = "what_if",
        BuyDate = new DateOnly(2020, 1, 1),
        Quantity = 100m,
        QuantityUnit = "try",
        CreatedAt = createdAt,
    };
}
