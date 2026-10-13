// T2: pure contract — K-1 foundation ports, T-019, frozen (Wave 0; 07-decided-plan D-21).

namespace EraCore.Contracts.Foundation;

/// <summary>
/// Synchronous single-threaded event bus (K-1; concurrency semantics frozen
/// by 07-decided-plan D-12): handlers are invoked inline, in publication
/// order, inside the tick that published the event — never queued, never on
/// another thread. Async dispatch would silently break deterministic replay,
/// which is a product promise (war-report replay, relic mapping,
/// leaderboards). Audio/UI consumers are relayed by the Godot adapter via
/// <c>call_deferred</c> on the presentation side, outside this contract.
/// </summary>
public interface IEventBus
{
    /// <summary>
    /// Subscribes <paramref name="handler"/> to events of type
    /// <typeparamref name="TEvent"/>. Disposing the returned token
    /// unsubscribes exactly that registration.
    /// </summary>
    /// <typeparam name="TEvent">Event type; must carry <c>(tick, seq)</c> ordering.</typeparam>
    IDisposable Subscribe<TEvent>(Action<TEvent> handler) where TEvent : IGameEvent;

    /// <summary>
    /// Publishes <paramref name="evt"/>: all matching handlers run inline and
    /// synchronously, in subscription order, before this call returns.
    /// </summary>
    /// <typeparam name="TEvent">Event type; must carry <c>(tick, seq)</c> ordering.</typeparam>
    void Publish<TEvent>(TEvent evt) where TEvent : IGameEvent;
}
