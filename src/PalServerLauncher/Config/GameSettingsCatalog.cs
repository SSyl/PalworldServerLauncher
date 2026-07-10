using System.Collections.Generic;

namespace PalServerLauncher.Config;

/// <summary>UI grouping for a PalWorldSettings.ini key (mirrors the official config doc's sections).</summary>
public enum SettingCategory
{
    ServerAdmin,
    Performance,
    Gameplay,
    GameBalance,
}

public enum SettingType
{
    Bool,
    Int,
    Float,
    Text,
    Enum,
    IpAddress,
    /// <summary>A value written back verbatim (no quoting) - for tuple/list values like
    /// <c>CrossplayPlatforms=(Steam,Xbox,PS5,Mac)</c> that the other types would mis-quote. The save path
    /// round-trip-verifies a Raw edit and refuses if it would corrupt the blob.</summary>
    Raw,
}

/// <summary>How well a key is covered by official sources, which drives its UI placement and marker.</summary>
public enum DocStatus
{
    /// <summary>In an official source (config doc, PvP doc, or the in-game "Edit World Settings" screen). No marker.</summary>
    Documented,
    /// <summary>Not in official docs but well enough understood to describe. Stays in its category tab with an "undocumented" marker.</summary>
    Undocumented,
    /// <summary>Not in official docs and we cannot confidently say what it does. Routed to the Undocumented Settings tab.</summary>
    Unknown,
}

/// <summary>
/// Metadata for one PalWorldSettings.ini <c>OptionSettings</c> key: how to label it, which category
/// it belongs to, and how to type its value (which drives quoting on write via <see cref="OptionSettingsBlob"/>).
/// </summary>
/// <param name="AppDefault">The launcher's preferred value, overriding the game default for the reset
/// affordances only: the per-field reset targets this (so the ↺ shows only when the value differs from what
/// the app wants), and the key is left out of bulk "Reset to defaults". Null = use the game default. Set for
/// RESTAPIEnabled, which the launcher relies on being on.</param>
public sealed record GameSetting(
    string Key,
    string Label,
    SettingCategory Category,
    SettingType Type,
    string Description = "",
    IReadOnlyList<string>? Options = null,
    double? Min = null,
    double? Max = null,
    bool Secret = false,
    string? AppDefault = null,
    DocStatus Doc = DocStatus.Documented);

/// <summary>
/// Data-driven catalog of PalWorldSettings.ini <c>OptionSettings</c> keys as typed fields. Covers EVERY
/// key a current default config contains (verified against a real 1.0 DefaultPalWorldSettings.ini), so the
/// "Undocumented" tab's "new in your config" section is empty on a normal install and only lists a key
/// once a future game update adds one we haven't catalogued yet.
///
/// Labels use the game's own in-game "Edit World Settings" wording for keys that appear there, so they
/// match what players see. Keys the game screen doesn't expose (server management, and options only a
/// dedicated server has) keep a plain-language launcher label, and keys whose meaning we can't state
/// confidently show the raw variable name instead, so a label never asserts a meaning we are guessing at.
/// The tooltip always leads with the real ini key, rendered as "Key&lt;newline&gt;Description".
///
/// Types match the exact format the game writes (rates as 6-decimal floats, enums bare, strings quoted,
/// tuples/lists <see cref="SettingType.Raw"/>). Descriptions come from the official configuration
/// reference (https://docs.palworldgame.com/settings-and-operation/configuration). Keys the official doc
/// omits carry a short launcher-authored description instead, prefixed "Undocumented" where the meaning
/// is inferred.
/// </summary>
public static class GameSettingsCatalog
{
    public static readonly IReadOnlyList<GameSetting> All = new List<GameSetting>
    {
        // ===================== Server management (Admin tab) =====================
        new("ServerName", "Server name", SettingCategory.ServerAdmin, SettingType.Text,
            "Server name."),
        new("ServerDescription", "Description", SettingCategory.ServerAdmin, SettingType.Text,
            "Server description."),
        new("ServerPassword", "Server password", SettingCategory.ServerAdmin, SettingType.Text,
            "Password required to log in to the server. Blank = no password.", Secret: true),
        new("AdminPassword", "Admin password", SettingCategory.ServerAdmin, SettingType.Text,
            "Password used to obtain administrative privileges on the server. Required for the REST API and RCON.", Secret: true),
        new("ServerPlayerMaxNum", "Max players", SettingCategory.ServerAdmin, SettingType.Int,
            "Maximum number of players who can join the server. The -players launch argument overrides this if set.", Min: 1),
        new("CoopPlayerMaxNum", "CoopPlayerMaxNum", SettingCategory.ServerAdmin, SettingType.Int,
            "Undocumented. Community guesses a cooperative-play party size, but this may be an internal value that has no effect on a dedicated server.", Min: 1, Doc: DocStatus.Unknown),
        new("RESTAPIEnabled", "REST API enabled", SettingCategory.ServerAdmin, SettingType.Bool,
            "Enable the REST API. The launcher needs this for stats, graceful shutdown, and player logging.", AppDefault: "True"),
        new("RESTAPIPort", "REST API port", SettingCategory.ServerAdmin, SettingType.Int,
            "Listening port for the REST API.", Min: 1, Max: 65535),
        new("RCONEnabled", "RCON enabled", SettingCategory.ServerAdmin, SettingType.Bool,
            "Enable RCON."),
        new("RCONPort", "RCON port", SettingCategory.ServerAdmin, SettingType.Int,
            "Port number used for RCON.", Min: 1, Max: 65535),
        new("bUseAuth", "bUseAuth", SettingCategory.ServerAdmin, SettingType.Bool,
            "Undocumented. Defaults to True.", Doc: DocStatus.Unknown),
        new("BanListURL", "BanListURL", SettingCategory.ServerAdmin, SettingType.Text,
            "Undocumented. Likely the ban list the server checks against, but the exact format and behavior are unconfirmed.", Doc: DocStatus.Unknown),
        new("bIsUseBackupSaveData", "Built-in save backups", SettingCategory.ServerAdmin, SettingType.Bool,
            "Enable Palworld's own world backups (separate from the launcher's). Enabling this increases disk load."),
        new("AutoSaveSpan", "Auto save interval", SettingCategory.ServerAdmin, SettingType.Float,
            "How often the world auto-saves (seconds).", Min: 0),
        new("bIsShowJoinLeftMessage", "Show join/leave messages", SettingCategory.ServerAdmin, SettingType.Bool,
            "On dedicated servers, show in-game messages when players join or leave."),
        new("bAllowClientMod", "Allow client mods", SettingCategory.ServerAdmin, SettingType.Bool,
            "Allow players with mods enabled to join the server."),
        new("ChatPostLimitPerMinute", "Chat rate limit", SettingCategory.ServerAdmin, SettingType.Int,
            "Maximum number of chat messages allowed per minute.", Min: 0),
        new("LogFormatType", "Log format", SettingCategory.ServerAdmin, SettingType.Enum,
            "Server log file format.", new[] { "Text", "Json" }),
        new("bIsMultiplay", "bIsMultiplay", SettingCategory.ServerAdmin, SettingType.Bool,
            "Undocumented. Likely the game client's 'Multiplayer: Yes/No' option shown when creating a world, so it has no effect on a dedicated server. False by default.", Doc: DocStatus.Unknown),
        new("Region", "Region", SettingCategory.ServerAdmin, SettingType.Text,
            "Undocumented. A region code of some kind, but the expected format (NA, US, a number?) and its purpose (matchmaking, server browser, or neither) are both unknown. Blank by default.", Doc: DocStatus.Unknown),
        new("CrossplayPlatforms", "Crossplay platforms", SettingCategory.ServerAdmin, SettingType.Raw,
            "Allowed platforms to connect to the server, as a tuple. Default: (Steam,Xbox,PS5,Mac)."),
        new("PublicIP", "Public IP", SettingCategory.ServerAdmin, SettingType.IpAddress,
            "Community server: explicitly specify the external public IP. Blank = auto-detect."),
        new("PublicPort", "Public port", SettingCategory.ServerAdmin, SettingType.Int,
            "Community server: explicitly specify the external public port. Does not change the server's listening port.", Min: 1, Max: 65535),
        new("bEnableBuildingPlayerUIdDisplay", "Show builder ID on structures", SettingCategory.ServerAdmin, SettingType.Bool,
            "Whether to display the creator's player ID on structures."),
        new("BuildingNameDisplayCacheTTLSeconds", "BuildingNameDisplayCacheTTLSeconds", SettingCategory.ServerAdmin, SettingType.Int,
            "Undocumented. Likely how long a building's cached display name (see 'Show builder ID on structures') is kept before refreshing (seconds).", Min: 0, Doc: DocStatus.Unknown),

        // ===================== Performance =====================
        new("BaseCampMaxNum", "Max bases (world)", SettingCategory.Performance, SettingType.Int,
            "Maximum total number of base camps on the map (all guild bases combined).", Min: 0),
        new("BaseCampMaxNumInGuild", "Maximum Number of Bases for Each Guild", SettingCategory.Performance, SettingType.Int,
            "Maximum number of bases per guild. Default 4 (max 10). Increasing this raises processing load.", Min: 0, Max: 10),
        new("BaseCampWorkerMaxNum", "Maximum Number of Work Pals at the Base", SettingCategory.Performance, SettingType.Int,
            "Maximum number of Pals per base (max 50). Increasing this raises processing load.", Min: 0, Max: 50),
        new("MaxBuildingLimitNum", "Maximum Number of Structures per Base", SettingCategory.Performance, SettingType.Int,
            "Per-player building count cap (0 = No Limit).", Min: 0),
        new("DropItemMaxNum", "Maximum Number of Dropped Items in a World", SettingCategory.Performance, SettingType.Int,
            "Maximum number of items lying dropped in the world before they start to despawn.", Min: 0),
        new("PhysicsActiveDropItemMaxNum", "Maximum Number of Dropped Active Items in a World", SettingCategory.Performance, SettingType.Int,
            "Maximum number of dropped items that can use physics behavior. -1 = No Limit. Increasing this can affect processing load.", Min: -1),
        new("DropItemMaxNum_UNKO", "DropItemMaxNum_UNKO", SettingCategory.Performance, SettingType.Int,
            "Undocumented. Appears unused, likely deadweight or an unimplemented feature.", Min: 0, Doc: DocStatus.Unknown),
        new("ServerReplicatePawnCullDistance", "Pal sync distance", SettingCategory.Performance, SettingType.Float,
            "Pal sync distance from players (cm). Minimum 5000, maximum 15000.", Min: 5000, Max: 15000),
        new("ItemContainerForceMarkDirtyInterval", "Container re-sync interval", SettingCategory.Performance, SettingType.Float,
            "How often to force a re-sync while a container UI is open (seconds).", Min: 0),
        new("MaxGuildsPerFrame", "MaxGuildsPerFrame", SettingCategory.Performance, SettingType.Int,
            "Undocumented. Likely a per-frame cap on how many guilds the server processes each tick, a performance lever.", Min: 0, Doc: DocStatus.Unknown),
        new("PlayerDataPalStorageUpdateCheckTickInterval", "PlayerDataPalStorageUpdateCheckTickInterval", SettingCategory.Performance, SettingType.Float,
            "Undocumented. Likely how often the server checks player Pal Box/storage data for changes to sync (seconds).", Min: 0, Doc: DocStatus.Unknown),

        // ===================== Features (Gameplay) =====================
        new("Difficulty", "Difficulty", SettingCategory.Gameplay, SettingType.Enum,
            "Difficulty preset. None applies your individual settings below.", new[] { "None", "Casual", "Normal", "Hard" }),
        new("bHardcore", "Hardcore Mode: Permanent Loss of Progress Upon Player Death", SettingCategory.Gameplay, SettingType.Bool,
            "Enable Hardcore. You will not be able to respawn on death."),
        new("bCharacterRecreateInHardcore", "Recreate character (Hardcore)", SettingCategory.Gameplay, SettingType.Bool,
            "Whether you may recreate your character upon death in Hardcore mode."),
        new("bIsPvP", "PvP", SettingCategory.Gameplay, SettingType.Bool,
            "Enable PvP. Only takes effect with Player-vs-player damage and Base defense vs. other guilds also on."),
        new("bEnablePlayerToPlayerDamage", "Player-vs-player damage", SettingCategory.Gameplay, SettingType.Bool,
            "Let players harm each other. One of the three settings that together enable PvP."),
        new("bEnableFriendlyFire", "Friendly fire", SettingCategory.Gameplay, SettingType.Bool,
            "Undocumented. Community says it lets players and their Pals damage members of their own guild.", Doc: DocStatus.Undocumented),
        new("bCanPickupOtherGuildDeathPenaltyDrop", "Loot other guilds' drops", SettingCategory.Gameplay, SettingType.Bool,
            "Allow players to pick up the Pals and items dropped by other guilds' players on death."),
        new("bEnableDefenseOtherGuildPlayer", "Base defense vs. other guilds", SettingCategory.Gameplay, SettingType.Bool,
            "Base Pals engage hostile players who trespass into your base. One of the three settings that enable PvP."),
        new("bEnableFastTravel", "Enable Fast Travel", SettingCategory.Gameplay, SettingType.Bool,
            "Enable fast travel."),
        new("bEnableFastTravelOnlyBaseCamp", "Restrict Fast Travel to Bases Only", SettingCategory.Gameplay, SettingType.Bool,
            "Restrict fast travel to between bases only."),
        new("bEnableInvaderEnemy", "Enable Raid Events", SettingCategory.Gameplay, SettingType.Bool,
            "Enable Invaders (raid events)."),
        new("EnablePredatorBossPal", "Enable Predator Pals", SettingCategory.Gameplay, SettingType.Bool,
            "Allows Predator Pals to spawn in the world."),
        new("bBuildAreaLimit", "Build area limit", SettingCategory.Gameplay, SettingType.Bool,
            "Prevent building near structures such as fast-travel points."),
        new("bInvisibleOtherGuildBaseCampAreaFX", "Show base area boundaries", SettingCategory.Gameplay, SettingType.Bool,
            "Show base area boundaries."),
        new("bShowPlayerList", "Player list in ESC menu", SettingCategory.Gameplay, SettingType.Bool,
            "Enable the player list on the ESC menu."),
        new("bExistPlayerAfterLogout", "Body stays after logout", SettingCategory.Gameplay, SettingType.Bool,
            "Whether players enter a sleeping state at their current location when logging out."),
        new("bEnableNonLoginPenalty", "bEnableNonLoginPenalty", SettingCategory.Gameplay, SettingType.Bool,
            "Undocumented. May relate to whether your Pals get debuffs when you haven't logged in. Untested.", Doc: DocStatus.Unknown),
        new("bIsStartLocationSelectByMap", "Choose starting location", SettingCategory.Gameplay, SettingType.Bool,
            "Whether to allow players to choose their starting location."),
        new("bActiveUNKO", "bActiveUNKO", SettingCategory.Gameplay, SettingType.Bool,
            "Undocumented. Appears unused, likely deadweight or an unimplemented feature.", Doc: DocStatus.Unknown),
        new("bEnableAimAssistPad", "Aim assist (controller)", SettingCategory.Gameplay, SettingType.Bool,
            "Aim assist for controllers. Set to False to disable it."),
        new("bEnableAimAssistKeyboard", "Aim assist (keyboard)", SettingCategory.Gameplay, SettingType.Bool,
            "Enable aim assist for mouse & keyboard.", Doc: DocStatus.Undocumented),
        new("bAllowEnhanceStat_Attack", "Allow stat points: Attack", SettingCategory.Gameplay, SettingType.Bool,
            "Allow allocating stat points to Attack."),
        new("bAllowEnhanceStat_Health", "Allow stat points: HP", SettingCategory.Gameplay, SettingType.Bool,
            "Allow allocating stat points to HP."),
        new("bAllowEnhanceStat_Stamina", "Allow stat points: Stamina", SettingCategory.Gameplay, SettingType.Bool,
            "Allow allocating stat points to Stamina."),
        new("bAllowEnhanceStat_Weight", "Allow stat points: Carry Weight", SettingCategory.Gameplay, SettingType.Bool,
            "Allow allocating stat points to Carry Weight."),
        new("bAllowEnhanceStat_WorkSpeed", "Allow stat points: Work Speed", SettingCategory.Gameplay, SettingType.Bool,
            "Allow allocating stat points to Work Speed."),
        new("bAllowGlobalPalboxExport", "Allow Pal Genetic Data to be Saved in the Global Palbox", SettingCategory.Gameplay, SettingType.Bool,
            "Allow saving to the Global Palbox."),
        new("bAllowGlobalPalboxImport", "Allow Pals to be Reconstructed from the Global Palbox", SettingCategory.Gameplay, SettingType.Bool,
            "Allow loading from the Global Palbox."),
        new("bAutoResetGuildNoOnlinePlayers", "Auto-reset inactive guilds", SettingCategory.Gameplay, SettingType.Bool,
            "If no guild members log in, automatically delete structures and base Pals."),
        new("AutoResetGuildTimeNoOnlinePlayers", "Auto-reset guild after (hours)", SettingCategory.Gameplay, SettingType.Float,
            "Offline duration before an inactive guild is auto-reset (hours). Ignored unless 'Auto-reset inactive guilds' is on.", Min: 0),
        new("bDisplayPvPItemNumOnWorldMap_BaseCamp", "Show base PvP items on map", SettingCategory.Gameplay, SettingType.Bool,
            "Whether to show, on the map, the number of PvP-exclusive items in each base."),
        new("bDisplayPvPItemNumOnWorldMap_Player", "Show player PvP items on map", SettingCategory.Gameplay, SettingType.Bool,
            "Whether to show player locations and the number of PvP-exclusive items on the map."),
        new("bIsRandomizerPalLevelRandom", "Randomize Pal levels", SettingCategory.Gameplay, SettingType.Bool,
            "If on, wild Pal levels are fully random. If off, levels are randomized within each area's intended range."),
        new("RandomizerType", "Random Pal Mode", SettingCategory.Gameplay, SettingType.Enum,
            "Pal spawn randomization. None = no randomization. Region = randomize per region. All = fully randomized.", new[] { "None", "Region", "All" }),
        new("RandomizerSeed", "Randomizer seed", SettingCategory.Gameplay, SettingType.Text,
            "Seed value used when Pal spawn randomization is enabled."),
        new("bEnableVoiceChat", "Voice chat", SettingCategory.Gameplay, SettingType.Bool,
            "Enable in-game voice chat."),
        new("VoiceChatMaxVolumeDistance", "Voice chat full-volume distance", SettingCategory.Gameplay, SettingType.Float,
            "Distance at which voice chat volume does not attenuate.", Min: 0),
        new("VoiceChatZeroVolumeDistance", "Voice chat silence distance", SettingCategory.Gameplay, SettingType.Float,
            "Distance at which voice chat volume becomes zero.", Min: 0),

        // ===================== Game balances =====================
        new("DayTimeSpeedRate", "Day Time Speed", SettingCategory.GameBalance, SettingType.Float,
            "Daytime progression speed.", Min: 0),
        new("NightTimeSpeedRate", "Night Time Speed", SettingCategory.GameBalance, SettingType.Float,
            "Nighttime progression speed.", Min: 0),
        new("ExpRate", "EXP Rate", SettingCategory.GameBalance, SettingType.Float,
            "EXP gain multiplier.", Min: 0),
        new("PalCaptureRate", "Pal Capture Rate", SettingCategory.GameBalance, SettingType.Float,
            "Capture rate multiplier.", Min: 0),
        new("PalSpawnNumRate", "Pal Appearance Rate", SettingCategory.GameBalance, SettingType.Float,
            "Pal spawn rate. Impacts performance.", Min: 0),
        new("PalEggDefaultHatchingTime", "Time (h) to incubate Massive Egg", SettingCategory.GameBalance, SettingType.Float,
            "Time to hatch a Massive (Huge) Egg (hours). Other eggs also require time to incubate.", Min: 0),
        new("WorkSpeedRate", "Work Speed Multiplier", SettingCategory.GameBalance, SettingType.Float,
            "How fast both players and Pals complete work. Multiplier: 2.0 = work completed twice as fast.", Min: 0),
        new("MonsterFarmActionSpeedRate", "Grazing Item Production Rate Multiplier", SettingCategory.GameBalance, SettingType.Float,
            "Item production speed multiplier from grazing.", Min: 0),
        new("PalDamageRateAttack", "Damage from Pals Multiplier", SettingCategory.GameBalance, SettingType.Float,
            "Damage dealt by Pals multiplier.", Min: 0),
        new("PalDamageRateDefense", "Damage to Pals Multiplier", SettingCategory.GameBalance, SettingType.Float,
            "Damage taken by Pals multiplier.", Min: 0),
        new("PlayerDamageRateAttack", "Damage from Player Multiplier", SettingCategory.GameBalance, SettingType.Float,
            "Damage dealt by players multiplier.", Min: 0),
        new("PlayerDamageRateDefense", "Damage to Player Multiplier", SettingCategory.GameBalance, SettingType.Float,
            "Damage taken by players multiplier.", Min: 0),
        new("PlayerStomachDecreaceRate", "Player Hunger Depletion Rate", SettingCategory.GameBalance, SettingType.Float,
            "Player hunger depletion rate multiplier.", Min: 0),
        new("PlayerStaminaDecreaceRate", "Player Stamina Reduction Rate", SettingCategory.GameBalance, SettingType.Float,
            "Player stamina depletion rate multiplier.", Min: 0),
        new("PlayerAutoHPRegeneRate", "Player Auto Health Regeneration Rate", SettingCategory.GameBalance, SettingType.Float,
            "Player natural HP regen multiplier.", Min: 0),
        new("PlayerAutoHpRegeneRateInSleep", "Player Sleep Health Regeneration Rate", SettingCategory.GameBalance, SettingType.Float,
            "Player HP regen while sleeping multiplier.", Min: 0),
        new("PalStomachDecreaceRate", "Pal Hunger Depletion Rate", SettingCategory.GameBalance, SettingType.Float,
            "Pal hunger depletion rate multiplier.", Min: 0),
        new("PalStaminaDecreaceRate", "Pal Stamina Reduction Rate", SettingCategory.GameBalance, SettingType.Float,
            "Pal stamina depletion rate multiplier.", Min: 0),
        new("PalAutoHPRegeneRate", "Pal Auto Health Regeneration Rate", SettingCategory.GameBalance, SettingType.Float,
            "Pal natural HP regen multiplier.", Min: 0),
        new("PalAutoHpRegeneRateInSleep", "Pal Sleep Health Regeneration Rate", SettingCategory.GameBalance, SettingType.Float,
            "Pal HP regen while sleeping in the Palbox multiplier.", Min: 0),
        new("CollectionDropRate", "Gatherable Items Multiplier", SettingCategory.GameBalance, SettingType.Float,
            "Gatherable items multiplier.", Min: 0),
        new("CollectionObjectHpRate", "Gatherable Objects Health Multiplier", SettingCategory.GameBalance, SettingType.Float,
            "Gatherable objects health multiplier.", Min: 0),
        new("CollectionObjectRespawnSpeedRate", "Gatherable Objects Respawn Interval", SettingCategory.GameBalance, SettingType.Float,
            "Gatherable objects respawn interval multiplier.", Min: 0),
        new("EnemyDropItemRate", "Dropped Items Multiplier", SettingCategory.GameBalance, SettingType.Float,
            "Dropped item quantity multiplier.", Min: 0),
        new("DropItemAliveMaxHours", "Dropped item lifetime (hours)", SettingCategory.GameBalance, SettingType.Float,
            "How long dropped items remain in the world before they despawn (hours). Lowering it helps server cleanup / performance.", Min: 0, Doc: DocStatus.Undocumented),
        new("ItemWeightRate", "Item Weight Rate", SettingCategory.GameBalance, SettingType.Float,
            "Item weight multiplier.", Min: 0),
        new("ItemCorruptionMultiplier", "Item Decay Rate Multiplier", SettingCategory.GameBalance, SettingType.Float,
            "Item corruption (decay) speed multiplier.", Min: 0),
        new("EquipmentDurabilityDamageRate", "Equipment Durability Loss Multiplier", SettingCategory.GameBalance, SettingType.Float,
            "Equipment durability loss multiplier.", Min: 0),
        new("BuildObjectHpRate", "Building HP", SettingCategory.GameBalance, SettingType.Float,
            "Multiplier for how much health buildings have. 2.0 = double building HP.", Min: 0, Doc: DocStatus.Undocumented),
        new("BuildObjectDamageRate", "Damage to Structure Multiplier", SettingCategory.GameBalance, SettingType.Float,
            "Damage multiplier to buildings.", Min: 0),
        new("BuildObjectDeteriorationDamageRate", "Structure Deterioration Rate", SettingCategory.GameBalance, SettingType.Float,
            "Building decay speed multiplier.", Min: 0),
        new("DeathPenalty", "Death Penalty", SettingCategory.GameBalance, SettingType.Enum,
            "What you drop on death. None = nothing. Item = all items except equipment. ItemAndEquipment = all items. All = all items and all Pals in your party.",
            new[] { "None", "Item", "ItemAndEquipment", "All" }),
        new("bPalLost", "Hardcore Mode: Pals Who Die Will Be Permanently Lost", SettingCategory.GameBalance, SettingType.Bool,
            "Permanently lose Pals on death."),
        new("BlockRespawnTime", "Respawn cooldown (seconds)", SettingCategory.GameBalance, SettingType.Float,
            "Cooldown before you can respawn after death (seconds).", Min: 0),
        new("RespawnPenaltyDurationThreshold", "Respawn penalty threshold", SettingCategory.GameBalance, SettingType.Float,
            "Survival-time threshold (seconds). Dying again before surviving this long applies the respawn cooldown multiplier below.", Min: 0),
        new("RespawnPenaltyTimeScale", "Respawn penalty multiplier", SettingCategory.GameBalance, SettingType.Float,
            "Multiplier applied to the respawn cooldown.", Min: 0),
        new("GuildPlayerMaxNum", "Maximum Number of Guild Members", SettingCategory.GameBalance, SettingType.Int,
            "Maximum number of players per guild.", Min: 0),
        new("GuildRejoinCooldownMinutes", "Guild rejoin cooldown (min)", SettingCategory.GameBalance, SettingType.Int,
            "Cooldown before rejoining a guild (minutes).", Min: 0),
        new("DenyTechnologyList", "Disabled technologies", SettingCategory.GameBalance, SettingType.Raw,
            "Disable specific technologies. Specify Technology IDs - example: (\"PALBOX\", \"RepairBench\"). Blank = none."),
        new("bAdditionalDropItemWhenPlayerKillingInPvPMode", "PvP kill: drop item", SettingCategory.GameBalance, SettingType.Bool,
            "Whether to drop a special item when a player is killed while PvP is enabled."),
        new("AdditionalDropItemWhenPlayerKillingInPvPMode", "PvP kill: item ID", SettingCategory.GameBalance, SettingType.Text,
            "The ID of the item to drop on a PvP kill (used when 'PvP kill: drop item' is on)."),
        new("AdditionalDropItemNumWhenPlayerKillingInPvPMode", "PvP kill: item count", SettingCategory.GameBalance, SettingType.Int,
            "The quantity of the item to drop on a PvP kill (used when 'PvP kill: drop item' is on).", Min: 0),
        new("SupplyDropSpan", "Meteorite/Supplies Drop Interval", SettingCategory.GameBalance, SettingType.Int,
            "Meteorite / supply drop interval (minutes).", Min: 0),
        new("AutoTransferMasterCheckIntervalSeconds", "AutoTransferMasterCheckIntervalSeconds", SettingCategory.GameBalance, SettingType.Float,
            "Undocumented. Likely how often the server checks whether guild/base ownership should auto-transfer from an inactive master (seconds).", Min: 0, Doc: DocStatus.Unknown),
        new("AutoTransferMasterThresholdDays", "AutoTransferMasterThresholdDays", SettingCategory.GameBalance, SettingType.Int,
            "Undocumented. Likely the days a guild/base master must be inactive before ownership auto-transfers.", Min: 0, Doc: DocStatus.Unknown),
    };
}
