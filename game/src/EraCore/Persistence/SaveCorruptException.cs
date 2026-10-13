using System;

namespace EraCore.Persistence;

/// <summary>
/// A persisted save store exists but its contents cannot be trusted:
/// damaged payload JSON, a missing schema-version table, or a stored
/// version the migration chain cannot resolve (T-041 / 07 D-11).
/// Carries the stored format version so callers and crash reports can
/// tell WHICH layout was found — recovery then goes through the rotating
/// backups, never through a partial re-parse.
/// </summary>
public sealed class SaveCorruptException : Exception
{
    /// <summary>
    /// Format version found in the store (0 when unreadable/absent), as
    /// stored in the schema_version table at detection time.
    /// </summary>
    public int StoredVersion { get; }

    public SaveCorruptException(string message, int storedVersion, Exception? innerException = null)
        : base(message, innerException)
    {
        StoredVersion = storedVersion;
    }
}
