using System.Text.Json;

namespace EraCore.Tests.Narrative;

// T-089 (C-24 叙事薄版) 测试基建：仓库相对寻址的叙事内容装载（RL9：走目录
// 上溯，同 CinematicContent 模式）+ 文本量统计（F-106 主线字数口径）。纯辅
// 助类，不含测试方法（T3 不适用）。

/// <summary>Repo-relative loaders and text-budget helpers for the narrative content.</summary>
internal static class NarrativeContent
{
    private static string FindContentDir(params string[] relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string probe = Path.Combine(new[] { dir.FullName }.Concat(relative).ToArray());
            if (Directory.Exists(probe))
            {
                return probe;
            }

            dir = dir.Parent!;
        }

        throw new InvalidOperationException($"未找到 game/content/{string.Join("/", relative)}（测试须在仓库内运行）");
    }

    /// <summary>Raw text of one file under game/content/narrative/.</summary>
    public static string Read(string fileName)
    {
        string dir = FindContentDir("game", "content", "narrative");
        return File.ReadAllText(Path.Combine(dir, fileName));
    }

    /// <summary>Raw text of one file under another game/content/ subdirectory.</summary>
    public static string ReadSibling(string subDir, string fileName)
    {
        string dir = FindContentDir("game", "content", subDir);
        return File.ReadAllText(Path.Combine(dir, fileName));
    }

    /// <summary>The E1 chapter straight out of content.</summary>
    public static EraCore.Narrative.StoryChapter StoryE1() =>
        EraCore.Narrative.StoryChapter.Parse(Read("story-e1.json"), "narrative/story-e1.json");

    /// <summary>The E2 chapter straight out of content.</summary>
    public static EraCore.Narrative.StoryChapter StoryE2() =>
        EraCore.Narrative.StoryChapter.Parse(Read("story-e2.json"), "narrative/story-e2.json");

    /// <summary>The E3 chapter straight out of content (T-120).</summary>
    public static EraCore.Narrative.StoryChapter StoryE3() =>
        EraCore.Narrative.StoryChapter.Parse(Read("story-e3.json"), "narrative/story-e3.json");

    /// <summary>The E4 chapter straight out of content (T-127).</summary>
    public static EraCore.Narrative.StoryChapter StoryE4() =>
        EraCore.Narrative.StoryChapter.Parse(Read("story-e4.json"), "narrative/story-e4.json");

    /// <summary>The remnant catalog straight out of content.</summary>
    public static EraCore.Narrative.RemnantCatalog Remnants() =>
        EraCore.Narrative.RemnantCatalog.Parse(Read("remnants.json"), "narrative/remnants.json");

    /// <summary>The cross-era hook registry straight out of content.</summary>
    public static EraCore.Narrative.CrossEraHookRegistry Hooks() =>
        EraCore.Narrative.CrossEraHookRegistry.Parse(Read("hooks.json"), "narrative/hooks.json");

    /// <summary>The 15-template system line library straight out of content.</summary>
    public static EraCore.Narrative.SystemLineLibrary SystemLines() =>
        EraCore.Narrative.SystemLineLibrary.Parse(Read("system-lines.json"), "narrative/system-lines.json");

    /// <summary>
    /// F-106 文本量口径：narrative 文本承载字段（正文/说话人/标题/记忆/低语/
    /// 埋点/回收/剪影/威胁台词/嘲讽）的全部码点合计——id/schema/注解键（_ 前缀）
    /// 不计。
    /// </summary>
    public static int CountNarrativeChars(params string[] fileNames) =>
        fileNames.Sum(CountNarrativeChars);

    /// <summary>Single-file variant of <see cref="CountNarrativeChars(string[])"/>.</summary>
    public static int CountNarrativeChars(string fileName)
    {
        using JsonDocument document = JsonDocument.Parse(Read(fileName));
        return CountElement(document.RootElement);
    }

    private static readonly string[] TextFields =
    {
        "text", "speaker", "title_zh", "memory_zh", "echo_whisper_zh",
        "planted_anchor_zh", "teaser_zh", "payoff_zh", "silhouette_zh",
        "threat_line_zh",
        // T-127 主线脉络数据面（story-arc.json）承载字段：叙事正文同口径计入
        "summary_zh", "acts_zh", "truth_step_zh", "persona_step_zh",
        "masterline_zh", "lineage_zh",
    };

    private static int CountElement(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
            {
                int sum = 0;
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    if (property.Name.StartsWith('_'))
                    {
                        continue; // 注解键不计
                    }

                    sum += TextFields.Contains(property.Name) && property.Value.ValueKind == JsonValueKind.String
                        ? property.Value.GetString()!.Length
                        : CountElement(property.Value);
                }

                return sum;
            }
            case JsonValueKind.Array:
                return element.EnumerateArray().Sum(CountElement);
            default:
                return 0;
        }
    }
}
