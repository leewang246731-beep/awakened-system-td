using EraCore.Cinematics;

namespace EraCore.Tests.Cinematics;

// T-086 (C-24) 测试基建：仓库相对寻址的演出内容装载（RL9：走目录上溯，
// 同 SimEra1Content 模式）。纯辅助类，不含测试方法（T3 不适用）。

/// <summary>Repo-relative loaders for the cinematic content (RL9: walk up, no absolute paths).</summary>
internal static class CinematicContent
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

    private static string Read(params string[] relative)
    {
        // The directory probe MUST carry the full game/content prefix — a
        // bare "cinematics" probe would case-insensitively match this test
        // project's own Cinematics folder on Windows.
        string fileName = relative[^1];
        string directory = FindContentDir(
            new[] { "game", "content" }.Concat(relative[..^1]).ToArray());
        return File.ReadAllText(Path.Combine(directory, fileName));
    }

    /// <summary>The E1 aha script straight out of content (F-113).</summary>
    public static CinematicScript E1Aha() =>
        CinematicScript.Parse(Read("cinematics", "e1-aha.json"), "cinematics/e1-aha.json");

    /// <summary>The E2 awakening script straight out of content (F-114).</summary>
    public static CinematicScript E2Awaken() =>
        CinematicScript.Parse(Read("cinematics", "e2-awaken.json"), "cinematics/e2-awaken.json");

    /// <summary>The T-087 victory settlement script straight out of content (F-002/F-104).</summary>
    public static CinematicScript Victory() =>
        CinematicScript.Parse(Read("cinematics", "victory.json"), "cinematics/victory.json");

    /// <summary>Raw level file text (hook rows live in items[*].cinematic_hooks).</summary>
    public static string LevelJson(string era) => Read("levels", era + ".json");

    /// <summary>The T-152 visual asset catalog straight out of content (31 号 §二/§三).</summary>
    public static EraCore.Cinematics.VisualAssetCatalog VisualAssets() =>
        EraCore.Cinematics.VisualAssetCatalog.Parse(
            Read("cinematics", "visual-assets.json"), "cinematics/visual-assets.json");

    /// <summary>One T-152 era-transition script (transition-e1-e2 … transition-e5-e6).</summary>
    public static CinematicScript Transition(string fromEra, string toEra) =>
        CinematicScript.Parse(
            Read("cinematics", $"transition-{fromEra}-{toEra}.json"),
            $"cinematics/transition-{fromEra}-{toEra}.json");

    /// <summary>The T-152 finale script (A-07 终章播片位).</summary>
    public static CinematicScript Finale() =>
        CinematicScript.Parse(Read("cinematics", "finale-zero.json"), "cinematics/finale-zero.json");

    /// <summary>Repo root (the directory containing game/assets — T-152 入库产物探针).</summary>
    public static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "game", "assets", "cinematics")))
            {
                return dir.FullName;
            }

            dir = dir.Parent!;
        }

        throw new InvalidOperationException("未找到仓库根（测试须在仓库内运行）");
    }

    /// <summary>One content file's text by repo-relative path (T-152 共享探针).</summary>
    public static string ContentFile(string relative) =>
        File.ReadAllText(Path.Combine(RepoRoot(), "game", "content",
            relative.Replace('/', Path.DirectorySeparatorChar)));
}
