using System.Text.Json;
using EraCore.Agent.Grammar;
using EraCore.Combat;
using EraCore.Contracts.Content;
using EraCore.Mods;
using EraCore.Scrolls;
using EraCore.Units;

namespace EraCore.Agent.Actions;

/// <summary>
/// One WaveGroup face of the F-095 extension (T-104): the registry entry a
/// mod authored for wave composition — its roster (the <c>enemies[]</c>
/// extension face, an ordinary key the closed schema ignores by design) and
/// the <c>spawn_wave_group</c> world-op targets its <c>world_ops[]</c>
/// declared (already validated against the closed DSL family 2 table at
/// load). Hosts read this face to extend the wave schedule's roster — the
/// sim's own spawn machinery stays untouched.
/// </summary>
/// <param name="GroupId">Namespaced WaveGroup id (<c>player-pack:pack-…</c>).</param>
/// <param name="Enemies">Enemy content ids the group contributes to the wave roster.</param>
/// <param name="SpawnOpTargets">WaveGroup ids the group's spawn_wave_group world-ops name.</param>
public sealed record ModWaveRoster(
    string GroupId,
    IReadOnlyList<string> Enemies,
    IReadOnlyList<string> SpawnOpTargets);

/// <summary>
/// F-095 mod parameter-space auto-extension (T-104): the composition stage
/// that widens the advisor's CLOSED parameter spaces with mod content AFTER
/// the C-02 loader composed the registry — mods may widen parameter spaces,
/// never the 19-verb list itself (v2.1 §16 item 4). Built from:
/// <list type="bullet">
/// <item>the caller's builtin tower catalog (file order preserved — the
/// HoldRegion default-build and golden hashes depend on it), with each
/// builtin row REBUILT from its registry overlay item so mod patches
/// (F-156 later-load-wins) reach the combat numbers;</item>
/// <item>mod Building items appended in registry id order, each gated by
/// the R7 anchor extension: era-tier cost/dps/hp bands and class DPS bands
/// computed from the BUILTIN rows of the same tier (numbers from content,
/// never hardcoded here), plus the frozen <see cref="UpgradeLaw"/> (growth
/// declaration, upgrades_to step ladder) and the projectile link
/// consistency check (a mod tower whose ammo names a registry Projectile
/// must carry that projectile's damage_per_shot and mirror its on_hit
/// primitives in the engine-executable traits block). ANY violation =
/// <see cref="ModLoadError"/> with file+line and the tower is REFUSED
/// (越带=装载错误，不是运行时崩溃) — the rest of the mod still loads;</item>
/// <item>the L1 alias dictionaries (T-090 pipeline, mod source now wired):
/// tower and scroll surfaces = builtin items plus accepted mod items; a mod
/// surface colliding with an existing surface rejects the whole tower with
/// a conflict error (K-2 conflicts surface, never silently overwrite);</item>
/// <item>the enemy catalog: first-party rows of the requested namespace
/// plus mod enemies (each pre-validated to the strict EnemySpecCatalog
/// shape), composed through the SAME production parser;</item>
/// <item>scroll definitions through the SAME frozen
/// <see cref="ScrollDataLoader"/> the first-party scrolls use, and the
/// WaveGroup roster faces.</item>
/// </list>
/// Zero drift: with zero mods registered the tower sequence equals the
/// caller's builtin catalog value-for-value in the same order, the alias
/// dictionaries equal the T-090 first-party loaders' output, and the enemy
/// catalog equals the first-party parse — pinned by tests (金样断言).
/// Cells-cap note: the BuildPlan cells cap 4+2×era is frozen K-3 law
/// (v2.1 §11.3) and deliberately NOT mod-adaptable — mods widen the
/// tower_id parameter space only; per-tower data (tier/max_tier) feeds the
/// anchor and upgrade-law gates above instead.
/// </summary>
public sealed class ModParamSpace
{
    /// <summary>
    /// tick_rate_hz of the synthetic enemy document — the K-1 frozen 30 Hz
    /// tick domain every enemies.json declares (11-spec-tables.md 单位约定).
    /// </summary>
    private const int EnemyDocTickRateHz = 30; // ci-allow: R7 K-1 30 Hz 冻结域常量（enemies.json tick_rate_hz 同值）

    private ModParamSpace(
        IReadOnlyList<TowerSpec> towers,
        EnemySpecCatalog enemies,
        IReadOnlyList<ScrollDefinition> scrolls,
        AliasDictionary towerAliases,
        AliasDictionary scrollAliases,
        IReadOnlyList<ModWaveRoster> waveRosters,
        IReadOnlyList<ModLoadError> errors)
    {
        Towers = towers;
        Enemies = enemies;
        Scrolls = scrolls;
        TowerAliases = towerAliases;
        ScrollAliases = scrollAliases;
        WaveRosters = waveRosters;
        Errors = errors;
        BuildableTowerIds = new HashSet<string>(towers.Select(static t => t.Id), StringComparer.Ordinal);
    }

    /// <summary>The extended BuildPlan tower catalog: builtin rows (file order, patched values honored) then accepted mod rows (id order).</summary>
    public IReadOnlyList<TowerSpec> Towers { get; }

    /// <summary>The extended enemy catalog (first-party namespace rows + accepted mod enemies) — the production <see cref="EnemySpecCatalog"/> shape.</summary>
    public EnemySpecCatalog Enemies { get; }

    /// <summary>Scroll definitions of every accepted scroll item (first-party + mod), id-sorted, parsed by the frozen scroll loader.</summary>
    public IReadOnlyList<ScrollDefinition> Scrolls { get; }

    /// <summary>The extended tower alias dictionary (L1 tower slot; mod towers are commandable by their Chinese aliases).</summary>
    public AliasDictionary TowerAliases { get; }

    /// <summary>The extended scroll alias dictionary (L1 ScrollControl slot).</summary>
    public AliasDictionary ScrollAliases { get; }

    /// <summary>WaveGroup roster faces (mod wave composition seam).</summary>
    public IReadOnlyList<ModWaveRoster> WaveRosters { get; }

    /// <summary>Every collected extension error, deterministic order (registry id order of the offending item).</summary>
    public IReadOnlyList<ModLoadError> Errors { get; }

    /// <summary>True when no errors were collected.</summary>
    public bool Ok => Errors.Count == 0;

    /// <summary>
    /// The BuildPlan tower_id membership set (builtin ∪ accepted mods) —
    /// feed to <see cref="JsonSchemaValidator"/> to make the closed schema
    /// validate the widened parameter space (F-095).
    /// </summary>
    public IReadOnlySet<string> BuildableTowerIds { get; }

    /// <summary>Membership probe of the widened tower_id space.</summary>
    public bool ContainsTower(string towerId) => BuildableTowerIds.Contains(towerId);

    /// <summary>
    /// Composes the extension. Pure function of the inputs (registry order
    /// is the deterministic K-2 sorted listing); never throws for content —
    /// every violation lands in <see cref="Errors"/> and the offending item
    /// is refused while the rest loads.
    /// </summary>
    /// <param name="builtinTowers">First-party tower catalog in file order (the level's own towers.json parse).</param>
    /// <param name="registry">Post-compose K-2 registry (first-party + mod items through the C-02 pipeline).</param>
    /// <param name="firstPartyEnemyNamespace">Id prefix of the first-party enemy rows the level fields (e.g. <c>era1:</c>); mod enemies always join.</param>
    public static ModParamSpace Compose(
        IReadOnlyList<TowerSpec> builtinTowers,
        IContentRegistry registry,
        string firstPartyEnemyNamespace)
    {
        ArgumentNullException.ThrowIfNull(builtinTowers);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentException.ThrowIfNullOrEmpty(firstPartyEnemyNamespace);

        var errors = new List<ModLoadError>();
        Dictionary<string, JsonContentItem> buildings = registry.List(ContentType.Building)
            .OfType<JsonContentItem>()
            .ToDictionary(static i => i.Id, StringComparer.Ordinal);
        Dictionary<string, JsonContentItem> projectiles = registry.List(ContentType.Projectile)
            .OfType<JsonContentItem>()
            .ToDictionary(static i => i.Id, StringComparer.Ordinal);

        // ---- towers: builtin rebuild (patch overlay honored) in file order
        var towers = new List<TowerSpec>(builtinTowers.Count);
        foreach (TowerSpec builtin in builtinTowers)
        {
            if (buildings.TryGetValue(builtin.Id, out JsonContentItem? overlayItem))
            {
                TowerSpec? rebuilt = TryParseTower(overlayItem, errors);
                if (rebuilt is not null)
                {
                    towers.Add(rebuilt);
                    continue;
                }
            }

            towers.Add(builtin); // registry miss / unparsable overlay: the caller's file value stands, error already recorded
        }

        // ---- mod tower candidates: registry id order, each gated
        var acceptedMods = new List<(JsonContentItem Item, TowerSpec Spec)>();
        foreach (JsonContentItem item in buildings.Values.OrderBy(static i => i.Id, StringComparer.Ordinal))
        {
            if (item.SourceModId is null || towers.Any(t => t.Id == item.Id))
            {
                continue; // first-party rows beyond the caller's catalog belong to other levels' catalogs
            }

            TowerSpec? spec = TryParseTower(item, errors);
            if (spec is null)
            {
                continue;
            }

            if (ValidateModTower(spec, item, builtinTowers, buildings, projectiles, errors))
            {
                acceptedMods.Add((item, spec));
            }
        }

        // ---- alias phase: builtin claims first (catalog order), then accepted
        // mods in id order; a collision rejects the whole mod tower (K-2).
        var rows = new List<(string Id, List<string> Surfaces)>();
        var claimed = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (TowerSpec tower in towers)
        {
            if (!buildings.TryGetValue(tower.Id, out JsonContentItem? item))
            {
                continue;
            }

            rows.Add((tower.Id, SurfacesOf(item)));
            ClaimSurfaces(tower.Id, rows[^1].Surfaces, item, claimed, errors);
        }

        foreach ((JsonContentItem item, TowerSpec spec) in acceptedMods)
        {
            var surfaces = SurfacesOf(item);
            bool conflicts = surfaces.Any(surface =>
                claimed.TryGetValue(TextNormalizer.Normalize(surface), out string? owner) && owner != spec.Id);
            if (conflicts)
            {
                errors.Add(Error(item, "/aliases", ModErrorKind.Duplicate,
                    $"tower alias collides with an already-claimed surface (K-2: conflicts surface; the tower was refused — rename the alias or patch the target)"));
                continue;
            }

            ClaimSurfaces(spec.Id, surfaces, item, claimed, errors);
            rows.Add((spec.Id, surfaces));
            towers.Add(spec);
        }

        AliasDictionary towerAliases = AliasDictionary.FromRows(
            rows.Select(static r => (r.Id, (IEnumerable<string>)r.Surfaces)));

        // ---- enemy catalog: first-party namespace rows + mod rows through
        // the production parser over one synthetic document.
        EnemySpecCatalog enemies = ComposeEnemies(registry, firstPartyEnemyNamespace, errors);

        // ---- scrolls + scroll aliases through the frozen loader
        var scrolls = new List<ScrollDefinition>();
        var scrollRows = new List<(string Id, List<string> Surfaces)>();
        foreach (JsonContentItem item in registry.List(ContentType.Scroll).OfType<JsonContentItem>())
        {
            try
            {
                ScrollDefinition definition = ScrollDataLoader
                    .Parse(JsonContentItemJson.AsCollectionDocument(item))
                    .Single();
                scrolls.Add(definition);
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException)
            {
                errors.Add(Error(item, string.Empty, ModErrorKind.Schema,
                    $"scroll item fails the frozen scroll loader at extension time: {ex.Message}"));
                continue;
            }

            scrollRows.Add((item.Id, SurfacesOf(item)));
        }

        AliasDictionary scrollAliases = AliasDictionary.FromRows(
            scrollRows.Select(static r => (r.Id, (IEnumerable<string>)r.Surfaces)));

        // ---- wave-group roster faces
        var rosters = new List<ModWaveRoster>();
        foreach (JsonContentItem item in registry.List(ContentType.WaveGroup).OfType<JsonContentItem>())
        {
            rosters.Add(ParseWaveRoster(item, errors));
        }

        return new ModParamSpace(towers, enemies, scrolls, towerAliases, scrollAliases, rosters, errors);
    }

    // ------------------------------------------------------------ towers

    private static TowerSpec? TryParseTower(JsonContentItem item, List<ModLoadError> errors)
    {
        try
        {
            return TowerSpec.LoadCollection(JsonContentItemJson.AsCollectionDocument(item)).Single();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException)
        {
            errors.Add(Error(item, string.Empty, ModErrorKind.Schema,
                $"building item does not parse as a TowerSpec: {ex.Message}"));
            return null;
        }
    }

    /// <summary>
    /// R7 anchor extension + frozen upgrade law for one mod tower. Every
    /// band derives from the BUILTIN rows of the same tier (numbers live in
    /// content — this method carries no design values). A tier without a
    /// builtin anchor row is not judged on the cost/dps band (the class and
    /// growth laws still apply — they are tier-relative, not tier-banded).
    /// </summary>
    private static bool ValidateModTower(
        TowerSpec spec,
        JsonContentItem item,
        IReadOnlyList<TowerSpec> builtin,
        Dictionary<string, JsonContentItem> buildings,
        Dictionary<string, JsonContentItem> projectiles,
        List<ModLoadError> errors)
    {
        bool accepted = true;
        TowerSpec[] sameTierWeapons = builtin
            .Where(t => t.Tier == spec.Tier && t.Dps > 0)
            .ToArray();

        // anchor bands: cost/dps/hp inside the builtin same-tier weapon band
        if (sameTierWeapons.Length > 0)
        {
            accepted &= WithinBand(errors, item, "cost_total", spec.CostTotal,
                sameTierWeapons.Min(static t => (double)t.CostTotal),
                sameTierWeapons.Max(static t => (double)t.CostTotal),
                "cost_total band (R7 anchor extension, builtin same-tier weapon towers)");
            accepted &= WithinBand(errors, item, "dps", spec.Dps,
                sameTierWeapons.Min(static t => t.Dps),
                sameTierWeapons.Max(static t => t.Dps),
                "dps band (R7 anchor extension, builtin same-tier weapon towers)");
            accepted &= WithinBand(errors, item, "hp", spec.Hp,
                builtin.Where(t => t.Tier == spec.Tier).Min(static t => (double)t.Hp),
                builtin.Where(t => t.Tier == spec.Tier).Max(static t => (double)t.Hp),
                "hp band (builtin same-tier towers)");
        }

        // frozen growth law (C-09): declared tier_growth multipliers
        foreach (string violation in UpgradeLaw.ValidateGrowthDeclaration(spec))
        {
            errors.Add(Error(item, "/tier_growth", ModErrorKind.Schema,
                $"upgrade-law violation: {violation} (R7 anchors extend to mod towers — off-curve rows are refused at load)"));
            accepted = false;
        }

        // Class-band law (SP-1a) governs the E2+ ladder rows; the E1 base
        // rows themselves sit outside it (木箭塔 10 vs 落石台 20 would both
        // fail a cross-class band), so mod towers anchored to builtin
        // same-tier rows are judged by the SAME numeric space the builtin
        // rows occupy — the anchor band above — never by a stricter law
        // than the base game (模组与本体同律，不更严).

        // declared successor row must exist and step inside the frozen ladder
        if (!string.IsNullOrEmpty(spec.UpgradesTo))
        {
            if (!buildings.TryGetValue(spec.UpgradesTo!, out JsonContentItem? targetItem))
            {
                errors.Add(Error(item, "/upgrades_to", ModErrorKind.Schema,
                    $"upgrade-law violation: upgrades_to '{spec.UpgradesTo}' names no loaded building row"));
                accepted = false;
            }
            else
            {
                TowerSpec? upper = TryParseTower(targetItem, errors);
                if (upper is not null)
                {
                    foreach (string violation in UpgradeLaw.ValidateStep(spec, upper))
                    {
                        errors.Add(Error(item, "/upgrades_to", ModErrorKind.Schema,
                            $"upgrade-law violation: {violation} (mod chains obey the frozen ×3 cost ladder)"));
                        accepted = false;
                    }
                }
            }
        }

        // projectile link (伤害走模组弹种): when the ammo id names a loaded
        // Projectile, the tower must carry that projectile's per-shot damage
        // and mirror its on_hit primitives in the executable traits block.
        accepted &= ValidateProjectileLink(spec, item, projectiles, errors);
        return accepted;
    }

    private static bool ValidateProjectileLink(
        TowerSpec spec,
        JsonContentItem item,
        Dictionary<string, JsonContentItem> projectiles,
        List<ModLoadError> errors)
    {
        if (string.IsNullOrEmpty(spec.AmmoItem) || spec.AmmoItem.IndexOf(':', StringComparison.Ordinal) < 0)
        {
            return true; // builtin ammo resource ids are not Projectile content; nothing to link
        }

        if (!projectiles.TryGetValue(spec.AmmoItem, out JsonContentItem? projectile))
        {
            return true; // not a Projectile item (builtin ammo resource) — no link to enforce
        }

        bool accepted = true;
        using JsonDocument document = JsonDocument.Parse(projectile.RawJson);
        JsonElement root = document.RootElement;
        if (root.TryGetProperty("damage_per_shot", out JsonElement damage)
            && damage.ValueKind == JsonValueKind.Number
            && damage.TryGetDouble(out double projectileDamage)
            && Math.Abs(spec.DamagePerShot - projectileDamage) > 1e-9)
        {
            errors.Add(Error(item, "/damage_per_shot", ModErrorKind.Schema,
                $"projectile link mismatch: tower damage_per_shot {spec.DamagePerShot} must equal projectile '{spec.AmmoItem}' damage_per_shot {projectileDamage} (伤害走模组弹种 — tower damage flows from its projectile)"));
            accepted = false;
        }

        foreach (JsonElement op in root.EnumerateArrayProperty("on_hit"))
        {
            string? name = op.ValueKind == JsonValueKind.Object && op.TryGetProperty("op", out JsonElement opName)
                && opName.ValueKind == JsonValueKind.String
                    ? opName.GetString()
                    : null;
            switch (name)
            {
                case "slow_percent":
                    accepted &= MirrorTrait(item, spec, op, "slow_percent", "percent", "slow_ticks", "ticks", errors, spec.AmmoItem);
                    break;
                case "burn_dps":
                    accepted &= MirrorTrait(item, spec, op, "burn_dps", "dps", "burn_ticks", "ticks", errors, spec.AmmoItem);
                    break;
                case "splash_radius_cells":
                    accepted &= MirrorTrait(item, spec, op, "splash_radius_cells", "radius_cells", null, null, errors, spec.AmmoItem);
                    break;
                default:
                    // other primitives (root/heal/attack-speed/range/…) have no
                    // engine-executable trait mirror yet — load-validated only.
                    break;
            }
        }

        return accepted;
    }

    /// <summary>
    /// One on_hit primitive and the tower's traits block must AGREE when both
    /// faces exist (identical numbers — the engine executes traits, the DSL
    /// face authors them; a disagreement means the tower does not do what its
    /// projectile says). An absent trait mirror is the T-103 status quo
    /// (effect primitives load-validated, executable mirror pending the unit
    /// status system) and stays legal — more-arrows ships exactly that shape.
    /// </summary>
    private static bool MirrorTrait(
        JsonContentItem item,
        TowerSpec spec,
        JsonElement op,
        string traitKey,
        string opArg,
        string? ticksTrait,
        string? ticksArg,
        List<ModLoadError> errors,
        string projectileId)
    {
        bool accepted = true;
        double magnitude = op.TryGetProperty(opArg, out JsonElement value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : double.NaN;
        double? mirror = spec.Traits?.GetTraitNumber(traitKey);
        if (!double.IsNaN(magnitude) && mirror is not null && Math.Abs(mirror.Value - magnitude) > 1e-9)
        {
            errors.Add(Error(item, "/traits", ModErrorKind.Schema,
                $"projectile link mismatch: on_hit '{traitKey}' ({magnitude}) disagrees with traits.{traitKey} ({mirror}) of tower '{spec.Id}' (projectile '{projectileId}') — both faces must carry identical numbers"));
            accepted = false;
        }

        if (ticksTrait is null || ticksArg is null)
        {
            return accepted;
        }

        double ticks = op.TryGetProperty(ticksArg, out JsonElement tickValue) && tickValue.ValueKind == JsonValueKind.Number
            ? tickValue.GetDouble()
            : double.NaN;
        double? mirrorTicks = spec.Traits?.GetTraitNumber(ticksTrait);
        if (!double.IsNaN(ticks) && mirrorTicks is not null && Math.Abs(mirrorTicks.Value - ticks) > 1e-9)
        {
            errors.Add(Error(item, "/traits", ModErrorKind.Schema,
                $"projectile link mismatch: on_hit '{traitKey}' ticks ({ticks}) disagrees with traits.{ticksTrait} ({mirrorTicks}) of tower '{spec.Id}' (projectile '{projectileId}')"));
            accepted = false;
        }

        return accepted;
    }

    private static bool WithinBand(
        List<ModLoadError> errors,
        JsonContentItem item,
        string field,
        double value,
        double min,
        double max,
        string label)
    {
        if (value < min - 1e-9 || value > max + 1e-9)
        {
            errors.Add(Error(item, "/" + field, ModErrorKind.Schema,
                $"{label}: {field}={value} outside [{min}, {max}] (mod tower refused — R7 anchors extend to mod space)"));
            return false;
        }

        return true;
    }

    // ------------------------------------------------------------ enemies

    private static EnemySpecCatalog ComposeEnemies(
        IContentRegistry registry,
        string firstPartyEnemyNamespace,
        List<ModLoadError> errors)
    {
        var included = new List<JsonContentItem>();
        foreach (JsonContentItem item in registry.List(ContentType.Enemy).OfType<JsonContentItem>())
        {
            bool firstParty = item.SourceModId is null
                && item.Id.StartsWith(firstPartyEnemyNamespace, StringComparison.Ordinal);
            if (!firstParty && item.SourceModId is null)
            {
                continue; // other-era first-party rows stay with their own level catalogs
            }

            if (item.SourceModId is not null && !EnemyItemParses(item, errors))
            {
                continue; // mod enemy refused; the rest of the document still composes
            }

            included.Add(item);
        }

        return EnemySpecCatalog.Parse(SyntheticEnemyDocument(included));
    }

    /// <summary>Pre-validates one mod enemy against the strict catalog shape (sp_id etc.) so one bad row cannot kill the whole catalog parse.</summary>
    private static bool EnemyItemParses(JsonContentItem item, List<ModLoadError> errors)
    {
        try
        {
            _ = EnemySpecCatalog.Parse(SyntheticEnemyDocument(new[] { item }));
            return true;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            errors.Add(Error(item, string.Empty, ModErrorKind.Schema,
                $"enemy item fails the production catalog shape: {ex.Message}"));
            return false;
        }
    }

    private static string SyntheticEnemyDocument(IReadOnlyList<JsonContentItem> items)
    {
        var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("content_type", nameof(ContentType.Enemy));
            writer.WriteNumber("tick_rate_hz", EnemyDocTickRateHz);
            writer.WriteNumber("cell_size_px", EraCore.Sim.Simulation.CellSizePx);
            writer.WritePropertyName("items");
            writer.WriteStartArray();
            foreach (JsonContentItem item in items)
            {
                JsonDocument.Parse(item.RawJson).RootElement.WriteTo(writer);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    // ------------------------------------------------------------ waves

    private static ModWaveRoster ParseWaveRoster(JsonContentItem item, List<ModLoadError> errors)
    {
        var enemies = new List<string>();
        var spawnTargets = new List<string>();
        using JsonDocument document = JsonDocument.Parse(item.RawJson);
        JsonElement root = document.RootElement;
        if (root.TryGetProperty("enemies", out JsonElement roster) && roster.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement entry in roster.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(entry.GetString()))
                {
                    errors.Add(Error(item, "/enemies", ModErrorKind.Schema,
                        "wave-group roster entry must be a non-empty enemy content id string"));
                    continue;
                }

                enemies.Add(entry.GetString()!);
            }
        }

        if (root.TryGetProperty("world_ops", out JsonElement ops) && ops.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement op in ops.EnumerateArray())
            {
                if (op.ValueKind == JsonValueKind.Object
                    && op.TryGetProperty("op", out JsonElement name)
                    && name.ValueKind == JsonValueKind.String
                    && name.GetString() == "spawn_wave_group"
                    && op.TryGetProperty("wave_group", out JsonElement target)
                    && target.ValueKind == JsonValueKind.String)
                {
                    spawnTargets.Add(target.GetString()!);
                }
            }
        }

        return new ModWaveRoster(item.Id, enemies, spawnTargets);
    }

    // ------------------------------------------------------------ aliases

    private static List<string> SurfacesOf(JsonContentItem item)
    {
        var surfaces = new List<string>();
        using JsonDocument document = JsonDocument.Parse(item.RawJson);
        JsonElement root = document.RootElement;
        foreach (string field in new[] { "name_zh", "name_en" })
        {
            if (root.TryGetProperty(field, out JsonElement name)
                && name.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(name.GetString()))
            {
                surfaces.Add(name.GetString()!);
            }
        }

        if (root.TryGetProperty("aliases", out JsonElement aliases) && aliases.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement alias in aliases.EnumerateArray())
            {
                if (alias.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(alias.GetString()))
                {
                    surfaces.Add(alias.GetString()!);
                }
            }
        }

        return surfaces;
    }

    /// <summary>Claims every normalized surface of one item; same-id duplicates are idempotent (FromRows contract).</summary>
    private static void ClaimSurfaces(
        string id,
        List<string> surfaces,
        JsonContentItem item,
        Dictionary<string, string> claimed,
        List<ModLoadError> errors)
    {
        foreach (string raw in surfaces)
        {
            string surface = TextNormalizer.Normalize(raw);
            if (surface.Length == 0)
            {
                continue;
            }

            if (claimed.TryGetValue(surface, out string? owner) && owner != id)
            {
                errors.Add(Error(item, "/aliases", ModErrorKind.Duplicate,
                    $"alias '{surface}' claimed by both '{owner}' and '{id}' (K-2: conflicts surface)"));
                continue;
            }

            claimed[surface] = id;
        }
    }

    private static ModLoadError Error(JsonContentItem item, string pointer, ModErrorKind kind, string message) =>
        new(item.SourceModId ?? string.Empty, item.SourceFile, Math.Max(1, item.SourceLine), pointer, kind, message);
}

/// <summary>JsonElement enumeration helper for optional array properties (returns empty on absence/wrong shape).</summary>
internal static class JsonElementArrayExtensions
{
    public static IEnumerable<JsonElement> EnumerateArrayProperty(this JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray()
            : Enumerable.Empty<JsonElement>();
}

/// <summary>Trait-number readers of <see cref="TowerTraits"/> (null-safe; the block is optional per tower kind).</summary>
internal static class TowerTraitNumberExtensions
{
    public static double? GetTraitNumber(this TowerTraits traits, string key) => key switch
    {
        "slow_percent" => traits.SlowPercent,
        "slow_ticks" => traits.SlowTicks,
        "splash_radius_cells" => traits.SplashRadiusCells,
        "burn_dps" => traits.BurnDps,
        "burn_ticks" => traits.BurnTicks,
        "root_ticks" => traits.RootTicks,
        "aura_radius_cells" => traits.AuraRadiusCells,
        "aura_regen_hp_per_s" => traits.AuraRegenHpPerS,
        _ => null,
    };
}
