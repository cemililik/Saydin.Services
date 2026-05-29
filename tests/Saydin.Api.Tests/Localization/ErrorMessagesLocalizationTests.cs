using System.Globalization;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Saydin.Api;

namespace Saydin.Api.Tests.Localization;

/// <summary>
/// D4 regresyon kilidi: IStringLocalizer&lt;ErrorMessages&gt;'in resx VALUE'ları döndürdüğünü
/// (ham KEY değil) ve tr/en ayrımının çalıştığını doğrular.
///
/// Kök neden (düzeltildi): resx'ler ErrorMessages.cs (namespace Saydin.Api) ile DependentUpon
/// olduğundan "Saydin.Api.ErrorMessages.resources" olarak gömülür ("Resources" segmenti YOK).
/// Program.cs AddLocalization()'ı ResourcesPath OLMADAN çağırmalı; aksi halde factory
/// "Saydin.Api.Resources.ErrorMessages" arar, her lookup ıskalar ve key sızar.
/// </summary>
public class ErrorMessagesLocalizationTests
{
    private static IStringLocalizer<ErrorMessages> CreateLocalizer(Action<LocalizationOptions>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();                   // ResourceManagerStringLocalizerFactory ILoggerFactory ister
        if (configure is null)
            services.AddLocalization();          // Program.cs ile birebir (ResourcesPath YOK)
        else
            services.AddLocalization(configure);
        return services.BuildServiceProvider().GetRequiredService<IStringLocalizer<ErrorMessages>>();
    }

    [Theory]
    [InlineData("DeviceIdRequired")]
    [InlineData("AmountMustBePositive")]
    [InlineData("PriceNotFound")]
    public void GetString_WithValidKey_ReturnsLocalizedValueNotKey(string key)
    {
        var localizer = CreateLocalizer();

        foreach (var culture in new[] { "tr", "en" })
        {
            using var _ = new CultureScope(culture);
            var entry = localizer[key];

            entry.ResourceNotFound.Should().BeFalse(
                "'{0}' anahtarı '{1}' kültüründe resx'ten çözülmeli", key, culture);
            entry.Value.Should().NotBe(key, "lokalize değer dönmeli, ham anahtar değil");
            entry.Value.Should().NotBeNullOrWhiteSpace();
        }
    }

    [Fact]
    public void GetString_WithDifferentCultures_ReturnsDifferentTranslations()
    {
        var localizer = CreateLocalizer();

        string tr;
        string en;
        using (new CultureScope("tr")) tr = localizer["DeviceIdRequired"].Value;
        using (new CultureScope("en")) en = localizer["DeviceIdRequired"].Value;

        tr.Should().NotBe(en, "tr ve en çevirileri farklı olmalı (lokalizasyon gerçekten çalışıyor)");
    }

    [Fact]
    public void EmbeddedResource_UsesClassNamespace_NotResourcesFolderSegment()
    {
        var names = typeof(ErrorMessages).Assembly.GetManifestResourceNames();

        names.Should().Contain("Saydin.Api.ErrorMessages.resources",
            "resx, sınıf namespace'i (Saydin.Api) ile gömülür");
        names.Should().NotContain("Saydin.Api.Resources.ErrorMessages.resources",
            "klasör-segmentli isim yok; bu yüzden ResourcesPath='Resources' yanlıştır (D4 bug)");
    }

    [Fact]
    public void GetString_WithWrongResourcesPath_ReturnsResourceNotFound()
    {
        // Eski hatalı yapılandırmanın neden çalışmadığını kilitler: ResourcesPath="Resources"
        // embedding ile uyuşmaz → key çözülemez. Bu test kırılırsa embedding değişmiştir ve
        // Program.cs'teki ResourcesPath kararı yeniden gözden geçirilmelidir.
        var localizer = CreateLocalizer(o => o.ResourcesPath = "Resources");

        using var _ = new CultureScope("tr");
        localizer["DeviceIdRequired"].ResourceNotFound.Should().BeTrue(
            "ResourcesPath='Resources' embedding ile uyuşmaz; Program.cs onu KULLANMAMALI");
    }

    private sealed class CultureScope : IDisposable
    {
        private readonly CultureInfo _previousCulture;
        private readonly CultureInfo _previousUiCulture;

        public CultureScope(string culture)
        {
            _previousCulture = CultureInfo.CurrentCulture;
            _previousUiCulture = CultureInfo.CurrentUICulture;
            var info = new CultureInfo(culture);
            CultureInfo.CurrentCulture = info;
            CultureInfo.CurrentUICulture = info;
        }

        public void Dispose()
        {
            CultureInfo.CurrentCulture = _previousCulture;
            CultureInfo.CurrentUICulture = _previousUiCulture;
        }
    }
}
