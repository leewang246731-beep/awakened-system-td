namespace EraCore.Presentation;

/// <summary>
/// Agent-tier availability under the current platform (T-106, F-164 Web 版
/// 约束, 设计 v3.2 §17 条 15: "单线程导出；L2/L3 在 Web 版自动禁用（浏览器
/// 沙箱）+只保留 L1"). Pure facts row — the Godot host supplies the single
/// input (<c>OS.HasFeature("web")</c>) and every consumer (settings panel
/// entry visibility, LLM gateway gate, smoke assertions) derives from this
/// one table, so the constraint cannot drift between call sites.
/// </summary>
/// <param name="L1Grammar">L1 军令语法（<c>L1IntentParser</c>，纯离线正则/词典解析）可用性——天然无网络无原生依赖，全平台恒可用。</param>
/// <param name="L2LocalModel">L2 本地小模型（llama.cpp GDExtension，§17 条 14）可用性——原生动态库不能进浏览器沙箱，Web=false。</param>
/// <param name="L3CloudByok">L3 BYOK 云模型（<c>LlmGateway</c> 直连）可用性——Web 版零网络承诺（§17 条 15），Web=false。</param>
public sealed record AgentTierAvailability(
    bool L1Grammar,
    bool L2LocalModel,
    bool L3CloudByok);

/// <summary>
/// The frozen Web-constraint table (T-106): one pure function from the
/// platform fact to the three tier switches. L1 stays on everywhere (the
/// grammar parser is pure offline code — 军令语法不依赖任何网络/原生组件);
/// L2/L3 switch off on the Web export (浏览器沙箱禁 GDExtension 原生推理 +
/// Web 版零网络承诺). Desktop keeps all three (L2 lands with its own future
/// task; L3 is the BYOK gateway).
/// </summary>
public static class WebConstraints
{
    /// <summary>
    /// Resolves the tier-availability row for one platform fact.
    /// The ONLY legal source of the row — callers never branch on
    /// <c>OS.HasFeature("web")</c> themselves (能力表驱动).
    /// </summary>
    /// <param name="isWeb">True inside the HTML5 export (<c>OS.HasFeature("web")</c>).</param>
    public static AgentTierAvailability Availability(bool isWeb) => new(
        L1Grammar: true,
        L2LocalModel: !isWeb,
        L3CloudByok: !isWeb);
}
