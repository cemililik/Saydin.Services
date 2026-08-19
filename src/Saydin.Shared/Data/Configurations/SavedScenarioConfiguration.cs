using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Saydin.Shared.Constants;
using Saydin.Shared.Entities;

namespace Saydin.Shared.Data.Configurations;

public sealed class SavedScenarioConfiguration : IEntityTypeConfiguration<SavedScenario>
{
    public void Configure(EntityTypeBuilder<SavedScenario> builder)
    {
        // F2.5-2 / F2.5-7 ([C-E-9], [G-E-02]): saved_scenarios.type CHECK constraint
        // kod tarafında da modellenir. DB CHECK ile ScenarioTypes.All listesi
        // birebir aynı olmalıdır.
        builder.ToTable("saved_scenarios", table =>
        {
            table.HasCheckConstraint(
                "chk_saved_scenarios_type",
                $"type IN ({string.Join(", ", ScenarioTypes.All.Select(v => $"'{v}'"))})");
            table.HasCheckConstraint(
                "chk_saved_scenarios_unit",
                $"quantity_unit IN ({string.Join(", ", QuantityUnits.All.Select(v => $"'{v}'"))})");
            table.HasCheckConstraint(
                "chk_saved_scenarios_dates",
                "sell_date IS NULL OR sell_date > buy_date");
            table.HasCheckConstraint(
                "chk_saved_scenarios_type_unit",
                $"type <> '{ScenarioTypes.Dca}' OR quantity_unit = '{QuantityUnits.Try}'");
            table.HasCheckConstraint(
                "chk_saved_scenarios_extra_data_object",
                "extra_data IS NULL OR jsonb_typeof(extra_data) IN ('object', 'null')");
            table.HasCheckConstraint(
                "chk_saved_scenarios_extra_data_size",
                "extra_data IS NULL OR octet_length(extra_data::text) <= 8192");
        });
        builder.HasKey(s => s.Id);

        builder.Property(s => s.Quantity).HasColumnType("numeric(18,8)").IsRequired();
        builder.Property(s => s.QuantityUnit).HasMaxLength(20).IsRequired();
        builder.Property(s => s.Label).HasMaxLength(200);

        builder.Property(s => s.AssetSymbol).HasMaxLength(100).IsRequired();
        builder.Property(s => s.AssetDisplayName).HasMaxLength(200).IsRequired();
        builder.Property(s => s.Type).HasMaxLength(20).IsRequired().HasDefaultValue(ScenarioTypes.WhatIf);
        // SHRD-012: JsonElement? için ValueComparer. EF Core'un default object
        // identity karşılaştırması struct JsonElement için her zaman "değişti"
        // diyebiliyor; bu ValueComparer raw text üzerinden equality sağlar.
        // NOT: GetRawText() normalizasyon YAPMAZ — whitespace farkları ve
        // property order'ı doğrudan etkiler. Üretici tarafında deterministik
        // serializer kullanılırsa (System.Text.Json default) aynı C# objesi
        // için aynı raw text üretilir; pratikte gereksiz UPDATE riski yoktur.
        // Tam normalize karşılaştırma istenirse JsonNode tree'ye parse + sort
        // gerekir (allocation pahalı) — şu an basit yaklaşım kabul edilebilir.
        builder.Property(s => s.ExtraData)
            .HasColumnType("jsonb")
            .Metadata.SetValueComparer(new ValueComparer<JsonElement?>(
                (a, b) => CompareJson(a, b),
                v => v.HasValue ? v.Value.GetRawText().GetHashCode() : 0,
                v => v));

        // F2.5-3 ([C-E-10]): DB DEFAULT NOW() ile init-only CreatedAt arasındaki kayma
        // EF'in farkındalığına alınır. Caller explicit DateTimeOffset.UtcNow geçerse
        // o değer kullanılır; explicit değer yoksa DB tarafı NOW() ile doldurur.
        // EF "Add-Migration" zamanı kolonun DEFAULT NOW() olduğunu schema'da görür,
        // gereksiz "drop default" üretmez.
        builder.Property(s => s.CreatedAt)
            .HasDefaultValueSql("NOW()");

        builder.HasOne(s => s.User)
            .WithMany(u => u.SavedScenarios)
            .HasForeignKey(s => s.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(s => s.Asset)
            .WithMany()
            .HasForeignKey(s => s.AssetId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(s => new { s.UserId, s.CreatedAt, s.Id })
            .IsDescending(false, true, true)
            .HasDatabaseName("idx_saved_scenarios_user_created_id_desc");
    }

    /// <summary>
    /// SHRD-012: İki <c>JsonElement?</c>'i ham metin (raw text) üzerinden karşılaştır.
    /// Null vs HasValue=false eşittir. Whitespace ve property order farkları
    /// karşılaştırmayı etkiler — JsonElement.GetRawText() normalizasyon yapmaz;
    /// deterministik serializer kullanmak çağıranın sorumluluğunda.
    /// </summary>
    private static bool CompareJson(JsonElement? a, JsonElement? b)
    {
        if (!a.HasValue && !b.HasValue) return true;
        if (!a.HasValue || !b.HasValue) return false;
        return string.Equals(a.Value.GetRawText(), b.Value.GetRawText(), StringComparison.Ordinal);
    }
}
