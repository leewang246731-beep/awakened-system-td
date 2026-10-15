namespace EraCore.Narrative;

/// <summary>
/// The four system personas (T-089, F-109 first slice; full per-persona
/// situation banks are T-126): 毒舌/参谋/沙雕/傲娇. A persona changes
/// WORDING ONLY — never capability (§10.7/§12.1: 人格只改表达不改能力;
/// the capability-equivalence assertion is the T-126 acceptance).
/// </summary>
public enum SystemPersona
{
    /// <summary>参谋 — the neutral advisor voice (default).</summary>
    Advisor = 0,

    /// <summary>毒舌 — snarky.</summary>
    Snarky = 1,

    /// <summary>沙雕 — goofy.</summary>
    Goofy = 2,

    /// <summary>傲娇 — tsundere.</summary>
    Tsundere = 3,
}

/// <summary>
/// One narrative line (T-089, C-24 narrative slice): key + text + speaker,
/// optional persona tag (F-109 weighting), optional sound-cue slot
/// (音效位, the closed <c>ding_*</c> set shared with
/// <see cref="EraCore.Cinematics.CinematicScript.SoundCues"/>) and optional
/// illustration slot (T-152 配图位 <see cref="ImageKey"/>, 31 号 §三——
/// 素材键来自 visual-assets.json story 图批). Pure data — presentation
/// renders it through the existing SystemPanel bubble / cinematic subtitle
/// precedents; the record itself performs no I/O.
/// </summary>
/// <param name="Key">Stable content key (chapter-scoped, e.g. <c>e1.act1.system</c>).</param>
/// <param name="Speaker">Speaker label shown to the player (旁白/系统/零/斥候/使者/工头).</param>
/// <param name="Text">The line text (already resolved — templates live in <see cref="SystemLineLibrary"/>).</param>
/// <param name="Persona">Persona tag for weighted selection; null = untagged (context line).</param>
/// <param name="SoundCue">Optional ding cue slot (ding_info/ding_reward/ding_crisis).</param>
/// <param name="ImageKey">Optional illustration slot (T-152, 31 号 §三：story-*/banner-*/hook-* 素材键；null = 纯文本行).</param>
public sealed record DialogueLine(
    string Key,
    string Speaker,
    string Text,
    SystemPersona? Persona,
    string? SoundCue,
    string? ImageKey = null)
{
    /// <summary>
    /// Presentation-ready bubble text (the SystemPanel subtitle convention:
    /// <c>说话人：内容</c> — same shape as the cinematic subtitle join).
    /// </summary>
    public string BubbleText => $"{Speaker}：{Text}";

    /// <summary>Chinese label of a persona (UI/bubble decoration; pure mapping).</summary>
    public static string PersonaZh(SystemPersona persona) => persona switch
    {
        SystemPersona.Advisor => "参谋",
        SystemPersona.Snarky => "毒舌",
        SystemPersona.Goofy => "沙雕",
        SystemPersona.Tsundere => "傲娇",
        _ => persona.ToString(),
    };

    /// <summary>Strict persona-name mapping used by the content parsers (closed set).</summary>
    public static SystemPersona ParsePersona(string name, string source)
    {
        foreach (SystemPersona persona in Enum.GetValues<SystemPersona>())
        {
            if (string.Equals(persona.Name(), name, StringComparison.Ordinal))
            {
                return persona;
            }
        }

        throw new NarrativeSchemaException(
            $"{source}: 未登记人格 {name}（封闭表 {string.Join("/", PersonaNames())}）");
    }

    /// <summary>The closed persona name list (snake_case, content-facing).</summary>
    public static IReadOnlyList<string> PersonaNames() =>
        Enum.GetValues<SystemPersona>().Select(persona => persona.Name()).ToArray();
}

/// <summary>Extensions keeping persona name mapping in one place.</summary>
internal static class SystemPersonaExtensions
{
    public static string Name(this SystemPersona persona) => persona switch
    {
        SystemPersona.Advisor => "advisor",
        SystemPersona.Snarky => "snarky",
        SystemPersona.Goofy => "goofy",
        SystemPersona.Tsundere => "tsundere",
        _ => persona.ToString(),
    };
}
