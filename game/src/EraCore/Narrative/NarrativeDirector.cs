using EraCore.Agent.Governance;

namespace EraCore.Narrative;

/// <summary>Lifecycle phase of one chapter run (T-089).</summary>
public enum NarrativePhase
{
    /// <summary>Constructed, <see cref="NarrativeDirector.Begin"/> not called yet.</summary>
    NotStarted = 0,

    /// <summary>Chapter live: acts advance on beats, intermission lines draw on wave ends.</summary>
    InChapter = 1,

    /// <summary>Victory reached: handover lines and 卡点预告 emitted, chapter closed.</summary>
    Ended = 2,
}

/// <summary>
/// The chapter state machine (T-089, C-24 narrative slice): owns one
/// <see cref="StoryChapter"/>'s opening → acts → ending progression plus the
/// trust-tiered, persona-weighted intermission line selection. Pure logic —
/// the host feeds tick-stamped beat facts (wave starts, cinematic fires,
/// victory, trust-badge / persona changes) and renders the returned
/// <see cref="DialogueLine"/> batches through the existing bubble/subtitle
/// path. Zero clock/rng reads (CS-1.4): the same beat schedule yields the
/// same line trace (determinism); intermission draws rotate through the
/// pool deterministically instead of sampling.
/// </summary>
public sealed class NarrativeDirector
{
    private readonly StoryChapter _chapter;
    private readonly GoldenActs _golden;
    private readonly int[] _intermissionCursor;
    private TrustBadge _badge;
    private SystemPersona _persona;

    /// <summary>
    /// Creates a director for one chapter session. The optional armed
    /// <see cref="MockArcRow"/> (打脸/扮猪) adds wave-anchored NPC taunts to
    /// the wave-start feed — the judgment itself lives in
    /// <see cref="MockArcRulebook"/> (先定后演 discipline).
    /// </summary>
    /// <param name="chapter">The bound story chapter (content row).</param>
    /// <param name="initialBadge">Trust badge at session start (F.4 tiers).</param>
    /// <param name="persona">Active system persona (F-109 weighting).</param>
    /// <param name="mockArc">Armed 打脸/扮猪 row of this level, when one is configured.</param>
    public NarrativeDirector(
        StoryChapter chapter,
        TrustBadge initialBadge,
        SystemPersona persona,
        MockArcRow? mockArc = null)
    {
        ArgumentNullException.ThrowIfNull(chapter);
        _chapter = chapter;
        _golden = new GoldenActs(chapter.GoldenActs);
        _intermissionCursor = new int[StoryChapter.TrustTiers.Count];
        _badge = initialBadge;
        _persona = persona;
        MockArc = mockArc;
    }

    /// <summary>The bound chapter.</summary>
    public StoryChapter Chapter => _chapter;

    /// <summary>The golden three acts executor (timing invariants live here).</summary>
    public GoldenActs Golden => _golden;

    /// <summary>The armed 打脸/扮猪 row (null when this level has none).</summary>
    public MockArcRow? MockArc { get; }

    /// <summary>Current lifecycle phase.</summary>
    public NarrativePhase Phase { get; private set; } = NarrativePhase.NotStarted;

    /// <summary>Number of golden acts fired so far (delegates to <see cref="GoldenActs.FiredCount"/>).</summary>
    public int ActsCompleted => _golden.FiredCount;

    /// <summary>Current trust badge (host keeps it in sync via <see cref="SetTrust"/>).</summary>
    public TrustBadge Badge => _badge;

    /// <summary>Active persona (host keeps it in sync via <see cref="SetPersona"/>).</summary>
    public SystemPersona Persona => _persona;

    /// <summary>
    /// Begins the chapter: emits the opening lines and enters
    /// <see cref="NarrativePhase.InChapter"/>. Double begin is rejected.
    /// </summary>
    /// <exception cref="InvalidOperationException">Already begun.</exception>
    public IReadOnlyList<DialogueLine> Begin()
    {
        if (Phase != NarrativePhase.NotStarted)
        {
            throw new InvalidOperationException($"Begin called twice (phase={Phase}).");
        }

        Phase = NarrativePhase.InChapter;
        return _chapter.Opening;
    }

    /// <summary>
    /// One wave-start beat: emits the armed mock-arc taunts anchored to this
    /// wave, then the golden act's lines when the next unfired act's
    /// predicate matches the wave. Ordering is owned by
    /// <see cref="GoldenActs"/> (acts fire strictly in sequence).
    /// </summary>
    public IReadOnlyList<DialogueLine> OnWaveStart(int wave, long tick)
    {
        RequireInChapter();
        var lines = new List<DialogueLine>();
        if (MockArc is { } arc)
        {
            foreach (MockArcTaunt taunt in MockArcRulebook.TauntsDue(arc, wave))
            {
                lines.Add(new DialogueLine(
                    $"mock.{arc.LevelId}.taunt.w{taunt.Wave}", taunt.Speaker, taunt.Text, null, null));
            }
        }

        if (_golden.Offer(ActPredicateKind.WaveStart, wave, null, tick) is { } act)
        {
            lines.AddRange(act.Lines);
        }

        return lines;
    }

    /// <summary>
    /// One cinematic-fired beat (host routes <c>CinematicHookRouter</c>
    /// launches through here so the binding act can reuse the existing
    /// performance, e.g. <c>cinematic:e1-aha</c>).
    /// </summary>
    public IReadOnlyList<DialogueLine> OnCinematicFired(string scriptId, long tick)
    {
        ArgumentNullException.ThrowIfNullOrWhiteSpace(scriptId);
        RequireInChapter();
        return _golden.Offer(ActPredicateKind.CinematicFired, 0, scriptId, tick) is { } act
            ? act.Lines
            : Array.Empty<DialogueLine>();
    }

    /// <summary>
    /// The victory beat: fires a victory-predicate act when due, then —
    /// once the acts are complete — emits the handover lines plus the
    /// 卡点预告 (silhouette + threat line rendered as narrator lines) and
    /// closes the chapter. The tease rides the SAME hook the level declares
    /// for its ending (<c>ChapterEnding.HookId</c>).
    /// </summary>
    public IReadOnlyList<DialogueLine> OnVictory(long tick)
    {
        RequireInChapter();
        var lines = new List<DialogueLine>();
        if (_golden.Offer(ActPredicateKind.Victory, 0, null, tick) is { } act)
        {
            lines.AddRange(act.Lines);
        }

        if (!_golden.Complete)
        {
            return lines; // acts not done — no handover yet
        }

        lines.AddRange(_chapter.Ending.Lines);
        lines.Add(new DialogueLine(
            $"{_chapter.Id}.tease.silhouette", "旁白", _chapter.Ending.Tease.SilhouetteZh, null, null));
        lines.Add(new DialogueLine(
            $"{_chapter.Id}.tease.threat", "旁白", _chapter.Ending.Tease.ThreatLineZh, null, null));
        Phase = NarrativePhase.Ended;
        return lines;
    }

    /// <summary>
    /// One wave end (波间): draws the next intermission line from the CURRENT
    /// trust tier's pool, persona-weighted — the first line tagged with the
    /// active persona in rotation order; an untagged-pool tier falls back
    /// to its first line. Deterministic round-robin (no sampling).
    /// </summary>
    public DialogueLine? OnWaveEnd()
    {
        RequireInChapter();
        if (Phase != NarrativePhase.InChapter)
        {
            return null;
        }

        IReadOnlyList<DialogueLine> pool = _chapter.IntermissionPools[_badge];
        int cursor = _intermissionCursor[TierIndex(_badge)];
        for (int offset = 0; offset < pool.Count; offset++)
        {
            DialogueLine candidate = pool[(cursor + offset) % pool.Count];
            if (candidate.Persona == _persona)
            {
                _intermissionCursor[TierIndex(_badge)] = (cursor + offset + 1) % pool.Count;
                return candidate;
            }
        }

        // Pool carries no line for the active persona — tier fallback is the
        // pool head (persona changes wording only; the draw never starves).
        _intermissionCursor[TierIndex(_badge)] = (cursor + 1) % pool.Count;
        return pool[cursor];
    }

    /// <summary>Host sync: trust badge changed (F.4 modulation is wording-only).</summary>
    public void SetTrust(TrustBadge badge) => _badge = badge;

    /// <summary>Host sync: persona switched (F-109 — 只改表达不改能力).</summary>
    public void SetPersona(SystemPersona persona) => _persona = persona;

    private static int TierIndex(TrustBadge badge) => badge switch
    {
        TrustBadge.Skeptical => 0,
        TrustBadge.Trustworthy => 1,
        TrustBadge.Devoted => 2,
        _ => 0,
    };

    private void RequireInChapter()
    {
        if (Phase == NarrativePhase.NotStarted)
        {
            throw new InvalidOperationException("Beat before Begin — call Begin first.");
        }
    }
}
