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
}
