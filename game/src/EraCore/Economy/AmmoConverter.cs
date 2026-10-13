using EraCore.Contracts.Foundation;
using EraCore.Energy;
using EraCore.Logistics;

namespace EraCore.Economy;

/// <summary>
/// Ammo crafting building (加工, C-06 E1 thin, T-034, F-020/F-021): pulls
/// recipe inputs from its input hopper (fed by Logistics porters through
/// <see cref="ICargoReceiver"/>), crafts whole ammo items on a tick-based
/// timer scaled by the §5.3 efficiency chain
/// <see cref="EfficiencyLaw.Min"/>(supply rate of the hopper, energy
/// satisfaction of its <see cref="EnergyLedger"/> node) — an energy
/// satisfaction of 0.5 halves the craft rate exactly (能源 0.5 → 加工 0.5 →
/// 弹药产出减半). One finished ammo item is <see cref="ShotsPerItem"/> = 10
/// shots (v3.2 §5 L75 1 件弹药 = 10 发; same anchor as the Logistics tier
/// constant, equality pinned by AmmoConverterTests). Finished items wait
/// in the output buffer for the tower side (<see cref="ICargoSupplier"/>),
/// and every completion is logged as a structured
/// <see cref="CraftCompleted"/> record — the 弹药入塔 observability seam
/// until Combat wiring (bus-facing <c>(tick, seq)</c> events land with
/// C-12/K-8). A FULL output buffer is backpressure, not a stall (满仓停产):
/// efficiency reads 0, the running craft freezes mid-progress, and inputs
/// are never destroyed — draining the buffer resumes exactly where the
/// craft stopped (v3.2 §5 L75 断链减速不停摆; 断供 proportional supply is
/// T-048). Deterministic: pure per-tick arithmetic, no RNG (CS-1.4); same
/// seed + tick sequence ⇒ equal <see cref="ComputeStateHash"/>.
/// </summary>
public sealed class AmmoConverter : ICargoReceiver, ICargoSupplier
{
    private const ulong FnvOffset = 14695981039346656037UL;
    private const ulong FnvPrime = 1099511628211UL;

    private readonly IClock _clock;
    private readonly EnergyLedger _ledger;
    private readonly EnergyNodeId _energyNode;
    private readonly AmmoRecipe _recipe;
    private readonly Dictionary<ResourceKey, int> _hopper = new();
    private readonly List<CraftCompleted> _craftLog = new();
    private long _lastEventSeq;

    private bool _crafting;
    private double _remainingCraftTicks;
    private int _outputBuffer;
    private long _cumulativeCrafts;

    /// <summary>
    /// Places the converter and registers its energy demand on
    /// <paramref name="ledger"/> (<see cref="E1EnergyDemandPerSecond"/>,
    /// tagged with the ammo resource it supplies).
    /// </summary>
    /// <param name="clock">Deterministic time source (K-1, 30 Hz domain) — stamps craft events.</param>
    /// <param name="ledger">Shared era energy ledger (C-08, read-only reuse).</param>
    /// <param name="energyNode">Consumer node id for this building on the ledger.</param>
    /// <param name="recipe">Crafting recipe (inputs → ammo item; see <see cref="AmmoRecipe"/>).</param>
    /// <exception cref="ArgumentNullException">any argument is null.</exception>
    /// <exception cref="ArgumentException">the recipe shape is invalid (no inputs, non-positive amounts, duplicate input resources, or craft ticks &lt; 1).</exception>
    public AmmoConverter(IClock clock, EnergyLedger ledger, EnergyNodeId energyNode, AmmoRecipe recipe)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(energyNode);
        ValidateRecipe(recipe);

        _clock = clock;
        _ledger = ledger;
        _energyNode = energyNode;
        _recipe = recipe;
        foreach (AmmoRecipe.Input input in recipe.Inputs)
        {
            _hopper[input.Resource] = 0;
        }

        ledger.RegisterConsumer(energyNode, E1EnergyDemandPerSecond, supplies: recipe.AmmoResource);
    }

    /// <summary>
    /// Crafting recipe of one ammo item (content-table shape, F-023): the
    /// input basket plus the tick cost of one craft. E1 anchor:
    /// <see cref="E1ArrowRecipe"/>.
    /// </summary>
    /// <param name="Inputs">Input basket — each entry a resource and a whole-item amount ≥ 1; at least one entry; no duplicate resources.</param>
    /// <param name="AmmoResource">Ammo item produced (K-2 typed key, e.g. era1:ammo-arrow).</param>
    /// <param name="CraftTicks">Ticks one craft takes at efficiency 1, ≥ 1 (30 Hz tick domain).</param>
    public sealed record AmmoRecipe(IReadOnlyList<AmmoRecipe.Input> Inputs, ResourceKey AmmoResource, int CraftTicks)
    {
        /// <summary>One basket entry: <paramref name="Items"/> whole items of <paramref name="Resource"/>.</summary>
        public sealed record Input(ResourceKey Resource, int Items);
    }

    /// <summary>
    /// Structured craft-completion record (F-020 observability): one ammo
    /// item left the machine on <paramref name="Tick"/> — the tower-side
    /// 弹药入塔 counter follows from these (shots = items × 10). The
    /// bus-facing <c>IGameEvent</c> twin lands with C-12/K-8.
    /// </summary>
    /// <param name="Tick">Simulation tick of the completion.</param>
    /// <param name="Seq">Per-converter completion counter, strictly increasing from 1.</param>
    /// <param name="AmmoResource">Ammo item crafted.</param>
    /// <param name="Items">Items completed — always 1 per craft in E1 thin.</param>
    /// <param name="Shots">Shots the items stand for (<see cref="ShotsPerItem"/> per item).</param>
    public sealed record CraftCompleted(long Tick, long Seq, ResourceKey AmmoResource, int Items, int Shots);

    /// <summary>
    /// Frozen conversion anchor: 1 件弹药 = 10 发 (v3.2 §5 L75) — equals
    /// <see cref="Carrier.ShotsPerItem"/>; AmmoConverterTests pins the
    /// equality so the two constants cannot drift.
    /// </summary>
    public const int ShotsPerItem = 10;

    /// <summary>
    /// E1 anchor recipe: 2 件木材 → 1 件箭矢弹药, 60 ticks (2 s at 30 Hz) per
    /// craft. [NEW-T-034] v3.2 pins only 1 件=10 发; the basket and craft
    /// time are the thin-era anchors later tasks calibrate against.
    /// </summary>
    public static AmmoRecipe E1ArrowRecipe { get; } = new(
        new[] { new AmmoRecipe.Input(new ResourceKey("era1:res-wood"), 2) },
        new ResourceKey("era1:ammo-arrow"),
        E1CraftTicks);

    /// <summary>Craft time of the E1 anchor recipe in ticks (2 s at 30 Hz). [NEW-T-034]</summary>
    public const int E1CraftTicks = 60; // ci-allow: R7 T-034 [NEW-T-034] E1 薄版配方工艺时长锚点（2s×30Hz；v3.2 仅钉 1 件=10 发，时长为任务卡锚点）——内容注册表接管配方前的文档化域常量，处置见 REV-20261013-wave1-round3

    /// <summary>
    /// Energy demand of one converter in points/s.
    /// [NEW-T-034] v3.2 pins no E1 converter demand; 2.0/s keeps a
    /// one-labor-camp economy (10/s) able to feed several converters while
    /// still starving under a 2× demand shock (integration test seam).
    /// </summary>
    public const double E1EnergyDemandPerSecond = 2.0;

    /// <summary>
    /// Hopper room per input resource in whole items.
    /// [NEW-T-034] 40 items = 20 anchor crafts — one porter can top the
    /// hopper up without immediate refusal (待卸队列 keeps the rest queued).
    /// </summary>
    public const int HopperCapacityPerInput = 40; // ci-allow: R7 T-034 [NEW-T-034] 料斗容量=40 件（20 次锚定 crafting）文档化域常量，同 E1CraftTicks 口径

    /// <summary>
    /// Output buffer size in ammo items.
    /// [NEW-T-034] 10 items = 100 shots — a tower magazine's worth of
    /// headroom before 满仓停产 backpressure sets in.
    /// </summary>
    public const int OutputBufferCapacity = 10;

    /// <summary>Recipe in force (inputs, ammo resource, craft ticks).</summary>
    public AmmoRecipe Recipe => _recipe;

    /// <summary>Ammo items waiting in the output buffer, 0..<see cref="OutputBufferCapacity"/>.</summary>
    public int OutputBuffer => _outputBuffer;

    /// <summary>Crafts completed since construction.</summary>
    public long CumulativeCrafts => _cumulativeCrafts;

    /// <summary>Remaining ticks of the running craft; null while idle between crafts.</summary>
    public double? RemainingCraftTicks => _crafting ? _remainingCraftTicks : null;

    /// <summary>Structured completion log, in (tick, seq) order.</summary>
    public IReadOnlyList<CraftCompleted> CraftLog => _craftLog;

    /// <summary>True when the output buffer is full — the machine is paused (满仓停产), not stalled.</summary>
    public bool IsBackpressured => _outputBuffer >= OutputBufferCapacity;

    /// <summary>
    /// Supply-rate dimension of the §5.3 chain: 1 while a craft is running
    /// (its inputs are already paid into the work slot) or the hopper holds
    /// a complete basket for the next one; 0 when starved (E1 binary —
    /// proportional supply rates land with T-048).
    /// </summary>
    public double SupplyRate => _crafting || HasFullBasket() ? 1.0 : 0.0;

    /// <summary>Energy-satisfaction dimension: this node's ledger value (需求≤0 恒 1 lives there).</summary>
    public double EnergySatisfaction => _ledger.SatisfactionOf(_energyNode);

    /// <summary>
    /// Current craft efficiency: 0 while backpressured (输出满→停产：效率降
    /// 0 但不销毁输入——降速不停摆语义与 §5.3 一致：缺的是输出空间，不是
    /// 输入), otherwise <see cref="EfficiencyLaw.Min"/> of supply and
    /// energy satisfaction.
    /// </summary>
    public double Efficiency => IsBackpressured ? 0.0 : EfficiencyLaw.Min(SupplyRate, EnergySatisfaction);

    /// <summary>
    /// Advances exactly one simulation tick: (1) under backpressure the
    /// tick is a no-op — the running craft freezes and nothing is consumed
    /// or destroyed; (2) otherwise an idle machine with a full basket in
    /// the hopper pays the inputs into the work slot and starts the craft
    /// timer at <see cref="AmmoRecipe.CraftTicks"/>; (3) the timer
    /// advances by the current efficiency (a 0.5 satisfaction halves the
    /// per-tick progress — the craft takes twice the ticks); (4) a timer
    /// that reaches 0 emits one ammo item into the output buffer and logs
    /// the <see cref="CraftCompleted"/> record.
    /// </summary>
    public void Tick()
    {
        if (IsBackpressured)
        {
            return;
        }

        if (!_crafting && HasFullBasket())
        {
            foreach (AmmoRecipe.Input input in _recipe.Inputs)
            {
                _hopper[input.Resource] -= input.Items;
            }

            _remainingCraftTicks = _recipe.CraftTicks;
            _crafting = true;
        }

        if (!_crafting)
        {
            return; // starved: supply 0, efficiency 0 — idle without consuming anything
        }

        _remainingCraftTicks -= Efficiency;

        if (_remainingCraftTicks <= 1e-9)
        {
            _outputBuffer++;
            _cumulativeCrafts++;
            _crafting = false;
            _lastEventSeq++;
            _craftLog.Add(
                new CraftCompleted(_clock.Tick, _lastEventSeq, _recipe.AmmoResource, Items: 1, Shots: ShotsPerItem));
        }
    }

    /// <summary>
    /// <see cref="ICargoReceiver"/> seam for porters delivering recipe
    /// inputs: accepts up to <paramref name="amount"/> items of a recipe
    /// input into the hopper (room = <see cref="HopperCapacityPerInput"/>
    /// per input) and returns the accepted part in [0, amount] (0 = wrong
    /// resource or hopper full — the porter stays queued and retries;
    /// accept 才卸).
    /// </summary>
    /// <exception cref="ArgumentNullException">resource is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">amount &lt; 0.</exception>
    public int TryAccept(ResourceKey resource, int amount)
    {
        ArgumentNullException.ThrowIfNull(resource);
        if (amount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(amount), amount, "Accept amount must be non-negative.");
        }

        if (!_hopper.TryGetValue(resource, out int stocked))
        {
            return 0; // not part of this recipe — refuse without side effects
        }

        int accepted = Math.Min(amount, HopperCapacityPerInput - stocked);
        if (accepted > 0)
        {
            _hopper[resource] = stocked + accepted;
        }

        return accepted;
    }

    /// <summary>
    /// <see cref="ICargoSupplier"/> seam for the tower side (弹药入塔):
    /// hands over up to <paramref name="maxAmount"/> ammo items from the
    /// output buffer and returns the amount given in [0, maxAmount]. The
    /// tower's shot count is items × <see cref="ShotsPerItem"/>.
    /// </summary>
    /// <exception cref="ArgumentNullException">resource is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">maxAmount &lt; 0.</exception>
    public int TryTake(ResourceKey resource, int maxAmount)
    {
        ArgumentNullException.ThrowIfNull(resource);
        if (maxAmount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxAmount), maxAmount, "Take amount must be non-negative.");
        }

        if (!resource.Equals(_recipe.AmmoResource))
        {
            return 0;
        }

        int taken = Math.Min(maxAmount, _outputBuffer);
        _outputBuffer -= taken;
        return taken;
    }

    /// <summary>Hopper stock of one recipe input; 0 for non-recipe resources.</summary>
    /// <exception cref="ArgumentNullException">resource is null.</exception>
    public int HopperStockOf(ResourceKey resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        return _hopper.TryGetValue(resource, out int stocked) ? stocked : 0;
    }

    /// <summary>
    /// FNV-1a hash over the full observable state (hopper in recipe-input
    /// order, craft timer as raw double bits, output buffer, cumulative
    /// crafts, event log tail). Same seed + tick sequence ⇒ equal hashes.
    /// </summary>
    public long ComputeStateHash()
    {
        ulong hash = FnvOffset;
        hash = Mix(hash, (ulong)_outputBuffer);
        hash = Mix(hash, _crafting ? 1UL : 0UL);
        hash = Mix(hash, BitConverter.DoubleToUInt64Bits(_remainingCraftTicks));
        hash = Mix(hash, (ulong)_cumulativeCrafts);
        hash = Mix(hash, (ulong)_lastEventSeq);
        hash = Mix(hash, (ulong)_craftLog.Count);
        if (_craftLog.Count > 0)
        {
            CraftCompleted last = _craftLog[^1];
            hash = Mix(hash, (ulong)last.Tick);
            hash = Mix(hash, (ulong)last.Seq);
        }

        foreach (AmmoRecipe.Input input in _recipe.Inputs)
        {
            foreach (char c in input.Resource.Value)
            {
                hash = Mix(hash, c);
            }

            hash = Mix(hash, (ulong)_hopper[input.Resource]);
            hash = Mix(hash, (uint)input.Items);
        }

        foreach (char c in _recipe.AmmoResource.Value)
        {
            hash = Mix(hash, c);
        }

        return unchecked((long)hash);

        static ulong Mix(ulong hash, ulong value)
        {
            hash ^= value;
            return hash * FnvPrime;
        }
    }

    private bool HasFullBasket()
    {
        foreach (AmmoRecipe.Input input in _recipe.Inputs)
        {
            if (_hopper[input.Resource] < input.Items)
            {
                return false;
            }
        }

        return true;
    }

    private static void ValidateRecipe(AmmoRecipe recipe)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        ArgumentNullException.ThrowIfNull(recipe.AmmoResource);
        if (recipe.Inputs is null || recipe.Inputs.Count == 0)
        {
            throw new ArgumentException("A recipe needs at least one input.", nameof(recipe));
        }

        var seen = new HashSet<ResourceKey>();
        foreach (AmmoRecipe.Input input in recipe.Inputs)
        {
            if (input.Resource is null)
            {
                throw new ArgumentException("Recipe inputs must carry a resource.", nameof(recipe));
            }

            if (input.Items < 1)
            {
                throw new ArgumentException(
                    $"Recipe input {input.Resource.Value} needs at least 1 item, got {input.Items}.", nameof(recipe));
            }

            if (!seen.Add(input.Resource))
            {
                throw new ArgumentException(
                    $"Recipe input {input.Resource.Value} appears twice; merge duplicate rows.", nameof(recipe));
            }
        }

        if (recipe.CraftTicks < 1)
        {
            throw new ArgumentException(
                $"Craft time must be at least 1 tick, got {recipe.CraftTicks}.", nameof(recipe));
        }
    }
}
