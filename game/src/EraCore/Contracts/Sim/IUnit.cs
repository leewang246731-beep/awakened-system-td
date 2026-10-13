// T2: pure contract — K-6 sim-side draft (T-023 remainder).

using EraCore.Contracts.Foundation;

namespace EraCore.Contracts.Sim;

/// <summary>
/// A simulated unit — enemy, elite, boss or hero (K-6 draft). This is the
/// frozen seam that lets C-09 (towers/weapons) and C-10 (unit system)
/// proceed in parallel: towers read targeting state, units take damage,
/// neither owns the other's internals (03-decomposition §3.1). Field
/// semantics derive from design v3.2 §6 (damage formula, armor clamp,
/// shields from E3, movement along the flow field).
/// </summary>
public interface IUnit
{
    /// <summary>Stable identity within the current level run.</summary>
    EntityId Id { get; }

    /// <summary>Content id of the enemy/hero definition (K-2 registry, e.g. "e2_ram").</summary>
    string ContentId { get; }

    /// <summary>World position X in pixels (32 px = 1 grid cell; units move along the flow field).</summary>
    float X { get; }

    /// <summary>World position Y in pixels.</summary>
    float Y { get; }

    /// <summary>Current hit points.</summary>
    int Hp { get; }

    /// <summary>Maximum hit points.</summary>
    int MaxHp { get; }

    /// <summary>
    /// Armor in points, clamped ≥ 0. Damage applies
    /// max(d×decay − armor, 10%×d×decay) (v3.2 §6).
    /// </summary>
    int Armor { get; }

    /// <summary>
    /// Shield points — E3 onwards (breaks, then regenerates 20/s after 5 s
    /// unless a pierce hit interrupts; v3.2 §6). Always 0 before E3.
    /// </summary>
    int Shield { get; }

    /// <summary>Movement speed in px per simulation tick; tick domain = fixed 30 Hz (K-1 IClock).</summary>
    float SpeedPxPerTick { get; }
}
