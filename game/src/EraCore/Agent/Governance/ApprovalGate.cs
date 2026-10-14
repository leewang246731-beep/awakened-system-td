using System.Text.Json;
using EraCore.Agent.Actions;
using EraCore.Contracts.Agent;
using EraCore.Contracts.Actions;

namespace EraCore.Agent.Governance;

/// <summary>
/// How a danger bit (F.4 危险位) shapes the verdict of the verb carrying it.
/// </summary>
public enum DangerBitMode
{
    /// <summary>
    /// The bit must be explicitly true, else the request is DENIED
    /// outright (SellTower.confirm_sell: no confirmation, no sale).
    /// When true the verb still asks — it can never be auto-allowed.
    /// </summary>
    RequireTrueElseDeny,

    /// <summary>
    /// When the bit is true the verb always ASKS — even under a level
    /// auto-approve whitelist (BuildPlan.confirm_overwrite: demolishing
    /// existing buildings never runs silently). Absent/false is normal.
    /// </summary>
    ForceAskWhenTrue,
}

/// <summary>
/// One danger-bit rule: a JSON property on one verb's action payload
/// (F.4). Pure data — later eras add rows (遗迹与纪念馆拆除) without
/// touching gate logic.
/// </summary>
/// <param name="Verb">The closed verb carrying the bit.</param>
/// <param name="Property">The action-JSON property name of the bit.</param>
/// <param name="Mode">Refusal/ask semantics of the bit.</param>
public sealed record DangerBitRule(SystemActionKind Verb, string Property, DangerBitMode Mode);

/// <summary>
/// Stage-4 approval gate (T-050, C-14 / feature F-063; implements the
/// frozen K-5 <see cref="IApprovalGate"/>): decides allow/ask/deny per
/// the F.4 permission matrix. Decision pipeline per plan step, first
/// failure wins:
/// <list type="number">
/// <item>Capability lock: verb unlock era &gt; current era → Deny
/// (the frozen K-5 contract lists locked capability as a Deny cause;
/// the C-03 registry answers ErrCapabilityLocked with clarifying copy at
/// execution time).</item>
/// <item>Danger bits (fail closed): a bit whose payload is unreadable →
/// Deny; RequireTrueElseDeny bit absent/false → Deny; a true bit forces
/// Ask — level auto-approve never covers danger bits (危险位永不自动允许).</item>
/// <item>Tier: tariff Allow → Allow; tariff Ask → Allow only under the
/// level-scoped "本关卡内自动允许" whitelist, else Ask.</item>
/// </list>
/// Plan aggregation: any Deny → Deny; else any Ask → Ask; else Allow.
/// Trust tiers never feed this gate (F.4: 信任只调制主动性，不影响功能 —
/// wording modulation is the presentation layer's job, C-22).
/// </summary>
public sealed class ApprovalGate : IApprovalGate
{
    /// <summary>
    /// Frozen danger-bit rules (F.4). E1 rows: SellTower.confirm_sell
    /// (explicit confirmation, never auto-allowed) and
    /// BuildPlan.confirm_overwrite (demolition flag, always asks).
    /// Later eras append rows (遗迹与纪念馆拆除) — the gate logic is
    /// rule-driven, no per-verb branches.
    /// </summary>
    public static readonly IReadOnlyList<DangerBitRule> DangerBits = new DangerBitRule[]
    {
        new(SystemActionKind.SellTower, "confirm_sell", DangerBitMode.RequireTrueElseDeny),
        new(SystemActionKind.BuildPlan, "confirm_overwrite", DangerBitMode.ForceAskWhenTrue),
    };

    private static readonly IReadOnlyDictionary<SystemActionKind, IReadOnlyList<DangerBitRule>> DangerBitsByVerb =
        DangerBits.GroupBy(rule => rule.Verb)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<DangerBitRule>)group.ToArray());

    private readonly HashSet<SystemActionKind> _autoApproved = new();

    /// <summary>
    /// Creates a gate anchored at the given era (capability-lock floor).
    /// </summary>
    /// <param name="eraIndex">Era index 1..6.</param>
    public ApprovalGate(int eraIndex)
    {
        EraIndex = Guard.EnsureInRange(eraIndex, 1, 6, nameof(eraIndex));
    }

    /// <summary>Current era index; change only via <see cref="SetEra"/> (era jump keeps the level whitelist — it is saved per level, not per era).</summary>
    public int EraIndex { get; private set; }

    /// <summary>Whether a verb is on the level-scoped auto-approve whitelist.</summary>
    public bool IsAutoApproved(SystemActionKind verb) => _autoApproved.Contains(verb);

    /// <summary>
    /// Era transition: re-anchors the capability-lock floor. The level
    /// auto-approve memory persists across the jump (关卡级语义).
    /// </summary>
    public void SetEra(int eraIndex)
    {
        EraIndex = Guard.EnsureInRange(eraIndex, 1, 6, nameof(eraIndex));
    }

    /// <summary>
    /// Sets/clears the "本关卡内自动允许" flag of one verb (player checkbox,
    /// persisted with the level save, v3.2 §11.4).
    /// </summary>
    /// <param name="verb">An Ask-tier verb without a never-auto rule.</param>
    /// <param name="enabled">true = whitelist, false = remove.</param>
    /// <exception cref="ArgumentException">
    /// The verb may never be auto-approved (SellTower — F.4 危险位永不
    /// 自动允许) or is already Allow-tier (nothing to whitelist).</exception>
    public void SetLevelAutoApprove(SystemActionKind verb, bool enabled)
    {
        var tariff = SystemActionTariffTable.Get(verb);
        if (enabled && tariff.NeverAutoApprove)
        {
            throw new ArgumentException(
                $"{verb} may never enter an auto-approve whitelist (F.4 危险位永不自动允许).",
                nameof(verb));
        }

        if (enabled && tariff.DefaultTier == ActionPermissionTier.Allow)
        {
            throw new ArgumentException(
                $"{verb} is Allow-tier already; auto-approve whitelists apply to Ask-tier verbs only.",
                nameof(verb));
        }

        if (enabled)
        {
            _autoApproved.Add(verb);
        }
        else
        {
            _autoApproved.Remove(verb);
        }
    }

    /// <summary>K-5 stage-4 verdict of a whole plan (see class remarks for the per-step pipeline).</summary>
    /// <exception cref="ArgumentNullException">plan is null.</exception>
    public ApprovalDecision Decide(Plan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        ApprovalDecisionKind worst = ApprovalDecisionKind.Allow;
        string? reason = null;

        foreach (var step in plan.Steps)
        {
            var decision = DecideStep(step.Action, step.ActionJson);
            if (decision.Kind == ApprovalDecisionKind.Deny)
            {
                return decision;
            }

            if (decision.Kind == ApprovalDecisionKind.Ask && worst == ApprovalDecisionKind.Allow)
            {
                worst = ApprovalDecisionKind.Ask;
                reason = decision.Reason;
            }
        }

        return new ApprovalDecision(worst, reason);
    }

    /// <summary>Verdict of one plan step (public for the pipeline's step-level retry loop, C-13).</summary>
    public ApprovalDecision DecideStep(SystemActionKind verb, string actionJson)
    {
        var tariff = SystemActionTariffTable.Get(verb);

        if (EraIndex < tariff.UnlockEra)
        {
            return new ApprovalDecision(
                ApprovalDecisionKind.Deny,
                $"capability_locked:{verb}(era{EraIndex}<{tariff.UnlockEra})");
        }

        bool dangerForcedAsk = false;
        if (DangerBitsByVerb.TryGetValue(verb, out var rules))
        {
            if (!TryReadDangerFlags(actionJson, rules, out var flags))
            {
                return new ApprovalDecision(
                    ApprovalDecisionKind.Deny, $"danger_bit_unreadable:{verb}");
            }

            foreach (var (rule, value) in flags)
            {
                if (rule.Mode == DangerBitMode.RequireTrueElseDeny && !value)
                {
                    return new ApprovalDecision(
                        ApprovalDecisionKind.Deny, $"danger_bit_required:{rule.Property}");
                }

                if (rule.Mode == DangerBitMode.ForceAskWhenTrue && value)
                {
                    dangerForcedAsk = true;
                }
            }

            // A satisfied RequireTrueElseDeny bit (explicit confirmation)
            // also pins the verb at Ask — confirmation semantics mean the
            // player sees the card.
            if (flags.Any(pair => pair.Rule.Mode == DangerBitMode.RequireTrueElseDeny))
            {
                dangerForcedAsk = true;
            }
        }

        if (tariff.DefaultTier == ActionPermissionTier.Allow && !dangerForcedAsk)
        {
            return new ApprovalDecision(ApprovalDecisionKind.Allow);
        }

        if (dangerForcedAsk)
        {
            return new ApprovalDecision(
                ApprovalDecisionKind.Ask, $"danger_bit_never_auto:{verb}");
        }

        if (_autoApproved.Contains(verb) && !tariff.NeverAutoApprove)
        {
            return new ApprovalDecision(ApprovalDecisionKind.Allow);
        }

        return new ApprovalDecision(ApprovalDecisionKind.Ask, $"tier_ask:{verb}");
    }

    /// <summary>Exports the level-scoped session (whitelist + era) for the level save.</summary>
    public ApprovalSessionState ExportSession()
    {
        return new ApprovalSessionState(
            EraIndex,
            _autoApproved.Select(verb => verb.ToString()).OrderBy(name => name, StringComparer.Ordinal).ToArray());
    }

    /// <summary>
    /// Restores a session saved by <see cref="ExportSession"/> (随关卡存档).
    /// </summary>
    /// <param name="state">Saved state (typically round-tripped through <see cref="ApprovalSessionState.Save"/>).</param>
    /// <exception cref="ArgumentException">Hostile/corrupt payload: era out of range, unknown verb name, or a never-auto verb riding in the whitelist.</exception>
    public void RestoreSession(ApprovalSessionState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        Guard.EnsureInRange(state.EraIndex, 1, 6, nameof(state.EraIndex));

        var parsed = new HashSet<SystemActionKind>();
        foreach (var name in state.AutoApprovedVerbs)
        {
            if (!Enum.TryParse<SystemActionKind>(name, out var verb)
                || !Enum.IsDefined(verb))
            {
                throw new ArgumentException($"Unknown verb name '{name}' in saved approval session.", nameof(state));
            }

            if (SystemActionTariffTable.Get(verb).NeverAutoApprove)
            {
                throw new ArgumentException(
                    $"Saved session tried to auto-approve {verb} (F.4 危险位永不自动允许).", nameof(state));
            }

            parsed.Add(verb);
        }

        EraIndex = state.EraIndex;
        _autoApproved.Clear();
        _autoApproved.UnionWith(parsed);
    }

    private static bool TryReadDangerFlags(
        string actionJson,
        IReadOnlyList<DangerBitRule> rules,
        out IReadOnlyList<(DangerBitRule Rule, bool Value)> flags)
    {
        flags = Array.Empty<(DangerBitRule, bool)>();
        try
        {
            using var doc = JsonDocument.Parse(actionJson ?? string.Empty);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var result = new List<(DangerBitRule, bool)>(rules.Count);
            foreach (var rule in rules)
            {
                bool value = doc.RootElement.TryGetProperty(rule.Property, out var element)
                    && element.ValueKind == JsonValueKind.True;
                result.Add((rule, value));
            }

            flags = result;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
