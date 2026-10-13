using System.Security.AccessControl;
using System.Security.Principal;
using EraCore.Agent.Actions;
using EraCore.Contracts.Foundation;
using EraCore.IO;

namespace EraCore.Secrets;

/// <summary>
/// Fallback <see cref="ISecretStore"/> backend (C-17, T-054 / F-084; 方案
/// §11.6 回落 "独立文件 0600"): plaintext provider→secret map at
/// <see cref="RelativePath"/> under the injected <see cref="IFileStore"/>
/// root (D-03), with permissions tightened to the current user right after
/// every write — Windows: inheritance off + current-user-only ACL; Unix:
/// chmod 0600. Tightening is best effort (0600 尽力): a filesystem that
/// rejects ACL edits logs a warning and keeps the file rather than losing
/// the secrets. Selected automatically by <see cref="SecretStoreFactory"/>
/// whenever DPAPI is unavailable (logged with the reason).
/// </summary>
public sealed class FileSecretStore : ISecretStore
{
    /// <summary>Fallback file location (relative to the injected IFileStore root).</summary>
    public const string RelativePath = "secrets.bin";

    private readonly IFileStore _files;
    private readonly ILogSink _log;

    public FileSecretStore(IFileStore files, ILogSink log)
    {
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>Fallback backend designation (diagnostics only).</summary>
    public string Backend => "file-fallback";

    /// <summary>Plain file backend works wherever the injected root is writable.</summary>
    public bool IsAvailable => true;

    public void Store(string providerId, string? secretValue)
    {
        ValidateProviderId(providerId);
        var map = new Dictionary<string, string>(
            SecretsContainer.Read(_files, RelativePath), StringComparer.Ordinal);
        if (string.IsNullOrEmpty(secretValue))
        {
            map.Remove(providerId);
        }
        else
        {
            map[providerId] = secretValue;
        }

        Write(map);
        Log("store", providerId, secretValue);
    }

    public string Retrieve(string providerId)
    {
        ValidateProviderId(providerId);
        return SecretsContainer.Read(_files, RelativePath)
            .TryGetValue(providerId, out var value) ? value : string.Empty;
    }

    public void Clear(string providerId)
    {
        ValidateProviderId(providerId);
        var map = new Dictionary<string, string>(
            SecretsContainer.Read(_files, RelativePath), StringComparer.Ordinal);
        map.Remove(providerId);
        Write(map);
        Log("clear", providerId, secretValue: null);
    }

    public void ClearAll()
    {
        Write(new Dictionary<string, string>());
        Log("clear-all", providerId: "-", secretValue: null);
    }

    // ------------------------------------------------------------ persistence

    private void Write(Dictionary<string, string> map)
    {
        SecretsContainer.Write(_files, RelativePath, map);
        TightenPermissions(Path.Combine(_files.Root, RelativePath));
    }

    private void Log(string op, string providerId, string? secretValue)
    {
        // F-084 red line: only the masked tail may ever appear, and the final
        // line still passes through the Actions redaction pre-scan (defense
        // in depth — RL5).
        var line = secretValue is null
            ? $"secret-store backend={Backend} op={op} provider={providerId}"
            : $"secret-store backend={Backend} op={op} provider={providerId} " +
              $"len={secretValue.Length} masked={KeyMasker.Mask(secretValue)}";
        _log.Write(LogLevel.Debug, -1, ActionLog.Scrub(line));
    }

    private static void ValidateProviderId(string providerId)
    {
        if (string.IsNullOrWhiteSpace(providerId))
        {
            throw new ArgumentException(
                "Provider id must be a non-empty string (§11.6 provider id).",
                nameof(providerId));
        }
    }

    /// <summary>
    /// 0600 best effort: restrict the file to the current user. Failures are
    /// logged (Warn) and swallowed — losing stored secrets over an ACL
    /// restriction would be the worse failure mode.
    /// </summary>
    private void TightenPermissions(string absolutePath)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                RestrictToCurrentUser(absolutePath);
            }
            else
            {
                File.SetUnixFileMode(
                    absolutePath,
                    System.IO.UnixFileMode.UserRead | System.IO.UnixFileMode.UserWrite);
            }
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            _log.Write(LogLevel.Warn, -1, ActionLog.Scrub(
                $"secret-store backend={Backend} op=tighten-permissions " +
                $"result=best-effort-failed error={ex.GetType().Name}"));
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void RestrictToCurrentUser(string absolutePath)
    {
        var info = new FileInfo(absolutePath);
        var security = info.GetAccessControl();

        // Inheritance off, inherited rules dropped: the file keeps no rules
        // from the directory chain (§11.6 file fallback must not inherit the
        // root's broader grants).
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (FileSystemAccessRule rule in security.GetAccessRules(
                     includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)))
        {
            _ = security.RemoveAccessRule(rule);
        }

        security.AddAccessRule(new FileSystemAccessRule(
            WindowsIdentity.GetCurrent().User
                ?? throw new InvalidOperationException("current user SID unavailable"),
            FileSystemRights.FullControl,
            AccessControlType.Allow));
        info.SetAccessControl(security);
    }
}
