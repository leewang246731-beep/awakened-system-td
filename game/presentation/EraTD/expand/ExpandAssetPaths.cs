using EraCore.Cinematics;

namespace EraTD.Expand;

/// <summary>
/// T-152 素材路径解析（表现层共享）：ogv/序列帧/成就图标=repo 内置轨
/// （RL9-safe：相对探测 repo 走 walk-up，绝不硬编码盘符）；插画各批=双轨
/// （D21-5：开发轨=U 盘绝对路径[素材目录内容数据]优先，发布轨=repo/发布包
/// game/assets/expand-v1 聚合件回退[T-096-v02]——res:// 直放 PNG 源在导出
/// PCK 内会被导入器重映射吞掉，REV-20261015-T096 CC0 OGG 先例，故走 exe 旁
/// 松散文件轨）。全部方法失败返回 null——缺素材自动降级（缺图纯文本/静帧），
/// 永不抛异常阻断。
/// </summary>
internal static class ExpandAssetPaths
{
    private static string? _repoRoot;

    /// <summary>
    /// Repo root (contains game/assets/cinematics)，从程序集位置向上探测；
    /// dev/headless 均可解析；找不到（发布裸包）返回 null。
    /// </summary>
    internal static string? RepoRoot()
    {
        if (_repoRoot is not null)
        {
            return _repoRoot;
        }

        var dir = new DirectoryInfo(System.IO.Path.GetDirectoryName(AppContext.BaseDirectory)!);
        while (dir is not null)
        {
            string probe = System.IO.Path.Combine(dir.FullName, VisualAssetCatalog.RepoVideoRoot);
            if (System.IO.Directory.Exists(probe))
            {
                _repoRoot = dir.FullName;
                return _repoRoot;
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
}
