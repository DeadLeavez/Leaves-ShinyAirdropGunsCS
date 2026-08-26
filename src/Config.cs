using System.Text.Json.Serialization;
using SPTarkov.Server.Core.Models.Common;

namespace Leaves_ShinyAirdropGuns;

// Mirrors config/config.jsonc.
public record ShinyAirdropGunsConfig
{
    [JsonPropertyName("blacklistFromAirdrop")]
    public required bool BlacklistFromAirdrop { get; set; }

    [JsonPropertyName("addToQuests")]
    public required bool AddToQuests { get; set; }

    [JsonPropertyName("addToMasteries")]
    public required bool AddToMasteries { get; set; }

    [JsonPropertyName("replaceAllAirdrops")]
    public required bool ReplaceAllAirdrops { get; set; }

    [JsonPropertyName("debug")]
    public required bool Debug { get; set; }

    [JsonPropertyName("allowSpecialSlots")]
    public required bool AllowSpecialSlots { get; set; }

    [JsonPropertyName("addRevolversIntoPistolCase")]
    public required bool AddRevolversIntoPistolCase { get; set; }

    [JsonPropertyName("blacklistFromFence")]
    public required bool BlacklistFromFence { get; set; }

    [JsonPropertyName("weaponGroups")]
    public required Dictionary<string, WeaponGroupConfig> WeaponGroups { get; set; }
}

public record WeaponGroupConfig
{
    [JsonPropertyName("weaponsToMakeShiny")]
    public required List<MongoId> WeaponsToMakeShiny { get; set; }

    // All multiplier/flat-adjustment fields below are optional, defaulting to a no-op (1 for
    // multipliers, 0 for flat adjustments) when absent - mirrors the TS original's `field ? field
    // : default` pattern, but as a genuine "is this configured" check via nullable double instead
    // of JS's truthy check, which would also (almost certainly unintentionally) treat an explicit
    // 0 multiplier as "not configured". No shipped config sets a multiplier to exactly 0, so this
    // is a faithful, behavior-preserving translation, not a functional change.
    [JsonPropertyName("fireRateMultiplier")]
    public double? FireRateMultiplier { get; set; }

    [JsonPropertyName("fireRateFlatIncrease")]
    public double? FireRateFlatIncrease { get; set; }

    [JsonPropertyName("ergoFlatIncrease")]
    public double? ErgoFlatIncrease { get; set; }

    [JsonPropertyName("ergoMultiplier")]
    public double? ErgoMultiplier { get; set; }

    [JsonPropertyName("horizontalRecoilFlatReduction")]
    public double? HorizontalRecoilFlatReduction { get; set; }

    [JsonPropertyName("horizontalRecoilMultiplier")]
    public double? HorizontalRecoilMultiplier { get; set; }

    [JsonPropertyName("verticalRecoilFlatReduction")]
    public double? VerticalRecoilFlatReduction { get; set; }

    [JsonPropertyName("verticalRecoilMultiplier")]
    public double? VerticalRecoilMultiplier { get; set; }

    [JsonPropertyName("heatFactorMultiplier")]
    public double? HeatFactorMultiplier { get; set; }

    [JsonPropertyName("heatFactorByShotMultiplier")]
    public double? HeatFactorByShotMultiplier { get; set; }

    [JsonPropertyName("durabilityBurnMultiplier")]
    public double? DurabilityBurnMultiplier { get; set; }

    [JsonPropertyName("accuracyMultiplier")]
    public double? AccuracyMultiplier { get; set; }

    [JsonPropertyName("velocityMultiplier")]
    public double? VelocityMultiplier { get; set; }

    [JsonPropertyName("doubleActionAccuracyPenaltyMultiplier")]
    public double? DoubleActionAccuracyPenaltyMultiplier { get; set; }

    [JsonPropertyName("addToSpecialSlots")]
    public bool AddToSpecialSlots { get; set; }

    [JsonPropertyName("setFireMode")]
    public List<string>? SetFireMode { get; set; }

    [JsonPropertyName("weaponBoxWeight")]
    public double? WeaponBoxWeight { get; set; }

    [JsonPropertyName("priceMulti")]
    public double? PriceMulti { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    // These 5 have to exist in every group block (JSON key present), but everything besides
    // background can be an empty string if desired - matches the TS config's own doc comment.
    [JsonPropertyName("background")]
    public required string Background { get; set; }

    [JsonPropertyName("prefix")]
    public required string Prefix { get; set; }

    [JsonPropertyName("suffix")]
    public required string Suffix { get; set; }

    [JsonPropertyName("shortNamePrefix")]
    public required string ShortNamePrefix { get; set; }

    [JsonPropertyName("shortNameSuffix")]
    public required string ShortNameSuffix { get; set; }
}
