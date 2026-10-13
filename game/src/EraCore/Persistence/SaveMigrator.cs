using Microsoft.Data.Sqlite;

namespace EraCore.Persistence;

/// <summary>
/// Single-hop migration over the SQLite save store (K-7 chain-style
/// forward migration, T-041): a step moves the store from
/// <see cref="FromVersion"/> to <see cref="ToVersion"/> (always +1 hop)
/// by executing its SQL on the live connection. v1 ships no steps — the
/// chain is reserved for future bumps, which are contract changes
/// (review + version increment + lane notification, see SaveVersion).
/// </summary>
public interface ISaveMigrationStep
{
    /// <summary>Stored version this step transforms FROM (step start).</summary>
    int FromVersion { get; }

    /// <summary>Stored version this step produces (FromVersion + 1).</summary>
    int ToVersion { get; }

    /// <summary>Applies the transformation on <paramref name="connection"/>.</summary>
    void Apply(SqliteConnection connection);
}

/// <summary>
/// Migration-chain executor (T-041). Walks the registered single-hop steps
/// from a stored version forward to the requested target inside ONE
/// transaction, advancing the schema_version row after each hop — so a
/// crash mid-migration leaves the store at the previous intact version,
/// never between layouts.
/// </summary>
public sealed class SaveMigrator
{
    private readonly Dictionary<int, ISaveMigrationStep> _stepsByFromVersion = new();

    /// <summary>
    /// The shipped chain for the current build: v1 has no hops (identity);
    /// future format bumps register their steps here as they are added.
    /// </summary>
    public static SaveMigrator CreateDefault() => new();

    /// <summary>Registers one hop; duplicate FromVersion is rejected.</summary>
    public void Register(ISaveMigrationStep step)
    {
        ArgumentNullException.ThrowIfNull(step);

        if (step.ToVersion != step.FromVersion + 1)
        {
            throw new ArgumentException(
                $"Migration steps are single-hop: expected ToVersion == FromVersion + 1, "
                + $"got {step.FromVersion} -> {step.ToVersion}.");
        }

        if (!_stepsByFromVersion.TryAdd(step.FromVersion, step))
        {
            throw new ArgumentException(
                $"A migration step from version {step.FromVersion} is already registered.");
        }
    }

    /// <summary>
    /// Migrates the store from <paramref name="storedVersion"/> forward to
    /// <paramref name="targetVersion"/>. Equal versions are the identity
    /// (v1 -&gt; v1); a stored version newer than the target or a missing hop
    /// fails as <see cref="SaveCorruptException"/> with the stored version.
    /// </summary>
    public void Migrate(SqliteConnection connection, int storedVersion, int targetVersion)
    {
        ArgumentNullException.ThrowIfNull(connection);

        if (storedVersion == targetVersion)
        {
            return; // identity: nothing to do (v1 -> v1)
        }

        if (storedVersion > targetVersion)
        {
            throw new SaveCorruptException(
                $"Save store is at format version {storedVersion}, newer than the requested "
                + $"{targetVersion}; backward migration does not exist.",
                storedVersion);
        }

        using var transaction = connection.BeginTransaction();
        var current = storedVersion;
        while (current < targetVersion)
        {
            if (!_stepsByFromVersion.TryGetValue(current, out var step))
            {
                throw new SaveCorruptException(
                    $"No migration step is registered for {current} -> {current + 1}; the "
                    + "chain from the stored version to the requested version is broken.",
                    storedVersion);
            }

            step.Apply(connection);
            current = step.ToVersion;
            SqliteSavePort.ExecuteNonQuery(
                connection, "UPDATE schema_version SET version = $version WHERE id = 1;",
                ("$version", current));
        }

        transaction.Commit();
    }
}
