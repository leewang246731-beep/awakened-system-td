namespace EraCore.Tests.Agent.Grammar;

/// <summary>
/// 仓库相对寻址辅助（RL9：禁盘符绝对路径）——从测试程序集向上找
/// game/content/grammar 与 game/content/era1，与 Era1ContentTests 同一手法。
/// 纯辅助类，不含测试方法（T3 不适用）。
/// </summary>
internal static class GrammarTestPaths
{
    /// <summary>game/content/grammar 目录（句式/词典/种子语料）。</summary>
    public static string GrammarDir()
        => FindContentDir(Path.Combine("game", "content", "grammar"), "game/content/grammar");

    /// <summary>game/content/era1/towers.json 内容（别名词典来源）。</summary>
    public static string Era1TowersJson()
        => File.ReadAllText(Path.Combine(
            FindContentDir(Path.Combine("game", "content", "era1"), "game/content/era1"),
            "towers.json"));

    /// <summary>读取 grammar 目录下的一个文件全文。</summary>
    public static string ReadGrammarFile(string fileName)
        => File.ReadAllText(Path.Combine(GrammarDir(), fileName));

    private static string FindContentDir(string relative, string label)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var probe = Path.Combine(dir.FullName, relative);
            if (Directory.Exists(probe))
            {
                return probe;
            }

            dir = dir.Parent!;
        }

        throw new InvalidOperationException($"未找到 {label}（测试须在仓库内运行）");
    }
}
