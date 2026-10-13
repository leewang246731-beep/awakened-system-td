namespace EraCore.IO;

/// <summary>
/// Development-machine <see cref="IFileStore"/> (D-03). Root priority:
/// explicit constructor argument, then <c>ERA_DATA_ROOT</c>, then the pinned
/// dev root. Release builds inject their own root (exe-adjacent saves dir) —
/// this default exists only for the dev workstation registered in
/// docs/engineering/07-decided-plan.md (environment facts table).
/// </summary>
public sealed class DevFileStore : IFileStore
{
    /// <summary>Pinned development root (<see cref="DefaultDevRoot"/>). Never res:// or user://.</summary>
    public const string DefaultDevRoot = @"D:\game\game\data"; // ci-allow: R9 T-027 D-03 已登记开发机根（07 §三附"代码根"）

    private static readonly string? EnvironmentRoot =
        Environment.GetEnvironmentVariable("ERA_DATA_ROOT");

    public DevFileStore(string? root = null)
    {
        Root = root ?? EnvironmentRoot ?? DefaultDevRoot;
    }

    public string Root { get; }

    public bool Exists(string relativePath) => File.Exists(Resolve(relativePath));

    public byte[] ReadAllBytes(string relativePath) => File.ReadAllBytes(Resolve(relativePath));

    public void WriteAllBytes(string relativePath, byte[] bytes)
    {
        var fullPath = Resolve(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllBytes(fullPath, bytes);
    }

    private string Resolve(string relativePath)
    {
        // Both sides normalized through GetFullPath first: mixed separators
        // and ".." segments must be resolved before any prefix comparison.
        var root = Path.GetFullPath(Root);
        var fullPath = Path.GetFullPath(Path.Combine(root, relativePath));

        // Compare against root + separator (root itself stays allowed), never
        // a bare prefix: <root>X next to <root>\ is a different tree. A plain
        // StartsWith(root) would let "dataX"-style siblings escape (review
        // round-1, REV-20261013-wave0-round1).
        var normalizedRoot = root.TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var isRootItself = fullPath.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase);
        var isInsideRoot = fullPath.StartsWith(
            normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        if (!isRootItself && !isInsideRoot)
        {
            // Fail closed as an access violation: the path targets outside the
            // injected root, so this store must not read or write it.
            throw new UnauthorizedAccessException(
                $"Relative path '{relativePath}' escapes the injected root '{root}'.");
        }

        return fullPath;
    }
}
