using System.Text.Json;

namespace EraCore.Cinematics;

/// <summary>
/// T-152 素材目录（31 号 v2.0 §二/§三）：`game/content/cinematics/visual-assets.json`
/// 的严格解析产物——51 条视频（五层）+8 张 Boss 序列帧+《零的真相》合成篇+
/// 73 张图（七批+成就图标 24）+六时代色板锚。纯数据：路径/时长/像素化/音频/
/// 用途全部来自内容文件（RL7），本类零硬编码数值；文件是否真实存在由表现层
/// 装载器判定（外部轨缺图自动降级，D21-5 双轨）。
/// </summary>
public sealed record VisualAssetCatalog(
    IReadOnlyList<VisualVideoAsset> Videos,
    IReadOnlyList<VisualStripAsset> Strips,
    VisualCompositeAsset Composite,
    IReadOnlyDictionary<string, IReadOnlyList<VisualImageAsset>> Images,
    IReadOnlyList<VisualImageAsset> Achievements,
    IReadOnlyDictionary<string, EraPalette> Palettes,
    int TicksPerSecond,
    string ExternalImageRoot,
    string RepoAchievementRoot)
{
    /// <summary>Content schema URN of the visual asset catalog (K-2 family).</summary>
    public const string SchemaUrn = "urn:eratd:content:visualassets:1";

    /// <summary>Content type marker the loader requires (<c>content_type</c> field).</summary>
    public const string ContentType = "VisualAssetCatalog";

    /// <summary>Repo-relative root of the transcoded video library (ogv 内置轨).</summary>
    public const string RepoVideoRoot = "game/assets/cinematics";

    /// <summary>Closed era key set (e1~e6，六时代身份卡 31 号 §1.2).</summary>
    public static readonly IReadOnlyList<string> EraKeys = new[] { "e1", "e2", "e3", "e4", "e5", "e6" };

    /// <summary>解析异常（与 CinematicSchemaException 同纪律：内容缺陷=装载失败）。</summary>
    public sealed class VisualAssetSchemaException : Exception
    {
        public VisualAssetSchemaException(string message)
            : base(message)
        {
        }
    }

    /// <summary>Video by catalog id (A-01…E-d)；未登记返回 null。</summary>
    public VisualVideoAsset? VideoById(string id) => Videos.FirstOrDefault(v => v.Id == id);

    /// <summary>Strip by catalog id (C-01…C-08)；未登记返回 null。</summary>
    public VisualStripAsset? StripById(string id) => Strips.FirstOrDefault(s => s.Id == id);

    /// <summary>Strip for a boss performance (era + enter/death kind)。</summary>
    public VisualStripAsset? StripFor(string era, string kind) =>
        Strips.FirstOrDefault(s => s.Era == era && s.Kind == kind);

    /// <summary>Image by content key across every batch (story-*/banner-*/hook-*/…)。</summary>
    public VisualImageAsset? ImageByKey(string key) =>
        Images.SelectMany(batch => batch.Value).Concat(Achievements)
            .FirstOrDefault(image => image.Id == key);

    /// <summary>One batch (banners/story/hooks/easter/skins/loading/personas)。</summary>
    public IReadOnlyList<VisualImageAsset> Batch(string name) =>
        Images.TryGetValue(name, out IReadOnlyList<VisualImageAsset>? batch)
            ? batch
            : System.Array.Empty<VisualImageAsset>();

    /// <summary>Videos of one usage slot (31 号 §三 插入点表：menu_loop/era_transition/…)。</summary>
    public IReadOnlyList<VisualVideoAsset> VideosByUsage(string usage) =>
        Videos.Where(v => v.Usage == usage).ToArray();

    /// <summary>视频时长折算演出拍数（30 tick/s 时域，K-1）。</summary>
    public int DurationTicksOf(VisualVideoAsset video) =>
        (int)Math.Round(video.DurationSeconds * TicksPerSecond, MidpointRounding.AwayFromZero);

    /// <summary>Strict parser（缺字段/未知层/未知时代/重复 id 即异常）。</summary>
    /// <exception cref="VisualAssetSchemaException">Any schema violation.</exception>
    public static VisualAssetCatalog Parse(string json, string sourceName = "visual-assets.json")
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new VisualAssetSchemaException($"{sourceName}: 素材目录为空");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new VisualAssetSchemaException($"{sourceName}: JSON 解析失败（{ex.Message}）");
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            Require(root.ValueKind == JsonValueKind.Object, $"{sourceName}: 根节点须为对象");
            string contentType = String(root, "content_type", sourceName);
            Require(
                contentType == ContentType,
                $"{sourceName}: content_type 须为 {ContentType}（得到 {contentType}）");

            int tps = Int(root, "ticks_per_second", sourceName);
            Require(tps == 30, $"{sourceName}: ticks_per_second 须为 30（K-1 固定 30Hz 时域，得到 {tps}）");

            var videos = new List<VisualVideoAsset>();
            var seenVideos = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonElement el in Array(root, "videos", sourceName).EnumerateArray())
            {
                string id = String(el, "id", sourceName);
                Require(seenVideos.Add(id), $"{sourceName}: videos.id 重复（{id}）");
                string layer = String(el, "layer", sourceName);
                Require(
                    layer is "A" or "B" or "C" or "D" or "E",
                    $"{sourceName}: videos.layer 未登记（{layer}，封闭表 A/B/C/D/E）");
                videos.Add(new VisualVideoAsset(
                    Id: id,
                    Layer: layer,
                    File: String(el, "file", sourceName),
                    DurationSeconds: Double(el, "duration_s", sourceName),
                    Pixelate: Bool(el, "pixelate", sourceName),
                    Audio: Bool(el, "audio", sourceName),
                    Usage: String(el, "usage", sourceName)));
            }

            Require(videos.Count >= 1, $"{sourceName}: videos 至少 1 条");

            // 序列帧 id 与 C 层视频同源（同一母片的双交付物：ogv+Atlas PNG）——
            // 各节内查重，节间允许同名（C-01 既是 boss-enter 视频位也是序列帧位）。
            var strips = new List<VisualStripAsset>();
            var seenStrips = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonElement el in Array(root, "strips", sourceName).EnumerateArray())
            {
                string id = String(el, "id", sourceName);
                Require(seenStrips.Add(id), $"{sourceName}: strips.id 重复（{id}）");
                strips.Add(new VisualStripAsset(
                    Id: id,
                    File: String(el, "file", sourceName),
                    Era: EraOf(el, sourceName),
                    Kind: String(el, "kind", sourceName),
                    Frames: Int(el, "frames", sourceName),
                    FrameWidth: Int(el, "frame_w", sourceName),
                    FrameHeight: Int(el, "frame_h", sourceName),
                    Cols: Int(el, "cols", sourceName)));
            }

            JsonElement compositeEl = Object(root, "composite", sourceName);
            var composite = new VisualCompositeAsset(
                Id: String(compositeEl, "id", sourceName),
                File: String(compositeEl, "file", sourceName),
                DurationSeconds: Double(compositeEl, "duration_s", sourceName),
                Requires: Array(compositeEl, "requires", sourceName)
                    .EnumerateArray().Select(r => r.GetString()!).ToArray());

            var images = new Dictionary<string, IReadOnlyList<VisualImageAsset>>(StringComparer.Ordinal);
            var seenImages = new HashSet<string>(StringComparer.Ordinal); // 图批+成就图标共用查重域
            foreach (JsonProperty batch in Object(root, "images", sourceName).EnumerateObject())
            {
                var batchItems = new List<VisualImageAsset>();
                foreach (JsonElement el in batch.Value.EnumerateArray())
                {
                    string id = String(el, "id", sourceName);
                    Require(seenImages.Add(id), $"{sourceName}: images.{batch.Name}.id 重复（{id}）");
                    batchItems.Add(new VisualImageAsset(
                        Id: id,
                        File: String(el, "file", sourceName),
                        Era: EraOf(el, sourceName)));
                }

                Require(batchItems.Count >= 1, $"{sourceName}: images.{batch.Name} 至少 1 张");
                images[batch.Name] = batchItems;
            }

            var achievements = new List<VisualImageAsset>();
            foreach (JsonElement el in Array(root, "achievements", sourceName).EnumerateArray())
            {
                string id = String(el, "id", sourceName);
                Require(seenImages.Add(id), $"{sourceName}: achievements.id 重复（{id}）");
                achievements.Add(new VisualImageAsset(
                    Id: id, File: String(el, "file", sourceName), Era: "e1"));
            }

            var palettes = new Dictionary<string, EraPalette>(StringComparer.Ordinal);
            foreach (JsonProperty palette in Object(root, "palettes", sourceName).EnumerateObject())
            {
                Require(
                    EraKeys.Contains(palette.Name),
                    $"{sourceName}: palettes 键未登记（{palette.Name}，封闭表 {string.Join("/", EraKeys)}）");
                Require(palettes.TryAdd(palette.Name, new EraPalette(
                    LabelZh: String(palette.Value, "label_zh", sourceName),
                    Primary: ParseHex(String(palette.Value, "primary", sourceName), sourceName, palette.Name),
                    Secondary: Array(palette.Value, "secondary", sourceName)
                        .EnumerateArray().Select(s => ParseHex(s.GetString()!, sourceName, palette.Name)).ToArray(),
                    MotifZh: String(palette.Value, "motif_zh", sourceName))),
                    $"{sourceName}: palettes.{palette.Name} 重复");
            }

            Require(palettes.Count == EraKeys.Count, $"{sourceName}: palettes 须覆盖六时代（得到 {palettes.Count}）");

            return new VisualAssetCatalog(
                videos, strips, composite, images, achievements, palettes, tps,
                ExternalImageRoot: String(root, "external_image_root", sourceName),
                RepoAchievementRoot: String(root, "repo_achievement_root", sourceName));
        }
    }

    private static string EraOf(JsonElement el, string source)
    {
        string era = String(el, "era", source);
        Require(
            EraKeys.Contains(era),
            $"{source}: era 未登记（{era}，封闭表 {string.Join("/", EraKeys)}）");
        return era;
    }

    private static (int R, int G, int B) ParseHex(string hex, string source, string label)
    {
        Require(
            hex.Length == 6 && hex.All(c => char.IsAsciiHexDigit(c)),
            $"{source}: palettes.{label} 颜色须为 6 位 hex（得到 {hex}）");
        return (Convert.ToInt32(hex[..2], 16), Convert.ToInt32(hex[2..4], 16), Convert.ToInt32(hex[4..], 16));
    }

    // ---- JSON helpers（与 CinematicScript 同约定） ----------------------------

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new VisualAssetSchemaException(message);
        }
    }

    private static string String(JsonElement parent, string name, string source)
    {
        Require(parent.ValueKind == JsonValueKind.Object, $"{source}: {name} 所在节点须为对象");
        if (!parent.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.String)
        {
            throw new VisualAssetSchemaException($"{source}: 缺字符串字段 {name}");
        }

        return value.GetString()!;
    }

    private static int Int(JsonElement parent, string name, string source)
    {
        if (!parent.TryGetProperty(name, out JsonElement value)
            || value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt32(out int parsed))
        {
            throw new VisualAssetSchemaException($"{source}: 缺整数字段 {name}");
        }

        return parsed;
    }

    private static double Double(JsonElement parent, string name, string source)
    {
        if (!parent.TryGetProperty(name, out JsonElement value)
            || value.ValueKind != JsonValueKind.Number
            || !value.TryGetDouble(out double parsed))
        {
            throw new VisualAssetSchemaException($"{source}: 缺数值字段 {name}");
        }

        return parsed;
    }

    private static bool Bool(JsonElement parent, string name, string source)
    {
        if (!parent.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.True
            && value.ValueKind != JsonValueKind.False)
        {
            throw new VisualAssetSchemaException($"{source}: 缺布尔字段 {name}");
        }

        return value.ValueKind == JsonValueKind.True;
    }

    private static JsonElement Array(JsonElement parent, string name, string source)
    {
        Require(parent.ValueKind == JsonValueKind.Object, $"{source}: {name} 所在节点须为对象");
        if (!parent.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.Array)
        {
            throw new VisualAssetSchemaException($"{source}: 缺数组字段 {name}");
        }

        return value;
    }

    private static JsonElement Object(JsonElement parent, string name, string source)
    {
        Require(parent.ValueKind == JsonValueKind.Object, $"{source}: {name} 所在节点须为对象");
        if (!parent.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.Object)
        {
            throw new VisualAssetSchemaException($"{source}: 缺对象字段 {name}");
        }

        return value;
    }
}

/// <summary>One transcoded video clip（ogv 内置轨相对路径 = RepoVideoRoot + File）。</summary>
public sealed record VisualVideoAsset(
    string Id,
    string Layer,
    string File,
    double DurationSeconds,
    bool Pixelate,
    bool Audio,
    string Usage);

/// <summary>One Boss sequence-frame atlas（战斗内零视频铁律的载体，C 层）。</summary>
public sealed record VisualStripAsset(
    string Id,
    string File,
    string Era,
    string Kind,
    int Frames,
    int FrameWidth,
    int FrameHeight,
    int Cols);

/// <summary>《零的真相》合成篇（集齐 requires 记忆段解锁，零生成重剪）。</summary>
public sealed record VisualCompositeAsset(
    string Id,
    string File,
    double DurationSeconds,
    IReadOnlyList<string> Requires);

/// <summary>One illustration asset（外部轨 D21-5：开发期 U 盘/发布期 res://）。</summary>
public sealed record VisualImageAsset(string Id, string File, string Era);

/// <summary>六时代身份卡色板锚（31 号 §1.2：主色+辅助色+母题名）。</summary>
public sealed record EraPalette(
    string LabelZh,
    (int R, int G, int B) Primary,
    IReadOnlyList<(int R, int G, int B)> Secondary,
    string MotifZh);
