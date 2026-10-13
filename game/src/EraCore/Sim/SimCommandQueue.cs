using EraCore.Contracts.Sim;

namespace EraCore.Sim;

/// <summary>
/// Outcome of one applied (or rejected) command (C-12, T-040). Rejections
/// are observability records only — they never mutate simulation state, so
/// the state hash of a run with a rejected command equals the hash of the
/// same run without it (T-040 boundary acceptance).
/// </summary>
/// <param name="Tick">Tick the command was applied or refused at.</param>
/// <param name="Seq">Queue sequence number of the command (enqueue order).</param>
/// <param name="Kind">Closed verb kind carried by the command (K-6).</param>
/// <param name="Accepted">Whether the command mutated state.</param>
/// <param name="RejectionReason">Machine-readable rejection code; null when accepted.</param>
public sealed record SimCommandResult(
    long Tick,
    long Seq,
    SimCommandKind Kind,
    bool Accepted,
    string? RejectionReason)
{
    /// <summary>Factory for an applied command.</summary>
    public static SimCommandResult Applied(long tick, long seq, SimCommandKind kind) =>
        new(tick, seq, kind, true, null);

    /// <summary>Factory for a rejected command.</summary>
    public static SimCommandResult Rejected(long tick, long seq, SimCommandKind kind, string reason) =>
        new(tick, seq, kind, false, reason);
}

/// <summary>
/// Ordered command intake of one simulation (C-12, T-040): presentation and
/// approved system actions enter the deterministic core ONLY through this
/// queue (03-decomposition §1.2 / K-6 SimCommand). Entries are keyed by
/// <c>(IssuedAtTick, seq)</c> — seq is issued monotonically at enqueue — and
/// <see cref="DequeueDue"/> returns everything due at (or overdue for) a tick
/// in exactly that order, so the application order inside one tick is fully
/// determined by the enqueue order.
/// </summary>
public sealed class SimCommandQueue
{
    private readonly List<Entry> _pending = new();
    private long _nextSeq;

    /// <summary>Queue entry: one command plus its enqueue sequence number.</summary>
    public sealed record Entry(long Seq, SimCommand Command);

    /// <summary>
    /// Enqueues one command and returns its sequence number (1-based,
    /// monotonic). Commands may target any future tick; a command enqueued
    /// for a tick at or before the current one is applied on the next
    /// <see cref="DequeueDue"/> call (late commands are legal — replay
    /// journals can interleave freely).
    /// </summary>
    /// <exception cref="ArgumentNullException">command is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">IssuedAtTick is negative.</exception>
    public long Enqueue(SimCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.IssuedAtTick < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(command), command.IssuedAtTick, "IssuedAtTick must be non-negative.");
        }

        long seq = ++_nextSeq;
        InsertOrdered(new Entry(seq, command));
        return seq;
    }

    /// <summary>Number of commands waiting to be applied.</summary>
    public int PendingCount => _pending.Count;

    /// <summary>Last issued sequence number; 0 when nothing was ever enqueued.</summary>
    public long LastSeq => _nextSeq;

    /// <summary>
    /// Removes and returns every command due at <paramref name="tick"/>
    /// (IssuedAtTick ≤ tick), ordered by (IssuedAtTick, Seq). Entries carry
    /// their sequence numbers so the caller can report per-command results.
    /// </summary>
    public IReadOnlyList<Entry> DequeueDue(long tick)
    {
        if (_pending.Count == 0 || _pending[0].Command.IssuedAtTick > tick)
        {
            return Array.Empty<Entry>();
        }

        var due = new List<Entry>();
        while (_pending.Count > 0 && _pending[0].Command.IssuedAtTick <= tick)
        {
            due.Add(_pending[0]);
            _pending.RemoveAt(0);
        }

        return due;
    }

    /// <summary>
    /// Inserts keeping the (IssuedAtTick, Seq) total order — the list is
    /// always sorted, so DequeueDue only ever pops from the head.
    /// </summary>
    private void InsertOrdered(Entry entry)
    {
        int index = _pending.Count;
        while (index > 0 && Compare(_pending[index - 1], entry) > 0)
        {
            index--;
        }

        _pending.Insert(index, entry);
    }

    private static int Compare(Entry left, Entry right)
    {
        int byTick = left.Command.IssuedAtTick.CompareTo(right.Command.IssuedAtTick);
        return byTick != 0 ? byTick : left.Seq.CompareTo(right.Seq);
    }
}
