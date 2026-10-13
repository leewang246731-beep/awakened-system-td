namespace EraCore.IO;

/// <summary>
/// Root-injected file access port (D-03). All writable game data goes through
/// this interface — the engine's <c>user://</c> default (which lands on the
/// C-drive <c>%APPDATA%</c>) is never used.
/// Root per deployment: dev = <see cref="DevFileStore.DefaultDevRoot"/> (env override
/// <c>ERA_DATA_ROOT</c>), release = exe-adjacent <c>saves\</c>, Web = browser FS.
/// </summary>
public interface IFileStore
{
    /// <summary>Absolute injected root that all relative paths resolve against.</summary>
    string Root { get; }

    /// <summary>Returns true when a file exists under the root.</summary>
    bool Exists(string relativePath);

    /// <summary>Reads a file's full contents. Throws if missing.</summary>
    byte[] ReadAllBytes(string relativePath);

    /// <summary>Atomically creates parent directories and writes the file.</summary>
    void WriteAllBytes(string relativePath, byte[] bytes);
}
