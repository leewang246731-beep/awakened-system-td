using System.Security.Cryptography;

namespace EraCore.Llm.L2;

/// <summary>Terminal state of one download run (T-137).</summary>
public enum L2DownloadOutcome
{
    /// <summary>Artifact complete and verified (size gate + sha256 when the manifest carries one).</summary>
    Completed,

    /// <summary>Player cancelled — the .part file is kept so the next run resumes (断点续传 across sessions).</summary>
    Cancelled,

    /// <summary>Wire failed mid-run — .part kept for resume; the engine retries nothing by itself (caller policy).</summary>
    TransportFailed,

    /// <summary>Downloaded bytes failed the size or digest gate twice — files deleted, honest failure.</summary>
    VerificationFailed,
}

/// <summary>
/// Result of one <see cref="L2DownloadEngine.RunAsync"/> (terminal — progress
/// rides the callback, not the result).
/// </summary>
/// <param name="Outcome">Terminal state.</param>
/// <param name="ArtifactId">Which artifact.</param>
/// <param name="Detail">Diagnostic line (exception type / digest mismatch fact; no secrets exist on this lane).</param>
/// <param name="DigestMismatch">Typed retry signal — true only for a sha256 gate failure (a clean restart can help); size-gate and second-mismatch results stay false.</param>
public sealed record L2DownloadResult(L2DownloadOutcome Outcome, string ArtifactId, string Detail, bool DigestMismatch = false)
{
    /// <summary>True only when the artifact is usable now.</summary>
    public bool IsUsable => Outcome == L2DownloadOutcome.Completed;
}

/// <summary>
/// The L2 artifact download engine (T-137): plan → range-fetch loop → size
/// gate → sha256 gate → atomic promote. All networking and file IO ride the
/// injected ports (mock-able; the test suite downloads synthetic KB-sized
/// artifacts — 真下 1.9 GB 是红线, 验收纪律). Deterministic policy:
/// <list type="bullet">
/// <item>resume — a partial <c>.part</c> continues from its length (sessions included);</item>
/// <item>verify — size first, then sha256 (streamed, IncrementalHash); a digest mismatch restarts ONCE from byte 0, a second mismatch fails honestly;</item>
/// <item>cancel — the token stops the loop; the .part file survives (never bill the player twice for the same bytes);</item>
/// <item>promote — only a fully verified artifact lands on the final path (atomic Move).</item>
/// </list>
/// </summary>
public sealed class L2DownloadEngine
{
    private readonly IL2DownloadWirePort _wire;
    private readonly IL2DownloadFilePort _files;

    /// <param name="wire">Range-fetch port (real network lives outside EraCore).</param>
    /// <param name="files">Streaming file port under the injected data root.</param>
    public L2DownloadEngine(IL2DownloadWirePort wire, IL2DownloadFilePort files)
    {
        _wire = wire ?? throw new ArgumentNullException(nameof(wire));
        _files = files ?? throw new ArgumentNullException(nameof(files));
    }

    /// <summary>Final relative path of one artifact (under its <c>RelativeDir</c>).</summary>
    public static string FinalPath(L2Artifact artifact) =>
        $"{artifact.RelativeDir}/{artifact.FileName}";

    /// <summary>In-flight relative path of one artifact (final + <c>.part</c>).</summary>
    public static string PartPath(L2Artifact artifact) =>
        $"{artifact.RelativeDir}/{artifact.FileName}.part";

    /// <summary>
    /// Runs one artifact to a terminal state. The progress callback fires on
    /// every persisted chunk boundary (plus resume start); it never throws
    /// out of the engine.
    /// </summary>
    /// <param name="artifact">Artifact facts.</param>
    /// <param name="progress">Progress sink (UI thread marshalling is the caller's).</param>
    /// <param name="cancellationToken">Cancel — keeps the .part file.</param>
    public async Task<L2DownloadResult> RunAsync(
        L2Artifact artifact,
        Action<L2DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);

        try
        {
            // Attempt 1 (possibly resuming); a digest mismatch triggers one
            // clean restart, a second mismatch fails honestly.
            for (int attempt = 0; attempt < 2; attempt++)
            {
                var result = await RunOnceAsync(artifact, progress, cancellationToken).ConfigureAwait(false);
                if (result.Outcome != L2DownloadOutcome.VerificationFailed)
                {
                    return result;
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    return result;
                }

                // Clean slate for the restart (both paths — a wrong-size final
                // must not shadow the retry).
                _files.Delete(FinalPath(artifact));
                _files.Delete(PartPath(artifact));
                if (attempt == 0 && result.DigestMismatch)
                {
                    continue; // digest mismatch ⇒ restart once (typed signal, not diagnostic text)
                }

                return result; // size-gate failure ⇒ no restart can help
            }

            return new L2DownloadResult(
                L2DownloadOutcome.VerificationFailed, artifact.Id, "verification failed twice");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new L2DownloadResult(
                L2DownloadOutcome.Cancelled, artifact.Id, "cancelled — partial kept for resume");
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            return new L2DownloadResult(
                L2DownloadOutcome.TransportFailed, artifact.Id, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private async Task<L2DownloadResult> RunOnceAsync(
        L2Artifact artifact,
        Action<L2DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        long finalSize = _files.SizeOf(FinalPath(artifact));
        long partSize = _files.SizeOf(PartPath(artifact));
        L2DownloadDirective directive = L2DownloadPlan.Decide(
            artifact, finalSize, partSize, finalDigestVerified: false);

        switch (directive.Action)
        {
            case L2DownloadAction.AlreadyComplete:
                Report(progress, artifact, artifact.SizeBytes);
                return new L2DownloadResult(L2DownloadOutcome.Completed, artifact.Id, "already complete");

            case L2DownloadAction.VerifyOnly:
                // Either an unverified final file, or a complete-but-unpromoted
                // part (hash the part in place, then promote on success).
                bool hashingPart = partSize == artifact.SizeBytes;
                string verifyPath = hashingPart ? PartPath(artifact) : FinalPath(artifact);
                if (!await VerifyDigestAsync(artifact, verifyPath).ConfigureAwait(false))
                {
                    return new L2DownloadResult(
                        L2DownloadOutcome.VerificationFailed, artifact.Id,
                        $"digest mismatch at {verifyPath}",
                        DigestMismatch: true);
                }

                if (hashingPart)
                {
                    _files.Move(PartPath(artifact), FinalPath(artifact));
                }

                Report(progress, artifact, artifact.SizeBytes);
                return new L2DownloadResult(L2DownloadOutcome.Completed, artifact.Id, "verified");

            case L2DownloadAction.FreshStart:
                _files.Delete(PartPath(artifact));
                _files.Delete(FinalPath(artifact));
                partSize = 0;
                break;

            case L2DownloadAction.ResumeFromPartial:
                break;
        }

        // Range-fetch loop from partSize to SizeBytes.
        long offset = directive.Action == L2DownloadAction.ResumeFromPartial ? directive.ResumeOffset : 0;
        Report(progress, artifact, offset);
        using (Stream append = _files.OpenAppend(PartPath(artifact)))
        {
            if (append.Position != offset)
            {
                append.Seek(offset, SeekOrigin.Begin); // OpenAppend port may position at end; align defensively
            }

            while (offset < artifact.SizeBytes)
            {
                cancellationToken.ThrowIfCancellationRequested();

                int want = (int)Math.Min(L2Facts.DownloadChunkBytes, artifact.SizeBytes - offset);
                L2RangeReply reply = await _wire.FetchRangeAsync(artifact, offset, want, cancellationToken)
                    .ConfigureAwait(false);

                if (reply.TotalLength != artifact.SizeBytes)
                {
                    return new L2DownloadResult(
                        L2DownloadOutcome.VerificationFailed, artifact.Id,
                        $"size gate: server total {reply.TotalLength} != manifest {artifact.SizeBytes}");
                }

                if (reply.Chunk.Length == 0 || reply.Chunk.Length > want)
                {
                    throw new IOException($"range reply out of contract: {reply.Chunk.Length} bytes at {offset}");
                }

                await append.WriteAsync(reply.Chunk, cancellationToken).ConfigureAwait(false);
                await append.FlushAsync(cancellationToken).ConfigureAwait(false);
                offset += reply.Chunk.Length;
                Report(progress, artifact, offset);
            }
        }

        // Size gate then digest gate on the completed part; promote on success.
        long completedSize = _files.SizeOf(PartPath(artifact));
        if (completedSize != artifact.SizeBytes)
        {
            return new L2DownloadResult(
                L2DownloadOutcome.VerificationFailed, artifact.Id,
                $"size gate: part {completedSize} != manifest {artifact.SizeBytes}");
        }

        if (!await VerifyDigestAsync(artifact, PartPath(artifact)).ConfigureAwait(false))
        {
            return new L2DownloadResult(
                L2DownloadOutcome.VerificationFailed, artifact.Id, $"digest mismatch at {PartPath(artifact)}",
                DigestMismatch: true);
        }

        _files.Delete(FinalPath(artifact));
        _files.Move(PartPath(artifact), FinalPath(artifact));
        return new L2DownloadResult(L2DownloadOutcome.Completed, artifact.Id, "downloaded and verified");
    }

    /// <summary>Streaming sha256 gate (skipped for digest-less artifacts — size gate only, manifest fact).</summary>
    private async Task<bool> VerifyDigestAsync(L2Artifact artifact, string relativePath)
    {
        if (artifact.Sha256Hex is null)
        {
            return true;
        }

        string actual;
        using (Stream read = _files.OpenRead(relativePath))
        {
            using var hash = SHA256.Create();
            actual = Convert.ToHexString(await hash.ComputeHashAsync(read).ConfigureAwait(false)).ToLowerInvariant();
        }

        return string.Equals(actual, artifact.Sha256Hex, StringComparison.Ordinal);
    }

    private static void Report(Action<L2DownloadProgress>? progress, L2Artifact artifact, long downloadedBytes)
    {
        if (progress is null)
        {
            return;
        }

        try
        {
            progress(L2DownloadPlan.Progress(artifact, downloadedBytes));
        }
        catch (Exception)
        {
            // A UI sink must never break the download loop.
        }
    }
}
