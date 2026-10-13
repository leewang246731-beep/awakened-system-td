using EraCore.Contracts.Foundation;
using EraCore.IO;

namespace EraCore.Tests.Secrets;

/// <summary>
/// In-memory <see cref="ILogSink"/> that captures every line verbatim so
/// tests can assert the F-084 red line (no plaintext key ever reaches a
/// log) against real captured output.
/// </summary>
public sealed class CaptureLogSink : ILogSink
{
    private readonly List<string> _lines = [];

    public IReadOnlyList<string> Lines => _lines;

    public void Write(LogLevel level, long tick, string message)
    {
        _lines.Add($"[{level}] tick={tick} {message}");
    }

    public string Joined => string.Join(Environment.NewLine, _lines);
}

/// <summary>
/// D-drive GUID sandbox under the pinned dev root (CS-3.8, mirrors
/// DevFileStoreTests): every secrets test fixture gets its own throwaway
/// root so parallel test instances never share a secrets file. The
/// disposable root is deleted on teardown; only the fixture's own GUID
/// directory is touched.
/// </summary>
public abstract class SecretsSandbox : IDisposable
{
    private readonly string _root = Path.Combine(
        DevFileStore.DefaultDevRoot, ".tmp-tests", Guid.NewGuid().ToString("N"));

    protected string Root => _root;

    protected IFileStore NewFiles() => new DevFileStore(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
