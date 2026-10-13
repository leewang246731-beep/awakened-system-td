// T2: pure contract — K-4 intent layer, T-022, draft (12-intent enum frozen by 07 correction #19/A11; parser signature rolling with C-15).

namespace EraCore.Contracts.Intents;

/// <summary>
/// Unified intent-parse entry (K-4): the L1 grammar (C-15), the L3 cloud
/// tier (C-16) and the future L2 local tier (C-27) all implement this one
/// port, so the five-stage pipeline (C-13) never knows which tier answered.
/// Parsing is pure: same text ⇒ same result, no hidden state, no I/O.
/// </summary>
public interface IIntentParser
{
    /// <summary>
    /// Parses one military order. Failure never throws: it returns
    /// <see cref="IntentParseResult.Failure"/> carrying the clarifying
    /// question key.
    /// </summary>
    IntentParseResult Parse(string text);
}
