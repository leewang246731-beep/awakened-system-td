using EraCore.Contracts.Agent;

namespace EraCore.Llm;

/// <summary>
/// Context trimmer of the C-16 gateway (F-090, 方案 §11.7 上下文裁剪:
/// system+最近 N 轮，工具调用对成对保留). Pure function over the flat
/// <see cref="LlmChatMessage"/> history:
/// <list type="bullet">
/// <item>system messages are always kept (they carry the closed action
/// contract and must reach every call);</item>
/// <item>non-system messages are grouped into rounds that START at each
/// <c>user</c> message — an assistant tool-call and its <c>tool</c> result
/// therefore always live in the same round and can never be split by a
/// cut (成对保留 by construction, asserted by tests);</item>
/// <item>the last <paramref name="historyRounds"/> rounds survive the
/// history cut (≤0 keeps only the newest round — the current turn);</item>
/// <item>then the oldest surviving round is dropped repeatedly while the
/// <see cref="LlmTokenEstimator"/> estimate exceeds
/// <paramref name="contextBudgetTokens"/>; the newest round is never
/// dropped (the trimmer refuses to empty the prompt) and a budget ≤0
/// disables the budget loop.</item>
/// </list>
/// </summary>
public static class LlmContextTrimmer
{
    /// <summary>
    /// Per-message framing overhead added to the content estimate (role +
    /// envelope markers; keeps the estimate conservative for budgeting).
    /// </summary>
    public const int PerMessageOverheadTokens = 4;

    /// <summary>Trims one history to system + last rounds within the budget.</summary>
    /// <param name="messages">Full flat history (system/user/assistant/tool roles).</param>
    /// <param name="historyRounds">Rounds of history to keep (≤0 ⇒ newest round only).</param>
    /// <param name="contextBudgetTokens">Token budget (≤0 ⇒ no budget cut).</param>
    public static IReadOnlyList<LlmChatMessage> Trim(
        IReadOnlyList<LlmChatMessage> messages,
        int historyRounds,
        int contextBudgetTokens)
    {
        ArgumentNullException.ThrowIfNull(messages);
        if (messages.Count == 0)
        {
            return [];
        }

        var system = messages.Where(m => m.Role == "system").ToList();
        var rounds = SplitIntoRounds(messages);

        // History cut: keep the newest N rounds (at least one).
        var keep = Math.Max(1, historyRounds);
        if (rounds.Count > keep)
        {
            rounds = rounds[^keep..];
        }

        // Budget cut: drop the oldest round while over budget and more than
        // one round remains (never empty the prompt).
        if (contextBudgetTokens > 0)
        {
            while (rounds.Count > 1
                && EstimateMessages(system) + EstimateMessages(rounds.SelectMany(r => r)) > contextBudgetTokens)
            {
                rounds.RemoveAt(0);
            }
        }

        // Original relative order: system messages ride first (they are
        // declared first in practice), then the kept rounds in arrival order.
        return [.. system, .. rounds.SelectMany(r => r)];
    }

    /// <summary>Estimated token count of one message list (content + overhead).</summary>
    public static int EstimateMessages(IEnumerable<LlmChatMessage> messages) =>
        messages.Sum(m => LlmTokenEstimator.EstimateTokens(m.Content) + PerMessageOverheadTokens);

    /// <summary>
    /// Groups non-system messages into rounds starting at each user
    /// message. Orphan non-system traffic before the first user message
    /// (assistant/tool without an opener) forms round 0 — the first
    /// candidate to be dropped by every cut.
    /// </summary>
    private static List<List<LlmChatMessage>> SplitIntoRounds(
        IReadOnlyList<LlmChatMessage> messages)
    {
        var rounds = new List<List<LlmChatMessage>>();
        foreach (var message in messages)
        {
            if (message.Role == "system")
            {
                continue;
            }

            if (message.Role == "user" || rounds.Count == 0)
            {
                rounds.Add([]);
            }

            rounds[^1].Add(message);
        }

        return rounds;
    }
}
