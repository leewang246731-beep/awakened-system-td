using EraCore.Agent.Pipeline;
using EraCore.Audio;
using EraCore.Contracts.Journal;
using Xunit;

namespace EraCore.Tests.Audio;

/// <summary>
/// Tests of the K-8 journal → ding binding (T-060, C-25, F-112): only the
/// final system report the player receives dings (情报 "叮"); clarify
/// cards, inputs, plans and validation traces stay silent.
/// </summary>
// [C-25][F-112] 战报事件→"叮"触发绑定：终局汇报→情报；其余阶段静默
public class DingJournalMapTests
{
    [Theory] // legal + boundary — every (kind, stage-type) pair of the frozen pipeline
    [InlineData(JournalEventKind.Report, PipelineJournalTypes.Report, DingTrigger.SystemReport)] // 终局汇报 → "叮"
    [InlineData(JournalEventKind.Report, PipelineJournalTypes.Clarify, null)]  // 澄清卡不响
    [InlineData(JournalEventKind.Input, PipelineJournalTypes.Input, null)]
    [InlineData(JournalEventKind.Plan, PipelineJournalTypes.Plan, null)]
    [InlineData(JournalEventKind.Plan, PipelineJournalTypes.Approval, null)]
    [InlineData(JournalEventKind.Validation, PipelineJournalTypes.Execution, null)]
    public void Only_final_reports_ding(JournalEventKind kind, string payloadType, DingTrigger? expected)
    {
        DingTrigger? actual = DingJournalMap.Map(kind, payloadType);

        Assert.Equal(expected, actual);
    }

    [Fact] // boundary — an unknown payload discriminator inside a Report never dings
    public void Unknown_payload_type_inside_report_never_dings()
    {
        Assert.Null(DingJournalMap.Map(JournalEventKind.Report, "future-stage"));
    }

    [Fact] // illegal — null payload type is treated as not-a-report, never throws
    public void Null_payload_type_maps_to_no_ding()
    {
        Assert.Null(DingJournalMap.Map(JournalEventKind.Report, null));
        Assert.Null(DingJournalMap.Map(JournalEventKind.Input, null));
        Assert.Null(DingJournalMap.Map(JournalEventKind.Plan, null));
        Assert.Null(DingJournalMap.Map(JournalEventKind.Validation, null));
    }
}
