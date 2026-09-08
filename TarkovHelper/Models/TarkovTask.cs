using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace TarkovHelper.Models
{
    /// <summary>
    /// Task data merged from tarkov.dev API with multilingual support
    /// </summary>
    public class TarkovTask
    {
        /// <summary>
        /// tarkov.dev Task IDs (can be multiple for aliased/variant tasks)
        /// Null for wiki-only quests that don't exist in tarkov.dev API
        /// </summary>
        [JsonPropertyName("ids")]
        public List<string>? Ids { get; set; } = new();

        /// <summary>
        /// English task name
        /// </summary>
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Korean task name (null if no translation available)
        /// </summary>
        [JsonPropertyName("nameKo")]
        public string? NameKo { get; set; }

        /// <summary>
        /// Japanese task name (null if no translation available)
        /// </summary>
        [JsonPropertyName("nameJa")]
        public string? NameJa { get; set; }

        /// <summary>
        /// Whether this task is required for Kappa container
        /// </summary>
        [JsonPropertyName("reqKappa")]
        public bool ReqKappa { get; set; }

        /// <summary>
        /// Trader who gives this task
        /// </summary>
        [JsonPropertyName("trader")]
        public string Trader { get; set; } = string.Empty;

        /// <summary>
        /// Maps where this quest takes place (normalized names)
        /// </summary>
        [JsonPropertyName("maps")]
        public List<string>? Maps { get; set; }

        /// <summary>
        /// Normalized name (URL-friendly format)
        /// Null for wiki-only quests that don't exist in tarkov.dev API
        /// </summary>
        [JsonPropertyName("normalizedName")]
        public string? NormalizedName { get; set; }

        /// <summary>
        /// Prerequisite quests (normalized names)
        /// </summary>
        [JsonPropertyName("previous")]
        public List<string>? Previous { get; set; }

        /// <summary>
        /// Follow-up quests (normalized names)
        /// </summary>
        [JsonPropertyName("leadsTo")]
        public List<string>? LeadsTo { get; set; }

        /// <summary>
        /// Required player level to start this quest
        /// </summary>
        [JsonPropertyName("requiredLevel")]
        public int? RequiredLevel { get; set; }

        /// <summary>
        /// Required Scav Karma (Fence reputation) for this quest
        /// Positive value means >= requirement, negative means <= requirement
        /// </summary>
        [JsonPropertyName("requiredScavKarma")]
        public double? RequiredScavKarma { get; set; }

        /// <summary>
        /// Required skills for this quest
        /// </summary>
        [JsonPropertyName("requiredSkills")]
        public List<SkillRequirement>? RequiredSkills { get; set; }

        /// <summary>
        /// Required items for this quest
        /// </summary>
        [JsonPropertyName("requiredItems")]
        public List<QuestItem>? RequiredItems { get; set; }

        /// <summary>
        /// Quest objectives from wiki (list of objectives to complete)
        /// </summary>
        [JsonPropertyName("objectives")]
        public List<string>? Objectives { get; set; }

        /// <summary>
        /// Guide text from wiki (description of how to complete the quest)
        /// </summary>
        [JsonPropertyName("guideText")]
        public string? GuideText { get; set; }

        /// <summary>
        /// Guide images from wiki gallery (file names)
        /// </summary>
        [JsonPropertyName("guideImages")]
        public List<GuideImage>? GuideImages { get; set; }

        /// <summary>
        /// Required faction for this quest (bear, usec, or null for any)
        /// </summary>
        [JsonPropertyName("faction")]
        public string? Faction { get; set; }

        /// <summary>
        /// Required game edition for this quest (e.g., "eod", "unheard")
        /// </summary>
        [JsonPropertyName("requiredEdition")]
        public string? RequiredEdition { get; set; }

        /// <summary>
        /// Excluded game edition for this quest (e.g., "unheard")
        /// </summary>
        [JsonPropertyName("excludedEdition")]
        public string? ExcludedEdition { get; set; }

        /// <summary>
        /// Required prestige level for this quest (0-6; patch 1.1 added prestige 5 and 6)
        /// </summary>
        [JsonPropertyName("requiredPrestigeLevel")]
        public int? RequiredPrestigeLevel { get; set; }

        /// <summary>
        /// Required DSP decode count for this quest (for Make Amends quest branches)
        /// </summary>
        [JsonPropertyName("requiredDecodeCount")]
        public int? RequiredDecodeCount { get; set; }

        /// <summary>
        /// Alternative quests (mutually exclusive - completing one fails the others)
        /// These are "Other choices" quests from wiki's |related field
        /// </summary>
        [JsonPropertyName("alternativeQuests")]
        public List<string>? AlternativeQuests { get; set; }

        /// <summary>
        /// Whether this quest is a mutually exclusive choice (completing one of the
        /// alternatives fails the others). The single definition shared by the
        /// status engine, the completion-cascade core, and the sync paths.
        /// </summary>
        [JsonIgnore]
        public bool HasAlternatives => AlternativeQuests is { Count: > 0 };

        /// <summary>
        /// Trader loyalty levels this quest requires, one entry per trader it names. Null or
        /// empty means the quest has no loyalty gate.
        /// <para>
        /// Usually one entry naming the quest's own <see cref="Trader"/>, but not always: five
        /// quests in the 1.1 data gate on a trader other than the one who gives them (Chemical -
        /// Part 3 is Skier's and needs Jaeger 2), and Collector names seven traders at once. So
        /// this is a list rather than a level on the task, and the gate reads every entry.
        /// </para>
        /// </summary>
        [JsonPropertyName("traderLoyaltyRequirements")]
        public List<QuestTraderRequirement>? TraderLoyaltyRequirements { get; set; }

        /// <summary>
        /// Whether this quest is gated on any trader's loyalty level. The single definition
        /// shared by the status engine, the badge and the detail pane, like
        /// <see cref="HasAlternatives"/>.
        /// </summary>
        [JsonIgnore]
        public bool HasTraderLoyaltyRequirements => TraderLoyaltyRequirements is { Count: > 0 };

        /// <summary>
        /// Task requirements with status conditions from tarkov.dev API
        /// Each requirement specifies which status(es) the prerequisite task must have
        /// </summary>
        [JsonPropertyName("taskRequirements")]
        public List<TaskRequirement>? TaskRequirements { get; set; }

        /// <summary>
        /// Tarkov Market internal quest UID (UUID)
        /// Used for matching with quest markers from Tarkov Market API
        /// </summary>
        [JsonPropertyName("tarkovMarketQuestUid")]
        public string? TarkovMarketQuestUid { get; set; }

        /// <summary>
        /// Wiki page link for this quest
        /// </summary>
        [JsonPropertyName("wikiPageLink")]
        public string? WikiPageLink { get; set; }
    }

    /// <summary>
    /// One trader loyalty level a quest requires, as published in QuestTraderRequirements.
    /// Mirrors <see cref="HideoutTraderRequirement"/>, the sibling shape on the hideout side.
    /// </summary>
    public class QuestTraderRequirement
    {
        /// <summary>
        /// tarkov.dev trader id. The identifier everything compares on: the entered level is
        /// stored under it and the gate looks it up by it, because a nickname can change upstream
        /// while the id cannot.
        /// </summary>
        [JsonPropertyName("traderId")]
        public string TraderId { get; set; } = string.Empty;

        /// <summary>
        /// The trader's English nickname as the row carries it. Display fallback only, for when
        /// the Traders table has no row for <see cref="TraderId"/>; it is also what the badge
        /// compares against <see cref="TarkovTask.Trader"/> to decide whether the quest's own
        /// trader is the one being asked for.
        /// </summary>
        [JsonPropertyName("traderName")]
        public string TraderName { get; set; } = string.Empty;

        /// <summary>Loyalty level required, 2 to 4 in the published data.</summary>
        [JsonPropertyName("level")]
        public int Level { get; set; }
    }

    /// <summary>
    /// Task requirement with status condition
    /// </summary>
    public class TaskRequirement
    {
        /// <summary>
        /// Database ID of the required task (primary identifier)
        /// </summary>
        [JsonPropertyName("taskId")]
        public string TaskId { get; set; } = string.Empty;

        /// <summary>
        /// Normalized name of the required task (deprecated, kept for backwards compatibility)
        /// </summary>
        [JsonPropertyName("taskNormalizedName")]
        public string TaskNormalizedName { get; set; } = string.Empty;

        /// <summary>
        /// Required status(es) for the task: "active", "complete", or both
        /// If "active" is included, the prerequisite only needs to be started (not completed)
        /// </summary>
        [JsonPropertyName("status")]
        public List<string>? Status { get; set; }

        /// <summary>
        /// OR group ID for prerequisite requirements.
        /// GroupId = 0: AND condition (must be completed)
        /// GroupId > 0: OR condition (any one in the same group must be satisfied)
        /// </summary>
        [JsonPropertyName("groupId")]
        public int GroupId { get; set; }
    }

    /// <summary>
    /// Image from wiki guide gallery
    /// </summary>
    public class GuideImage
    {
        /// <summary>
        /// Wiki file name (e.g., "Delivery from the past Customs.png")
        /// </summary>
        [JsonPropertyName("fileName")]
        public string FileName { get; set; } = string.Empty;

        /// <summary>
        /// Caption for the image
        /// </summary>
        [JsonPropertyName("caption")]
        public string? Caption { get; set; }
    }

    /// <summary>
    /// Skill requirement for a quest
    /// </summary>
    public class SkillRequirement
    {
        /// <summary>
        /// Skill normalized name for lookup in skills.json
        /// </summary>
        [JsonPropertyName("skillNormalizedName")]
        public string SkillNormalizedName { get; set; } = string.Empty;

        /// <summary>
        /// Required skill level
        /// </summary>
        [JsonPropertyName("level")]
        public int Level { get; set; }
    }

    /// <summary>
    /// Item requirement for a quest
    /// </summary>
    public class QuestItem
    {
        /// <summary>
        /// Item normalized name for lookup in items.json
        /// </summary>
        [JsonPropertyName("itemNormalizedName")]
        public string ItemNormalizedName { get; set; } = string.Empty;

        /// <summary>
        /// Display name for the item (original name from database).
        /// Used as fallback when item lookup fails.
        /// </summary>
        [JsonPropertyName("itemDisplayName")]
        public string? ItemDisplayName { get; set; }

        /// <summary>
        /// Required quantity
        /// </summary>
        [JsonPropertyName("amount")]
        public int Amount { get; set; }

        /// <summary>
        /// Requirement type: "Handover", "Required", "Optional"
        /// </summary>
        [JsonPropertyName("requirement")]
        public string Requirement { get; set; } = string.Empty;

        /// <summary>
        /// Whether the item must be Found in Raid
        /// </summary>
        [JsonPropertyName("foundInRaid")]
        public bool FoundInRaid { get; set; }

        /// <summary>
        /// Minimum dogtag level required (for dogtag items only)
        /// Null means no level restriction
        /// </summary>
        [JsonPropertyName("dogtagMinLevel")]
        public int? DogtagMinLevel { get; set; }
    }
}
