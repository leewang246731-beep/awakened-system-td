using EraCore.Cinematics;
using EraCore.Presentation;

namespace EraTD.Expand;

/// <summary>
/// T-152 素材路径解析（表现层共享）：ogv/序列帧/成就图标=repo 内置轨
/// （RL9-safe：相对探测 repo 走 walk-up，绝不硬编码盘符）；插画各批=双轨
/// （D21-5：开发轨=U 盘绝对路径[素材目录内容数据]优先，发布轨=repo/发布包
/// game/assets/expand-v1 聚合件回退[T-096-v02]——res:// 直放 PNG 源在导出
/// PCK 内会被导入器重映射吞掉，REV-20261015-T096 CC0 OGG 先例，故走 exe 旁
/// 松散文件轨）；banner=选关卡片美术轨（T-162——同走 exe 旁松散文件律）。
/// 根解析=与 SimHost/DevFileStore 同族的三级链（T-162 裸包降级修复，见
/// <see cref="RepoRoot"/>）。全部方法失败返回 null——缺素材自动降级（缺图
/// 纯文本/静帧），永不抛异常阻断。
/// </summary>
internal static class ExpandAssetPaths
{
    private static string? _repoRoot;

    /// <summary>
    /// 表现层美术资产根（含 game/assets 树的根），三级链解析（T-162 修复：
    /// 裸导出包[玩家机无仓库]经旧 walk-up 会全部回落无美术态——业主怒斥场景）：
    /// <list type="number">
    /// <item>env <c>ERA_DATA_ROOT</c>：发布件正规入口（start-game.bat）注入的
    /// 包内 data 子根——自身与其<b>父目录</b>（=包根，game/assets 随包在
    /// exe 旁）都探测；</item>
    /// <item>editor 特征：dev 编辑器轨=仓库根（walk-up 探测——dev 仓库形态
    /// 行为不回退）；</item>
    /// <item>exe 旁目录：发布裸包直点 exe（无环境变量）——game/assets 与
    /// game/content 随包在 exe 旁，直接可达。</item>
    /// </list>
    /// 兜底=既有 AppContext walk-up（dev 直跑 mono 临时 exe 等形态不回退）。
    /// 缓存进程级事实；全部落空返回 null。
    /// </summary>
    internal static string? RepoRoot()
    {
        if (_repoRoot is not null)
        {
            return _repoRoot;
        }

        foreach (string candidate in ArtRootCandidates())
        {
            string probe = System.IO.Path.Combine(
                candidate, VisualAssetCatalog.RepoVideoRoot.Replace('/', System.IO.Path.DirectorySeparatorChar));
            if (System.IO.Directory.Exists(probe))
            {
                _repoRoot = candidate;
                return _repoRoot;
            }
        }

        return null;
    }

    /// <summary>三级链候选根（顺序=优先级；探测由 <see cref="RepoRoot"/> 统一做）.</summary>
    private static IEnumerable<string> ArtRootCandidates()
    {
        // ① env ERA_DATA_ROOT（SimHost/DevFileStore 同族第一级）：launcher 注入的
        //    是包内 data 子根——自身（data 根即包根的形态）与父目录（launcher
        //    形态的包根）都作候选。
        if (System.Environment.GetEnvironmentVariable("ERA_DATA_ROOT") is { Length: > 0 } injected)
        {
            yield return injected;
            string trimmed = injected.TrimEnd(
                System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
            if (System.IO.Path.GetDirectoryName(trimmed) is { } parent)
            {
                yield return parent;
            }
        }

        // ② editor 特征：dev 编辑器轨=仓库根（walk-up——dev 仓库形态不回退）。
        if (Godot.OS.HasFeature("editor") && WalkUpRepoRoot() is { } editorRoot)
        {
            yield return editorRoot;
        }

        // ③ exe 旁目录：发布裸包直点 exe（game/assets、game/content 随包可达）。
        if (System.Environment.ProcessPath is { } processPath
            && System.IO.Path.GetDirectoryName(processPath) is { } exeDir)
        {
            yield return exeDir;
        }

        // ④ 兜底：程序集位置 walk-up（既有行为——dev 直跑形态不回退）。
        if (WalkUpRepoRoot() is { } walked)
        {
            yield return walked;
        }
    }

    /// <summary>从程序集位置向上探测仓库根（既有 walk-up——dev 形态兜底）.</summary>
    private static string? WalkUpRepoRoot()
    {
        var dir = new DirectoryInfo(System.IO.Path.GetDirectoryName(AppContext.BaseDirectory)!);
        while (dir is not null)
        {
            string probe = System.IO.Path.Combine(
                dir.FullName, VisualAssetCatalog.RepoVideoRoot.Replace('/', System.IO.Path.DirectorySeparatorChar));
            if (System.IO.Directory.Exists(probe))
            {
                return dir.FullName;
            }

            dir = dir.Parent!;
        }

        return null;
    }

    /// <summary>ogv 绝对路径（内置轨）；repo 不可达或文件缺失返回 null。</summary>
    internal static string? OgvPath(VisualAssetCatalog catalog, string assetId)
    {
        VisualVideoAsset? video = catalog.VideoById(assetId);
        if (video is null || RepoRoot() is not { } root)
        {
            return null;
        }

        string path = System.IO.Path.Combine(root, VisualAssetCatalog.RepoVideoRoot,
            video.File.Replace('/', System.IO.Path.DirectorySeparatorChar));
        return System.IO.File.Exists(path) ? path : null;
    }

    /// <summary>《零的真相》合成篇绝对路径；缺失返回 null。</summary>
    internal static string? CompositePath(VisualAssetCatalog catalog)
    {
        if (RepoRoot() is not { } root)
        {
            return null;
        }

        string path = System.IO.Path.Combine(root, VisualAssetCatalog.RepoVideoRoot,
            catalog.Composite.File.Replace('/', System.IO.Path.DirectorySeparatorChar));
        return System.IO.File.Exists(path) ? path : null;
    }

    /// <summary>Boss 序列帧图集绝对路径（内置轨）；缺失返回 null。</summary>
    internal static string? StripPath(VisualAssetCatalog catalog, string era, string kind)
    {
        VisualStripAsset? strip = catalog.StripFor(era, kind);
        if (strip is null || RepoRoot() is not { } root)
        {
            return null;
        }

        string path = System.IO.Path.Combine(root, VisualAssetCatalog.RepoVideoRoot,
            strip.File.Replace('/', System.IO.Path.DirectorySeparatorChar));
        return System.IO.File.Exists(path) ? path : null;
    }

    /// <summary>成就图标绝对路径（repo 内置 64px 重绘件）；缺失返回 null。</summary>
    internal static string? AchievementPath(VisualAssetCatalog catalog, string achievementId)
    {
        VisualImageAsset? icon = catalog.Achievements.FirstOrDefault(a => a.Id == achievementId);
        if (icon is null || RepoRoot() is not { } root)
        {
            return null;
        }

        string path = System.IO.Path.Combine(root, "game", "assets", "expand-v1",
            icon.File.Replace('/', System.IO.Path.DirectorySeparatorChar));
        return System.IO.File.Exists(path) ? path : null;
    }

    /// <summary>
    /// 插画绝对路径（外部轨 D21-5 双轨）：① 开发轨=目录 external_image_root
    /// （U 盘绝对路径，内容数据）；② 发布轨=repo/发布包内 game/assets/expand-v1
    /// 聚合拷贝（T-096-v02：tools/art/expand_aggregate_images.py 2×BOX 降采样
    /// 入库，build_v020.sh 装配到 exe 旁）。两轨均未命中返回 null→缺图降级。
    /// </summary>
    internal static string? ExternalImagePath(VisualAssetCatalog catalog, string imageKey)
    {
        VisualImageAsset? image = catalog.ImageByKey(imageKey);
        if (image is null)
        {
            return null;
        }

        string relative = image.File.Replace('/', System.IO.Path.DirectorySeparatorChar);
        string external = System.IO.Path.Combine(catalog.ExternalImageRoot, relative);
        if (System.IO.File.Exists(external))
        {
            return external;
        }

        if (RepoRoot() is { } root)
        {
            string bundled = System.IO.Path.Combine(
                root, "game", "assets", "expand-v1", relative);
            if (System.IO.File.Exists(bundled))
            {
                return bundled;
            }
        }

        return null;
    }

    /// <summary>外部 PNG→贴图（18 号 C6：FileAccess buffer 版，可拿错误）；失败 null。</summary>
    internal static Godot.ImageTexture? LoadPng(string? path)
    {
        if (path is null)
        {
            return null;
        }

        byte[] bytes = Godot.FileAccess.GetFileAsBytes(path);
        if (bytes is not { Length: > 0 })
        {
            return null;
        }

        var image = new Godot.Image();
        return image.LoadPngFromBuffer(bytes) == Godot.Error.Ok
            ? Godot.ImageTexture.CreateFromImage(image)
            : null;
    }

    // ------------------------------------------- T-162 选关卡片 banner 轨 --

    /// <summary>
    /// banner 目录文件名清单（决策表 <see cref="LevelCardDecisions.
    /// PickBannerFile"/> 的输入；目录不可达=空表——缺图自动降级不阻断）。
    /// </summary>
    internal static IReadOnlyList<string> BannerFileNames()
    {
        if (RepoRoot() is not { } root)
        {
            return Array.Empty<string>();
        }

        string dir = System.IO.Path.Combine(
            root, LevelCardDecisions.BannerDirRelative.Replace('/', System.IO.Path.DirectorySeparatorChar));
        if (!System.IO.Directory.Exists(dir))
        {
            return Array.Empty<string>();
        }

        return System.IO.Directory.GetFiles(dir, "*.png")
            .Select(file => System.IO.Path.GetFileName(file))
            .ToArray();
    }

    /// <summary>
    /// banner 绝对路径（选关卡片装载面）：banner 目录+决策表挑好的文件名；
    /// 根不可达或文件缺失返回 null（缺图=深色垫底卡片，选关照常可用）。
    /// </summary>
    internal static string? BannerPath(string bannerFileName)
    {
        if (bannerFileName.Length == 0 || RepoRoot() is not { } root)
        {
            return null;
        }

        string path = System.IO.Path.Combine(
            root,
            LevelCardDecisions.BannerDirRelative.Replace('/', System.IO.Path.DirectorySeparatorChar),
            bannerFileName);
        return System.IO.File.Exists(path) ? path : null;
    }
}
