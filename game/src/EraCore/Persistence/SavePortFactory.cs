using EraCore.Agent.Actions;
using EraCore.Contracts.Foundation;
using EraCore.Contracts.Persistence;
using EraCore.IO;
using Microsoft.Data.Sqlite;

namespace EraCore.Persistence;

/// <summary>
/// Backend selection for <see cref="ISavePort"/> (C-04, T-100 / 07 D-32,
/// R-E2): prefers <see cref="SqliteSavePort"/> when the runtime can really
/// load the native <c>e_sqlite3</c> library, otherwise falls back to
/// <see cref="JsonFileSavePort"/> and records the reason through
/// <see cref="ILogSink"/> (Warn) so the degradation is observable. The probe
/// is RUNTIME, not compile-time: on the Web export the native library is
/// absent from the wasm payload and the probe fails exactly as it would on
/// a desktop with a broken installation — one code path, decided live.
/// The <c>ERA_SAVE_BACKEND</c> environment switch ("sqlite" / "json") lets a
/// headless desktop run SIMULATE the no-SQLite Web environment (tests and
/// Web smoke use it); an explicit override beats the probe and is logged as
/// Info because it is deliberate, an unknown value fails loudly.
/// </summary>
public static class SavePortFactory
{
    /// <summary>Operator override variable (values: sqlite / json).</summary>
    public const string BackendOverrideVariable = "ERA_SAVE_BACKEND";

    /// <summary>Override value selecting the SQLite backend.</summary>
    public const string SqliteBackendValue = "sqlite";

    /// <summary>Override value selecting the JSON backend (Web simulation).</summary>
    public const string JsonBackendValue = "json";

    /// <summary>
    /// Creates the best available save port over the injected file root.
    /// Selection order: environment override → native-library probe
    /// (SQLite) → JSON fallback.
    /// </summary>
    /// <param name="files">Root-injected file access (D-03); owns every path.</param>
    /// <param name="log">Diagnostic sink receiving the selection record.</param>
    /// <param name="sqliteProbe">
    /// Probe override for tests; production callers omit it and get the
    /// genuine try-load roundtrip below.
    /// </param>
    public static ISavePort Create(
        IFileStore files, ILogSink log, Func<bool>? sqliteProbe = null)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(log);

        var overrideValue = Environment.GetEnvironmentVariable(BackendOverrideVariable)?
            .Trim()
            .ToLowerInvariant();
        if (overrideValue is { Length: > 0 })
        {
            switch (overrideValue)
            {
                case SqliteBackendValue:
                    log.Write(LogLevel.Info, -1, ActionLog.Scrub(
                        $"save-port factory=select backend=sqlite reason=env-override " +
                        $"var={BackendOverrideVariable}"));
                    return new SqliteSavePort(files);
                case JsonBackendValue:
                    log.Write(LogLevel.Info, -1, ActionLog.Scrub(
                        $"save-port factory=select backend=json reason=env-override " +
                        $"var={BackendOverrideVariable} (Web simulation)"));
                    return new JsonFileSavePort(files);
                default:
                    throw new ArgumentException(
                        $"Unknown {BackendOverrideVariable} value '{overrideValue}' "
                        + $"(expected '{SqliteBackendValue}' or '{JsonBackendValue}').",
                        BackendOverrideVariable);
            }
        }

        bool probeOk;
        string failure;
        if (sqliteProbe is null)
        {
            probeOk = TryProbeSqliteNativeLibrary(out failure);
        }
        else
        {
            probeOk = sqliteProbe();
            failure = "sqlite probe returned false";
        }

        if (probeOk)
        {
            log.Write(LogLevel.Info, -1, ActionLog.Scrub(
                "save-port factory=select backend=sqlite reason=native-e_sqlite3-available"));
            return new SqliteSavePort(files);
        }

        log.Write(LogLevel.Warn, -1, ActionLog.Scrub(
            $"save-port factory=select backend=json-fallback reason={failure}"));
        return new JsonFileSavePort(files);
    }

    /// <summary>
    /// Real capability probe: opening an in-memory store forces
    /// Microsoft.Data.Sqlite to load the bundled <c>e_sqlite3</c> native
    /// library — the exact dependency the wasm export cannot carry (R-E2).
    /// Returns false with the reason instead of throwing: a failed probe
    /// means "fall back", not "crash".
    /// </summary>
    private static bool TryProbeSqliteNativeLibrary(out string failure)
    {
        try
        {
            using var connection = new SqliteConnection("Data Source=:memory:");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1;";
            command.ExecuteScalar();
            failure = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            failure = $"sqlite native library unavailable: {ex.GetType().Name}";
            return false;
        }
    }
}
