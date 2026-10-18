namespace EraCore.Procedural;

/// <summary>
/// Deterministic child-seed derivation of the procedural level generator
/// (T-140, F-007): every sub-stream of a generation (terrain draw, layout
/// draw, reroll attempt) derives its own seed from the ONE run seed by
/// FNV-1a 64 hashing over the domain tag, then byte-mixing the seed and the
/// attempt counter — the <see cref="EraCore.World.GridWorld.ComputeMapHash"/>
/// / <see cref="EraCore.Progression.CampaignHash"/> primitive family. No
/// <c>Random</c>, no <c>string.GetHashCode</c> (platform-stable requirement,
/// CS-1.4): same inputs ⇒ same child seed on every platform and run, so the
/// reroll chain (seed → attempt child seeds) is itself deterministic and
/// "重摇次数入哈希" holds by construction — the attempt counter is an input
/// of the derivation, two levels that differ only in reroll count can never
/// collide onto one terrain stream.
/// </summary>
public static class ProceduralSeed
{
    private const ulong Offset = 14695981039346656037UL;

    private const ulong Prime = 1099511628211UL;

    /// <summary>
    /// Derives one child seed: FNV-1a over the domain tag's UTF-16 code
    /// units (<see cref="EraCore.Progression.CampaignHash.Fnv64"/>
    /// convention), then byte-mixes the run seed and the attempt counter
    /// (<see cref="EraCore.World.GridWorld"/> Mix convention — 8 bits per
    /// round, order-stable).
    /// </summary>
    /// <exception cref="ArgumentNullException">domain is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">attempt is negative.</exception>
    public static long Derive(long seed, string domain, int attempt)
    {
        ArgumentNullException.ThrowIfNull(domain);
        if (attempt < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(attempt), attempt, "Reroll attempt must be non-negative.");
        }

        ulong hash = Offset;
        foreach (char c in domain)
        {
            hash ^= c;
            hash *= Prime;
        }

        hash = Mix(hash, (ulong)seed);
        hash = Mix(hash, (ulong)attempt);
        return unchecked((long)hash);
    }

    /// <summary>
    /// Projects one derived 64-bit seed into the positive int32 window the
    /// handcrafted-level JSON schema carries (<c>map.seed</c> parses through
    /// an int32 read on the loading face). Mask keeps the value in
    /// [1, int.MaxValue] — the zero case maps to 1 (deterministic fallback,
    /// never a collision risk: the derivation feeds the mask).
    /// </summary>
    public static int ToMapSeed(long derived)
    {
        int masked = (int)(derived & int.MaxValue);
        return masked == 0 ? 1 : masked;
    }

    private static ulong Mix(ulong hash, ulong value)
    {
        unchecked
        {
            for (int shift = 0; shift < 64; shift += 8)
            {
                hash ^= (value >> shift) & 0xFFUL;
                hash *= Prime;
            }

            return hash;
        }
    }
}
