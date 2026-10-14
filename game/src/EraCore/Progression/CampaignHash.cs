namespace EraCore.Progression;

/// <summary>
/// Stable 64-bit FNV-1a string hash for campaign seed derivation (T-073):
/// objective draws and chest rolls derive their deterministic seeds from
/// content ids (level ids, tier ids), so the mixing function must be fixed
/// forever — same id ⇒ same seed on every platform and run (CS-4.9 奖励
/// 先定后演；SP-8c 确定性 seed=关卡 id). Same primitive as
/// <c>Economy.ResourceStock.ComputeStateHash</c> (FNV-1a 64), kept public
/// here because two independent consumers (judge, chest) must agree on it.
/// </summary>
public static class CampaignHash
{
    private const ulong Offset = 14695981039346656037UL;

    private const ulong Prime = 1099511628211UL;

    /// <summary>
    /// FNV-1a over the UTF-16 code units of <paramref name="text"/> — the
    /// character-wise form the rest of the codebase hashes with (no encoding
    /// step, no allocation).
    /// </summary>
    /// <exception cref="ArgumentNullException">text is null.</exception>
    public static long Fnv64(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        ulong hash = Offset;
        foreach (char c in text)
        {
            hash ^= c;
            hash *= Prime;
        }

        return unchecked((long)hash);
    }

    /// <summary>
    /// Combines a content-id hash with a run seed (xors the unchecked bit
    /// patterns): level replays with the same run seed and the same tier
    /// reproduce the same roll stream, different ids never collide onto one
    /// stream by accident of equal run seeds.
    /// </summary>
    /// <exception cref="ArgumentNullException">tierId is null.</exception>
    public static long Combine(string tierId, long runSeed) => Fnv64(tierId) ^ runSeed;
}
