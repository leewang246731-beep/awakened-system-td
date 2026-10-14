using System.Text.Json;
using System.Text.Json.Serialization;
using EraCore.IO;
using EraCore.Narrative;

namespace EraCore.Presentation;

/// <summary>
/// Persisted persona choice (T-093, F-122 人格切换 UI, §10.7): which of the
/// four system personas the player picked. The choice only changes
/// expression (台词/音色 weighting), never capability — the store carries
/// the id; the presenters apply it through their own persona seams
/// (<c>NarrativeDirector.SetPersona</c> / timbre resolution).
/// </summary>
public sealed record PersonaSettings
{
    /// <summary>Picked persona id (frozen enum name: advisor/snarky/goofy/tsundere).</summary>
    public required string PersonaId { get; init; }

    /// <summary>Factory default: the advisor voice (参谋).</summary>
    public static PersonaSettings Default { get; } = new() { PersonaId = "advisor" };
}

/// <summary>
/// Pure mapping of the frozen four-persona set (§10.7 毒舌/参谋/沙雕/傲娇):
/// id ↔ <see cref="SystemPersona"/> + zh display labels. Enum display
/// metadata, not balance values (TrustBadgeLabels precedent).
/// </summary>
public static class PersonaChoice
{
    /// <summary>The four frozen ids in enum order.</summary>
    public static readonly IReadOnlyList<string> FrozenIds = new[]
    {
        "advisor", "snarky", "goofy", "tsundere",
    };

    /// <summary>Chinese labels keyed by id（参谋/毒舌/沙雕/傲娇，§10.7）.</summary>
    public static string LabelZh(string personaId) => personaId switch
    {
        "advisor" => "参谋",
        "snarky" => "毒舌",
        "goofy" => "沙雕",
        "tsundere" => "傲娇",
        _ => personaId,
    };

    /// <summary>Parses one id into the enum; null for an unknown id.</summary>
    public static SystemPersona? TryParse(string personaId) => personaId switch
    {
        "advisor" => SystemPersona.Advisor,
        "snarky" => SystemPersona.Snarky,
        "goofy" => SystemPersona.Goofy,
        "tsundere" => SystemPersona.Tsundere,
        _ => null,
    };

    /// <summary>Enum → id (round-trip twin of <see cref="TryParse"/>).</summary>
    public static string IdOf(SystemPersona persona) => persona switch
    {
        SystemPersona.Advisor => "advisor",
        SystemPersona.Snarky => "snarky",
        SystemPersona.Goofy => "goofy",
        _ => "tsundere",
    };
}

/// <summary>
/// D-03 store binding of the persona choice (PanelSettingsStore pattern):
/// one JSON file, tolerant load (corrupt → factory default advisor — a UI
/// preference never blocks startup), atomic write.
/// </summary>
public static class PersonaSettingsStore
{
    /// <summary>File path relative to the injected data root.</summary>
    public const string RelativePath = "settings/persona.json";

    /// <summary>
    /// Loads the choice; missing/corrupt/unknown-persona files yield
    /// <see cref="PersonaSettings.Default"/> (never a startup failure, never
    /// a silently-broken persona id).
    /// </summary>
    /// <exception cref="ArgumentNullException">store is null.</exception>
    public static PersonaSettings Load(IFileStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (!store.Exists(RelativePath))
        {
            return PersonaSettings.Default;
        }

        try
        {
            byte[] bytes = store.ReadAllBytes(RelativePath);
            PersonaSettings? parsed = JsonSerializer.Deserialize(
                bytes, UiContentJsonContext.Default.PersonaSettings);
            if (parsed is null || PersonaChoice.TryParse(parsed.PersonaId) is null)
            {
                return PersonaSettings.Default; // unknown persona id — factory advisor
            }

            return parsed;
        }
        catch (JsonException)
        {
            return PersonaSettings.Default; // corrupt preference — factory default
        }
    }

    /// <summary>Round-trips the choice through the source-generated codec.</summary>
    /// <exception cref="ArgumentNullException">an argument is null.</exception>
    /// <exception cref="ArgumentException">personaId is not a frozen id.</exception>
    public static void Save(IFileStore store, string personaId)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(personaId);
        if (PersonaChoice.TryParse(personaId) is null)
        {
            throw new ArgumentException($"'{personaId}' is not a frozen persona id.", nameof(personaId));
        }

        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(
            new PersonaSettings { PersonaId = personaId }, UiContentJsonContext.Default.PersonaSettings);
        store.WriteAllBytes(RelativePath, bytes);
    }
}
