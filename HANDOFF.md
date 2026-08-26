# Leaves-ShinyAirdropGuns — Handoff

Last updated: 2026-07-15. Read `4.0 Mods\HANDOFF.md` first for project-wide rules.

---

## 1. What the mod does

Clones selected weapons into "shiny" special editions with buffed stats and a colored
background, then wires those clones into the sealed-airdrop weapon-crate reward pool. Each
clone is a genuine new item (`CustomItemService.CreateItemFromClone`), not a runtime stat
tweak — so it needs its own id, presets, quest/mastery references, and inventory-slot filters,
which is most of what this mod does. Player-facing behavior is in `readme.md`.

Config groups weapons into named **weapon groups**, each with its own stat adjustments,
name prefix/suffix, background color, and airdrop weight — so you can have a "Gold" tier and
a "Cursed" tier with different buffs from one file.

## 2. Lifecycle

`IOnLoad` only — no periodic behavior, so unlike BarterEconomy/LimitedTraders there's **no**
double-construction Singleton concern (that pitfall only bites classes implementing two lifecycle
interfaces; see project handoff §4.1). Everything happens once in `OnLoad`.

### ⚠️ 2.1 The TypePriority window — read before touching it

`[Injectable(TypePriority = OnLoadOrder.PostDBModLoader + 90000)]` (= **490000**) is wedged between
two hard deadlines. Both were learned by breaking them:

| Stage | Value | Constraint |
|---|---|---|
| Content mods add weapons (WTT etc.) | 400002 | must be **after** — else a modded gun in `weaponGroups` doesn't exist yet and can't be cloned |
| **this mod** | **490000** | |
| SPT `PresetCallbacks` **caches every preset** | 900000 | must be **before** — see below |
| Questrandomizer `LateOnLoad` reads equivalents | 1100002 | must be before |

**The PresetCallbacks trap (2026-07-16):** moving this mod to `PostSptModLoader+1` to see modded
guns broke **every sealed weapon case**. `FixPresets` still wrote the shiny presets into globals,
but `PresetCallbacks` had already hydrated `PresetHelper`'s cache at 900000 — so a preset added
afterwards is invisible to it. `LootGenerator.GetSealedWeaponCaseLoot` then finds no preset for the
shiny reward, falls back to "a random preset of the same weapon type", gets an empty list, and
throws `Sequence contains no elements` the moment a player opens a case. It only ever worked at
PostDBModLoader by luck of running before that cache.

490000 satisfies everything: the PostDBModLoader stage runs 400000–499999 (next stage starts at
+100000), so +90000 is after every mod in it — the same trick LEWDS uses to go last — and still
comfortably below 900000.

**Also guarded:** `AddWeaponGroup` skips any weapon that has no default (`_encyclopedia`) preset,
since `FixPresets` can only clone a preset that exists and a preset-less shiny reward crashes cases
the same way. Defense-in-depth — currently rejects nothing, as every configured gun has one.

`OnLoad` order (each step gated by its config toggle):
1. Load config + `config/ids.jsonc` (runtime-owned; see §5). Grab `ItemConfig`,
   `InventoryConfig`, `AirdropConfig` off the (obsolete-but-only-option) `ConfigServer`.
2. If `replaceAllAirdrops`: clear the vanilla `WeaponRewardWeight` so only shinies drop.
3. For each weapon group → `AddWeaponGroup` (the core clone-and-register pass, §3).
4. Save `ids.jsonc` back (new ids were minted in step 3).
5. `RegisterQuestrandomizerEquivalents` — the soft dependency (§4).
6. `FixQuests`, `FixMastery`, `FixPresets` — propagate the clones into weapon-referencing game
   data (§3).
7. Blacklist the clones from normal airdrop loot / Fence so they only appear as crate rewards.

## 3. The clone pass (`AddWeaponGroup` + fixups)

Per weapon in a group:
- Records a **locale job** (shiny id, original id, group prefix/suffix) for the late name pass —
  see §3.1. The `Name`/`ShortName` set on the clone here are placeholders only.
- Price = original handbook price × `priceMulti` (default 5).
- `ReadWeaponConfigData` computes adjusted stats: **flat adjustment first, then multiplier**
  (matches the TS config doc). Every stat is guarded on the original actually having it —
  shotguns have no fire rate, bolt actions no fire-type, etc.
- `CreateItemFromClone` with an `OverrideProperties` that likewise only sets stats the original
  had.
- **Inventory-slot filter patches** (the fiddly part — some items don't follow the normal
  "parent category decides fit" rule):
  - `allowSpecialSlots` + group `addToSpecialSlots` → pockets special slots. **Do NOT target one
    fixed pockets id.** The equipped pockets item varies — vanilla `627a4e6b255f7527fb05a0f6`,
    Unheard/TUE `65e080be269cbd5c5005e529`, and SVM's *Custom Pocket* feature clones the pockets
    into a fresh id `a8edfb0bce53d103d3f62b9b` and re-equips THAT on the profile (see
    `PrestigeOverrider`/`ProfileControllerOverrider` in SVM). Patching only `627…` silently no-ops
    on those profiles ("filter added, doesn't work in-game"). `addWeaponToSpecialSlots` instead
    walks **every item in the pockets category** (`_parent == 557596e64bdc2dc2118b4571`) and adds
    the clone to any slot whose **name** starts with `SpecialSlot` — by name, not index, because
    SVM renumbers the slot ids and its 4/5-slot configs add `SpecialSlot4`/`SpecialSlot5`. Load
    order is safe: we run at `PostDBModLoader+90000`, after SVM (`+5`) has created its clone.
  - `SnowflakeWeapons` set (Rhino ×2, RSh-12, sawed-off) → character holster slot; revolvers
    also into the pistol case if `addRevolversIntoPistolCase`.
  - Shotguns (except sawed-off) → the two character weapon slots.
  These mirror the TS original's manual exception lists.
- Registers the clone in `WeaponRewardWeight` and records `newId → originalId`.

The three fixups walk game data replacing/augmenting references to the original:
- **`FixQuests`** — adds each clone id into any quest kill-condition `Weapon` list containing
  the original, so shiny kills count. (This is a *static* patch to the shared quest DB at load,
  independent of Questrandomizer — see §4 for the Questrandomizer path, which is different.)
- **`FixMastery`** — adds clones to matching weapon-mastery templates so shiny use levels up
  the right mastery.
- **`FixPresets`** — clones each weapon's default (encyclopedia) preset, re-ids the items, and
  points it at the clone, so the shiny has a working default build.

### 3.1 The late locale pass (`ShinyLocaleFixup`) — why the name is applied separately

The shiny **name and short-name are NOT baked at clone time** — that was the bug behind the
"empty hearts" name (`˙·٠•●♥  ♥●•٠·˙` with nothing between) on every WTT-Armory gun. At the clone
pass's load order (490000) a modded gun's own locale isn't registered yet, so
`getLocale(original, " Name")` returns `""` and the decoration wraps nothing. Vanilla guns were
fine (base-locale names exist from the start); modded ones were not.

`ShinyLocaleFixup` is a separate `IOnLoad` **Singleton** at `PostSptModLoader + 1`. It can't be
folded back into the clone pass — the clone must stay before `PresetCallbacks` (900000, see §2.1),
but the name can't be read until after every content mod's locale transformer has run. Split them:
clone early, name late.

- The main pass fills `ShinyLocaleFixup.Jobs` (shiny id, original id, group prefix/suffix) — shared
  because the class is a Singleton injected into `ShinyAirdropGuns`.
- The fixup registers ONE transformer on `GetLocales().Global["en"]` (same channel as TreasureBox's
  vodka fix). It computes `prefix + locale["{original} Name"] + suffix` **lazily at materialization**.
- The whole thing works because locale transformers run in **registration order** each time the
  locale is materialized. Registered last (latest load order), ours reads an original name that
  every earlier mod's transformer has already filled in. Load-order-timing solved by lazy read, not
  by guessing exactly when WTT materializes.

## 4. Questrandomizer integration (the soft dependency — read this carefully)

Two *separate* mechanisms make shiny guns count for quests, don't confuse them:

- **`FixQuests`** (§3) patches the vanilla quest DB directly. Works with or without
  Questrandomizer.
- **`RegisterQuestrandomizerEquivalents`** registers each `(original, shiny)` pair with
  Questrandomizer's compat API, so shinies also count in *randomized* quests and get the
  "Equivalent to:" description line. This is the soft-dependency path.

How the softness works (this is the reference implementation for the whole workspace):
- `QuestrandomizerCompatibility` is resolved via `serviceProvider.GetService<>()` (nullable),
  **not** constructor-injected — a constructor param would make this mod fail to load whenever
  Questrandomizer is absent.
- The one method that names a `Leaves_Questrandomizer` type is quarantined behind
  `[MethodImpl(MethodImplOptions.NoInlining)]` and called inside a try/catch. On an install
  without Questrandomizer, .NET throws `FileNotFoundException` when it prepares that method
  (its IL references the missing assembly); NoInlining keeps that failure at the call site
  where the catch handles it. Inlined into `OnLoad`, the failure would escape and kill the mod.
- Deliberately **not** declared in `ModDependencies` — that would make it a hard dependency.

Registration happens in this mod's OnLoad, which runs before any client request, so the
equivalents are in place before Questrandomizer generates any profile's quests (which is
request-triggered). See Questrandomizer handoff §9 for the receiving side.

## 5. `ids.jsonc` (runtime-owned — the shared footgun)

Minted shiny ids are cached in `config/ids.jsonc` and reused across launches so saves stay
valid. Two consequences:
- The build **excludes** `config/ids.jsonc` from the copy glob (`<None Include="config\**"
  Exclude="config\ids.jsonc">` in the csproj) so a rebuild never clobbers the live file with a
  stale/empty shipped one — the identical fix to Questrandomizer's `assets/generated/ids.jsonc`.
  A fresh install has no file; OnLoad handles that (empty dict).
- The id lookup key is `"{weapon}_shiny_{groupName}"`. **Renaming a weapon group changes the
  key → mints new ids → orphans the old shinies in players' saves.** The TS readme warns this
  "AT BEST will remove them from your inventory, and at WORST corrupt your save." Treat group
  names as stable identifiers, not cosmetic labels.

## 6. Faithful-port quirks preserved (flagged, not silently changed)

- **`FixQuests` uses `break`, not `continue`**, when a condition has no weapon list — skips all
  remaining conditions in that counter. Possibly a TS continue/break mix-up, but preserved in
  case some counter relies on weapon-conditions being listed first. Flagged here if quest
  weapon propagation ever looks incomplete.
- **Debug weapon-id dump was dead code in TS** (`for (const ID in weaponIDs && this.config.debug)`
  iterated a boolean → never ran). Fixed to actually log under `debug`. A genuine bug fix, not
  a faithful port.
- Dropped a TS duplicate lowercase `shotgunDispersion` check (real data only has PascalCase) —
  was always-false dead code.
- HashSet mutation-during-enumeration: quest/mastery/preset fixups snapshot with `.ToList()`
  before adding, since C# `HashSet`/`IEnumerable` throw where the TS live arrays tolerated
  push-during-iteration.

## 7. Files

| File | Role |
|---|---|
| `src/ShinyAirdropGuns.cs` | Everything (§2–§6), incl. the `ShinyLocaleFixup` late-locale companion + `ShinyLocaleJob` (§3.1). |
| `src/Config.cs` | Config record: 8 top-level toggles + per-group stat/naming knobs (all group knobs nullable = "leave stat unchanged"). |
| `config/config.jsonc` | Toggles + the weapon-group definitions. |
| `config/ids.jsonc` | Runtime-owned id cache (§5). Not shipped; do not hand-edit. |
| `src/ModMetadata.cs` | SPT metadata. |

## 8. SPT 4.1 note

Uses the obsolete `ConfigServer` (CS0618, suppressed) to reach
`ItemConfig`/`InventoryConfig`/`AirdropConfig`/`TraderConfig` — the only way in 4.0.13. SPT 4.1
removes ConfigServer ("all server configs are now injected"), so these accesses need
reworking to injected configs then. Same blocker as BarterEconomy's debug resupply.
