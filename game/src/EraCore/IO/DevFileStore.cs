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
        var root = Path.GetFullPath(Root);
        var fullPath = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"Relative path '{relativePath}' escapes the injected root '{root}'.",
                nameof(relativePath));
        }

        return fullPath;
    }
}
