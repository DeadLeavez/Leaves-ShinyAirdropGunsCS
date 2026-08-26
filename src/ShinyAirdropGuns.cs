using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Logging;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Models.Spt.Mod;
using SPTarkov.Server.Core.Servers;
using SPTarkov.Server.Core.Services;
using SPTarkov.Server.Core.Services.Mod;
using SPTarkov.Server.Core.Utils;
using SPTarkov.Server.Core.Utils.Cloners;

namespace Leaves_ShinyAirdropGuns;

// Grab-bag of small toggles plus one big item-generation pass - no periodic behavior, so IOnLoad
// only (no Singleton/double-construction concern like BarterEconomy/LimitedTraders have).
//
// This priority is squeezed between two hard deadlines - do not move it without re-reading both.
//
//   LATE ENOUGH: content mods (WTT etc.) inject their weapons at PostDBModLoader+2. Running before
//   that means a modded gun listed in weaponGroups simply doesn't exist yet, so it can't be cloned.
//
//   EARLY ENOUGH: SPT's PresetCallbacks (OnLoadOrder.PresetCallbacks = 900000) HYDRATES A CACHE of
//   every weapon's presets. FixPresets below writes the shiny presets into globals, but a preset
//   added after that cache is built is invisible to PresetHelper - so the sealed weapon case finds
//   no preset for the shiny reward, falls back to "a random preset of the same weapon type", gets
//   an empty list and throws "Sequence contains no elements" the moment a player opens a case.
//   (Learned the hard way: parking this at PostSptModLoader broke every weapon case.)
//
// PostDBModLoader+90000 sits in that window: after every mod in the PostDBModLoader stage (which
// runs 400000-499999, since the next stage starts at +100000 - same trick LEWDS uses to go last),
// and well before 900000. It also still lands before Questrandomizer's LateOnLoad
// (PostSptModLoader+2), which reads the equivalents RegisterQuestrandomizerEquivalents registers.
[Injectable(TypePriority = OnLoadOrder.PostDBModLoader + 90000)]
public class ShinyAirdropGuns(
    DatabaseService databaseService,
    ModHelper modHelper,
    HandbookHelper handbookHelper,
    CustomItemService customItemService,
    ICloner cloner,
#pragma warning disable CS0618 // ConfigServer is obsolete (see BarterEconomy.ApplyDebugFastResupply's comment) but still the only way to reach ItemConfig/InventoryConfig/AirdropConfig/TraderConfig in 4.0.13.
    ConfigServer configServer,
#pragma warning restore CS0618
    // The raw DI container, for the OPTIONAL Questrandomizer integration - injecting
    // QuestrandomizerCompatibility directly as a constructor parameter would make this whole mod
    // fail to load whenever Questrandomizer isn't installed, whereas GetService through this
    // returns null in that case instead. See RegisterQuestrandomizerEquivalents below.
    IServiceProvider serviceProvider,
    // Where the shiny NAME/SHORT-NAME is actually applied - see ShinyLocaleFixup for why it can't be
    // done here in the clone pass. We just record a job per shiny gun during AddWeaponGroup.
    ShinyLocaleFixup shinyLocaleFixup)
    : IOnLoad
{
    // The "pockets" item category. Special slots (SpecialSlot1/2/3) live on the equipped pockets
    // item, but WHICH pockets item that is varies: vanilla is 627a4e6b255f7527fb05a0f6, Unheard/TUE
    // profiles equip 65e080be269cbd5c5005e529, and SVM's Custom Pocket feature clones the pockets
    // item under a fresh id (a8edfb0bce53d103d3f62b9b) and re-equips THAT on the profile. So we can't
    // target one fixed id - we patch the special-slot filters on every item in this category (see
    // addWeaponToSpecialSlots).
    private static readonly MongoId PocketsParentId = "557596e64bdc2dc2118b4571";
    private static readonly MongoId CharacterInventory = "55d7217a4bdc2d86028b456d";
    private static readonly MongoId PistolCase = "567143bf4bdc2d1a0f8b4567";
    private static readonly MongoId ShotgunParentId = "5447b6094bdc2dc3278b4567";
    private static readonly MongoId SawedOffId = "64748cb8de82c85eaf0a273a";

    // BSG-side items whose inventory-slot filters don't follow the normal "parent category decides
    // where it fits" rule, so their shiny clones need the same manual filter patches the originals
    // needed - faithful port of the TS original's snowflakeWeapons list.
    private static readonly HashSet<MongoId> SnowflakeWeapons = new()
    {
        "624c2e8614da335f1e034d8c", // Rhino
        "61a4c8884f95bc3b2c5dc96f", // Rhino (different chambering)
        "633ec7c2a6918cb895019c6c", // Rsh 50
        SawedOffId,
    };

    private ShinyAirdropGunsConfig config = null!;
    private Dictionary<MongoId, TemplateItem> itemDB = null!;

    // Weapons that have a DEFAULT ("encyclopedia") preset. Built once in OnLoad; see the guard in
    // AddWeaponGroup for why a weapon without one must never be made shiny.
    private HashSet<MongoId> weaponsWithDefaultPreset = new();

    public Task OnLoad()
    {
        string modPath = modHelper.GetAbsolutePathToModFolder(Assembly.GetExecutingAssembly());
        config = modHelper.GetJsonDataFromFile<ShinyAirdropGunsConfig>(modPath, "config/config.jsonc");

        // ids.jsonc is runtime-owned and no longer shipped by the build (see the .csproj), so a
        // fresh install won't have it yet - IdStore starts empty in that case.
        var ids = new Leaves.IdStore(modPath, "config/ids.jsonc");

        var itemConfig = configServer.GetConfig<ItemConfig>();
        var inventoryConfig = configServer.GetConfig<InventoryConfig>();
        var airdropConfig = configServer.GetConfig<AirdropConfig>();

        itemDB = databaseService.GetItems();

        Leaves.Utils.Instance.printColor("[ShinyAirdropGuns] ShinyAirdropGuns Starting. Stay away from SPT, they bite!");

        var weaponIds = new Dictionary<MongoId, MongoId>();

        // Which weapons actually have a default preset - checked against in AddWeaponGroup below.
        weaponsWithDefaultPreset = databaseService.GetGlobals().ItemPresets.Values
            .Where(preset => preset.Encyclopedia != null && preset.Items?.Count > 0)
            .Select(preset => preset.Items![0].Template)
            .ToHashSet();

        // Has to happen before generation, since weighting is set per-weapon as groups are built.
        if (config.ReplaceAllAirdrops)
        {
            inventoryConfig.SealedAirdropContainer.WeaponRewardWeight.Clear();
        }

        foreach (var groupName in config.WeaponGroups.Keys)
        {
            AddWeaponGroup(groupName, weaponIds, ids, inventoryConfig);
        }

        // New ids may have been generated above - save them back so they're stable next launch.
        ids.Save();

        // Questrandomizer integration (optional): register every original->shiny pair so shiny
        // guns count toward "kill using X" quests and get the "Equivalent to:" description text.
        // The try/catch is what makes the dependency SOFT: if Questrandomizer isn't installed,
        // .NET throws a FileNotFoundException the moment it tries to prepare the method below
        // (because that method's code mentions a type from the missing assembly) - the exception
        // surfaces HERE, at the call, where we catch it and move on. That's also why the type
        // reference is quarantined in its own [MethodImpl(NoInlining)] method: inlined into
        // OnLoad, the load failure would escape this try/catch and kill the whole mod.
        try
        {
            RegisterQuestrandomizerEquivalents(weaponIds);
        }
        catch (Exception)
        {
            Leaves.Utils.Instance.printColor("[ShinyAirdropGuns] Questrandomizer not installed - skipping weapon-equivalent registration.");
        }

        if (config.Debug)
        {
            // TS original guarded this with `for (const ID in weaponIDs && this.config.debug)` -
            // `weaponIDs && this.config.debug` evaluates to just `this.config.debug` (a boolean),
            // and `for...in` over a boolean has zero enumerable properties, so this loop never ran
            // regardless of the debug setting. A genuine bug (almost certainly meant to be `if
            // (this.config.debug) { for (const ID in weaponIDs) ... }`), fixed here to actually
            // log like the debug setting promises.
            foreach (var (newId, oldId) in weaponIds)
            {
                Leaves.Utils.Instance.printColor($"NEW_ID:{newId}  OLD_ID:{oldId}");
            }
        }

        if (config.AddToQuests)
        {
            FixQuests(weaponIds);
        }

        if (config.AddToMasteries)
        {
            FixMastery(weaponIds);
        }

        if (config.Debug)
        {
            Leaves.Utils.Instance.debugJsonOutput(inventoryConfig.SealedAirdropContainer.WeaponRewardWeight, "inventoryConfig.sealedAirdropContainer.weaponRewardWeight");
        }

        var newIds = weaponIds.Keys.ToList();

        if (config.Debug)
        {
            Leaves.Utils.Instance.debugJsonOutput(newIds, "newIDs");
        }

        if (config.BlacklistFromAirdrop)
        {
            var loot = airdropConfig.Loot;

            loot["mixed"].ItemBlacklist.AddRange(newIds);
            loot["weaponArmor"].ItemBlacklist.AddRange(newIds);
            loot["foodMedical"].ItemBlacklist.AddRange(newIds);
            loot["barter"].ItemBlacklist.AddRange(newIds);

            itemConfig.Blacklist.UnionWith(newIds);

            if (config.Debug)
            {
                Leaves.Utils.Instance.debugJsonOutput(loot["mixed"].ItemBlacklist, "loot.mixed.itemBlacklist");
                Leaves.Utils.Instance.debugJsonOutput(loot["weaponArmor"].ItemBlacklist, "loot.weaponArmor.itemBlacklist");
                Leaves.Utils.Instance.debugJsonOutput(loot["foodMedical"].ItemBlacklist, "loot.foodMedical.itemBlacklist");
                Leaves.Utils.Instance.debugJsonOutput(loot["barter"].ItemBlacklist, "loot.barter.itemBlacklist");
            }
        }

        FixPresets(weaponIds);

        if (config.BlacklistFromFence)
        {
            var traderConfig = configServer.GetConfig<TraderConfig>();
            foreach (var newId in newIds)
            {
                traderConfig.Fence.Blacklist.Add(newId);
            }
        }

        return Task.CompletedTask;
    }

    // Quarantine zone for the soft Questrandomizer dependency - the ONLY place in this mod allowed
    // to mention a Leaves_Questrandomizer type. NoInlining keeps the JIT from folding this into
    // OnLoad, so on an install without Questrandomizer the assembly-load failure stays catchable
    // at the call site (see OnLoad). weaponIds maps new shiny id -> original id, and the compat
    // API wants (original, equivalent) - hence the swap.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void RegisterQuestrandomizerEquivalents(Dictionary<MongoId, MongoId> weaponIds)
    {
        var compatibility = serviceProvider.GetService<Leaves_Questrandomizer.QuestrandomizerCompatibility>();
        if (compatibility == null)
        {
            // Assembly present but the service isn't registered - shouldn't happen in practice
            // (installing the mod is what registers it), but don't die over it.
            Leaves.Utils.Instance.printColor("[ShinyAirdropGuns] Questrandomizer compatibility service not found - skipping weapon-equivalent registration.");
            return;
        }

        foreach (var (newId, oldId) in weaponIds)
        {
            compatibility.AddWeaponEquivalent(oldId, newId);
        }
        Leaves.Utils.Instance.printColor($"[ShinyAirdropGuns] Registered {weaponIds.Count} shiny weapons with Questrandomizer as quest-kill equivalents.");
    }

    private void AddWeaponGroup(string groupName, Dictionary<MongoId, MongoId> weaponIds, Leaves.IdStore ids, InventoryConfig inventoryConfig)
    {
        var handbook = databaseService.GetHandbook();
        var weaponGroup = config.WeaponGroups[groupName];

        // The stat buffs are the same for every gun in the group, so build the summary line once
        // here and reuse it for every clone below (see BuildStatSummary).
        string statSummary = BuildStatSummary(weaponGroup);

        foreach (var weapon in weaponGroup.WeaponsToMakeShiny)
        {
            // Skip weapons that aren't in the database instead of dying on them. By this stage every
            // mod has loaded, so a missing id means that weapon's mod simply isn't installed - which
            // is expected once weaponGroups start listing modded guns. Without this the raw indexer
            // threw KeyNotFoundException and took the whole mod down over one absent gun.
            if (!itemDB.TryGetValue(weapon, out var template))
            {
                Leaves.Utils.Instance.printColor(
                    $"[ShinyAirdropGuns] Weapon {weapon} in group '{groupName}' isn't in the database - its mod probably isn't installed. Skipped.",
                    LogTextColor.Yellow);
                continue;
            }

            // A shiny weapon MUST have a default preset, because FixPresets below can only clone a
            // preset that already exists - and a sealed weapon case looks the reward's default
            // preset up. Without one the server falls back to "a random preset of the same weapon
            // type", finds nothing, and throws "Sequence contains no elements" the moment a player
            // opens a case. Some mods ship weapons with no presets at all (WTT-Armory's
            // WeaponPresets.json is literally empty), so this is the difference between "that gun
            // isn't shiny" and "weapon cases are broken".
            if (!weaponsWithDefaultPreset.Contains(weapon))
            {
                Leaves.Utils.Instance.printColor(
                    $"[ShinyAirdropGuns] Weapon {weapon} ({template.Name}) in group '{groupName}' has no default preset, so it can't be a weapon-case reward - skipping it. "
                    + "That weapon's own mod needs to ship a preset before it can be made shiny.",
                    LogTextColor.Yellow);
                continue;
            }

            var props = template.Properties;

            // PLACEHOLDER names. These reads return "" for modded guns (WTT-Armory etc.) because
            // those mods register their item locales AFTER this clone pass runs at this load order,
            // which would leave the shiny name as just the prefix+suffix decoration with nothing
            // between it. The correct names are rebuilt later by ShinyLocaleFixup once every mod's
            // locale is present; what we set here is overwritten by that transformer at request time.
            string originalName = Leaves.Utils.Instance.getLocale("en", weapon.ToString(), " Name") ?? "";
            string originalShortName = Leaves.Utils.Instance.getLocale("en", weapon.ToString(), " ShortName") ?? "";

            string newLocaleName = weaponGroup.Prefix + originalName + weaponGroup.Suffix;
            string newShortName = weaponGroup.ShortNamePrefix + originalShortName + weaponGroup.ShortNameSuffix;

            Leaves.Utils.Instance.printColor($"[LeavesShinyGuns]\tModding: {weapon} {newLocaleName}", LogTextColor.Yellow);

            double priceMult = weaponGroup.PriceMulti ?? 5;
            double price = handbookHelper.GetTemplatePrice(weapon) * priceMult;

            var stats = ReadWeaponConfigData(weaponGroup, template);

            var handbookEntry = handbook.Items.FirstOrDefault(item => item.Id == weapon);
            string? handbookParentId = handbookEntry?.ParentId.ToString();

            string description = weaponGroup.Description ?? "Modded weapon by an expert gunsmith. Better in almost every way.";

            // Tack the group's buff list onto the flavor text so a player can see AT A GLANCE what
            // makes this shiny special - the raw multipliers/adjustments, not the resulting numbers.
            if (statSummary.Length > 0)
            {
                description = description + "\n\n" + statSummary;
            }

            // Ids are cached across runs (config/ids.jsonc) - renaming a weapon group changes its
            // lookup key and therefore generates a brand new id, same footgun the TS readme warns
            // about ("AT BEST will remove them from your inventory, and at WORST corrupt your save").
            string idLookupString = $"{weapon}_shiny_{groupName}";
            MongoId newId = ids.GetOrCreate(idLookupString);

            // Record the real name for the LATE locale pass. The Name/ShortName set on the clone
            // below is only a placeholder - correct for vanilla guns but blank for modded ones
            // whose locales aren't registered yet at this load order. ShinyLocaleFixup rebuilds
            // both from the fully-loaded original name once every mod has loaded. See that class.
            shinyLocaleFixup.Jobs.Add(new ShinyLocaleJob
            {
                ShinyId = newId,
                OriginalId = weapon,
                Prefix = weaponGroup.Prefix,
                Suffix = weaponGroup.Suffix,
                ShortNamePrefix = weaponGroup.ShortNamePrefix,
                ShortNameSuffix = weaponGroup.ShortNameSuffix,
            });

            var overrideProperties = new TemplateItemProperties();

            // Only overrides a stat if the original weapon actually has it - e.g. shotguns have no
            // bFirerate, bolt actions have no WeapFireType. Faithful to the TS original's per-field
            // truthy guard, but using "!= null" instead of JS truthiness (irrelevant here - none of
            // these stats are legitimately 0 on a real weapon that has them).
            if (props?.BFirerate != null) overrideProperties.BFirerate = stats.FireRate;
            if (props?.Ergonomics != null) overrideProperties.Ergonomics = stats.Ergonomics;
            if (props?.RecoilForceUp != null) overrideProperties.RecoilForceUp = stats.HorizontalRecoil;
            if (props?.RecoilForceBack != null) overrideProperties.RecoilForceBack = stats.VerticalRecoil;
            if (props?.WeapFireType != null) overrideProperties.WeapFireType = stats.FireModes;
            if (props?.HeatFactorGun != null) overrideProperties.HeatFactorGun = stats.HeatFactor;
            if (props?.HeatFactorByShot != null) overrideProperties.HeatFactorByShot = stats.HeatFactorByShot;
            if (props?.BackgroundColor != null) overrideProperties.BackgroundColor = stats.BackgroundColor;
            // TS also checked a lowercase "shotgunDispersion" alongside this - real item data only
            // has the PascalCase ShotgunDispersion, so that duplicate check was always false (dead
            // code); dropped here.
            if (props?.ShotgunDispersion != null) overrideProperties.ShotgunDispersion = stats.Dispersion;
            if (props?.DurabilityBurnRatio != null) overrideProperties.DurabilityBurnRatio = stats.DurabilityBurn;
            if (props?.CenterOfImpact != null) overrideProperties.CenterOfImpact = stats.Accuracy;
            if (props?.Velocity != null) overrideProperties.Velocity = stats.Velocity;
            if (props?.DoubleActionAccuracyPenalty != null) overrideProperties.DoubleActionAccuracyPenalty = stats.DoubleActionAccuracy;

            var newItemDetails = new NewItemFromCloneDetails
            {
                ItemTplToClone = weapon,
                OverrideProperties = overrideProperties,
                NewId = newId.ToString(),
                ParentId = template.Parent.ToString(),
                HandbookParentId = handbookParentId,
                FleaPriceRoubles = price,
                HandbookPriceRoubles = price,
                Locales = new Dictionary<string, LocaleDetails>
                {
                    ["en"] = new LocaleDetails
                    {
                        Name = newLocaleName,
                        ShortName = newShortName,
                        Description = description
                    }
                }
            };

            customItemService.CreateItemFromClone(newItemDetails);

            if (config.AllowSpecialSlots && weaponGroup.AddToSpecialSlots)
            {
                addWeaponToSpecialSlots(newId);
            }

            if (SnowflakeWeapons.Contains(weapon))
            {
                Leaves.Utils.Instance.printColor($"[LeavesShinyGuns]\tAdding special exception for silly holster slot items:{template.Name}", LogTextColor.Cyan);
                Leaves.Utils.Instance.getSlotFilter(itemDB[CharacterInventory], 2).Add(newId);

                if (config.AddRevolversIntoPistolCase && weapon != SawedOffId)
                {
                    Leaves.Utils.Instance.printColor($"[LeavesShinyGuns]\tAdding revolver to pistol case: {template.Name}", LogTextColor.Cyan);
                    Leaves.Utils.Instance.getGridFilter(itemDB[PistolCase], 0).Add(newId);
                }
            }

            if (template.Parent == ShotgunParentId && weapon != SawedOffId)
            {
                Leaves.Utils.Instance.printColor($"[LeavesShinyGuns]\tAdding special exception for silly shotguns:{template.Name}", LogTextColor.Cyan);
                Leaves.Utils.Instance.getSlotFilter(itemDB[CharacterInventory], 0).Add(newId);
                Leaves.Utils.Instance.getSlotFilter(itemDB[CharacterInventory], 1).Add(newId);
            }

            inventoryConfig.SealedAirdropContainer.WeaponRewardWeight[newId] = stats.Weight;

            weaponIds[newId] = weapon;
        }
    }

    // Add a shiny weapon to the special-slot filters of every pockets-category item, so it fits the
    // special slots no matter which pockets template the profile actually equips (vanilla, Unheard/TUE,
    // or an SVM-cloned custom pocket - see PocketsParentId). We match slots by NAME prefix rather than
    // positional index: SVM's clone keeps the "SpecialSlotN" names but renumbers the slot ids, and its
    // 4/5-special-slot configs add SpecialSlot4/SpecialSlot5, which a fixed index/name list would miss.
    // Safe on load order - this runs at PostDBModLoader+90000, after SVM (+5) has created its clone.
    private void addWeaponToSpecialSlots(MongoId newId)
    {
        foreach (var item in itemDB.Values)
        {
            if (item.Parent != PocketsParentId)
            {
                continue;
            }

            if (item.Properties?.Slots == null)
            {
                continue;
            }

            foreach (var slot in item.Properties.Slots)
            {
                if (slot.Name != null && slot.Name.StartsWith("SpecialSlot"))
                {
                    slot.Properties!.Filters!.First().Filter!.Add(newId);
                }
            }
        }
    }

    private sealed record AdjustedWeaponStats
    {
        public double? FireRate { get; init; }
        public double? Ergonomics { get; init; }
        public double? HorizontalRecoil { get; init; }
        public double? VerticalRecoil { get; init; }
        public HashSet<string>? FireModes { get; init; }
        public double? HeatFactor { get; init; }
        public double? HeatFactorByShot { get; init; }
        public double? DurabilityBurn { get; init; }
        public double? Accuracy { get; init; }
        public double? Velocity { get; init; }
        public double? DoubleActionAccuracy { get; init; }
        public required string BackgroundColor { get; init; }
        public double Weight { get; init; }
        public double Dispersion { get; init; }
    }

    // Build the buff list shown in a shiny gun's description. These are the group's CONFIGURED
    // adjustments (the multipliers and flat changes themselves), not the resulting per-weapon
    // numbers - those differ per base gun, whereas the buffs are what actually make the shiny
    // special. Only fields the group actually sets are listed; a group with no stat changes at all
    // returns "" and gets no appended block.
    //
    // Sign convention matches ReadWeaponConfigData: recoil flat fields are REDUCTIONS (subtracted),
    // so they show as "-N"; the other flat fields are increases and show as "+N". Multipliers show
    // as "xN". Numbers are formatted with the invariant culture so decimals never come out with a
    // comma separator.
    private static string BuildStatSummary(WeaponGroupConfig weaponGroup)
    {
        var lines = new List<string>();

        if (weaponGroup.FireRateFlatIncrease != null)
        {
            lines.Add("Fire rate: " + FormatFlat(weaponGroup.FireRateFlatIncrease.Value, false));
        }
        if (weaponGroup.FireRateMultiplier != null)
        {
            lines.Add("Fire rate: " + FormatMult(weaponGroup.FireRateMultiplier.Value));
        }

        if (weaponGroup.ErgoFlatIncrease != null)
        {
            lines.Add("Ergonomics: " + FormatFlat(weaponGroup.ErgoFlatIncrease.Value, false));
        }
        if (weaponGroup.ErgoMultiplier != null)
        {
            lines.Add("Ergonomics: " + FormatMult(weaponGroup.ErgoMultiplier.Value));
        }

        if (weaponGroup.HorizontalRecoilFlatReduction != null)
        {
            lines.Add("Horizontal recoil: " + FormatFlat(weaponGroup.HorizontalRecoilFlatReduction.Value, true));
        }
        if (weaponGroup.HorizontalRecoilMultiplier != null)
        {
            lines.Add("Horizontal recoil: " + FormatMult(weaponGroup.HorizontalRecoilMultiplier.Value));
        }

        if (weaponGroup.VerticalRecoilFlatReduction != null)
        {
            lines.Add("Vertical recoil: " + FormatFlat(weaponGroup.VerticalRecoilFlatReduction.Value, true));
        }
        if (weaponGroup.VerticalRecoilMultiplier != null)
        {
            lines.Add("Vertical recoil: " + FormatMult(weaponGroup.VerticalRecoilMultiplier.Value));
        }

        if (weaponGroup.HeatFactorMultiplier != null)
        {
            lines.Add("Heat factor: " + FormatMult(weaponGroup.HeatFactorMultiplier.Value));
        }
        if (weaponGroup.HeatFactorByShotMultiplier != null)
        {
            lines.Add("Heat per shot: " + FormatMult(weaponGroup.HeatFactorByShotMultiplier.Value));
        }
        if (weaponGroup.DurabilityBurnMultiplier != null)
        {
            lines.Add("Durability burn: " + FormatMult(weaponGroup.DurabilityBurnMultiplier.Value));
        }
        if (weaponGroup.AccuracyMultiplier != null)
        {
            lines.Add("Accuracy spread: " + FormatMult(weaponGroup.AccuracyMultiplier.Value));
        }
        if (weaponGroup.VelocityMultiplier != null)
        {
            lines.Add("Muzzle velocity: " + FormatMult(weaponGroup.VelocityMultiplier.Value));
        }
        if (weaponGroup.DoubleActionAccuracyPenaltyMultiplier != null)
        {
            lines.Add("Double-action penalty: " + FormatMult(weaponGroup.DoubleActionAccuracyPenaltyMultiplier.Value));
        }

        if (weaponGroup.SetFireMode != null && weaponGroup.SetFireMode.Count > 0)
        {
            lines.Add("Fire modes: " + string.Join(", ", weaponGroup.SetFireMode));
        }

        if (lines.Count == 0)
        {
            return "";
        }

        var summary = new System.Text.StringBuilder();
        summary.Append("Shiny buffs:");
        foreach (var line in lines)
        {
            summary.Append('\n');
            summary.Append(line);
        }
        return summary.ToString();
    }

    private static string FormatMult(double value)
    {
        return "x" + value.ToString(CultureInfo.InvariantCulture);
    }

    private static string FormatFlat(double value, bool isReduction)
    {
        string sign = isReduction ? "-" : "+";
        return sign + value.ToString(CultureInfo.InvariantCulture);
    }

    // Any multiplicative adjustment is applied AFTER any flat adjustment - matches the TS config
    // doc comment's own ordering note.
    private static AdjustedWeaponStats ReadWeaponConfigData(WeaponGroupConfig weaponGroup, TemplateItem template)
    {
        var props = template.Properties;

        double? fireRate = null;
        if (props?.BFirerate != null)
        {
            fireRate = (props.BFirerate.Value + (weaponGroup.FireRateFlatIncrease ?? 0)) * (weaponGroup.FireRateMultiplier ?? 1);
        }

        double? ergonomics = null;
        if (props?.Ergonomics != null)
        {
            ergonomics = (props.Ergonomics.Value + (weaponGroup.ErgoFlatIncrease ?? 0)) * (weaponGroup.ErgoMultiplier ?? 1);
        }

        double? horizontalRecoil = null;
        if (props?.RecoilForceUp != null)
        {
            horizontalRecoil = (props.RecoilForceUp.Value - (weaponGroup.HorizontalRecoilFlatReduction ?? 0)) * (weaponGroup.HorizontalRecoilMultiplier ?? 1);
        }

        double? verticalRecoil = null;
        if (props?.RecoilForceBack != null)
        {
            verticalRecoil = (props.RecoilForceBack.Value - (weaponGroup.VerticalRecoilFlatReduction ?? 0)) * (weaponGroup.VerticalRecoilMultiplier ?? 1);
        }

        HashSet<string>? fireModes = null;
        if (props?.WeapFireType != null)
        {
            fireModes = weaponGroup.SetFireMode != null ? new HashSet<string>(weaponGroup.SetFireMode) : new HashSet<string>(props.WeapFireType);
        }

        double? heatFactor = props?.HeatFactorGun != null ? props.HeatFactorGun.Value * (weaponGroup.HeatFactorMultiplier ?? 1) : null;
        double? heatFactorByShot = props?.HeatFactorByShot != null ? props.HeatFactorByShot.Value * (weaponGroup.HeatFactorByShotMultiplier ?? 1) : null;
        double? durabilityBurn = props?.DurabilityBurnRatio != null ? props.DurabilityBurnRatio.Value * (weaponGroup.DurabilityBurnMultiplier ?? 1) : null;
        double? accuracy = props?.CenterOfImpact != null ? props.CenterOfImpact.Value * (weaponGroup.AccuracyMultiplier ?? 1) : null;
        double? velocity = props?.Velocity != null ? props.Velocity.Value * (weaponGroup.VelocityMultiplier ?? 1) : null;
        double? doubleActionAccuracy = props?.DoubleActionAccuracyPenalty != null ? props.DoubleActionAccuracyPenalty.Value * (weaponGroup.DoubleActionAccuracyPenaltyMultiplier ?? 1) : null;

        return new AdjustedWeaponStats
        {
            FireRate = fireRate,
            Ergonomics = ergonomics,
            HorizontalRecoil = horizontalRecoil,
            VerticalRecoil = verticalRecoil,
            FireModes = fireModes,
            HeatFactor = heatFactor,
            HeatFactorByShot = heatFactorByShot,
            DurabilityBurn = durabilityBurn,
            Accuracy = accuracy,
            Velocity = velocity,
            DoubleActionAccuracy = doubleActionAccuracy,
            BackgroundColor = weaponGroup.Background,
            Weight = weaponGroup.WeaponBoxWeight ?? 1,
            Dispersion = template.Parent == ShotgunParentId ? 1.25 : 0,
        };
    }

    private void FixQuests(Dictionary<MongoId, MongoId> weaponIds)
    {
        var quests = databaseService.GetQuests();

        foreach (var quest in quests.Values)
        {
            var availableForFinish = quest.Conditions?.AvailableForFinish;
            if (availableForFinish == null)
            {
                continue;
            }

            foreach (var data in availableForFinish)
            {
                var conditions = data.Counter?.Conditions;
                if (conditions == null)
                {
                    continue;
                }

                foreach (var condition in conditions)
                {
                    // TS original used `break` here (not `continue`) - the moment a condition
                    // without a weapon list is hit, every remaining condition in this counter is
                    // skipped rather than just this one. Looks like it could be a continue/break
                    // mix-up, but preserved faithfully since it's plausible some counters rely on
                    // weapon-conditions always being listed first and this was intentional -
                    // flagged rather than silently changed.
                    if (condition.Weapon == null)
                    {
                        break;
                    }

                    // Snapshot before iterating: we add newly-generated ids into condition.Weapon
                    // below, and HashSet<T> throws on mutation during enumeration (unlike the TS
                    // original's live JS array, which tolerated push-during-for-of by just visiting
                    // the pushed entries harmlessly afterward).
                    foreach (var weapon in condition.Weapon.ToList())
                    {
                        var matches = GetNewIdsForOriginalWeapon(weaponIds, weapon);
                        if (matches.Count == 0)
                        {
                            continue;
                        }

                        foreach (var newId in matches)
                        {
                            condition.Weapon.Add(newId.ToString());
                        }

                        if (config.Debug)
                        {
                            Leaves.Utils.Instance.debugJsonOutput(condition.Weapon, $"New Weapon Array for Quest: {quest.QuestName}");
                        }
                    }
                }
            }
        }
    }

    private void FixMastery(Dictionary<MongoId, MongoId> weaponIds)
    {
        var masteries = databaseService.GetGlobals().Configuration.Mastering;

        foreach (var mastery in masteries)
        {
            var templates = mastery.Templates?.ToList();
            if (templates == null)
            {
                continue;
            }

            // Collected separately and applied once after the loop, rather than mutating
            // mastery.Templates mid-iteration (it's IEnumerable, not directly appendable anyway -
            // unlike the TS original's live array push-during-iteration, which just re-visited the
            // newly pushed ids harmlessly since they never match as an "original weapon").
            var additions = new List<MongoId>();
            foreach (var weapon in templates)
            {
                var matches = GetNewIdsForOriginalWeapon(weaponIds, weapon);
                if (matches.Count == 0)
                {
                    continue;
                }

                additions.AddRange(matches);
                if (config.Debug)
                {
                    Leaves.Utils.Instance.printColor($"Found mastery to push:{mastery.Name}", LogTextColor.Yellow);
                }
            }

            if (additions.Count > 0)
            {
                mastery.Templates = templates.Concat(additions);
            }
        }
    }

    private void FixPresets(Dictionary<MongoId, MongoId> weaponIds)
    {
        var presets = databaseService.GetGlobals().ItemPresets;
        var newPresets = new List<Preset>();

        foreach (var preset in presets.Values)
        {
            // Only presets carrying an _encyclopedia entry are the "default" preset for their
            // weapon - matches the TS original's hasOwnProperty("_encyclopedia") check.
            if (preset.Encyclopedia == null)
            {
                continue;
            }

            var originalWeapon = preset.Items[0].Template;
            foreach (var newWeapon in GetNewIdsForOriginalWeapon(weaponIds, originalWeapon))
            {
                var newPreset = cloner.Clone(preset)!;

                newPreset.Id = new MongoId();
                Leaves.Utils.Instance.generateNewItemIds(newPreset.Items);
                newPreset.Parent = newPreset.Items[0].Id;

                // Fix the link to the receiver, then mark this preset as the new weapon's default.
                newPreset.Items[0].Template = newWeapon;
                newPreset.Encyclopedia = newWeapon;
                newPreset.ChangeWeaponName = false;

                newPresets.Add(newPreset);

                if (config.Debug)
                {
                    Leaves.Utils.Instance.printColor($"Made new preset for: {newWeapon}");
                }
            }
        }

        foreach (var preset in newPresets)
        {
            presets[preset.Id] = preset;
        }
    }

    private static List<MongoId> GetNewIdsForOriginalWeapon(Dictionary<MongoId, MongoId> weaponIds, MongoId weapon)
    {
        var found = new List<MongoId>();
        foreach (var (newId, oldId) in weaponIds)
        {
            if (oldId == weapon)
            {
                found.Add(newId);
            }
        }
        return found;
    }
}

// One shiny gun's name-building recipe, recorded by ShinyAirdropGuns during its clone pass and
// consumed by ShinyLocaleFixup below. We store the ORIGINAL weapon id (not its name) so the name
// can be read late, after every mod's locale is in place.
public class ShinyLocaleJob
{
    public MongoId ShinyId;
    public MongoId OriginalId;
    public string Prefix = "";
    public string Suffix = "";
    public string ShortNamePrefix = "";
    public string ShortNameSuffix = "";
}

// Applies the shiny NAME and SHORT-NAME, and nothing else. This is split out from the main clone
// pass on purpose:
//
//   The clone itself must be created early (PostDBModLoader+90000) so its preset lands before SPT's
//   PresetCallbacks hydrates the preset cache at 900000 - see the big comment on ShinyAirdropGuns.
//   But at that load order a MODDED gun's own locale (WTT-Armory etc.) hasn't been registered yet,
//   so reading the original name there returns "" and the shiny name collapses to just the
//   prefix+suffix hearts with nothing between. Vanilla guns are fine (their names are in the base
//   locale from the start); modded guns are not.
//
//   The name is cosmetic and touches nothing the preset cache cares about, so we defer JUST that
//   part to here. Running at PostSptModLoader+1 means every content mod's OnLoad - and therefore
//   its locale transformer - has already run. Because locale transformers are applied in
//   REGISTRATION ORDER each time the locale is materialized, ours (registered last) reads an
//   original name that every earlier transformer has already filled in. So the name is computed
//   lazily at request time from fully-loaded data, instead of baked from a half-loaded snapshot.
//
// Singleton so the Jobs list ShinyAirdropGuns fills during its (earlier) OnLoad is the same
// instance this OnLoad reads.
[Injectable(InjectionType.Singleton, TypePriority = OnLoadOrder.PostSptModLoader + 1)]
public class ShinyLocaleFixup(
    DatabaseService databaseService)
    : IOnLoad
{
    // Filled by ShinyAirdropGuns.AddWeaponGroup before this OnLoad runs (earlier load order).
    public readonly List<ShinyLocaleJob> Jobs = new();

    public Task OnLoad()
    {
        if (Jobs.Count == 0)
        {
            return Task.CompletedTask;
        }

        // Register one transformer that rewrites every shiny name. It runs each time the "en"
        // locale is materialized, reading the (by-then fully populated) original names.
        Dictionary<string, string>? Transform(Dictionary<string, string>? locale)
        {
            if (locale == null)
            {
                return locale;
            }

            foreach (var job in Jobs)
            {
                string originalName;
                if (!locale.TryGetValue($"{job.OriginalId} Name", out originalName!))
                {
                    originalName = "";
                }

                string originalShortName;
                if (!locale.TryGetValue($"{job.OriginalId} ShortName", out originalShortName!))
                {
                    originalShortName = "";
                }

                locale[$"{job.ShinyId} Name"] = job.Prefix + originalName + job.Suffix;
                locale[$"{job.ShinyId} ShortName"] = job.ShortNamePrefix + originalShortName + job.ShortNameSuffix;
            }

            return locale;
        }

        databaseService.GetLocales().Global["en"].AddTransformer(Transform);
        Leaves.Utils.Instance.printColor($"[ShinyAirdropGuns] Registered late locale pass for {Jobs.Count} shiny weapons.", LogTextColor.Cyan);

        return Task.CompletedTask;
    }
}
