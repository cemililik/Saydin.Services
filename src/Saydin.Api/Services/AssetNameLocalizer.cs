using Microsoft.Extensions.Localization;

namespace Saydin.Api.Services;

/// <summary>
/// Asset sembolünden lokalize edilmiş display name döner. Önceden 3 dosyada
/// (WhatIfCalculator, DcaCalculator, AssetService) tekrarlanan helper bu sınıfta birleşti.
/// </summary>
public interface IAssetNameLocalizer
{
    string Localize(string symbol, string? fallbackDisplayName);
}

public sealed class AssetNameLocalizer(IStringLocalizer<ErrorMessages> localizer)
    : IAssetNameLocalizer
{
    public string Localize(string symbol, string? fallbackDisplayName)
    {
        var localized = localizer[$"Asset_{symbol}"];
        if (!localized.ResourceNotFound)
            return localized.Value;

        // Fallback: resx'te key yok. DB display name yerine sembolün kendisini döndür —
        // DB'de Türkçe metin kalmış olabilir (multi-language cache poisoning riski).
        return fallbackDisplayName ?? symbol;
    }
}
