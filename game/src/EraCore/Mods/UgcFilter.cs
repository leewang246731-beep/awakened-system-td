using System.Text.Json;
using System.Text.RegularExpressions;
using EraCore.Contracts.Content;

namespace EraCore.Mods;

/// <summary>
/// UGC 内容过滤规则 id（T-105, F-149）。七条规则与 T-082 对抗语料的七类
/// 攻击面（<c>adversarial.jsonl</c> 的 <c>attack_type</c> 闭集）一一对齐——
/// 模组文本（塔名/别名/描述/台词）是 L1 别名词典与军师 prompt 的上游注入面
/// （mindustry 逆向 F.4①：进入 prompt 的字符串一律按不可信处理），同一套
/// 攻击模式在装载期就拦截，而不是等它进了词典/prompt 再防。
/// </summary>
public enum UgcRule
{
    /// <summary>提示注入指令串（attack_type=prompt_injection：忽略以上/系统提示词/override system…）。</summary>
    PromptInjection,

    /// <summary>伪权限提升（privilege_escalation：绕过审批/开发者模式/最高权限…）。</summary>
    PrivilegeEscalation,

    /// <summary>钓鱼链接（phishing_link：http/https/www 与裸域名，F.4③ 同口径，过滤从宽）。</summary>
    PhishingLink,

    /// <summary>超长内容（length_overflow：字段超出内容安全上限——K-3 报告摘要 ≤200 同源的 UGC 面带宽）。</summary>
    LengthOverflow,

    /// <summary>混淆编码（encoding_obfuscation：零宽/双向覆写/控制字符/全角字母数字）。</summary>
    EncodingObfuscation,

    /// <summary>结构逃逸标记（schema_escape：&lt;/untrusted 分隔符闭合、```、聊天模板标记）。</summary>
    SchemaEscape,

    /// <summary>敏感词（social_engineering 的诱导话术 + UGC 敏感词闭集：免费领取/外挂/赌博/账号交易…）。</summary>
    SensitiveWord,
}

/// <summary>命中处置分级（命中≠一律拒载：按规则危害定级，玩家可见原因）。</summary>
public enum UgcSeverity
{
    /// <summary>剔除该字段/该别名（条目继续装载，rawJson 重写——被剔字段不再进别名词典与任何下游）。</summary>
    StripField,

    /// <summary>拒绝该条目（同模组其余条目继续装载）。</summary>
    RejectItem,

    /// <summary>拒绝整个模组（含回滚其已写入 overlay 的新增与胜出补丁；先例=T-104 越带=装载错误不崩）。</summary>
    RejectMod,
}

/// <summary>
/// 一条结构化 UGC 过滤命中（F-149 非静默要求）：模组 id / 条目 id / 字段
/// 指针 / 规则 id / 分级 + 玩家可见文案。落在 <see cref="ModLoadResult"/>
/// 的 UgcHits 与 <see cref="ModLoadError"/>（Kind=Ugc）两处，前者供 UI 结构化
/// 展示，后者进既有模组错误弹窗（坏模组弹错不崩，§16.3）。
/// </summary>
/// <param name="ModId">命中所在模组 id。</param>
/// <param name="ItemId">命中所在条目 id（manifest 级命中为空串）。</param>
/// <param name="Field">条目内字段指针（<c>/name_zh</c>、<c>/aliases/2</c>；manifest 级为 <c>name_zh</c> 等裸名）。</param>
/// <param name="Rule">命中的规则 id。</param>
/// <param name="Severity">处置分级。</param>
/// <param name="Message">命中详情（含证据片段，供日志与弹窗副行）。</param>
public sealed record UgcHit(
    string ModId,
    string ItemId,
    string Field,
    UgcRule Rule,
    UgcSeverity Severity,
    string Message)
{
    /// <summary>规则名（玩家可见，弹窗主行用）。</summary>
    public string RuleName => Rule switch
    {
        UgcRule.PromptInjection => "提示注入指令",
        UgcRule.PrivilegeEscalation => "伪权限提升",
        UgcRule.PhishingLink => "钓鱼链接",
        UgcRule.LengthOverflow => "超长内容",
        UgcRule.EncodingObfuscation => "混淆编码",
        UgcRule.SchemaEscape => "结构逃逸标记",
        UgcRule.SensitiveWord => "敏感词",
        _ => Rule.ToString(),
    };

    /// <summary>
    /// 玩家可见主行（非静默口径："该模组包含不安全内容[规则名]"），
    /// 按分级区分主语（模组/条目/字段）。
    /// </summary>
    public string PlayerMessage => Severity switch
    {
        UgcSeverity.RejectMod => $"该模组包含不安全内容[{RuleName}]，已拒绝装载：{Message}",
        UgcSeverity.RejectItem => $"该条目包含不安全内容[{RuleName}]，已拒绝装载：{Message}",
        _ => $"该内容包含不安全内容[{RuleName}]，已剔除字段：{Message}",
    };
}

/// <summary>单段文本的规则命中（无上下文轻量形，供 <see cref="UgcFilter.ScanText"/> 直测）。</summary>
/// <param name="Rule">规则 id。</param>
/// <param name="Evidence">证据片段（截断到安全长度）。</param>
public sealed record UgcTextHit(UgcRule Rule, string Evidence);

/// <summary>一个条目的过滤裁决：命中清单 + 剔除计划。</summary>
/// <param name="Hits">命中清单（字段扫描序，确定化）。</param>
/// <param name="StripProseFields">须剔除的散文字段名（描述/台词等可选字段）。</param>
/// <param name="StripAliasIndexes">须剔除的别名序号。</param>
public sealed record UgcItemVerdict(
    IReadOnlyList<UgcHit> Hits,
    IReadOnlySet<string> StripProseFields,
    IReadOnlySet<int> StripAliasIndexes)
{
    private static readonly UgcItemVerdict Clean = new(
        Array.Empty<UgcHit>(), new HashSet<string>(), new HashSet<int>());

    /// <summary>通过（零命中零剔除）。</summary>
    public static UgcItemVerdict Pass => Clean;

    /// <summary>存在整模组拒绝级命中。</summary>
    public bool RejectMod => Hits.Any(h => h.Severity == UgcSeverity.RejectMod);

    /// <summary>存在条目拒绝级命中。</summary>
    public bool RejectItem => Hits.Any(h => h.Severity == UgcSeverity.RejectItem);

    /// <summary>存在须落地的字段剔除。</summary>
    public bool HasStrips => StripProseFields.Count > 0 || StripAliasIndexes.Count > 0;
}

/// <summary>
/// 游戏侧 UGC 内容过滤层（T-105, F-149；设计 §16.3 内容安全条：provider 侧
/// 审核 1301 之外的游戏侧入口）。扫描模组文本字段——玩家可见面（模组名/
/// 条目名/别名/描述/台词）与 L1 别名词典、军师 prompt 的注入面是同一批
/// 字段，命中按分级处置：剔除字段 / 拒绝条目 / 拒绝整模组（越滤=拒载带
/// 原因，先例=T-104 越带=装载错误不崩）。规则对齐 T-082 对抗语料七类攻击
/// 面；命中样例库复用对抗语料的攻击模式子集（装载期拦截，不进词典/prompt）。
/// <para>
/// 零误杀纪律：扫描面=封闭字段集（名称/别名/散文键），'_' 注解键（装载器
/// 忽略的开发者注记，不进玩家面）不在扫描面；首方内容（随包发行、可审计）
/// 不过此层——本层只拦第三方 UGC。
/// </para>
/// </summary>
public static partial class UgcFilter
{
    // -------------------------------------------------------------- 带宽

    /// <summary>名称字段上限（name_zh/name_en/单个别名）：UI 标签可读带宽。</summary>
    public const int NameMaxLength = 48;

    /// <summary>散文字段上限（描述/台词/引言）：K-3 报告摘要 ≤200 先例（ReportToPlayerAction.SummaryMaxLength）对 UGC 面的移植。</summary>
    public const int ProseMaxLength = 200;

    /// <summary>单条目别名数上限：别名词典膨胀护栏（K-3 findings ≤8 先例的一倍带宽）。</summary>
    public const int AliasesMaxCount = 16;

    /// <summary>
    /// 扫描的散文键闭集（描述/台词/引言等玩家可见可选字段；mod 扩展缝上的
    /// 惯用名）。Localization 条目是纯文本表，另按"全字符串值皆散文"扫描。
    /// </summary>
    public static readonly IReadOnlyList<string> ProseFields = new[]
    {
        "description", "desc", "flavor", "flavor_text", "lore", "quote", "dialogue", "lines", "tooltip",
    };

    // ------------------------------------------------------ 攻击模式表
    // zh 表按 Ordinal 子串匹配；en 表按 OrdinalIgnoreCase 子串匹配。收词
    // 纪律：只收"指向系统/AI/权限的元话语"，不收游戏语汇（attack/damage/
    // ignore armor 类战斗描述不得误杀——边界用例钉死）。

    private static readonly string[] PromptInjectionZh =
    {
        "忽略以上", "忽略之前", "忽略所有", "忽略前面", "忽略指令", "无视以上", "无视之前", "无视指令",
        "系统提示词", "新的系统提示", "当作系统消息", "覆盖系统提示",
        "开发者模式", "调试模式",
        "你不再需要", "没有任何限制", "没有限制的", "无限制的", "无限制助手",
        "隐藏的命令", "执行其中隐藏", "打印你的系统", "注入防护", "军令作废",
    };

    private static readonly string[] PromptInjectionEn =
    {
        "ignore previous", "ignore all previous", "ignore all instructions", "ignore the above", "ignore above",
        "disregard previous", "disregard all instructions", "disregard the above",
        "system prompt", "system override", "developer mode", "dev mode", "override system",
        "you are now a", "unrestricted ai", "print your prompt", "reveal your prompt", "reveal your instructions",
        "execute hidden", "untrusted block", "tool descriptions",
    };

    private static readonly string[] PrivilegeEscalationZh =
    {
        "绕过审批", "跳过审批", "审批白名单", "自动允许所有", "自动放行",
        "我是开发者", "我是官方人员", "假冒官方", "内部调试",
        "越权", "管理员权限", "最高权限", "root权限",
    };

    private static readonly string[] PrivilegeEscalationEn =
    {
        "bypass approval", "skip approval", "bypass the gate", "auto approve", "auto-approve",
        "i am the developer", "official account", "admin access", "admin mode", "root access",
        "elevated privileges", "privileged action", "debug action", "cheat console",
    };

    private static readonly string[] SchemaEscapePatterns =
    {
        "</untrusted", "```", "<|im_start|>", "<|im_end|>", "[inst]", "[/inst]", "<system>", "</system>",
    };

    private static readonly string[] SensitiveWordZh =
    {
        "免费皮肤", "免费领取", "领取免费", "点击领取", "点击下载", "立即登录", "扫码领取", "扫码下载",
        "加群领取", "加群私聊", "外挂", "私服", "代充", "账号交易", "买卖账号", "刷金", "刷钱",
        "赌博", "博彩", "色情",
    };

    private static readonly string[] SensitiveWordEn =
    {
        "free skin", "free skins", "claim your free", "click to claim", "click to download",
        "cheat engine", "gold farming", "buy accounts", "account for sale", "sell accounts",
        "gambling", "casino", "betting site", "porn",
    };

    // ---------------------------------------------------------- 扫描 API

    /// <summary>
    /// 扫描一段文本的攻击模式（不含长度——长度按字段带宽另行裁决）。
    /// 规则判定序=枚举序，每条规则取首个命中模式；零命中返回空表。
    /// </summary>
    public static IReadOnlyList<UgcTextHit> ScanText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var hits = new List<UgcTextHit>();

        if (FirstMatch(text, PromptInjectionZh, Ordinal) is { } injection)
        {
            hits.Add(new UgcTextHit(UgcRule.PromptInjection, injection));
        }

        if (FirstMatch(text, PrivilegeEscalationZh, Ordinal) is { } escalationZh)
        {
            hits.Add(new UgcTextHit(UgcRule.PrivilegeEscalation, escalationZh));
        }

        if (FirstMatch(text, PrivilegeEscalationEn, IgnoreCase) is { } escalationEn)
        {
            hits.Add(new UgcTextHit(UgcRule.PrivilegeEscalation, escalationEn));
        }

        if (FirstMatch(text, PromptInjectionEn, IgnoreCase) is { } injectionEn)
        {
            hits.Add(new UgcTextHit(UgcRule.PromptInjection, injectionEn));
        }

        Match link = LinkRegex().Match(text);
        if (link.Success)
        {
            hits.Add(new UgcTextHit(UgcRule.PhishingLink, link.Value));
        }

        if (TryDescribeObfuscation(text, out string evidence))
        {
            hits.Add(new UgcTextHit(UgcRule.EncodingObfuscation, evidence));
        }

        if (FirstMatch(text, SchemaEscapePatterns, IgnoreCase) is { } escape)
        {
            hits.Add(new UgcTextHit(UgcRule.SchemaEscape, escape));
        }

        if (FirstMatch(text, SensitiveWordZh, Ordinal) is { } sensitiveZh)
        {
            hits.Add(new UgcTextHit(UgcRule.SensitiveWord, sensitiveZh));
        }

        if (FirstMatch(text, SensitiveWordEn, IgnoreCase) is { } sensitiveEn)
        {
            hits.Add(new UgcTextHit(UgcRule.SensitiveWord, sensitiveEn));
        }

        return hits;
    }

    /// <summary>
    /// 扫描模组 manifest 的玩家可见名（name_zh/name_en）。manifest 名任何
    /// 命中（含超长）都判 <see cref="UgcSeverity.RejectMod"/>——名是必填
    /// 字段无法剔除，且模组名直接进玩家模组列表。
    /// </summary>
    public static IReadOnlyList<UgcHit> ScanManifestNames(string modId, string nameZh, string nameEn)
    {
        var hits = new List<UgcHit>();
        ScanNameField(modId, string.Empty, "name_zh", nameZh, hits);
        ScanNameField(modId, string.Empty, "name_en", nameEn, hits);
        return hits;
    }

    /// <summary>
    /// 扫描一个内容条目的全部玩家可见文本字段。字段序与命中序确定化：
    /// name_zh → name_en → aliases（计数带宽 → 逐元素）→ 散文字段（闭集序）。
    /// 长度命中的分级按字段可剔除性裁决：名称=拒绝条目（必填不可剔）、
    /// 别名/散文=剔除。攻击类规则（注入/伪权限/钓鱼/混淆/逃逸）一律
    /// <see cref="UgcSeverity.RejectMod"/>；敏感词=<see cref="UgcSeverity.RejectItem"/>。
    /// </summary>
    /// <param name="modId">条目所属模组 id。</param>
    /// <param name="itemId">条目 id（报告用）。</param>
    /// <param name="item">条目 JSON 对象（schema 校验已过，形状可信）。</param>
    public static UgcItemVerdict ScanItem(string modId, string itemId, JsonElement item)
    {
        ArgumentNullException.ThrowIfNull(modId);
        ArgumentNullException.ThrowIfNull(itemId);
        if (item.ValueKind != JsonValueKind.Object)
        {
            return UgcItemVerdict.Pass; // 形状问题归 schema 矩阵管（T-103）
        }

        var hits = new List<UgcHit>();
        var stripProse = new HashSet<string>(StringComparer.Ordinal);
        var stripAlias = new HashSet<int>();

        ScanNameField(modId, itemId, "name_zh", StringOf(item, "name_zh"), hits);
        ScanNameField(modId, itemId, "name_en", StringOf(item, "name_en"), hits);

        if (item.TryGetProperty("aliases", out JsonElement aliases) && aliases.ValueKind == JsonValueKind.Array)
        {
            int aliasCount = aliases.GetArrayLength();
            if (aliasCount > AliasesMaxCount)
            {
                for (int index = AliasesMaxCount; index < aliasCount; index++)
                {
                    stripAlias.Add(index);
                }

                hits.Add(new UgcHit(
                    modId, itemId, "/aliases", UgcRule.LengthOverflow, UgcSeverity.StripField,
                    $"aliases 数量 {aliasCount} 超上限 {AliasesMaxCount}，多余别名剔除"));
            }

            int aliasIndex = 0;
            foreach (JsonElement alias in aliases.EnumerateArray())
            {
                if (alias.ValueKind == JsonValueKind.String)
                {
                    ScanAliasField(modId, itemId, aliasIndex, alias.GetString()!, hits, stripAlias);
                }

                aliasIndex++;
            }
        }

        ScanProse(modId, itemId, item, hits, stripProse);
        if (hits.Count == 0 && stripProse.Count == 0 && stripAlias.Count == 0)
        {
            return UgcItemVerdict.Pass;
        }

        return new UgcItemVerdict(hits, stripProse, stripAlias);
    }

    /// <summary>
    /// 把裁决的剔除计划落到条目 rawJson：删除待剔散文字段、过滤待剔别名
    /// 元素，其余属性（含 '_' 注解键与键序）原样保留——与 <c>RewriteId</c>
    /// 同手法。被剔别名从 rawJson 消失即从 <see cref="JsonContentItem.Aliases"/>
    /// 与 L1 别名词典同时消失（词典侧联动，下游无从看到）。
    /// </summary>
    public static string RewriteTextFields(string rawJson, UgcItemVerdict verdict)
    {
        ArgumentNullException.ThrowIfNull(rawJson);
        ArgumentNullException.ThrowIfNull(verdict);
        if (!verdict.HasStrips)
        {
            return rawJson;
        }

        using JsonDocument document = JsonDocument.Parse(rawJson);
        var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                if (verdict.StripProseFields.Contains(property.Name))
                {
                    continue;
                }

                if (property.Name == "aliases" && verdict.StripAliasIndexes.Count > 0)
                {
                    writer.WritePropertyName("aliases");
                    writer.WriteStartArray();
                    int index = 0;
                    foreach (JsonElement alias in property.Value.EnumerateArray())
                    {
                        if (!verdict.StripAliasIndexes.Contains(index))
                        {
                            alias.WriteTo(writer);
                        }

                        index++;
                    }

                    writer.WriteEndArray();
                    continue;
                }

                property.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>
    /// 混淆编码检测（encoding_obfuscation）：零宽/双向覆写/不可见字符/
    /// 控制字符（TAB/LF/CR 外）/全角字母数字。中文散文的全角标点（（）：！？）
    /// 是正常文面，不在命中面；全角字母数字是同形异码冒充面（ｅｒａ２≠era2，
    /// 对抗语料 adv-eo-* 同源），中文内容无需它们。
    /// </summary>
    public static bool TryDescribeObfuscation(string text, out string evidence)
    {
        ArgumentNullException.ThrowIfNull(text);
        foreach (char c in text)
        {
            string? found = c switch
            {
                '\t' or '\n' or '\r' => null,
                < ' ' => $"控制字符 U+{(int)c:X4}",
                '\x7f' => "控制字符 U+007F",
                >= '\u200b' and <= '\u200f' => $"零宽/方向标记 U+{(int)c:X4}",
                >= '\u202a' and <= '\u202e' => $"双向覆写 U+{(int)c:X4}",
                >= '\u2060' and <= '\u2069' => $"不可见字符 U+{(int)c:X4}",
                '\ufeff' => "零宽不换行 U+FEFF",
                >= '０' and <= '９' => $"全角数字 U+{(int)c:X4}",
                >= 'Ａ' and <= 'Ｚ' or >= 'ａ' and <= 'ｚ' => $"全角字母 U+{(int)c:X4}",
                _ => null,
            };
            if (found is not null)
            {
                evidence = found;
                return true;
            }
        }

        evidence = string.Empty;
        return false;
    }

    // ---------------------------------------------------------- 内部实现

    private const StringComparison Ordinal = StringComparison.Ordinal;
    private const StringComparison IgnoreCase = StringComparison.OrdinalIgnoreCase;

    private static void ScanNameField(
        string modId, string itemId, string field, string? text, List<UgcHit> hits)
    {
        if (text is null || text.Length == 0)
        {
            return; // 必填性归 schema 矩阵（T-103），此处只管内容安全
        }

        bool manifestLevel = itemId.Length == 0;
        foreach (UgcTextHit hit in ScanText(text))
        {
            hits.Add(new UgcHit(
                modId, itemId, field, hit.Rule,
                manifestLevel ? UgcSeverity.RejectMod : SeverityOf(hit.Rule),
                $"{field}: '{Snippet(text)}' 命中 '{hit.Evidence}'"));
        }

        if (text.Length > NameMaxLength)
        {
            hits.Add(new UgcHit(
                modId, itemId, field, UgcRule.LengthOverflow,
                manifestLevel ? UgcSeverity.RejectMod : UgcSeverity.RejectItem,
                $"{field} 长度 {text.Length} 超上限 {NameMaxLength}: '{Snippet(text)}'"));
        }
    }

    private static void ScanAliasField(
        string modId, string itemId, int index, string text, List<UgcHit> hits, ISet<int> stripAlias)
    {
        foreach (UgcTextHit hit in ScanText(text))
        {
            hits.Add(new UgcHit(
                modId, itemId, $"/aliases/{index}", hit.Rule, SeverityOf(hit.Rule),
                $"aliases[{index}]: '{Snippet(text)}' 命中 '{hit.Evidence}'"));
        }

        if (text.Length > NameMaxLength)
        {
            stripAlias.Add(index);
            hits.Add(new UgcHit(
                modId, itemId, $"/aliases/{index}", UgcRule.LengthOverflow, UgcSeverity.StripField,
                $"aliases[{index}] 长度 {text.Length} 超上限 {NameMaxLength}，该别名剔除"));
        }
    }

    private static void ScanProse(
        string modId, string itemId, JsonElement item, List<UgcHit> hits, ISet<string> stripProse)
    {
        foreach (string field in ProseFields)
        {
            if (!item.TryGetProperty(field, out JsonElement value))
            {
                continue;
            }

            ScanProseValue(modId, itemId, field, "/" + field, value, hits, stripProse);
        }

        // Localization 条目是纯文本表：根层全部非 '_' 字符串值（与字符串数组
        // 元素）按散文扫描——本地化文本整表都是玩家可见面。
        if (StringOf(item, "content_type") == nameof(ContentType.Localization))
        {
            foreach (JsonProperty property in item.EnumerateObject())
            {
                if (property.Name.StartsWith('_') || ProseFields.Contains(property.Name)
                    || property.Name is "id" or "content_type" or "patch")
                {
                    continue;
                }

                ScanProseValue(modId, itemId, property.Name, "/" + property.Name, property.Value, hits, stripProse);
            }
        }
    }

    private static void ScanProseValue(
        string modId, string itemId, string field, string pointer, JsonElement value,
        List<UgcHit> hits, ISet<string> stripProse)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                ScanProseText(modId, itemId, field, pointer, value.GetString()!, hits, stripProse);
                break;
            case JsonValueKind.Array:
                int index = 0;
                foreach (JsonElement element in value.EnumerateArray())
                {
                    if (element.ValueKind == JsonValueKind.String)
                    {
                        ScanProseText(
                            modId, itemId, field, $"{pointer}/{index}", element.GetString()!, hits, stripProse);
                    }

                    index++;
                }

                break;
        }
    }

    private static void ScanProseText(
        string modId, string itemId, string field, string pointer, string text,
        List<UgcHit> hits, ISet<string> stripProse)
    {
        foreach (UgcTextHit hit in ScanText(text))
        {
            hits.Add(new UgcHit(
                modId, itemId, pointer, hit.Rule, SeverityOf(hit.Rule),
                $"{pointer}: '{Snippet(text)}' 命中 '{hit.Evidence}'"));
        }

        if (text.Length > ProseMaxLength)
        {
            // 超长散文整字段剔除（台词数组任一行超长亦然——台词表完整性
            // 优先，不做逐行重写）：字段从 rawJson 消失即不再进任何下游。
            hits.Add(new UgcHit(
                modId, itemId, pointer, UgcRule.LengthOverflow, UgcSeverity.StripField,
                $"{pointer} 长度 {text.Length} 超上限 {ProseMaxLength}，字段剔除"));
            stripProse.Add(field);
        }
    }

    /// <summary>攻击类规则的统一分级（长度分级按字段裁决，不在此列）。</summary>
    private static UgcSeverity SeverityOf(UgcRule rule) => rule switch
    {
        UgcRule.SensitiveWord => UgcSeverity.RejectItem,
        _ => UgcSeverity.RejectMod, // 注入/伪权限/钓鱼/混淆/逃逸：prompt 邻接攻击，整包拒
    };

    private static string? StringOf(JsonElement item, string field) =>
        item.TryGetProperty(field, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? FirstMatch(string text, string[] patterns, StringComparison comparison)
    {
        foreach (string pattern in patterns)
        {
            if (text.Contains(pattern, comparison))
            {
                return pattern;
            }
        }

        return null;
    }

    /// <summary>证据片段截断（≤24 字符）：命中详情可读但不搬运整段恶意载荷。</summary>
    private static string Snippet(string text) =>
        text.Length <= 24 ? text : text[..24] + "…";

    /// <summary>
    /// 链接样片段（http/https/www 与裸域名，TLD 闭集）——F.4③ 同一收词口径
    /// （ReportToPlayerAction.LinkRegex），UGC 面从宽全拒：模组名/描述不是
    /// 链接通道，过滤从宽可接受。
    /// </summary>
    [GeneratedRegex(
        @"(?:https?://|www\.)\S+|[A-Za-z0-9][A-Za-z0-9-]*\.(?:com|net|org|io|cn|xyz|top|example)\b\S*",
        RegexOptions.CultureInvariant)]
    private static partial Regex LinkRegex();
}
