using System.Text.Json;
using EraCore.Combat;
using EraCore.Contracts.Actions;
using EraCore.Contracts.Content;

namespace EraCore.Scrolls;

/// <summary>
/// C-02 scroll content loader (T-071, F-038): parses one scrolls JSON file
/// (game/content/scrolls/{era}.json — "files are content", CS-2.5) into
/// <see cref="ScrollDefinition"/>s and registers them into the K-2
/// <see cref="IContentRegistry"/> under
/// <see cref="ContentType.Scroll"/>. Every gameplay number comes from the
/// data; the loader only validates shape against the CLOSED primitive
/// vocabularies (condition kinds, effect kinds, verb names — F.1 模组可追加
/// 动作卡、动词不可扩展). Annotation keys prefixed <c>_</c> are ignored
/// (repo-wide convention). Malformed files throw — a red test, never a
/// silent partial load.
/// </summary>
public static class ScrollDataLoader
{
    private static readonly IReadOnlyDictionary<string, ScrollCardKind> CardKinds = new Dictionary<string, ScrollCardKind>(StringComparer.Ordinal)
    {
        ["condition_card"] = ScrollCardKind.ConditionCard,
        ["action_card"] = ScrollCardKind.ActionCard,
    };

    private static readonly IReadOnlyDictionary<string, ScrollConditionKind> ConditionKinds = new Dictionary<string, ScrollConditionKind>(StringComparer.Ordinal)
    {
        ["zone_enemy_count_at_least"] = ScrollConditionKind.ZoneEnemyCountAtLeast,
        ["enemies_remaining_at_most"] = ScrollConditionKind.EnemiesRemainingAtMost,
        ["enemy_in_zone"] = ScrollConditionKind.EnemyInZone,
        ["enemy_hp_below_percent"] = ScrollConditionKind.EnemyHpBelowPercent,
        ["enemy_hp_below"] = ScrollConditionKind.EnemyHpBelow,
        ["wave_is"] = ScrollConditionKind.WaveNumberIs,
        ["wave_at_least"] = ScrollConditionKind.WaveNumberAtLeast,
        ["resource_above"] = ScrollConditionKind.ResourceAbove,
        ["enemy_distance_below"] = ScrollConditionKind.EnemyDistanceToCellBelow,
        ["tower_hp_below_percent"] = ScrollConditionKind.TowerHpBelowPercent,
    };

    private static readonly IReadOnlyDictionary<string, ScrollEffectKind> EffectKinds = new Dictionary<string, ScrollEffectKind>(StringComparer.Ordinal)
    {
        ["zone_damage_bonus"] = ScrollEffectKind.ZoneDamageBonus,
        ["tower_class_attack_speed"] = ScrollEffectKind.TowerClassAttackSpeed,
        ["tower_heal_percent"] = ScrollEffectKind.TowerHealPercent,
        ["support_aura_range_bonus"] = ScrollEffectKind.SupportAuraRangeBonus,
        ["logistics_throughput_multiplier"] = ScrollEffectKind.LogisticsThroughputMultiplier,
        ["tower_energy_cost_multiplier"] = ScrollEffectKind.TowerEnergyCostMultiplier,
    };

    private static readonly IReadOnlyDictionary<string, ScrollTargetScope> Scopes = new Dictionary<string, ScrollTargetScope>(StringComparer.Ordinal)
    {
        ["none"] = ScrollTargetScope.None,
        ["trigger_zone"] = ScrollTargetScope.TriggerZone,
        ["trigger_tower"] = ScrollTargetScope.TriggerTower,
        ["tower_class"] = ScrollTargetScope.TowerClass,
    };

    private static readonly IReadOnlyDictionary<string, ScrollVerbTarget> VerbTargets = new Dictionary<string, ScrollVerbTarget>(StringComparer.Ordinal)
    {
        ["strongest_in_trigger_zone"] = ScrollVerbTarget.StrongestInTriggerZone,
        ["strongest_anywhere"] = ScrollVerbTarget.StrongestAnywhere,
    };

    private static readonly IReadOnlySet<string> TowerModeValues = new HashSet<string>(StringComparer.Ordinal)
    {
        "first", "last", "strong", "closest",
    };

    /// <summary>
    /// Parses a scrolls JSON document into definitions, file order. Full
    /// shape validation per item — any violation throws with the item id in
    /// the message (duplicate ids included).
    /// </summary>
    public static IReadOnlyList<ScrollDefinition> Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new ArgumentException("Scrolls JSON is empty.", nameof(json));
        }

        using JsonDocument document = JsonDocument.Parse(json); // strict: malformed JSON throws (red test)
        JsonElement root = document.RootElement;
        RequireObject(root, "document root");

        string declared = StringOf(root, "content_type", "document");
        if (declared != nameof(ContentType.Scroll))
        {
            throw new InvalidOperationException($"Scrolls document content_type must be '{nameof(ContentType.Scroll)}' (got '{declared}').");
        }

        if (!root.TryGetProperty("items", out JsonElement itemsElement) || itemsElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("Scrolls document lacks an 'items' array.");
        }

        var definitions = new List<ScrollDefinition>();
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonElement item in itemsElement.EnumerateArray())
        {
            ScrollDefinition definition = ParseItem(item);
            if (!seenIds.Add(definition.Id))
            {
                throw new InvalidOperationException($"Duplicate scroll id '{definition.Id}' (same kind + namespace conflicts must surface).");
            }

            definitions.Add(definition);
        }

        return definitions;
    }

    /// <summary>Parses into K-2 registry entries sorted by id (deterministic registration order, IContentRegistry contract).</summary>
    public static IReadOnlyList<ScrollContentItem> ParseItems(string json) =>
        Parse(json).OrderBy(static d => d.Id, StringComparer.Ordinal)
            .Select(static d => new ScrollContentItem(d))
            .ToList();

    /// <summary>
    /// Parses and registers every scroll into the K-2 registry (sorted by
    /// id). The registry itself still rejects duplicate registrations —
    /// this only feeds the same pipeline first-party content uses.
    /// </summary>
    public static void RegisterInto(IContentRegistry registry, string json)
    {
        ArgumentNullException.ThrowIfNull(registry);
        foreach (ScrollContentItem item in ParseItems(json))
        {
            registry.Register(item);
        }
    }

    // ------------------------------------------------------- item parsing

    private static ScrollDefinition ParseItem(JsonElement item)
    {
        RequireObject(item, "item");
        string id = StringOf(item, "id", "item");
        string spId = StringOf(item, "sp_id", id);
        string kindText = StringOf(item, "kind", id);
        if (!CardKinds.TryGetValue(kindText, out ScrollCardKind kind))
        {
            throw new InvalidOperationException($"Scroll '{id}': unknown kind '{kindText}' (condition_card|action_card).");
        }

        string itemContentType = StringOf(item, "content_type", id);
        if (itemContentType != nameof(ContentType.Scroll))
        {
            throw new InvalidOperationException($"Scroll '{id}': content_type must be '{nameof(ContentType.Scroll)}' (got '{itemContentType}').");
        }

        string nameZh = StringOf(item, "name_zh", id);
        string nameEn = StringOf(item, "name_en", id);
        var aliases = new List<string>();
        if (item.TryGetProperty("aliases", out JsonElement aliasesElement))
        {
            foreach (JsonElement alias in aliasesElement.EnumerateArray())
            {
                aliases.Add(alias.GetString()!);
            }
        }

        int priority = IntOf(item, "priority", id, min: 0);
        int cooldownTicks = IntOf(item, "cooldown_ticks", id, min: 0);
        int unlockEra = IntOf(item, "unlock_era", id, min: 1, max: 6);

        ScrollCondition? condition = null;
        bool hasCondition = item.TryGetProperty("condition", out JsonElement conditionElement)
            && conditionElement.ValueKind == JsonValueKind.Object;
        if (hasCondition)
        {
            condition = ParseCondition(conditionElement, id);
        }

        if (kind == ScrollCardKind.ConditionCard && condition is null)
        {
            throw new InvalidOperationException($"Scroll '{id}': condition_card requires a 'condition' object.");
        }

        if (kind == ScrollCardKind.ActionCard && hasCondition)
        {
            throw new InvalidOperationException($"Scroll '{id}': action_card must not carry a condition (TriggerScroll lane fires it).");
        }

        if (!item.TryGetProperty("action", out JsonElement actionElement) || actionElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException($"Scroll '{id}': lacks an 'action' object.");
        }

        ScrollAction action = ParseAction(actionElement, id, kind);

        int perTowerPerWaveLimit = 0;
        if (item.TryGetProperty("per_tower_per_wave_limit", out JsonElement limitElement))
        {
            perTowerPerWaveLimit = IntOf(item, "per_tower_per_wave_limit", id, min: 0);
        }

        if (!item.TryGetProperty("acquisition", out JsonElement acquisitionElement)
            || acquisitionElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException($"Scroll '{id}': lacks an 'acquisition' object (SP-5 获取途径列必填).");
        }

        string acquisitionKind = StringOf(acquisitionElement, "kind", $"{id}.acquisition");
        string acquisitionRef = StringOf(acquisitionElement, "ref", $"{id}.acquisition");

        return new ScrollDefinition(
            id, spId, kind, nameZh, nameEn, aliases, priority, cooldownTicks, unlockEra,
            condition, action, perTowerPerWaveLimit, new ScrollAcquisition(acquisitionKind, acquisitionRef));
    }

    private static ScrollCondition ParseCondition(JsonElement element, string id)
    {
        string kindText = StringOf(element, "kind", $"{id}.condition");
        if (!ConditionKinds.TryGetValue(kindText, out ScrollConditionKind kind))
        {
            throw new InvalidOperationException(
                $"Scroll '{id}': unknown condition kind '{kindText}' (closed vocabulary — {string.Join('|', ConditionKinds.Keys)}).");
        }

        var args = new List<ScrollConditionArg>();
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (property.Name == "kind" || property.Name.StartsWith("_", StringComparison.Ordinal))
            {
                continue;
            }

            switch (property.Value.ValueKind)
            {
                case JsonValueKind.Number:
                    args.Add(new ScrollConditionArg(property.Name, property.Value.GetDouble(), null));
                    break;
                case JsonValueKind.String:
                    args.Add(new ScrollConditionArg(property.Name, 0, property.Value.GetString()));
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Scroll '{id}': condition arg '{property.Name}' must be a number or string.");
            }
        }

        ValidateConditionArgs(id, kind, args);
        return new ScrollCondition(kind, args);
    }

    private static void ValidateConditionArgs(string id, ScrollConditionKind kind, List<ScrollConditionArg> args)
    {
        void RequireNumber(string name)
        {
            if (args.All(a => a.Name != name || a.Text is not null))
            {
                throw new InvalidOperationException($"Scroll '{id}': condition {kind} requires numeric arg '{name}'.");
            }
        }

        void RequireText(string name)
        {
            if (args.All(a => a.Name != name || a.Text is null))
            {
                throw new InvalidOperationException($"Scroll '{id}': condition {kind} requires text arg '{name}'.");
            }
        }

        switch (kind)
        {
            case ScrollConditionKind.ZoneEnemyCountAtLeast:
                RequireText("zone");
                RequireNumber("count");
                break;
            case ScrollConditionKind.EnemiesRemainingAtMost:
                RequireNumber("count");
                if (args.All(a => a.Name != "zone"))
                {
                    args.Add(new ScrollConditionArg("zone", 0, "*")); // whole-map default (波内剩敌)
                }

                break;
            case ScrollConditionKind.EnemyInZone:
                RequireText("zone");
                break;
            case ScrollConditionKind.EnemyHpBelowPercent:
                RequireNumber("percent");
                break;
            case ScrollConditionKind.EnemyHpBelow:
                RequireNumber("hp");
                break;
            case ScrollConditionKind.WaveNumberIs:
            case ScrollConditionKind.WaveNumberAtLeast:
                RequireNumber("wave");
                break;
            case ScrollConditionKind.ResourceAbove:
                RequireText("resource");
                RequireNumber("amount");
                break;
            case ScrollConditionKind.EnemyDistanceToCellBelow:
                RequireNumber("x");
                RequireNumber("y");
                RequireNumber("cells");
                break;
            case ScrollConditionKind.TowerHpBelowPercent:
                RequireNumber("percent");
                break;
            default:
                throw new InvalidOperationException($"Condition kind {kind} has no validation arm.");
        }
    }

    private static ScrollAction ParseAction(JsonElement element, string id, ScrollCardKind kind)
    {
        string? verb = null;
        if (element.TryGetProperty("verb", out JsonElement verbElement))
        {
            verb = verbElement.GetString();
            if (verb is not null && !ScrollEngine.DispatchableVerbs.Contains(verb))
            {
                throw new InvalidOperationException(
                    $"Scroll '{id}': verb '{verb}' is outside the scroll dispatchable set ({string.Join('|', ScrollEngine.DispatchableVerbs)}) — 动词不可扩展 (F.1).");
            }
        }

        string? verbMode = element.TryGetProperty("verb_mode", out JsonElement modeElement)
            ? modeElement.GetString()
            : null;
        if (verbMode is not null && !TowerModeValues.Contains(verbMode))
        {
            throw new InvalidOperationException($"Scroll '{id}': verb_mode '{verbMode}' is not a K-3 SetTowerMode value.");
        }

        ScrollVerbTarget? verbTarget = null;
        if (element.TryGetProperty("verb_target", out JsonElement targetElement))
        {
            string targetText = targetElement.GetString()!;
            if (!VerbTargets.TryGetValue(targetText, out ScrollVerbTarget parsed))
            {
                throw new InvalidOperationException(
                    $"Scroll '{id}': unknown verb_target '{targetText}' ({string.Join('|', VerbTargets.Keys)}).");
            }

            verbTarget = parsed;
        }

        ScrollEffectKind? effect = null;
        double magnitude = 0;
        int durationTicks = 0;
        bool untilWaveEnd = false;
        if (element.TryGetProperty("effect", out JsonElement effectElement)
            && effectElement.ValueKind == JsonValueKind.String)
        {
            string effectText = effectElement.GetString()!;
            if (!EffectKinds.TryGetValue(effectText, out ScrollEffectKind parsed))
            {
                throw new InvalidOperationException(
                    $"Scroll '{id}': unknown effect '{effectText}' (closed vocabulary — {string.Join('|', EffectKinds.Keys)}).");
            }

            effect = parsed;
            magnitude = NumberOf(element, "magnitude", $"{id}.action");
            durationTicks = element.TryGetProperty("duration_ticks", out JsonElement durationElement)
                ? (int)durationElement.GetDouble()
                : 0;
            untilWaveEnd = element.TryGetProperty("until_wave_end", out JsonElement untilElement)
                && untilElement.ValueKind == JsonValueKind.True;
            if (durationTicks < 0)
            {
                throw new InvalidOperationException($"Scroll '{id}': duration_ticks must be >= 0.");
            }
        }

        string scopeText = StringOf(element, "scope", $"{id}.action");
        if (!Scopes.TryGetValue(scopeText, out ScrollTargetScope scope))
        {
            throw new InvalidOperationException(
                $"Scroll '{id}': unknown scope '{scopeText}' ({string.Join('|', Scopes.Keys)}).");
        }

        if (kind == ScrollCardKind.ActionCard
            && scope is ScrollTargetScope.TriggerZone or ScrollTargetScope.TriggerTower)
        {
            throw new InvalidOperationException(
                $"Scroll '{id}': action_card cannot use trigger scope '{scopeText}' (no condition supplies the trigger).");
        }

        TowerClass? towerClass = null;
        if (element.TryGetProperty("tower_class", out JsonElement classElement))
        {
            towerClass = ParseTowerClass(classElement.GetString()!, id);
        }

        if (scope == ScrollTargetScope.TowerClass && towerClass is null)
        {
            throw new InvalidOperationException($"Scroll '{id}': scope 'tower_class' requires a 'tower_class' value.");
        }

        if (verb == nameof(SystemActionKind.FocusFire) && verbTarget is null)
        {
            throw new InvalidOperationException($"Scroll '{id}': FocusFire needs a 'verb_target' selector.");
        }

        if (verb == nameof(SystemActionKind.SetTowerMode) && verbMode is null)
        {
            throw new InvalidOperationException($"Scroll '{id}': SetTowerMode needs a 'verb_mode' value.");
        }

        if (verb is null && effect is null)
        {
            throw new InvalidOperationException($"Scroll '{id}': action needs a verb and/or an effect.");
        }

        return new ScrollAction(verb, verbMode, verbTarget, effect, magnitude, durationTicks, untilWaveEnd, scope, towerClass);
    }

    private static TowerClass ParseTowerClass(string text, string id)
    {
        if (!Enum.TryParse<TowerClass>(text, ignoreCase: true, out TowerClass towerClass)
            || !Enum.IsDefined(towerClass))
        {
            throw new InvalidOperationException(
                $"Scroll '{id}': unknown tower_class '{text}' (six SP-1a classes only).");
        }

        return towerClass;
    }

    // ------------------------------------------------------- JSON helpers

    private static void RequireObject(JsonElement element, string what)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException($"Scrolls {what} must be a JSON object.");
        }
    }

    private static string StringOf(JsonElement parent, string name, string context)
    {
        if (!parent.TryGetProperty(name, out JsonElement value)
            || value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new InvalidOperationException($"Scrolls '{context}': missing/non-string field '{name}'.");
        }

        return value.GetString()!;
    }

    private static double NumberOf(JsonElement parent, string name, string context)
    {
        if (!parent.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.Number)
        {
            throw new InvalidOperationException($"Scrolls '{context}': missing/non-number field '{name}'.");
        }

        return value.GetDouble();
    }

    private static int IntOf(JsonElement parent, string name, string context, int min, int max = int.MaxValue)
    {
        double value = NumberOf(parent, name, context);
        if (value < min || value > max || value != Math.Floor(value))
        {
            throw new InvalidOperationException(
                $"Scrolls '{context}': field '{name}' must be an integer in [{min}, {max}] (got {value}).");
        }

        return (int)value;
    }
}
