using EraCore.Contracts.Actions;

namespace EraCore.Agent.Autopilot;

/// <summary>
/// 轮内配额执法（T-081, C-13 / F-093）：全托管 1 轮迭代 = 意图→计划→≤3
/// 动作→汇报（v3.2 §11.8）。本守卫是"≤3 动作/轮"这条硬限的唯一执法点：
/// 每一轮开始时清零，动作进入五段管线之前先过 <see cref="TryEnter"/>，
/// 第 4 个动作被拒入本轮（配额已满——不是排队到下一轮，而是明确拒绝）。
/// <para>
/// 口径注记（v3.2 §11.8 的"→汇报"是独立阶段）：轮末的
/// <see cref="SystemActionKind.ReportToPlayer"/> 汇报动作不计入 3 动作配额
/// ——设计句式"意图→计划→≤3 动作→汇报"把汇报列为动作段之后的收尾段；
/// 侦察类动作（LookAround 等）属于 19 动作闭集，如实计数。守卫本身不与
/// 管线共享状态：导演在每次 <c>HandleSystemAction</c> 之前调用，拒绝即
/// 不进入管线（拒入，而非执行后回滚）。
/// </para>
/// 确定性：纯计数器，无随机、无时钟（CS-1.4 不适用——无时间输入）。
/// </summary>
public sealed class AutopilotRoundQuota
{
    /// <summary>每轮动作硬限：≤3 动作/轮（§11.8，第 4 动作拒入本轮）。</summary>
    public const int ActionsPerRound = 3; // ci-allow: R7 T-081 v3.2 §11.8 全托管"1 轮=≤3 动作"冻结口径（非平衡数值）

    private int _used;

    /// <summary>当前轮已进入配额的动作数。</summary>
    public int Used => _used;

    /// <summary>本轮配额是否已满（第 4 动作将拒入）。</summary>
    public bool Exhausted => _used >= ActionsPerRound;

    /// <summary>开始新一轮：清零计数（轮边界=导演的轮起点）。</summary>
    public void BeginRound() => _used = 0;

    /// <summary>
    /// 一个动作请求进入本轮。汇报动词（<see cref="SystemActionKind.ReportToPlayer"/>）
    /// 是轮末收尾段，不占配额；其余动词占 1 格，配额已满时拒绝
    /// （第 4 动作拒入本轮——调用方不得再把它送进管线）。
    /// </summary>
    /// <param name="verb">请求进入的动词。</param>
    /// <returns>true=准许进入；false=本轮配额已满，拒入。</returns>
    public bool TryEnter(SystemActionKind verb)
    {
        if (verb == SystemActionKind.ReportToPlayer)
        {
            return true; // 汇报是"→汇报"收尾段，§11.8 句式下不占 3 动作配额
        }

        if (_used >= ActionsPerRound)
        {
            return false;
        }

        _used++;
        return true;
    }
}
