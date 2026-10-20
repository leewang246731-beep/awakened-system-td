namespace EraCore.Presentation;

/// <summary>
/// T-161 (F-125) 主菜单决策表——纯逻辑半边（表现层 Main 面板/EraTD
/// MainMenuUi 消费）。任务卡口径：
/// <list type="bullet">
/// <item>背景=A-01 菜单循环视频（目录 menu_loop 用途位唯一条目），视频加载
/// 失败回退 docs/assets/concept-keyart-eras.png 静帧，再失败纯深色底——
/// 双保险链在此裁决，UI 只执行；</item>
/// <item>logo 牌匾槽位=res://textures/ui/logo-banner.png（主管落图位），
/// 不存在时用 keyart 横裁垫图（裁切带=中央 <see cref="LogoCropBandRatio"/>
/// 高度带，REV 登记回退）；</item>
/// <item>六主题按钮卡（战役/无尽/程序关卡/联机/设置/退出）——时代色描边
/// 取自 visual-assets.json palettes（RL7：色值不进产品代码，UI 按
/// <see cref="PaletteKey"/> 查表）；</item>
/// <item>菜单 BGM=AudioDirector calm 态循环（既有三态机，菜单态=无敌人
/// ⇒ Calm，无需新状态）。</item>
/// </list>
/// </summary>
public static class MainMenuDecisions
{
    /// <summary>中文大标题（游戏名，唯一真相源=方案文档名）。</summary>
    public const string TitleZh = "我在游戏觉醒系统";

    /// <summary>副题小字（英文名）。</summary>
    public const string TitleEn = "Awakened System TD";

    /// <summary>logo 牌匾纹理路径（主管落图位；缺图=keyart 横裁垫图）。</summary>
    public const string LogoTexturePath = "res://textures/ui/logo-banner.png";

    /// <summary>keyart 静帧回退路径（仓库 docs 轨，发布包缺此文件时降纯深色底）。</summary>
    public const string KeyartRelativePath = "docs/assets/concept-keyart-eras.png";

    /// <summary>keyart 横裁垫图带：中央高度占比（牌匾 4.5:1 视觉带的近似裁切）。</summary>
    public const float LogoCropBandRatio = 0.34f;

    /// <summary>主菜单背景裁决（双保险链：视频→静帧→深色底）。</summary>
    public enum BackgroundKind
    {
        /// <summary>A-01 菜单循环视频（VideoStreamTheora 循环播放）。</summary>
        MenuLoopVideo,

        /// <summary>concept-keyart-eras.png 全屏静帧（视频轨缺失/不可播）。</summary>
        KeyartStill,

        /// <summary>纯深色底（视频与静帧均不可读——发布裸包极端态，永不阻断）。</summary>
        DarkFallback,
    }

    /// <summary>六主题入口之一（Id 顺序=菜单纵列顺序）。</summary>
    public enum EntryId
    {
        Campaign,
        Endless,
        Procedural,
        Net,
        Settings,
        Quit,
    }

    /// <summary>一条入口行：Id + 中文标签 + 时代色板键（visual-assets.json palettes）。</summary>
    public sealed record Entry(EntryId Id, string LabelZh, string PaletteKey);

    /// <summary>
    /// 六主题入口表（顺序=菜单纵列）：战役/无尽/程序关卡/联机/设置/退出。
    /// 前五行各占一个时代色板键（e1~e5），退出=中性暗色（e6 深空色板的
    /// secondary 暗列由 UI 端取，此处只给键）。
    /// </summary>
    public static IReadOnlyList<Entry> Entries() => new[]
    {
        new Entry(EntryId.Campaign, "战役", "e1"),
        new Entry(EntryId.Endless, "无尽", "e2"),
        new Entry(EntryId.Procedural, "程序关卡", "e3"),
        new Entry(EntryId.Net, "联机", "e4"),
        new Entry(EntryId.Settings, "设置", "e5"),
        new Entry(EntryId.Quit, "退出", "e6"),
    };

    /// <summary>
    /// 背景双保险链裁决：视频可播→循环视频；否则 keyart 可读→静帧；再否则
    /// 深色底。UI 端一次探测后缓存本裁决（文件级事实不随帧变化）。
    /// </summary>
    public static BackgroundKind PickBackground(bool menuLoopPlayable, bool keyartReadable) =>
        menuLoopPlayable ? BackgroundKind.MenuLoopVideo
        : keyartReadable ? BackgroundKind.KeyartStill
        : BackgroundKind.DarkFallback;

    /// <summary>
    /// keyart 横裁垫图带（牌匾垫图）：中央 <paramref name="bandRatio"/> 高度带
    /// 的像素区间 [y, y+height)。bandRatio 夹取 (0, 1]——越界入参钳到边界，
    /// 永不抛出（缺图降级路径上的纯算术）。
    /// </summary>
    public static (int Y, int Height) LogoCropBand(int imageHeight, float bandRatio)
    {
        if (imageHeight <= 0)
        {
            return (0, 0); // 不可读图（高度 0）——UI 端拿空带即走无垫图分支
        }

        float ratio = Math.Clamp(bandRatio, 0f, 1f);
        int height = Math.Max(1, (int)Math.Round(imageHeight * ratio, MidpointRounding.AwayFromZero));
        int y = (imageHeight - height) / 2;
        return (y, height);
    }

    /// <summary>版本角标文案（VersionInfo 唯一真相源；v 前缀归一——重复前缀不叠加）。</summary>
    public static string VersionBadge(string version) =>
        "v" + (version.StartsWith("v", StringComparison.OrdinalIgnoreCase)
            ? version[1..]
            : version);

    /// <summary>菜单态 BGM 目标（AudioDirector 三态机：菜单=calm 循环）。</summary>
    public const string MenuBgmState = "calm";
}
