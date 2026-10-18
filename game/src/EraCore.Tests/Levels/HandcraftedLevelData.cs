global using CellPoint = EraCore.CampaignLoop.CellPoint;
global using StockLine = EraCore.CampaignLoop.StockLine;
global using HandcraftedMap = EraCore.CampaignLoop.HandcraftedMap;
global using HandcraftedWaveTable = EraCore.CampaignLoop.HandcraftedWaveTable;
global using ScriptStep = EraCore.CampaignLoop.ScriptStep;
global using LevelObjectives = EraCore.CampaignLoop.LevelObjectives;
global using CinematicHook = EraCore.CampaignLoop.CinematicHook;
global using LevelUnlocks = EraCore.CampaignLoop.LevelUnlocks;
global using HandcraftedLevel = EraCore.CampaignLoop.HandcraftedLevel;
global using HandcraftedLevelSet = EraCore.CampaignLoop.HandcraftedLevelSet;
global using HandcraftedLevelSchemaException = EraCore.CampaignLoop.HandcraftedLevelSchemaException;

using EraCore.CampaignLoop;
using Xunit;

namespace EraCore.Tests.Levels;

/// <summary>
/// Test-side facade of the (T-160 promoted) production loader: repo-relative
/// walk-up discovery (RL9) plus delegation to
/// <see cref="HandcraftedLevelCatalog.Parse"/> — the parse/validation law
/// itself lives in EraCore now (one source of truth).
/// </summary>
internal static class HandcraftedLevelData
{
    /// <summary>Closed verb vocabulary of the command script (production loader face).</summary>
    public static readonly IReadOnlySet<string> ScriptVerbs = HandcraftedLevelCatalog.ScriptVerbs;

    /// <summary>Closed carrier_tier vocabulary of assign-carrier steps (production loader face).</summary>
    public static readonly IReadOnlySet<string> CarrierTiers = HandcraftedLevelCatalog.CarrierTiers;

    private static DirectoryInfo FindLevelsDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var probe = Path.Combine(dir.FullName, "game", "content", "levels");
            if (Directory.Exists(probe))
            {
                return new DirectoryInfo(probe);
            }

            dir = dir.Parent!;
        }

        throw new InvalidOperationException("未找到 game/content/levels（测试须在仓库内运行）");
    }

    /// <summary>Parses and validates one handcrafted level file by name under game/content/levels/.</summary>
    public static HandcraftedLevelSet Load(string fileName)
    {
        string path = Path.Combine(FindLevelsDir().FullName, fileName);
        Assert.True(File.Exists(path), $"手工关卡文件缺失：{fileName}");
        return Parse(File.ReadAllText(path));
    }

    /// <summary>Parses and validates a handcrafted level document from raw JSON (negative tests feed mutated strings here).</summary>
    public static HandcraftedLevelSet Parse(string json) => HandcraftedLevelCatalog.Parse(json);
}
