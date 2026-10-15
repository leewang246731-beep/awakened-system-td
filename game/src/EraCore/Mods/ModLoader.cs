using System.Text;
using System.Text.Json;
using EraCore.Contracts.Content;

namespace EraCore.Mods;

/// <summary>Category of one mod-load error (drives player-facing copy and tests).</summary>
public enum ModErrorKind
{
    /// <summary>Package shape failure: bad/missing manifest, unreadable file.</summary>
    Manifest,

    /// <summary>Filesystem failure while reading a package.</summary>
    Io,

    /// <summary>Malformed JSON (parser line cited).</summary>
    Json,

    /// <summary>Item violates the per-kind closed schema matrix.</summary>
    Schema,

    /// <summary>Namespace violation (cross-namespace id without patch, bad id charset, …).</summary>
    Namespace,

    /// <summary>Missing dependency (unknown or dropped mod id).</summary>
    Dependency,

    /// <summary>Dependency cycle.</summary>
    Cycle,

    /// <summary>Duplicate content id (non-patch collision).</summary>
    Duplicate,

    /// <summary>DSL three-family violation (unknown op/verb, bad args, wrong kind).</summary>
    Dsl,

    /// <summary><c>patch:true</c> item whose target id is not loaded.</summary>
    PatchTargetMissing,
}

/// <summary>
/// One structured mod-load error: mod id (empty for first-party stage),
/// file name, 1-based line, JSON pointer, category, message. The loader
/// collects every error across every package and keeps loading the rest —
/// 坏模组弹错不崩 (v2.1 §16 item 3 / G.1 item 3).
/// </summary>
public sealed record ModLoadError(
    string ModId,
    string File,
    int Line,
    string Path,
    ModErrorKind Kind,
    string Message)
{
    /// <summary>Single-line human rendering for logs and the in-game mod dialog.</summary>
    public override string ToString() =>
        $"[{Kind}] {File}:{Line} {(string.IsNullOrEmpty(ModId) ? "(first-party)" : ModId)} {Path}: {Message}";
}

/// <summary>
/// Overlay event report (not an error): a patch item replaced an existing
/// registry entry (F-156 overlay — later load wins with a report).
/// </summary>
/// <param name="TargetId">Namespaced id that was replaced.</param>
/// <param name="Type">Content kind of the target.</param>
/// <param name="PatchModId">Mod that applied the winning patch.</param>
/// <param name="SupersededPatchModId">Earlier patch mod the winner replaced; null when the target was first-party (or mod-added) content.</param>
/// <param name="File">File the winning patch item lives in.</param>
/// <param name="Line">1-based line of the winning patch item.</param>
public sealed record ModOverlayReport(
    string TargetId,
    ContentType Type,
    string PatchModId,
    string? SupersededPatchModId,
    string File,
    int Line);

/// <summary>One discovered mod package (parsed manifest, before dependency resolution).</summary>
public sealed record ModPackage(
    string ModId,
    string NameZh,
    string NameEn,
    string Version,
    IReadOnlyList<string> Requires,
    string RootPath,
    bool IsSingleFile);

/// <summary>
/// Outcome of one <see cref="ModLoader.Compose"/> run: the final composed
/// item set (first-party + mod adds + winning patches, sorted by kind then
/// id — deterministic registration order per the K-2 contract), the mod
/// packages that loaded, every error collected on the way, and the overlay
/// reports. Immutability note: two Compose runs over the same inputs return
/// equal sequences (装载序确定化).
/// </summary>
/// <param name="Packages">Mods that loaded, in load order (topological, id-sorted tiebreak).</param>
/// <param name="Items">Final overlay items sorted by (kind, id).</param>
/// <param name="Errors">Every collected error, processing order (deterministic).</param>
/// <param name="Reports">Patch overlay events, processing order.</param>
public sealed record ModLoadResult(
    IReadOnlyList<ModPackage> Packages,
    IReadOnlyList<JsonContentItem> Items,
    IReadOnlyList<ModLoadError> Errors,
    IReadOnlyList<ModOverlayReport> Reports)
{
    /// <summary>True when no errors were collected (mods that failed are absent from <see cref="Packages"/>).</summary>
    public bool Ok => Errors.Count == 0;
}

/// <summary>
/// C-02 full mod loader (T-103, F-165): scans a mods directory (single
/// <c>.json</c> files or directory packages with a <c>mod.json</c> manifest
/// plus <c>content/</c> documents), resolves <c>requires</c> dependencies
/// into a deterministic topological load order (id-sorted tiebreak), and
/// composes first-party + modded content through ONE schema pipeline
/// (CS-2.5) into the final overlay the K-2 registry accepts.
/// <para>
/// Overlay semantics (v2.1 §16 item 3): modded items register under
/// <c>modid:contentid</c>; an item declaring <c>"patch": true</c> must name
/// an already-loaded full namespaced id and REPLACES it (later load wins,
/// reported through <see cref="ModOverlayReport"/>). Non-patch collisions,
/// cross-namespace ids, cycles, unknown dependencies and schema/DSL
/// violations are collected as <see cref="ModLoadError"/>s with file+line
/// — the loader never throws for bad content and keeps loading the rest.
/// </para>
/// </summary>
public sealed class ModLoader
{
    private const string ManifestFileName = "mod.json";
    private const string ContentDirName = "content";
    private const string FirstPartyStage = "(first-party)";

    private static readonly System.Text.RegularExpressions.Regex ModIdPattern =
        new("^[a-z][a-z0-9-]{1,31}$", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex ContentIdPattern =
        new("^[a-z][a-z0-9-]*$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Composes the final content overlay: first-party files (each a
    /// <c>{content_type, items[]}</c> document, items carrying full
    /// namespaced ids) in the given order, then the mods under
    /// <paramref name="modsRoot"/> in dependency-topological, id-sorted
    /// order. A missing/empty mods root loads zero mods with zero errors
    /// (the clean-uninstall case).
    /// </summary>
    /// <param name="firstPartyFiles">First-party content file paths (era files, scrolls, …).</param>
    /// <param name="modsRoot">Mods directory; null/missing = no mods.</param>
    public ModLoadResult Compose(IEnumerable<string> firstPartyFiles, string? modsRoot)
    {
        ArgumentNullException.ThrowIfNull(firstPartyFiles);
        var errors = new List<ModLoadError>();
        var reports = new List<ModOverlayReport>();
        var overlay = new Dictionary<(ContentType Type, string Id), JsonContentItem>();

        // ---- stage 1: first-party base (same schema matrix as mods, CS-2.5)
        foreach (string file in firstPartyFiles)
        {
            LoadContentDocument(file, mod: null, overlay, errors, reports);
        }

        // ---- stage 2: discover + parse mod manifests
        var packages = new List<ModPackage>();
        var byId = new Dictionary<string, ModPackage>(StringComparer.Ordinal);
        if (modsRoot is not null && Directory.Exists(modsRoot))
        {
            foreach (string single in SortedFiles(modsRoot))
            {
                var package = ParseSingleFileManifest(single, errors);
                if (package is not null && ClaimId(package, byId, errors))
                {
                    packages.Add(package);
                }
            }

            foreach (string dir in SortedDirectories(modsRoot))
            {
                var package = ParseDirectoryManifest(dir, errors);
                if (package is not null && ClaimId(package, byId, errors))
                {
                    packages.Add(package);
                }
            }
        }

        // ---- stage 3: dependency resolution (deterministic topo, id tiebreak)
        List<ModPackage> ordered = ResolveLoadOrder(packages, byId, errors);

        // ---- stage 4: content overlay in load order
        foreach (ModPackage package in ordered)
        {
            foreach (string file in ContentFilesOf(package))
            {
                LoadContentDocument(file, package, overlay, errors, reports);
            }
        }

        var items = overlay.Values
            .OrderBy(i => i.ContentType)
            .ThenBy(i => i.Id, StringComparer.Ordinal)
            .ToList();
        return new ModLoadResult(ordered, items, errors, reports);
    }

    /// <summary>
    /// Registers every composed item into a FRESH registry, sorted by
    /// (kind, id) — the deterministic registration order of the K-2
    /// contract. The overlay is already resolved, so
    /// <see cref="IContentRegistry.Register"/> sees each id exactly once;
    /// a duplicate throw here is a pipeline bug, never a content conflict.
    /// </summary>
    public void Apply(IContentRegistry registry, ModLoadResult result)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(result);
        foreach (JsonContentItem item in result.Items)
        {
            registry.Register(item);
        }
    }

    // ------------------------------------------------------------ discovery

    private static IEnumerable<string> SortedFiles(string dir) =>
        Directory.EnumerateFiles(dir, "*.json").Order(StringComparer.Ordinal);

    private static IEnumerable<string> SortedDirectories(string dir) =>
        Directory.EnumerateDirectories(dir).Order(StringComparer.Ordinal);

    private static IEnumerable<string> ContentFilesOf(ModPackage package) =>
        package.IsSingleFile
            ? new[] { package.RootPath }
            : Directory.EnumerateFiles(Path.Combine(package.RootPath, ContentDirName), "*.json", SearchOption.AllDirectories)
                .Order(StringComparer.Ordinal);

    private static bool ClaimId(ModPackage package, Dictionary<string, ModPackage> byId, List<ModLoadError> errors)
    {
        if (byId.ContainsKey(package.ModId))
        {
            errors.Add(new ModLoadError(
                package.ModId, ManifestFileName, 1, "/mod_id", ModErrorKind.Duplicate,
                $"another package already claims mod id '{package.ModId}' (namespaces must be unique)."));
            return false;
        }

        byId.Add(package.ModId, package);
        return true;
    }

    private static ModPackage? ParseSingleFileManifest(string file, List<ModLoadError> errors)
    {
        string name = Path.GetFileName(file);
        if (!TryReadJson(file, name, errors, out string? text, out JsonDocument? document))
        {
            return null;
        }

        JsonDocument parsed = document!;
        using (parsed)
        {
            JsonElement root = parsed.RootElement;
            string? modId = OptionalString(root, "mod_id");
            if (modId is null)
            {
                errors.Add(new ModLoadError(
                    string.Empty, name, 1, "/mod_id", ModErrorKind.Manifest,
                    $"single-file mod '{name}' lacks a 'mod_id' (single-file packages declare their header at the document root)."));
                return null;
            }

            return ParseManifestFields(root, modId, name, singleFile: true, rootPath: file, errors);
        }
    }

    private static ModPackage? ParseDirectoryManifest(string dir, List<ModLoadError> errors)
    {
        string dirName = Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        string manifestPath = Path.Combine(dir, ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            errors.Add(new ModLoadError(
                string.Empty, $"{dirName}/{ManifestFileName}", 1, string.Empty, ModErrorKind.Manifest,
                $"directory package '{dirName}' lacks a mod.json manifest."));
            return null;
        }

        if (!TryReadJson(manifestPath, $"{dirName}/{ManifestFileName}", errors, out string? _, out JsonDocument? document))
        {
            return null;
        }

        JsonDocument parsed = document!;
        using (parsed)
        {
            JsonElement root = parsed.RootElement;
            string? modId = OptionalString(root, "mod_id");
            if (modId is null)
            {
                errors.Add(new ModLoadError(
                    string.Empty, $"{dirName}/{ManifestFileName}", 1, "/mod_id", ModErrorKind.Manifest,
                    $"mod.json of '{dirName}' lacks 'mod_id'."));
                return null;
            }

            return ParseManifestFields(root, modId, $"{dirName}/{ManifestFileName}", singleFile: false, rootPath: dir, errors);
        }
    }

    private static ModPackage? ParseManifestFields(
        JsonElement root, string modId, string file, bool singleFile, string rootPath, List<ModLoadError> errors)
    {
        void ManifestError(string pointer, string message) =>
            errors.Add(new ModLoadError(modId, file, 1, pointer, ModErrorKind.Manifest, message));

        if (!ModIdPattern.IsMatch(modId))
        {
            ManifestError("/mod_id", $"mod_id '{modId}' must match lowercase 'a-z0-9-' (start letter, length 2..32).");
            return null;
        }

        string? nameZh = OptionalString(root, "name_zh");
        string? nameEn = OptionalString(root, "name_en");
        string? version = OptionalString(root, "version");
        if (nameZh is null)
        {
            ManifestError("/name_zh", "manifest requires 'name_zh'.");
            return null;
        }

        if (nameEn is null)
        {
            ManifestError("/name_en", "manifest requires 'name_en'.");
            return null;
        }

        if (version is null)
        {
            ManifestError("/version", "manifest requires 'version'.");
            return null;
        }

        var requires = new List<string>();
        if (root.TryGetProperty("requires", out JsonElement requiresElement))
        {
            if (requiresElement.ValueKind != JsonValueKind.Array
                || requiresElement.EnumerateArray().Any(e => e.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(e.GetString())))
            {
                ManifestError("/requires", "'requires' must be an array of mod id strings.");
                return null;
            }

            requires.AddRange(requiresElement.EnumerateArray().Select(e => e.GetString()!));
        }

        return new ModPackage(modId, nameZh, nameEn, version, requires, rootPath, singleFile);
    }

    private static string? OptionalString(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out JsonElement value)
            && value.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()
            : null;

    private static bool TryReadJson(
        string path, string display, List<ModLoadError> errors, out string? text, out JsonDocument? document)
    {
        text = null;
        document = null;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            errors.Add(new ModLoadError(string.Empty, display, 1, string.Empty, ModErrorKind.Io, ex.Message));
            return false;
        }

        try
        {
            document = JsonDocument.Parse(text);
            return true;
        }
        catch (JsonException ex)
        {
            int line = ex.LineNumber is { } zeroBased ? (int)zeroBased + 1 : 1;
            errors.Add(new ModLoadError(
                string.Empty, display, line, string.Empty, ModErrorKind.Json,
                $"malformed JSON: {ex.Message}"));
            return false;
        }
    }

    // ---------------------------------------------------- dependency order

    /// <summary>
    /// Deterministic load order: Kahn's algorithm, ready set always popped
    /// in id order (装载序=按 id 排序确定化, v2.1 §16 item 3). Unknown or
    /// dropped dependencies and cycles each produce errors; affected mods
    /// are excluded from the order (the rest still loads).
    /// </summary>
    internal static List<ModPackage> ResolveLoadOrder(
        List<ModPackage> packages, Dictionary<string, ModPackage> byId, List<ModLoadError> errors)
    {
        // Drop mods with missing deps (transitively: a mod requiring a
        // dropped mod is missing its dependency too).
        var dropped = new HashSet<string>(StringComparer.Ordinal);
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (ModPackage package in packages)
            {
                if (dropped.Contains(package.ModId))
                {
                    continue;
                }

                string? missing = package.Requires.FirstOrDefault(r =>
                    !byId.ContainsKey(r) || dropped.Contains(r));
                if (missing is not null)
                {
                    dropped.Add(package.ModId);
                    errors.Add(new ModLoadError(
                        package.ModId, ManifestFileName, 1, "/requires", ModErrorKind.Dependency,
                        $"requires '{missing}' which is not among the loadable mods (missing or itself failed)."));
                    changed = true;
                }
            }
        }

        // Cycle detection over the survivors.
        var color = new Dictionary<string, int>(StringComparer.Ordinal); // 0 white, 1 gray, 2 black
        var stack = new List<string>();
        var inCycle = new HashSet<string>(StringComparer.Ordinal);
        foreach (ModPackage package in packages.OrderBy(p => p.ModId, StringComparer.Ordinal))
        {
            if (dropped.Contains(package.ModId))
            {
                continue;
            }

            Visit(package.ModId);
        }

        foreach (string memberId in inCycle.OrderBy(m => m, StringComparer.Ordinal))
        {
            errors.Add(new ModLoadError(
                memberId, ManifestFileName, 1, "/requires", ModErrorKind.Cycle,
                $"part of a dependency cycle: {string.Join(" -> ", inCycle.OrderBy(m => m, StringComparer.Ordinal))}."));
        }

        // Kahn over acyclic survivors, id-ordered ready pops.
        var remaining = packages
            .Where(p => !dropped.Contains(p.ModId) && !inCycle.Contains(p.ModId))
            .ToDictionary(p => p.ModId, StringComparer.Ordinal);
        var dependsOnMe = remaining.Values.ToDictionary(p => p.ModId, _ => new List<string>(), StringComparer.Ordinal);
        var pendingDeps = remaining.Values.ToDictionary(p => p.ModId, p => p.Requires.Where(remaining.ContainsKey).Count());
        foreach (ModPackage package in remaining.Values)
        {
            foreach (string dep in package.Requires.Where(remaining.ContainsKey))
            {
                dependsOnMe[dep].Add(package.ModId);
            }
        }

        var ready = new SortedSet<string>(
            remaining.Where(kv => pendingDeps[kv.Key] == 0).Select(kv => kv.Key), StringComparer.Ordinal);
        var order = new List<ModPackage>();
        while (ready.Count > 0)
        {
            string id = ready.Min!;
            ready.Remove(id);
            order.Add(remaining[id]);
            foreach (string dependent in dependsOnMe[id])
            {
                pendingDeps[dependent]--;
                if (pendingDeps[dependent] == 0)
                {
                    ready.Add(dependent);
                }
            }
        }

        return order;

        void Visit(string id)
        {
            switch (color.GetValueOrDefault(id))
            {
                case 1:
                    // gray hit: everything from its first gray occurrence is the cycle
                    int start = stack.IndexOf(id);
                    foreach (string member in stack.Skip(start))
                    {
                        inCycle.Add(member);
                    }

                    return;
                case 2:
                    return;
                default:
                    color[id] = 1;
                    stack.Add(id);
                    foreach (string dep in byId[id].Requires.Where(d => byId.ContainsKey(d)))
                    {
                        Visit(dep);
                    }

                    stack.RemoveAt(stack.Count - 1);
                    color[id] = 2;
                    break;
            }
        }
    }

    // ---------------------------------------------------- content documents

    private static void LoadContentDocument(
        string file,
        ModPackage? mod,
        Dictionary<(ContentType Type, string Id), JsonContentItem> overlay,
        List<ModLoadError> errors,
        List<ModOverlayReport> reports)
    {
        string display = mod is null
            ? file
            : (mod.IsSingleFile
                ? Path.GetFileName(file)
                : $"{mod.ModId}/{Path.GetRelativePath(mod.RootPath, file).Replace('\\', '/')}");
        if (!TryReadJson(file, display, errors, out string? text, out JsonDocument? document))
        {
            return;
        }

        JsonDocument parsed = document!;
        using (parsed)
        {
            JsonElement root = parsed.RootElement;
            IReadOnlyDictionary<string, int> lines = ModJsonLineMap.Index(text!);
            JsonElement typeElement = default;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("content_type", out typeElement)
                || typeElement.ValueKind != JsonValueKind.String
                || !Enum.TryParse<ContentType>(typeElement.GetString(), ignoreCase: false, out ContentType type)
                || !Enum.IsDefined(type))
            {
                int typeLine = ModJsonLineMap.LineOf(lines, "/content_type");
                errors.Add(new ModLoadError(
                    mod?.ModId ?? string.Empty, display, typeLine > 0 ? typeLine : 1,
                    "/content_type", ModErrorKind.Schema,
                    $"document content_type must be a K-2 ContentType name (got {RawSummary(typeElement)})."));
                return;
            }

            if (!root.TryGetProperty("items", out JsonElement itemsElement) || itemsElement.ValueKind != JsonValueKind.Array)
            {
                errors.Add(new ModLoadError(
                    mod?.ModId ?? string.Empty, display, 1, "/items", ModErrorKind.Schema,
                    $"document of kind {type} lacks an 'items' array."));
                return;
            }

            int index = 0;
            foreach (JsonElement item in itemsElement.EnumerateArray())
            {
                string pointer = "/items/" + index;
                int line = ModJsonLineMap.LineOf(lines, pointer);
                LoadItem(type, item, pointer, line, display, mod, overlay, errors, reports, lines);
                index++;
            }
        }
    }

    private static void LoadItem(
        ContentType type,
        JsonElement item,
        string pointer,
        int line,
        string display,
        ModPackage? mod,
        Dictionary<(ContentType Type, string Id), JsonContentItem> overlay,
        List<ModLoadError> errors,
        List<ModOverlayReport> reports,
        IReadOnlyDictionary<string, int> lines)
    {
        string modId = mod?.ModId ?? string.Empty;

        void Error(string path, int at, ModErrorKind kind, string message) =>
            errors.Add(new ModLoadError(modId, display, at, path, kind, message));

        // ---- schema matrix (same pipeline first-party and modded, CS-2.5)
        IReadOnlyList<ModSchemaViolation> violations = ModContentSchema.ValidateItem(type, item, pointer, lines);
        if (violations.Count > 0)
        {
            foreach (ModSchemaViolation violation in violations)
            {
                Error(violation.Pointer, violation.Line > 0 ? violation.Line : line, violation.Kind, violation.Message);
            }

            return; // item dropped, siblings still load
        }

        string id = item.GetProperty("id").GetString()!;
        bool patch = item.TryGetProperty("patch", out JsonElement patchElement)
            && patchElement.ValueKind == JsonValueKind.True;
        string rawJson = item.GetRawText();

        // ---- namespace + overlay (modded content only)
        if (mod is null)
        {
            // first-party: ids pass through as authored (bare ids stay bare)
            if (!IsValidFirstPartyId(id))
            {
                Error(pointer + "/id", line, ModErrorKind.Namespace,
                    $"first-party id '{id}' must be namespaced 'prefix:id' or a bare lowercase id.");
                return;
            }

            if (patch)
            {
                Error(pointer + "/patch", line, ModErrorKind.Namespace,
                    "patch:true is a mod-side overlay verb; first-party content never patches.");
                return;
            }

            if (!overlay.TryAdd((type, id), Item(id, type, rawJson, display, line, sourceModId: null, patched: false)))
            {
                Error(pointer + "/id", line, ModErrorKind.Duplicate,
                    $"duplicate first-party id '{id}' under {type}.");
            }

            return;
        }

        int colon = id.IndexOf(':', StringComparison.Ordinal);
        if (patch)
        {
            if (colon < 0)
            {
                Error(pointer + "/id", line, ModErrorKind.Namespace,
                    "patch:true requires the FULL namespaced target id ('modid:contentid' or first-party 'era1:…').");
                return;
            }

            if (!overlay.TryGetValue((type, id), out JsonContentItem? target))
            {
                Error(pointer + "/id", line, ModErrorKind.PatchTargetMissing,
                    $"patch target '{id}' is not loaded under {type} (patch may only replace existing content).");
                return;
            }

            string? superseded = target.Patched ? target.SourceModId : null;
            overlay[(type, id)] = Item(id, type, rawJson, display, line, mod.ModId, patched: true);
            reports.Add(new ModOverlayReport(id, type, mod.ModId, superseded, display, line));
            return;
        }

        // non-patch modded item: bare id → own namespace; own-prefixed id kept
        string fullId;
        if (colon < 0)
        {
            if (!ContentIdPattern.IsMatch(id))
            {
                Error(pointer + "/id", line, ModErrorKind.Namespace,
                    $"content id '{id}' must be lowercase 'a-z0-9-' (namespaced automatically as '{mod.ModId}:{id}').");
                return;
            }

            fullId = mod.ModId + ":" + id;
            rawJson = RewriteId(rawJson, fullId); // downstream spec readers see the namespaced id
        }
        else
        {
            string prefix = id[..colon];
            string rest = id[(colon + 1)..];
            if (prefix == mod.ModId)
            {
                if (!ContentIdPattern.IsMatch(rest))
                {
                    Error(pointer + "/id", line, ModErrorKind.Namespace,
                        $"content id '{id}' has an invalid contentid part '{rest}'.");
                    return;
                }

                fullId = id;
            }
            else
            {
                Error(pointer + "/id", line, ModErrorKind.Namespace,
                    $"id '{id}' uses foreign namespace '{prefix}' without patch:true — mods add under their own '{mod.ModId}:' namespace or patch existing content.");
                return;
            }
        }

        if (!overlay.TryAdd((type, fullId), Item(fullId, type, rawJson, display, line, mod.ModId, patched: false)))
        {
            Error(pointer + "/id", line, ModErrorKind.Duplicate,
                $"duplicate id '{fullId}' under {type} (already loaded; use patch:true to override).");
        }

        static JsonContentItem Item(
            string id, ContentType type, string rawJson, string file, int line, string? sourceModId, bool patched)
        {
            var aliases = new List<string>();
            using (JsonDocument doc = JsonDocument.Parse(rawJson))
            {
                if (doc.RootElement.TryGetProperty("aliases", out JsonElement aliasElement))
                {
                    aliases.AddRange(aliasElement.EnumerateArray().Select(a => a.GetString()!));
                }
            }

            return new JsonContentItem(id, type, aliases, rawJson, file, line, sourceModId, patched);
        }
    }

    private static bool IsValidFirstPartyId(string id)
    {
        int colon = id.IndexOf(':', StringComparison.Ordinal);
        string bare = colon < 0 ? id : id[(colon + 1)..];
        return ContentIdPattern.IsMatch(bare) && (colon < 0 || ContentIdPattern.IsMatch(id[..colon]));
    }

    /// <summary>
    /// Re-serializes one item's raw JSON with the <c>id</c> field rewritten to
    /// its full namespaced form — a bare mod id becomes <c>modid:id</c> in the
    /// pass-through payload too, so downstream spec readers (TowerSpec, …)
    /// and the combat layer never see a namespace-stripped id that could
    /// collide across mods. Key order is preserved; '_' annotations ride along.
    /// </summary>
    private static string RewriteId(string rawJson, string fullId)
    {
        using JsonDocument document = JsonDocument.Parse(rawJson);
        var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                if (property.Name == "id")
                {
                    writer.WriteString("id", fullId);
                }
                else
                {
                    property.WriteTo(writer);
                }
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static string RawSummary(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => $"'{element.GetString()}'",
        JsonValueKind.Undefined => "absent",
        _ => element.ValueKind.ToString(),
    };
}
