using System.Runtime.InteropServices;
using EraCore.Agent.Actions;
using EraCore.Contracts.Foundation;
using EraCore.IO;

namespace EraCore.Secrets;

/// <summary>
/// Primary <see cref="ISecretStore"/> backend (C-17, T-054 / F-084; 方案
/// §11.6 优先 OS 凭据管理器): Windows DPAPI through crypt32
/// <c>CryptProtectData</c>/<c>CryptUnprotectData</c> — the same facility the
/// BCL <c>ProtectedData</c> class wraps — with <b>current-user</b> scope
/// (single-machine user profile, no administrator requirement) and a fixed
/// app entropy salt so blobs are bound to this application. The whole
/// provider→secret map is serialized by <see cref="SecretsContainer"/> and
/// stored as one DPAPI blob at <see cref="RelativePath"/> under the injected
/// <see cref="IFileStore"/> root (D-03; never the C-drive user:// default).
/// Outside Windows the backend reports <see cref="IsAvailable"/> = false and
/// every operation throws <see cref="PlatformNotSupportedException"/> with
/// an explicit message — callers route through
/// <see cref="SecretStoreFactory"/> for the file fallback.
/// </summary>
public sealed class DpapiSecretStore : ISecretStore
{
    /// <summary>DPAPI blob location (relative to the injected IFileStore root).</summary>
    public const string RelativePath = "secrets.dpapi.bin";

    /// <summary>
    /// Fixed app entropy salt (§11.6 Key 安全分级: 熵=固定 app salt). Bytes of
    /// the UTF-8 string below; constant for the lifetime of the format so
    /// existing blobs keep decrypting. Version-stamped on purpose.
    /// </summary>
    public static byte[] DefaultEntropy => "EraTD.C-17.secrets.v1"u8.ToArray();

    private const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;

    private readonly IFileStore _files;
    private readonly ILogSink _log;
    private readonly byte[] _entropy;

    public DpapiSecretStore(IFileStore files, ILogSink log, byte[]? entropy = null)
    {
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _entropy = entropy ?? DefaultEntropy;
    }

    /// <summary>DPAPI backend designation (diagnostics only).</summary>
    public string Backend => "dpapi-currentuser";

    /// <summary>True only on Windows (crypt32 DPAPI availability surface).</summary>
    public bool IsAvailable => OperatingSystem.IsWindows();

    public void Store(string providerId, string? secretValue)
    {
        ValidateProviderId(providerId);
        var map = LoadMap();
        if (string.IsNullOrEmpty(secretValue))
        {
            map.Remove(providerId);
        }
        else
        {
            map[providerId] = secretValue;
        }

        SaveMap(map);
        Log("store", providerId, secretValue);
    }

    public string Retrieve(string providerId)
    {
        ValidateProviderId(providerId);
        return LoadMap().TryGetValue(providerId, out var value) ? value : string.Empty;
    }

    public void Clear(string providerId)
    {
        ValidateProviderId(providerId);
        var map = LoadMap();
        map.Remove(providerId);
        SaveMap(map);
        Log("clear", providerId, secretValue: null);
    }

    public void ClearAll()
    {
        SaveMap(new Dictionary<string, string>());
        Log("clear-all", providerId: "-", secretValue: null);
    }

    /// <summary>
    /// Real capability probe used by <see cref="SecretStoreFactory"/>: runs a
    /// tiny protect/unprotect roundtrip with the app entropy. Returns false
    /// (with the reason captured in <paramref name="failure"/>) instead of
    /// throwing — a failed probe means "fall back", not "crash".
    /// </summary>
    public static bool Probe(out string failure)
    {
        if (!OperatingSystem.IsWindows())
        {
            failure = "dpapi requires Windows (crypt32)";
            return false;
        }

        try
        {
            Span<byte> sample = [1, 2, 3, 4];
            var protectedBytes = Protect(sample, DefaultEntropy);
            var roundtrip = Unprotect(protectedBytes, DefaultEntropy);
            if (roundtrip.AsSpan().SequenceEqual(sample))
            {
                failure = string.Empty;
                return true;
            }

            failure = "dpapi probe roundtrip mismatch";
            return false;
        }
        catch (Exception ex)
        {
            failure = $"dpapi probe failed: {ex.GetType().Name}";
            return false;
        }
    }

    // ------------------------------------------------------------ persistence

    private Dictionary<string, string> LoadMap()
    {
        if (!_files.Exists(RelativePath))
        {
            return [];
        }

        var blob = _files.ReadAllBytes(RelativePath);
        var plain = Unprotect(blob, _entropy);
        return new Dictionary<string, string>(
            SecretsContainer.Decode(plain), StringComparer.Ordinal);
    }

    private void SaveMap(Dictionary<string, string> map)
    {
        var blob = Protect(SecretsContainer.Encode(map), _entropy);
        _files.WriteAllBytes(RelativePath, blob);
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

    // ------------------------------------------------------------ crypt32 P/Invoke

    [StructLayout(LayoutKind.Sequential)]
    private struct DATA_BLOB
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode,
        EntryPoint = "CryptProtectData")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DATA_BLOB pDataIn,
        string? szDataDescr,
        ref DATA_BLOB pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        int dwFlags,
        out DATA_BLOB pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode,
        EntryPoint = "CryptUnprotectData")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DATA_BLOB pDataIn,
        IntPtr ppszDataDescr,
        ref DATA_BLOB pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        int dwFlags,
        out DATA_BLOB pDataOut);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr hMem);

    private static byte[] Protect(ReadOnlySpan<byte> plain, byte[] entropy)
    {
        RequireWindows();
        var input = plain.ToArray();
        var inputBlob = new DATA_BLOB
        {
            cbData = input.Length,
            pbData = Marshal.AllocHGlobal(input.Length),
        };
        var entropyBlob = new DATA_BLOB
        {
            cbData = entropy.Length,
            pbData = Marshal.AllocHGlobal(entropy.Length),
        };
        try
        {
            Marshal.Copy(input, 0, inputBlob.pbData, input.Length);
            Marshal.Copy(entropy, 0, entropyBlob.pbData, entropy.Length);
            if (!CryptProtectData(
                    ref inputBlob, null, ref entropyBlob, IntPtr.Zero, IntPtr.Zero,
                    CRYPTPROTECT_UI_FORBIDDEN, out var output))
            {
                throw LastError("CryptProtectData");
            }

            return CopyOut(output);
        }
        finally
        {
            Marshal.FreeHGlobal(inputBlob.pbData);
            Marshal.FreeHGlobal(entropyBlob.pbData);
        }
    }

    private static byte[] Unprotect(byte[] blob, byte[] entropy)
    {
        RequireWindows();
        var inputBlob = new DATA_BLOB
        {
            cbData = blob.Length,
            pbData = Marshal.AllocHGlobal(blob.Length),
        };
        var entropyBlob = new DATA_BLOB
        {
            cbData = entropy.Length,
            pbData = Marshal.AllocHGlobal(entropy.Length),
        };
        try
        {
            Marshal.Copy(blob, 0, inputBlob.pbData, blob.Length);
            Marshal.Copy(entropy, 0, entropyBlob.pbData, entropy.Length);
            if (!CryptUnprotectData(
                    ref inputBlob, IntPtr.Zero, ref entropyBlob, IntPtr.Zero,
                    IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, out var output))
            {
                throw LastError("CryptUnprotectData");
            }

            return CopyOut(output);
        }
        finally
        {
            Marshal.FreeHGlobal(inputBlob.pbData);
            Marshal.FreeHGlobal(entropyBlob.pbData);
        }
    }

    private static void RequireWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "DPAPI (crypt32 CryptProtectData) is only available on Windows; " +
                "the application must use the file fallback secret store (§11.6).");
        }
    }

    private static byte[] CopyOut(DATA_BLOB output)
    {
        try
        {
            var result = new byte[output.cbData];
            Marshal.Copy(output.pbData, result, 0, output.cbData);
            return result;
        }
        finally
        {
            if (output.pbData != IntPtr.Zero)
            {
                LocalFree(output.pbData);
            }
        }
    }

    private static System.Security.Cryptography.CryptographicException LastError(string api) =>
        new(
            $"DPAPI {api} failed (Win32 error {Marshal.GetLastWin32Error()}); " +
            "the secret store cannot encrypt/decrypt on this profile.");
}
