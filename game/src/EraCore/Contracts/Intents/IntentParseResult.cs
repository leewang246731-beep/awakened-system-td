// T2: pure contract — K-4 intent layer, T-022, draft (12-intent enum frozen by 07 correction #19/A11; parser signature rolling with C-15).

namespace EraCore.Contracts.Intents;

/// <summary>
/// Parse failure (K-4): the order could not be resolved into an intent; the
/// pipeline answers with a clarification card plus the quick wheel instead
/// of guessing (C-13).
/// </summary>
/// <param name="RawText">The original order text.</param>
/// <param name="ClarifyingKey">Localization key of the clarifying question to show.</param>
public sealed record IntentParseFailure(
    string RawText,
    string ClarifyingKey);

/// <summary>
/// Result of <see cref="IIntentParser.Parse"/>: exactly one of
/// <see cref="Intent"/> / <see cref="Failure"/> is non-null.
/// </summary>
public sealed record IntentParseResult(
    Intent? Intent,
    IntentParseFailure? Failure);
