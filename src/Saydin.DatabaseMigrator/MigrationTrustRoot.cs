namespace Saydin.Migrations;

// Single compile-time trust root linked into the migrator and audit executables.
// Raw bytes, not normalized SQL, are authenticated.
internal static class MigrationTrustRoot
{
    internal static IReadOnlyList<string> Versions { get; } = Array.AsReadOnly(new[]
    {
        "001_initial", "002_add_assets", "003_switch_precious_metals_to_oxr",
        "004_add_inflation_rates", "005_add_tcmb_currencies", "006_scenario_type",
        "007_add_dca_scenario_type", "008_add_activity_logs",
        "008b_disable_activity_log_compression", "009_widen_activity_log_columns",
        "010_add_geo_columns", "011_phase2_schema_hardening", "012_faz3_schema",
        "012b_create_exporter_role", "013_enable_activity_log_compression",
        "014_schema_migrations", "015_ingestion_windows", "016_ingestion_write_fence",
        "017_authoritative_market_calendars", "018_scenario_integrity",
        "019_privilege_separation", "020_price_authority_expand", "021_api_trust_expand",
        "022_principal_retention", "023_installation_lifecycle_admission",
        "024_installation_credential_rehash", "025_ingestion_calendar_rebind",
    });

    internal static IReadOnlyDictionary<string, string> Checksums { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["001_initial"] = "ab291bf72fbb2a3e164cdbbe4589f39fd4d403ba0a8f28be496a0a27bd8802ac",
            ["002_add_assets"] = "141094823b632a5c4c1aaee6b0c29f1e34879c81bbeb118943ff86fb73ad25b5",
            ["003_switch_precious_metals_to_oxr"] = "85e708df391c021527dbe1b87e37127cd32fb8b295c600f729e6d1038862b29f",
            ["004_add_inflation_rates"] = "de80a800583aba623aee55c0bc094bd73e7314acf31ee7458a190e44412adf86",
            ["005_add_tcmb_currencies"] = "8ee83f29ef8d0a5f1b6fa9dd9ec7e9997bf5a8fc3cd926094b35f43f70a381e8",
            ["006_scenario_type"] = "068524abad9ed0746d3ffe81680641460b1871d57bd2db526194da64efe41364",
            ["007_add_dca_scenario_type"] = "91af4b2916248d52eeabe5b36279769e5c04267c17f6abbc2c7dbb87b9664b3b",
            ["008_add_activity_logs"] = "a39d58a58abc3dbac1050f0d9fdbf4958e9953f088a7db45e27b082daa4e309c",
            ["008b_disable_activity_log_compression"] = "93aaf469834c01aa6ca412c0a18b0c0d3f126ce2b3e2dc8a9efcdfaadbf6b119",
            ["009_widen_activity_log_columns"] = "b514ca24fce06a37668b17a9184323ad5d619d6283623f9767cd3812e09fc0b6",
            ["010_add_geo_columns"] = "e405c7fd575f382a19e90bf7c5f173f1fbb7cb2e5aa15d4f1cd1add71e67e6e4",
            ["011_phase2_schema_hardening"] = "979e9f8bc613ba345789b25d88d8d6e1fa861f503ba40b76056c66355cdcdeb4",
            ["012_faz3_schema"] = "3bff94c152a619cd3288a981ede18a86df8ff979b67d111a36104f30724fd527",
            ["012b_create_exporter_role"] = "6c0e91e841ad671458b657ffb2516d1d19b2bfe78f9ebc1d545d26f03881e114",
            ["013_enable_activity_log_compression"] = "6c46996e279c7c09a78ae8b1ed91c5a05eed46e4c72253ddec8341758387e6ec",
            ["014_schema_migrations"] = "01bd31cee376d427f6677749974562c69ee3c128a453e9b764fc4fe1149e5995",
            ["015_ingestion_windows"] = "8e7aff61ad38ab4654d5b8936d97e033324528f6a05276b0f15148eb4945c14d",
            ["016_ingestion_write_fence"] = "695efbd317d31a35317cfe146029f90baa79a8fa6b0c2141c0821f2b351c7334",
            ["017_authoritative_market_calendars"] = "85712695a31627b3663116a29020fdd356a5ea965a0328aa396821361890193e",
            ["018_scenario_integrity"] = "8f6f76c12862c5f3696f9241c9e6566e75d048875552656b32b7eca84f65a056",
            ["019_privilege_separation"] = "213fd3dbe4d8de5f0ad6e88bddc3d059bc73917bf15f511f17713f81c920f31d",
            ["020_price_authority_expand"] = "8cb3f07bffef6013f42d196a20f0c08ed3e02547028d5694d6fba5f9749c52a8",
            ["021_api_trust_expand"] = "1f44aa1413d611cb8b078541e0100985c33614274e2fd700a8f8b94303045c1e",
            ["022_principal_retention"] = "568017c27eb6038a06b48ee00f2f0820bba6cf7b577dd5f283291ac9995e8afd",
            ["023_installation_lifecycle_admission"] = "1b76002b7c2e3b9156e433e1268a085027e383fa0025e82f398f2bb27aa1663e",
            ["024_installation_credential_rehash"] = "afda0e5a86b8d4b2c6b0f809372db72933f5c7e5b4b1dd18eaa8dd50dbc773d9",
            ["025_ingestion_calendar_rebind"] = "a20338e2d3db8f75a848949a937baaee4fa3f426e58814a4de352b0cfc2be051",
        };
}
