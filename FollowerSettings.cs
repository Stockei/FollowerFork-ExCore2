using ExileCore2.Shared.Attributes;
using ExileCore2.Shared.Interfaces;
using ExileCore2.Shared.Nodes;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace Follower;

public class FollowerSettings : ISettings
{
    public ToggleNode Enable { get; set; } = new ToggleNode(false);

    [Menu("General")]
    [Submenu(CollapsedByDefault = true)]
    public GeneralSettings General { get; set; } = new GeneralSettings();

    [Menu("TP / Trade")]
    [Submenu(CollapsedByDefault = true)]
    public TpTradeSettings TpTrade { get; set; } = new TpTradeSettings();


    [Menu("Party chat leader commands")]
    [Submenu(CollapsedByDefault = true)]
    public PartyChatLeaderCommandSettings PartyChatLeaderCommands { get; set; } = new PartyChatLeaderCommandSettings();


    [Menu("PickUp")]
    [Submenu(CollapsedByDefault = true)]
    public PickUpSettings PickUp { get; set; } = new PickUpSettings();

    [Menu("Transition")]
    [Submenu(CollapsedByDefault = true)]
    public TransitionSettings Transition { get; set; } = new TransitionSettings();

    [Menu("Debug")]
    [Submenu(CollapsedByDefault = true)]
    public DebugSettings Debug { get; set; } = new DebugSettings();
}

[Submenu(CollapsedByDefault = true)]
public class GeneralSettings
{
    [Menu("Follow enabled")]
    public ToggleNode IsFollowEnabled { get; set; } = new ToggleNode(false);

    [Menu("Toggle follower hotkey")]
    public HotkeyNode ToggleFollower { get; set; } = Keys.PageUp;

    [Menu("Panic pause hotkey")]
    public HotkeyNode PanicPauseHotkey { get; set; } = Keys.Pause;


    [Menu("Leader name")]
    public TextNode LeaderName { get; set; } = new TextNode("");

    [Menu("Movement key")]
    public HotkeyNode MovementKey { get; set; } = Keys.T;

    [Menu("Pause when inventory is open")]
    public ToggleNode PauseWhenInventoryOpen { get; set; } = new ToggleNode(true);

    [Menu("Close follow")]
    public ToggleNode IsCloseFollowEnabled { get; set; } = new ToggleNode(false);

    [Menu("Pathfinding node distance")]
    public RangeNode<int> PathfindingNodeDistance { get; set; } = new RangeNode<int>(200, 10, 1000);

    [Menu("Bot input frequency (ms) - safety clamped internally to min 90 ms")]
    public RangeNode<int> BotInputFrequency { get; set; } = new RangeNode<int>(90, 80, 250);

    [Menu("Clear path distance")]
    public RangeNode<int> ClearPathDistance { get; set; } = new RangeNode<int>(500, 100, 5000);

    [Menu("Random click offset")]
    public RangeNode<int> RandomClickOffset { get; set; } = new RangeNode<int>(10, 1, 100);

    [Menu("Anti-stuck watchdog")]
    public ToggleNode AntiStuckWatchdog { get; set; } = new ToggleNode(true);

    [Menu("Anti-stuck no-progress timeout (ms)")]
    public RangeNode<int> AntiStuckNoProgressMs { get; set; } = new RangeNode<int>(4500, 1500, 20000);

    [Menu("Anti-stuck min progress distance")]
    public RangeNode<int> AntiStuckMinProgressDistance { get; set; } = new RangeNode<int>(35, 10, 250);

    [Menu("Allow Dodge/Sprint")]
    public ToggleNode IsSprintEnabled { get; set; } = new ToggleNode(true);

    [Menu("Sprint key")]
    public HotkeyNode DodgeSprintKey { get; set; } = Keys.S;

    [Menu("Sprint distance to leader (world units)")]
    public RangeNode<int> SprintDistanceThreshold { get; set; } = new RangeNode<int>(500, 50, 2000);

    [Menu("Sprint re-trigger (ms)")]
    public RangeNode<int> SprintRetriggerCooldownMs { get; set; } = new RangeNode<int>(5000, 500, 15000);
}

[Submenu(CollapsedByDefault = true)]
public class PartyChatLeaderCommandSettings
{
    [Menu("Enabled")]
    public ToggleNode Enabled { get; set; } = new ToggleNode(true);

    [Menu("Stop command")]
    public TextNode StopCommand { get; set; } = new TextNode("-p");

    [Menu("Start command")]
    public TextNode StartCommand { get; set; } = new TextNode("-s");

    [Menu("Pause whole plugin + ESC command")]
    public TextNode PausePluginCommand { get; set; } = new TextNode("-pp");

    [Menu("Resume whole plugin + ESC command")]
    public TextNode ResumePluginCommand { get; set; } = new TextNode("-ss");

    [Menu("Find/click map portal command")]
    public TextNode ContinuePortalCommand { get; set; } = new TextNode("-c");

    [Menu("Dump inventory to trade command")]
    public TextNode DumpInventoryCommand { get; set; } = new TextNode("-d");

    [Menu("Start PickUp command")]
    public TextNode StartPickUpCommand { get; set; } = new TextNode("-l");

    [Menu("Pause PickUp command")]
    public TextNode PausePickUpCommand { get; set; } = new TextNode("-ls");

    [Menu("Command poll interval (ms)")]
    public RangeNode<int> PollMs { get; set; } = new RangeNode<int>(1000, 1000, 5000);
}

[Submenu(CollapsedByDefault = true)]
public class TransitionSettings
{
    [Menu("Auto-click boss Arena transition")]
    public ToggleNode AutoClickArenaTransition { get; set; } = new ToggleNode(true);

    [Menu("Arena transition label text")]
    public TextNode ArenaTransitionLabelText { get; set; } = new TextNode("Arena");

    [Menu("Auto-click Abyss sub-area transition")]
    public ToggleNode AutoClickAbyssSubAreaTransition { get; set; } = new ToggleNode(true);

    [Menu("Transition scan interval (ms)")]
    public RangeNode<int> ArenaTransitionScanMs { get; set; } = new RangeNode<int>(1000, 750, 5000);

    [Menu("Transition max click retries")]
    public RangeNode<int> ArenaTransitionMaxRetries { get; set; } = new RangeNode<int>(3, 1, 10);

    [Menu("Transition retry cooldown (ms)")]
    public RangeNode<int> ArenaTransitionRetryCooldownMs { get; set; } = new RangeNode<int>(5000, 500, 30000);
}

[Submenu(CollapsedByDefault = true)]
public class TpTradeSettings
{

    [Menu("Auto accept party invites")]
    public ToggleNode AutoAcceptParty { get; set; } = new ToggleNode(true);

    [Menu("Auto accept trade invites")]
    public ToggleNode AutoAcceptTrade { get; set; } = new ToggleNode(false);

    [Menu("Auto dump inventory to trade on leader command")]
    public ToggleNode AutoDumpInventoryToTrade { get; set; } = new ToggleNode(true);

    [Menu("Auto accept trade after dump")]
    public ToggleNode AutoAcceptTradeAfterDump { get; set; } = new ToggleNode(true);

    [IgnoreMenu]
    public bool[,] TradeDumpIgnoredCells { get; set; } = new bool[5, 12];

    [Menu("Trade dump item delay (ms)")]
    public RangeNode<int> TradeDumpItemDelayMs { get; set; } = new RangeNode<int>(65, 20, 250);

    [Menu("Trade accept delay after dump (ms) - internally clamped to min 1600")]
    public RangeNode<int> TradeDumpAcceptDelayMs { get; set; } = new RangeNode<int>(1600, 0, 6000);

    [Menu("Trade window wait timeout (ms)")]
    public RangeNode<int> TradeDumpWindowWaitTimeoutMs { get; set; } = new RangeNode<int>(10000, 1000, 60000);

    [Menu("Trade finish timeout (ms)")]
    public RangeNode<int> TradeDumpFinishTimeoutMs { get; set; } = new RangeNode<int>(60000, 5000, 180000);

    [Menu("Auto party poll interval (ms)")]
    public RangeNode<int> AutoPartyPollMs { get; set; } = new RangeNode<int>(500, 200, 2000);

    [Menu("Teleport to leader (party TP)")]
    public ToggleNode TeleportToLeader { get; set; } = new ToggleNode(true);

    [Menu("Auto-confirm 'Teleport?' dialog (click OK)")]
    public ToggleNode AutoConfirmTeleportDialog { get; set; } = new ToggleNode(true);

    [Menu("TP check interval (ms)")]
    public RangeNode<int> TpPollMs { get; set; } = new RangeNode<int>(800, 200, 3000);

    [Menu("TP click retries")]
    public RangeNode<int> TpMaxRetries { get; set; } = new RangeNode<int>(3, 0, 10);

    [Menu("Stop after hideout TP")]
    public ToggleNode StopAfterHideoutTeleport { get; set; } = new ToggleNode(true);

    [Menu("Teleport from own Hideout to leader Hideout")]
    public ToggleNode TeleportFromHideout { get; set; } = new ToggleNode(true);

    [Menu("Teleport confirm timeout (ms)")]
    public RangeNode<int> TpConfirmTimeoutMs { get; set; } = new RangeNode<int>(2500, 500, 8000);

    [Menu("Teleport confirm retries")]
    public RangeNode<int> TpConfirmRetries { get; set; } = new RangeNode<int>(2, 0, 5);
}


[Submenu(CollapsedByDefault = true)]
public class PickUpSettings
{
    [Menu("Enabled")]
    public ToggleNode Enabled { get; set; } = new ToggleNode(false);

    [Menu("PickUp Everything", "Pick up every visible clickable ground item within range, ignoring the rule table. Still respects item allocation.")]
    public ToggleNode PickUpEverything { get; set; } = new ToggleNode(false);


    [IgnoreMenu]
    public ToggleNode RespectItemAllocation { get; set; } = new ToggleNode(true);

    [IgnoreMenu]
    public ToggleNode DisableInTownOrHideout { get; set; } = new ToggleNode(true);

    [Menu("Only start pickup while near leader")]
    public ToggleNode RequireLeaderNearbyToStart { get; set; } = new ToggleNode(true);

    [Menu("Max distance from leader to start pickup")]
    public RangeNode<int> MaxDistanceFromLeaderToStart { get; set; } = new RangeNode<int>(650, 100, 3000);

    [Menu("Item scan range")]
    public RangeNode<int> ItemPickupRange { get; set; } = new RangeNode<int>(700, 50, 2500);

    [Menu("Click distance")]
    public RangeNode<int> ItemClickDistance { get; set; } = new RangeNode<int>(80, 10, 500);

    [IgnoreMenu]
    public RangeNode<int> ScanIntervalMs { get; set; } = new RangeNode<int>(250, 100, 2000);

    [Menu("Allow pickup click delay below 650 ms", "Advanced: allows faster but less safe pickup delays below the stable 650 ms default.")]
    public ToggleNode AllowPickupClickDelayBelowSafeMinimum { get; set; } = new ToggleNode(false);

    [Menu("Pickup click delay (ms)", "Delay after clicking a ground item before the next pickup action. Default 650 ms is safer on lag/desync.")]
    public RangeNode<int> PickupClickDelayMs { get; set; } = new RangeNode<int>(650, 90, 750);

    [Menu("Max attempts per item")]
    public RangeNode<int> MaxAttemptsPerItem { get; set; } = new RangeNode<int>(3, 1, 6);

    [IgnoreMenu]
    public RangeNode<int> FailedItemBlacklistMs { get; set; } = new RangeNode<int>(12000, 1000, 60000);

    [Menu("Max active pickup time (ms)")]
    public RangeNode<int> MaxActivePickupMs { get; set; } = new RangeNode<int>(6500, 1000, 30000);

    [IgnoreMenu]
    public RangeNode<int> MinimumFreeInventorySlots { get; set; } = new RangeNode<int>(0, 0, 20);

    [Menu("No pickup while enemy close")]
    public ToggleNode NoPickupWhileEnemyClose { get; set; } = new ToggleNode(false);

    [Menu("Enemy check range")]
    public RangeNode<int> MonsterCheckRange { get; set; } = new RangeNode<int>(800, 100, 2500);

    [IgnoreMenu]
    public ToggleNode DebugHighlight { get; set; } = new ToggleNode(false);

    [IgnoreMenu]
    public List<FollowerPickUpCategory> RuleCategories { get; set; } = new List<FollowerPickUpCategory>();

    [IgnoreMenu]
    public bool LegacyRulesMigrated { get; set; } = true;

    [IgnoreMenu]
    public bool AutoSampleRulesCleanupV4Done { get; set; }

    [IgnoreMenu]
    public bool AutoSampleRulesCleanupV5Done { get; set; }

    [IgnoreMenu]
    public List<FollowerPickUpRule> Rules { get; set; } = new List<FollowerPickUpRule>();
}

public class FollowerPickUpCategory
{
    public string Name { get; set; } = string.Empty;
    public bool AllowCustomItems { get; set; }
    public bool ShowOnlyAllowed { get; set; }
    public string SearchText { get; set; } = string.Empty;
    public List<FollowerPickUpRule> Rules { get; set; } = new List<FollowerPickUpRule>();

    public FollowerPickUpCategory()
    {
    }

    public FollowerPickUpCategory(string name, IEnumerable<string> itemNames, bool allowCustomItems = false, bool enabledByDefault = false)
    {
        Name = name ?? string.Empty;
        AllowCustomItems = allowCustomItems;
        Rules = MakeRules(itemNames, enabledByDefault);
    }

    public static List<FollowerPickUpCategory> CreateDefaultCategories()
    {
        return new List<FollowerPickUpCategory>
        {
            new FollowerPickUpCategory("Currency", CurrencyItems()),
            new FollowerPickUpCategory("Fragments", FragmentItems()),
            new FollowerPickUpCategory("Abyssal Bones", AbyssalBoneItems()),
            new FollowerPickUpCategory("Uncut Gems", UncutGemItems()),
            new FollowerPickUpCategory("Lineage Gems", LineageGemItems()),
            new FollowerPickUpCategory("Essences", EssenceItems()),
            new FollowerPickUpCategory("Soul Cores", SoulCoreItems()),
            new FollowerPickUpCategory("Idols", IdolItems()),
            new FollowerPickUpCategory("Runes", RuneItems()),
            new FollowerPickUpCategory("Omens", OmenItems()),
            new FollowerPickUpCategory("Expedition", ExpeditionItems()),
            new FollowerPickUpCategory("Liquid Emotions", LiquidEmotionItems()),
            new FollowerPickUpCategory("Catalysts", CatalystItems()),
            new FollowerPickUpCategory("Verisium", VerisiumItems()),
            new FollowerPickUpCategory("Unique Tablets", UniqueTabletItems()),
            new FollowerPickUpCategory("Precursor Tablets", PrecursorTabletItems()),
            new FollowerPickUpCategory("Breach Wombgift", BreachWombgiftItems()),
            new FollowerPickUpCategory("Own", Array.Empty<string>(), allowCustomItems: true)
        };
    }

    private static List<FollowerPickUpRule> MakeRules(IEnumerable<string> itemNames, bool enabledByDefault)
    {
        var rules = new List<FollowerPickUpRule>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var itemName in itemNames ?? Array.Empty<string>())
        {
            var normalized = (itemName ?? string.Empty).Trim();
            if (normalized.Length == 0 || !seen.Add(normalized))
                continue;

            rules.Add(new FollowerPickUpRule(normalized, enabled: enabledByDefault));
        }

        return rules;
    }

    private static string[] CurrencyItems() => new[]
    {
        "Mirror of Kalandra", "Hinekora's Lock", "Fracturing Orb", "Perfect Chaos Orb", "Perfect Exalted Orb",
        "Divine Orb", "Orb of Annulment", "Perfect Jeweller's Orb", "Greater Chaos Orb", "Cryptic Key",
        "Chaos Orb", "Perfect Regal Orb", "Perfect Orb of Augmentation", "Perfect Orb of Transmutation", "Orb of Chance",
        "Greater Exalted Orb", "Vaal Orb", "Gemcutter's Prism", "Greater Regal Orb", "Arcanist's Etcher",
        "Greater Jeweller's Orb", "Armourer's Scrap", "Glassblower's Bauble", "Orb of Augmentation", "Greater Orb of Augmentation",
        "Orb of Transmutation", "Greater Orb of Transmutation", "Regal Orb", "Exalted Orb", "Orb of Alchemy",
        "Artificer's Orb", "Lesser Jeweller's Orb", "Transmutation Shard", "Regal Shard", "Artificer's Shard",
        "Chance Shard", "Blacksmith's Whetstone", "Scroll of Wisdom"
    };

    private static string[] FragmentItems() => new[]
    {
        "The Trialmaster's Reliquary Key", "Azmeri Reliquary Key", "Zarokh's Reliquary Key: Against the Darkness",
        "Xesht's Reliquary Key", "Tangmazu's Reliquary Key", "The Arbiter's Reliquary Key", "Faded Crisis Fragment",
        "Olroth's Reliquary Key", "Origin Core", "Cowardly Fate", "Deadly Fate", "Victorious Fate", "Origin Cradle",
        "Origin Spark", "Ritualistic Reliquary Key", "Weathered Crisis Fragment", "Ancient Crisis Fragment", "Call of the Shadows",
        "Twilight Reliquary Key", "Breach Splinter", "Simulacrum Splinter", "Ravenous Splinter"
    };

    private static string[] AbyssalBoneItems() => new[]
    {
        "Preserved Cranium", "Ancient Jawbone", "Ancient Collarbone", "Ancient Rib", "Altered Collarbone",
        "Preserved Collarbone", "Gnawed Jawbone", "Gnawed Collarbone", "Preserved Jawbone", "Gnawed Rib",
        "Preserved Rib", "Preserved Vertebrae", "Ancient Vertebrae", "Gnawed Vertebrae", "Altered Jawbone",
        "Altered Rib", "Altered Vertebrae", "Altered Cranium"
    };

    private static string[] UncutGemItems() => new[]
    {
        "Uncut Skill Gem (Level 1)", "Uncut Skill Gem (Level 2)", "Uncut Skill Gem (Level 3)", "Uncut Skill Gem (Level 4)",
        "Uncut Skill Gem (Level 5)", "Uncut Skill Gem (Level 6)", "Uncut Skill Gem (Level 7)", "Uncut Skill Gem (Level 8)",
        "Uncut Skill Gem (Level 9)", "Uncut Skill Gem (Level 10)", "Uncut Skill Gem (Level 11)", "Uncut Skill Gem (Level 12)",
        "Uncut Skill Gem (Level 13)", "Uncut Skill Gem (Level 14)", "Uncut Skill Gem (Level 15)", "Uncut Skill Gem (Level 16)",
        "Uncut Skill Gem (Level 17)", "Uncut Skill Gem (Level 18)", "Uncut Skill Gem (Level 19)", "Uncut Skill Gem (Level 20)",
        "Uncut Support Gem (Level 1)", "Uncut Support Gem (Level 2)", "Uncut Support Gem (Level 3)", "Uncut Support Gem (Level 4)",
        "Uncut Support Gem (Level 5)", "Uncut Spirit Gem (Level 4)", "Uncut Spirit Gem (Level 5)", "Uncut Spirit Gem (Level 6)",
        "Uncut Spirit Gem (Level 7)", "Uncut Spirit Gem (Level 8)", "Uncut Spirit Gem (Level 9)", "Uncut Spirit Gem (Level 10)",
        "Uncut Spirit Gem (Level 11)", "Uncut Spirit Gem (Level 12)", "Uncut Spirit Gem (Level 13)", "Uncut Spirit Gem (Level 14)",
        "Uncut Spirit Gem (Level 15)", "Uncut Spirit Gem (Level 16)", "Uncut Spirit Gem (Level 17)", "Uncut Spirit Gem (Level 18)",
        "Uncut Spirit Gem (Level 19)", "Uncut Spirit Gem (Level 20)"
    };

    private static string[] LineageGemItems() => new[]
    {
        "Atziri's Communion", "Garukhan's Resolve", "Uul-Netol's Embrace", "Dialla's Desire", "Rakiata's Flow",
        "Rigwald's Ferocity", "Seraph's Heart", "Uhtred's Exodus", "Her Declaration", "Esh's Radiance", "Tul's Stillness",
        "Uhtred's Augury", "Xoph's Pyre", "Breachlord's Rift", "Vorana's Siege", "Uhtred's Omen", "Olroth's Conviction",
        "Ixchel's Torment", "Atalui's Bloodletting", "Atziri's Allure", "Khatal's Rejuvenation", "Arbiter's Ignition",
        "Breachlord's Amalgam", "Catha's Brilliance", "Amanamu's Tithe", "Mórrigan's Insight", "Esh's Prowess",
        "Sione's Temper", "Uhtred's Constellation", "Ailith's Chimes", "Brutus' Brain", "Medved's Felling",
        "Styrn's Ferocity", "Doedre's Undoing", "Zerphi's Infamy", "Zarokh's Revolt", "Styrn's Mountain",
        "Uhtred's Rite", "Zarokh's Refrain", "Hayoxi's Fulmination", "Paquate's Pact", "Trickster's Shard",
        "Ahn's Citadel", "Xibaqua's Rending", "Tecrod's Revenge", "Arbiter's Reach", "Einhar's Beastrite",
        "Atziri's Impatience", "Morgana's Tempest", "Prototype Seventeen", "Tul's Avalanche", "Dominus' Grasp",
        "Kulemak's Dominion", "Kurgal's Leash", "Olroth's Hubris", "Kalisa's Crescendo", "Uruk's Smelting",
        "Kaom's Madness", "Romira's Requital", "Oisín's Oath", "Tangmazu's Thurible", "Vilenta's Propulsion",
        "Ratha's Assault", "Tasalio's Rhythm", "Arjun's Medal", "Tawhoa's Tending", "Tacati's Ire",
        "Bhatair's Vengeance", "Daresso's Passion", "Varashta's Blessing", "Arakaali's Lust", "Guatelitzi's Ablation",
        "Cirel's Cultivation", "Vruun's Aftermath", "Vruun's Inevitability"
    };

    private static string[] EssenceItems() => new[]
    {
        "Essence of Hysteria", "Essence of Horror", "Essence of Opulence", "Essence of Delirium", "Essence of Abrasion",
        "Essence of the Breach", "Essence of Grounding", "Essence of Ruin", "Perfect Essence of the Infinite", "Lesser Essence of Abrasion",
        "Essence of Insulation", "Essence of Seeking", "Lesser Essence of Haste", "Essence of the Abyss", "Lesser Essence of Ruin",
        "Perfect Essence of Haste", "Lesser Essence of Opulence", "Greater Essence of Opulence", "Essence of Haste", "Essence of the Infinite",
        "Perfect Essence of Battle", "Perfect Essence of Opulence", "Essence of the Body", "Essence of Enhancement", "Essence of Thawing",
        "Essence of Sorcery", "Lesser Essence of Ice", "Lesser Essence of Enhancement", "Lesser Essence of Seeking", "Essence of Alacrity",
        "Perfect Essence of Enhancement", "Perfect Essence of Grounding", "Essence of Insanity", "Lesser Essence of Electricity", "Lesser Essence of Sorcery",
        "Essence of Ice", "Lesser Essence of Thawing", "Lesser Essence of Alacrity", "Essence of Electricity", "Lesser Essence of the Body",
        "Lesser Essence of Grounding", "Lesser Essence of the Infinite", "Essence of Command", "Greater Essence of Seeking", "Lesser Essence of Command",
        "Lesser Essence of Battle", "Essence of the Mind", "Essence of Battle", "Greater Essence of Ruin", "Perfect Essence of the Mind",
        "Perfect Essence of Abrasion", "Lesser Essence of Flames", "Perfect Essence of Flames", "Perfect Essence of Ice", "Perfect Essence of Electricity",
        "Perfect Essence of Sorcery", "Perfect Essence of Alacrity", "Perfect Essence of Seeking", "Lesser Essence of the Mind", "Lesser Essence of Insulation",
        "Essence of Flames", "Greater Essence of the Body", "Greater Essence of the Mind", "Greater Essence of Enhancement", "Greater Essence of Haste",
        "Perfect Essence of Thawing", "Greater Essence of the Infinite", "Greater Essence of Flames", "Greater Essence of Insulation", "Greater Essence of Ice",
        "Greater Essence of Thawing", "Greater Essence of Electricity", "Greater Essence of Grounding", "Greater Essence of Abrasion", "Greater Essence of Battle",
        "Greater Essence of Sorcery", "Greater Essence of Command", "Greater Essence of Alacrity", "Perfect Essence of the Body", "Perfect Essence of Insulation",
        "Perfect Essence of Ruin", "Perfect Essence of Command"
    };

    private static string[] SoulCoreItems() => new[]
    {
        "Soul Core of Azcapa", "Soul Core of Quipolatl", "Xopec's Soul Core of Power", "Opiloti's Soul Core of Assault",
        "Soul Core of Zalatl", "Soul Core of Cholotl", "Tzamoto's Soul Core of Ferocity", "Soul Core of Jiquani",
        "Soul Core of Citaqualotl", "Soul Core of Tacati", "Soul Core of Zantipi", "Quipolatl's Soul Core of Flow",
        "Soul Core of Topotante", "Soul Core of Opiloti", "Guatelitzi's Soul Core of Endurance", "Soul Core of Ticaba",
        "Soul Core of Atmohua", "Soul Core of Puhuarte", "Soul Core of Tzamoto", "Soul Core of Xopec",
        "Estazunti's Soul Core of Convalescence", "Xipocado's Soul Core of Dominion", "Atmohua's Soul Core of Retreat",
        "Hayoxi's Soul Core of Heatproofing", "Zalatl's Soul Core of Insulation", "Topotante's Soul Core of Dampening",
        "Citaqualotl's Soul Core of Foulness", "Tacati's Soul Core of Affliction", "Uromoti's Soul Core of Attenuation",
        "Cholotl's Soul Core of War"
    };

    private static string[] IdolItems() => new[]
    {
        "Carved Majesty", "Carved Guile", "Carved Tenacity", "Carved Mischief", "Fox Idol", "Rabbit Idol", "Bear Idol",
        "Boar Idol", "Cat Idol", "Owl Idol", "Ox Idol", "Primate Idol", "Stag Idol", "Serpent Idol", "Wolf Idol",
        "Idol of Grold", "Idol of the Martyr", "Idol of Ralakesh", "Idol of the Sycophant", "Idol of Thruldana",
        "Idol of Eramir", "Idol of Egrin", "Idol of Greust", "Idol of Yeena", "Idol of Oak", "Idol of Alira",
        "Idol of Silk", "Idol of Maxarius", "Idol of Kraityn", "Idol of Ishta", "Idol of the Pharisee"
    };

    private static string[] RuneItems() => new[]
    {
        "Aldur's Legacy", "Emergent Possibility", "Emergent Vigour", "Astrid's Creativity", "Greater Rune of Leadership",
        "Perfect Ward Rune", "Perfect Charging Rune", "Perfect Stone Rune", "Serle's Triumph", "Perfect Vision Rune", "Perfect Robust Rune",
        "Perfect Rebirth Rune", "Perfect Resolve Rune", "Perfect Body Rune", "Perfect Mind Rune", "Perfect Storm Rune", "Perfect Adept Rune",
        "Perfect Inspiration Rune", "Perfect Desert Rune", "Perfect Glacial Rune", "Perfect Iron Rune", "Greater Rune of Alacrity",
        "Cadigan's Epiphany", "Kolr's Hunt", "Emergent Instinct", "Greater Rune of Tithing", "Masterwork Rune", "Uhtred's Sidereus",
        "Lesser Robust Rune", "Iron Rune", "Warding Rune of Reinforcement", "Hedgewitch Assandra's Rune of Wisdom", "Emergent Protection",
        "Greater Ward Rune", "Medved's Tending", "Robust Rune", "Body Rune", "Farrul's Rune of the Chase", "Rune of the Blossom",
        "Vorana's Carnage", "Rune of Foundations", "Warding Rune of Heart", "Katla's Gloom", "Lesser Iron Rune", "Lesser Adept Rune",
        "Rune of Renown", "Charging Rune", "Lesser Inspiration Rune", "Lesser Storm Rune", "Saqawal's Rune of the Sky", "Rune of Acrobatics",
        "Rune of Accumulation", "Warding Rune of Disintegration", "Warding Rune of Stability", "Warding Rune of Salvaging", "Thrud's Might",
        "Rebirth Rune", "Inspiration Rune", "Adept Rune", "Rune of Culmination", "Storm Rune", "Craiceann's Rune of Warding",
        "Countess Seske's Rune of Archery", "Saqawal's Rune of Memory", "Lesser Ward Rune", "Ward Rune", "Greater Charging Rune",
        "Lesser Charging Rune", "Lesser Body Rune", "Greater Body Rune", "Lesser Mind Rune", "Mind Rune", "Greater Mind Rune",
        "Lesser Rebirth Rune", "Greater Rebirth Rune", "Lesser Vision Rune", "Vision Rune", "Greater Vision Rune", "Lesser Resolve Rune",
        "Resolve Rune", "Greater Resolve Rune", "Lesser Tempered Rune", "Tempered Rune", "Greater Tempered Rune", "Lesser Desert Rune",
        "Desert Rune", "Greater Desert Rune", "Lesser Glacial Rune", "Glacial Rune", "Greater Glacial Rune", "Greater Iron Rune",
        "Lesser Inspiration Rune", "Greater Inspiration Rune", "Rune of Vitality", "Rune of the Prism", "Warding Rune of Armature",
        "Warding Rune of Bodyguards", "Warding Rune of Hollowing", "The Greatwolf's Rune of Claws", "Saqawal's Rune of Erosion",
        "Farrul's Rune of Grace", "Courtesan Mannan's Rune of Cruelty", "Thane Grannell's Rune of Mastery",
        "Ancient Rune of Detonation", "Rune of the Hunt", "Rune of Reach", "The Greatwolf's Rune of Willpower",
        "Craiceann's Rune of Recovery", "Fenumus' Rune of Spinning", "Ancient Rune of the Titan", "Fenumus' Rune of Agony",
        "Fenumus' Rune of Draining", "Warding Rune of Nourishment", "Warding Rune of Equinox", "Betrayal of Aldur",
        "Greater Rune of Nobility", "Breath of Aldur", "Farrul's Rune of the Hunt", "Ancient Rune of Shattering",
        "Warding Rune of Annihilation", "Ire of Aldur", "Lesser Stone Rune", "Greater Stone Rune", "Thane Myrk's Rune of Summer",
        "Lady Hestra's Rune of Winter", "Ancient Rune of Prowess", "Ancient Rune of Control", "Ancient Rune of Animosity",
        "Rune of Confrontation", "Warding Rune of Desperation", "Warding Rune of Courage", "Warding Rune of Glancing",
        "Warding Rune of Obsession", "Passion of Aldur", "Rune of Vital Flame", "Warding Rune of Symbiosis",
        "Ancient Rune of Discovery", "Ancient Rune of Splinters", "Ancient Rune of Decay", "Ancient Rune of Witchcraft",
        "Ancient Rune of the Horde", "Ancient Rune of Retaliation", "Rune of Consistency", "Warding Rune of Protection",
        "Ancient Rune of Dueling", "Stone Rune"
    };

    private static string[] OmenItems() => new[]
    {
        "Omen of Chance", "Omen of Sinistral Annulment", "Omen of Sinistral Erasure", "Omen of Whittling",
        "Omen of Dextral Annulment", "Omen of Dextral Erasure", "Head of the King", "Omen of Dextral Crystallisation",
        "Omen of the Hunt", "Omen of Chaotic Rarity", "Omen of Chaotic Quantity", "Omen of Sanctification",
        "Omen of Secret Compartments", "Omen of Chaotic Monsters", "Omen of the Blessed", "Omen of Sinistral Crystallisation",
        "Omen of Reinforcements", "Omen of Answered Prayers", "Omen of Chaotic Effectiveness", "Omen of Resurgence",
        "Omen of Bartering", "Omen of Amelioration", "Omen of Sinistral Exaltation", "Omen of Catalysing Exaltation",
        "Omen of the Ancients", "Omen of Dextral Exaltation", "Omen of Greater Exaltation", "An Audience with the King",
        "Omen of Refreshment", "Omen of Gambling"
    };

    private static string[] ExpeditionItems() => new[]
    {
        "Expedition Logbook", "Aldur's Saga", "Uhtred's Saga", "Olroth's Saga", "Vorana's Saga", "Medved's Saga",
        "Uhtred's Crest of the Chalice", "Medved's Crest of the Circle", "Vorana's Crest of the Scythe"
    };

    private static string[] LiquidEmotionItems() => new[]
    {
        "Potent Liquid Ferocity", "Potent Liquid Contempt", "Ancient Potent Liquid Contempt", "Concentrated Liquid Isolation",
        "Concentrated Liquid Suffering", "Concentrated Liquid Fear", "Ancient Potent Liquid Melancholy", "Liquid Despair",
        "Ancient Potent Liquid Ferocity", "Ancient Concentrated Liquid Isolation", "Potent Liquid Melancholy", "Liquid Disgust",
        "Ancient Diluted Liquid Ire", "Ancient Diluted Liquid Guilt", "Ancient Liquid Paranoia", "Ancient Liquid Envy",
        "Ancient Liquid Disgust", "Ancient Liquid Despair", "Ancient Concentrated Liquid Fear", "Ancient Concentrated Liquid Suffering",
        "Diluted Liquid Ire", "Diluted Liquid Guilt", "Diluted Liquid Greed", "Liquid Paranoia", "Liquid Envy",
        "Ancient Diluted Liquid Greed"
    };

    private static string[] CatalystItems() => new[]
    {
        "Reaver Catalyst", "Sibilant Catalyst", "Necrotic Catalyst", "Neural Catalyst", "Carapace Catalyst", "Flesh Catalyst",
        "Chayula's Catalyst", "Esh's Catalyst", "Tul's Catalyst", "Xoph's Catalyst", "Uul-Netol's Catalyst",
        "Refined Reaver Catalyst", "Refined Sibilant Catalyst", "Refined Necrotic Catalyst", "Refined Neural Catalyst",
        "Refined Carapace Catalyst", "Refined Flesh Catalyst", "Refined Chayula's Catalyst", "Refined Esh's Catalyst",
        "Refined Tul's Catalyst", "Refined Xoph's Catalyst", "Refined Uul-Netol's Catalyst", "Breachlord Sac", "Hivebrain Gland",
        "Ancient Wombgift", "Mysterious Wombgift", "Provisioning Wombgift", "Cryonic Ring", "Enthalpic Ring",
        "Synaptic Ring", "Organic Ring", "Fugitive Ring", "Formless Ring"
    };

    private static string[] VerisiumItems() => new[]
    {
        "Verisium", "Exceptional Verisium", "Celestial Alloy", "Transcendent Alloy", "Sovereign Alloy", "Adaptive Alloy",
        "The Runebinder's Alloy", "Protective Alloy", "Prismatic Alloy", "Mystic Alloy", "Runic Alloy", "The Runefather's Alloy",
        "Expansive Alloy", "Cyclonic Alloy", "Swift Alloy", "Olroth's Crest of the Sun", "Veridical Starlit Ore",
        "Warding Starlit Ore", "Revered Starlit Ore", "Venerable Starlit Ore", "Perfect Flux", "Void Flux", "Blazing Flux",
        "Chilling Flux", "Crackling Flux", "Thaumaturgic Flux (Level 8)", "Thaumaturgic Flux (Level 9)",
        "Thaumaturgic Flux (Level 10)", "Thaumaturgic Flux (Level 11)", "Thaumaturgic Flux (Level 12)",
        "Thaumaturgic Flux (Level 13)", "Thaumaturgic Flux (Level 14)", "Thaumaturgic Flux (Level 15)",
        "Thaumaturgic Flux (Level 16)", "Thaumaturgic Flux (Level 17)", "Thaumaturgic Flux (Level 18)",
        "Thaumaturgic Flux (Level 19)", "Thaumaturgic Flux (Level 20)"
    };

    private static string[] UniqueTabletItems() => new[]
    {
        "Wraeclast Besieged", "Clear Skies", "Freedom of Faith", "The Grand Project", "Visions of Paradise",
        "Mastered Domain", "Season of the Hunt", "Cruel Hegemony", "Unforeseen Consequences"
    };

    private static string[] PrecursorTabletItems() => new[]
    {
        "Breach Precursor Tablet", "Breach Tablet", "Expedition Precursor Tablet", "Expedition Tablet", "Delirium Precursor Tablet",
        "Delirium Tablet", "Ritual Precursor Tablet", "Ritual Tablet", "Irradiated Precursor Tablet", "Irradiated Tablet",
        "Overseer Precursor Tablet", "Overseer Tablet", "Abyss Precursor Tablet", "Abyss Tablet", "Precursor Tablet", "Tablet"
    };

    private static string[] BreachWombgiftItems() => new[]
    {
        "Banded Wombgift",
        "Lavish Wombgift",
        "Ornate Wombgift",
        "Revelatory Wombgift",
        "Signet Wombgift"
    };
}

public class FollowerPickUpRule
{
    public bool Enabled { get; set; } = true;
    public string Name { get; set; } = string.Empty;
    public string Expression { get; set; } = string.Empty;

    public FollowerPickUpRule()
    {
    }

    public FollowerPickUpRule(string name, string expression = null, bool enabled = true)
    {
        Name = name ?? string.Empty;
        Expression = string.IsNullOrWhiteSpace(expression) ? BuildBaseNameExpression(Name) : expression;
        Enabled = enabled;
    }

    public string GetItemName()
    {
        if (!string.IsNullOrWhiteSpace(Name))
            return Name.Trim();

        var extracted = TryExtractBaseNameFromExpression(Expression);
        if (!string.IsNullOrWhiteSpace(extracted))
            return extracted.Trim();

        return (Expression ?? string.Empty).Trim();
    }

    public List<string> GetMatchNames()
    {
        var names = new List<string>();
        AddMatchName(names, GetItemName());

        var itemName = GetItemName();
        var commaIndex = itemName.IndexOf(',', StringComparison.Ordinal);
        if (commaIndex > 0)
            AddMatchName(names, itemName[..commaIndex]);

        return names;
    }

    public void NormalizeToBaseNameRule()
    {
        var itemName = GetItemName();
        Name = itemName;
        Expression = BuildBaseNameExpression(itemName);
    }

    public static string BuildBaseNameExpression(string itemName)
    {
        return $"BaseName == \"{EscapeIflString(itemName ?? string.Empty)}\"";
    }

    public static List<FollowerPickUpRule> CreateDefaultRules()
    {
        return new List<FollowerPickUpRule>();
    }

    private static void AddMatchName(List<string> names, string value)
    {
        var normalized = (value ?? string.Empty).Trim();
        if (normalized.Length == 0)
            return;

        if (!names.Any(existing => string.Equals(existing, normalized, StringComparison.OrdinalIgnoreCase)))
            names.Add(normalized);
    }

    private static string EscapeIflString(string value)
    {
        return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    private static string TryExtractBaseNameFromExpression(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return string.Empty;

        var text = expression.Trim();
        const string prefix = "BaseName";
        if (!text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return string.Empty;

        var equalsIndex = text.IndexOf("==", StringComparison.Ordinal);
        if (equalsIndex < 0)
            return string.Empty;

        var value = text[(equalsIndex + 2)..].Trim();
        if (value.Length < 2 || value[0] != '"')
            return string.Empty;

        var chars = new List<char>();
        var escaped = false;
        for (var i = 1; i < value.Length; i++)
        {
            var ch = value[i];
            if (escaped)
            {
                chars.Add(ch);
                escaped = false;
                continue;
            }

            if (ch == '\\')
            {
                escaped = true;
                continue;
            }

            if (ch == '"')
                return new string(chars.ToArray());

            chars.Add(ch);
        }

        return string.Empty;
    }
}

[Submenu(CollapsedByDefault = true)]
public class DebugSettings
{
    [Menu("Debug generate terrain PNG on area change")]
    public ToggleNode DebugGeneratePngOnAreaChange { get; set; } = new ToggleNode(false);

    [Menu("Spike Profiler - write option cost to temp txt")]
    public ToggleNode EnableSpikeProfiler { get; set; } = new ToggleNode(false);

    [Menu("Spike Profiler spike threshold (ms)")]
    public RangeNode<int> SpikeProfilerThresholdMs { get; set; } = new RangeNode<int>(8, 1, 250);

    [Menu("Spike Profiler flush interval (ms)")]
    public RangeNode<int> SpikeProfilerFlushIntervalMs { get; set; } = new RangeNode<int>(1000, 250, 10000);

    [Menu("Spike Profiler log every sample")]
    public ToggleNode SpikeProfilerLogEverySample { get; set; } = new ToggleNode(false);

    [Menu("Spike Profiler directory")]
    public TextNode SpikeProfilerDirectory { get; set; } = new TextNode(Path.Combine(Path.GetTempPath(), "Follower-SpikeProfiler"));

    [Menu("Debug TradeDump Accept click to txt")]
    public ToggleNode DebugTradeDumpAcceptToTxt { get; set; } = new ToggleNode(false);

    [Menu("Debug AutoParty scanner to txt")]
    public ToggleNode DebugAutoPartyScannerToTxt { get; set; } = new ToggleNode(false);

    [Menu("Debug AutoParty reactions only to txt")]
    public ToggleNode DebugAutoPartyReactionsToTxt { get; set; } = new ToggleNode(false);

    [Menu("AutoParty debug directory")]
    public TextNode AutoPartyDebugDirectory { get; set; } = new TextNode(Path.Combine(Path.GetTempPath(), "FollowerDebug"));

    [Menu("AutoParty hover/window context dump to txt")]
    public ToggleNode DebugAutoPartyHoverContextToTxt { get; set; } = new ToggleNode(false);

    [Menu("AutoParty hover/window context dump hotkey")]
    public HotkeyNode AutoPartyHoverContextDumpHotkey { get; set; } = Keys.F10;

    [Menu("AutoParty hover context max depth")]
    public RangeNode<int> AutoPartyHoverContextMaxDepth { get; set; } = new RangeNode<int>(8, 3, 20);

    [Menu("AutoParty hover context child limit")]
    public RangeNode<int> AutoPartyHoverContextChildLimit { get; set; } = new RangeNode<int>(300, 20, 1000);

    [Menu("AutoParty debug max nodes per tick")]
    public RangeNode<int> AutoPartyDebugMaxNodesPerTick { get; set; } = new RangeNode<int>(2000, 100, 20000);

    [Menu("AutoParty reaction context child limit")]
    public RangeNode<int> AutoPartyReactionContextChildLimit { get; set; } = new RangeNode<int>(80, 10, 300);

    [Menu("Show runtime overlay")]
    public ToggleNode ShowRuntimeOverlay { get; set; } = new ToggleNode(false);

    [Menu("Draw path and transitions")]
    public ToggleNode DrawPathAndTransitions { get; set; } = new ToggleNode(false);

    [Menu("Runtime overlay X")]
    public RangeNode<int> RuntimeOverlayX { get; set; } = new RangeNode<int>(500, 0, 4000);

    [Menu("Runtime overlay Y")]
    public RangeNode<int> RuntimeOverlayY { get; set; } = new RangeNode<int>(120, 0, 2500);
}
