using EraCore.Contracts.Content;
using EraCore.Mods;

namespace EraCore.Tests.Agent.Actions;

/// <summary>
/// 仓库相对寻址 + 装载辅助（RL9：禁盘符绝对路径；与 Mods/SampleModsTests、
/// Grammar/GrammarTestPaths 同一手法）。T-104 模组联动测试（F-095）共用：
/// 本体内容文件清单、样板模组库根、ModLoader+ContentRegistry 引导、临时
/// 模组目录（CS-3.8：系统临时目录，调用方 Dispose 清理）。纯辅助类，不含
/// 测试方法（T3 不适用）。
/// </summary>
internal static class ModLinkTestPaths
{
    private static string FindRepoMarker(string marker)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string probe = Path.Combine(dir.FullName, "game", marker);
            if (Directory.Exists(probe) || File.Exists(probe))
            {
                return Path.Combine(dir.FullName, "game");
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException($"未找到 game/{marker}（测试须在仓库内运行）");
    }

    /// <summary>game/ 仓库根（向上探测 assets 标记）。</summary>
    public static string GameDir() => FindRepoMarker("assets");

    public static string Era1File(string name) => Path.Combine(GameDir(), "content", "era1", name);

    public static string Era2File(string name) => Path.Combine(GameDir(), "content", "era2", name);

    /// <summary>样板模组库根（more-arrows / rebalance-patch / player-pack）。</summary>
    public static string SamplesDir() => Path.Combine(GameDir(), "assets", "mods", "samples");

    /// <summary>本体内容清单（与 SampleModsTests 同口径：era1+era2+卷轴）。</summary>
    public static readonly string[] FirstParty =
    {
        Era1File("era.json"),
        Era1File("towers.json"),
        Era1File("enemies.json"),
        Era2File("era.json"),
        Era2File("towers.json"),
        Era2File("enemies.json"),
        Era2File("recipes.json"),
        Era2File("levels.json"),
        Era2File("heroes.json"),
        Path.Combine(GameDir(), "content", "scrolls", "era2.json"),
    };

    /// <summary>本体内容 + 指定模组根（null=无模组的干净基准）过 C-02 全管线引导出注册表。</summary>
    public static (ModLoadResult Result, ContentRegistry Registry) Boot(string? modsRoot)
    {
        var loader = new ModLoader();
        ModLoadResult result = loader.Compose(FirstParty, modsRoot);
        var registry = new ContentRegistry();
        loader.Apply(registry, result);
        return (result, registry);
    }

    /// <summary>创建临时模组根并写入单文件模组（负测夹具；调用方负责删除）。</summary>
    public static string TempModRoot(params (string FileName, string Json)[] files)
    {
        string root = Path.Combine(Path.GetTempPath(), "eratd-t104-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        foreach ((string fileName, string json) in files)
        {
            File.WriteAllText(Path.Combine(root, fileName), json);
        }

        return root;
    }
}
