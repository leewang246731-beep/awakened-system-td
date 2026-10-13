// T2: pure contract — K-7 save format v1, T-024, frozen (Wave 0; 07-decided-plan D-21).

namespace EraCore.Contracts.Persistence;

/// <summary>
/// Save-format versioning (K-7). v1 is the Wave-0 frozen layout: campaign
/// meta plus per-level wave-boundary snapshot plus command tail
/// (07 D-11/D-32). Reads of older stored versions migrate forward
/// chain-style before use; writes always emit <see cref="Current"/>. A bump
/// is a contract change: review, version increment, lane notification.
/// </summary>
public static class SaveVersion
{
    /// <summary>Format version this build writes; migrations target this value.</summary>
    public const int Current = 1;

    /// <summary>Oldest stored format version the migration chain can still read.</summary>
    public const int OldestReadable = 1;
}
