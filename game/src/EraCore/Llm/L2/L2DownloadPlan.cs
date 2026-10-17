namespace EraCore.Llm.L2;

/// <summary>
/// One resume decision for an artifact download (T-137) — pure math over
/// (expected facts × on-disk state), produced before any wire call so the
///「断点续传」policy is testable without network (五级测试的网络 mock 面).
/// </summary>
/// <param name="Action">What the engine should do.</param>
/// <param name="ResumeOffset">Byte offset to continue from (ResumeFromPartial only).</param>
public sealed record L2DownloadDirective(
    L2DownloadAction Action,
    long ResumeOffset = 0)
{
    /// <summary>Final file already verified complete — nothing to do.</summary>
    public static L2DownloadDirective AlreadyComplete { get; } = new(L2DownloadAction.AlreadyComplete);

    /// <summary>Final file has the right size but the digest was never checked — verify only (no wire).</summary>
    public static L2DownloadDirective VerifyOnly { get; } = new(L2DownloadAction.VerifyOnly);

    /// <summary>Resume appending at <paramref name="offset"/> into the .part file.</summary>
    public static L2DownloadDirective ResumeFromPartial(long offset) => new(L2DownloadAction.ResumeFromPartial, offset);

    /// <summary>Delete both files and start from byte 0.</summary>
    public static L2DownloadDirective FreshStart { get; } = new(L2DownloadAction.FreshStart);
}

/// <summary>The action part of an <see cref="L2DownloadDirective"/>.</summary>
public enum L2DownloadAction
{
    /// <summary>Final file present with matching size AND a previously verified digest.</summary>
    AlreadyComplete,

    /// <summary>Final file present with matching size, digest unverified — hash it offline.</summary>
    VerifyOnly,

    /// <summary>.part file present (any size ≤ expected) — append from its length.</summary>
    ResumeFromPartial,

    /// <summary>Nothing usable on disk (or size over-run / stale unknown artifact) — restart clean.</summary>
    FreshStart,
}

/// <summary>
/// Pure decision table behind the L2 downloader's resume/verify/cancel
/// behaviour (T-137). Inputs are file facts only — the engine feeds them
/// from its file port, tests feed them directly (no network involved).
/// Policy (frozen):
/// <list type="bullet">
/// <item>final file + size match + digest verified → <see cref="L2DownloadAction.AlreadyComplete"/>;</item>
/// <item>final file + size match + digest unverified → <see cref="L2DownloadAction.VerifyOnly"/> (offline gate, zero wire);</item>
/// <item>.part file with size in (0, expected) → <see cref="L2DownloadAction.ResumeFromPartial"/> at its length (断点续传);</item>
/// <item>.part size == expected → promote-then-verify is the engine's job; the plan says verify-only over the part (rename first);</item>
/// <item>anything else (no files / oversized part / oversized final) → <see cref="L2DownloadAction.FreshStart"/> (a corrupt or foreign artifact is never resumed).</item>
/// </list>
/// </summary>
public static class L2DownloadPlan
{
    /// <summary>Decides the directive for one artifact from its on-disk state.</summary>
    /// <param name="artifact">The artifact facts (expected size/digest).</param>
    /// <param name="finalSize">Byte size of the final file, or -1 when absent.</param>
    /// <param name="partSize">Byte size of the .part file, or -1 when absent.</param>
    /// <param name="finalDigestVerified">True when a previous run already verified the final file's digest.</param>
    public static L2DownloadDirective Decide(
        L2Artifact artifact,
        long finalSize,
        long partSize,
        bool finalDigestVerified)
    {
        ArgumentNullException.ThrowIfNull(artifact);

        if (finalSize == artifact.SizeBytes)
        {
            return finalDigestVerified || artifact.Sha256Hex is null
                ? L2DownloadDirective.AlreadyComplete
                : L2DownloadDirective.VerifyOnly;
        }

        if (finalSize > artifact.SizeBytes)
        {
            return L2DownloadDirective.FreshStart; // foreign/oversized file at the final path — never trust
        }

        if (partSize == artifact.SizeBytes)
        {
            return L2DownloadDirective.VerifyOnly; // complete-but-unpromoted part: hash, then promote (engine)
        }

        return partSize > 0 && partSize < artifact.SizeBytes
            ? L2DownloadDirective.ResumeFromPartial(partSize)
            : L2DownloadDirective.FreshStart;
    }

    /// <summary>Progress value of one in-flight download (pure projection).</summary>
    /// <param name="artifact">Artifact facts.</param>
    /// <param name="downloadedBytes">Bytes confirmed on disk (resumed runs start above 0).</param>
    public static L2DownloadProgress Progress(L2Artifact artifact, long downloadedBytes) => new(
        artifact.Id,
        downloadedBytes,
        artifact.SizeBytes,
        PercentOf(downloadedBytes, artifact.SizeBytes));

    /// <summary>Percent in whole units, clamped to [0,100]; deterministic formatting lives in <see cref="L2DownloadProgress.PercentText"/>.</summary>
    public static int PercentOf(long downloadedBytes, long totalBytes)
    {
        if (totalBytes <= 0)
        {
            return 0;
        }

        long clamped = Math.Clamp(downloadedBytes, 0, totalBytes);
        return (int)(clamped * 100 / totalBytes); // ci-allow: R7 T-137 百分比换算常量（工程域常量，非平衡数值）
    }
}

/// <summary>Progress snapshot for UI display (percent precomputed — presenters never redo the math).</summary>
/// <param name="ArtifactId">Which artifact (<c>model</c> / <c>runtime</c>).</param>
/// <param name="DownloadedBytes">Confirmed bytes (resume-aware).</param>
/// <param name="TotalBytes">Expected total.</param>
/// <param name="Percent">Whole-percent, clamped [0,100].</param>
public sealed record L2DownloadProgress(
    string ArtifactId,
    long DownloadedBytes,
    long TotalBytes,
    int Percent)
{
    /// <summary>Player-visible percent line (the only wording source).</summary>
    public string PercentText => $"{Percent}%";
}
