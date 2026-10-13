// T2: pure contract — K-4 intent layer, T-022, draft (12-intent enum frozen by 07 correction #19/A11; parser signature rolling with C-15).

namespace EraCore.Contracts.Intents;

/// <summary>
/// A parsed commander intent (K-4): one of the 12 frozen kinds plus filled
/// slots. Slot keys are grammar-defined (region / tower / target / item /
/// lane / …); values are surface strings. Multimodal references (map
/// pointing, §10.5) and alias resolution against the content registry are
/// done by C-15 before planning; missing slots route to clarification.
/// </summary>
/// <param name="Kind">Frozen intent kind.</param>
/// <param name="RawText">The original order text (journal correlation, K-8).</param>
/// <param name="Slots">Filled slot map; keys per grammar, values as surface strings.</param>
public sealed record Intent(
    IntentKind Kind,
    string RawText,
    IReadOnlyDictionary<string, string> Slots);
