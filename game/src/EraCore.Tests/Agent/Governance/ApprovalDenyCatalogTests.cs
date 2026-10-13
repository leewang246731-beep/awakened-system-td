using EraCore.Agent.Governance;
using EraCore.Contracts.Agent;
using Xunit;

namespace EraCore.Tests.Agent.Governance;

// [C-14][F-063] Deny catalog (T-050 E1 framework): every
// "modify state rather than execute an action" request category is
// denied unconditionally — deny has no allow/ask row by construction.

/// <summary>[C-14][F-063] The frozen deny list: 改数值/跳波/剧情钥匙/付费/多人/直改状态.</summary>
public class ApprovalDenyCatalogTests
{
    [Theory] // illegal — every catalog category is denied outright (F.4 deny tier)
    [InlineData(DeniedRequestKind.ModifyValues)]
    [InlineData(DeniedRequestKind.SkipWave)]
    [InlineData(DeniedRequestKind.PlotKey)]
    [InlineData(DeniedRequestKind.PaidItem)]
    [InlineData(DeniedRequestKind.MoveTeammateStructures)]
    [InlineData(DeniedRequestKind.DirectStateMutation)]
    public void Every_category_is_denied(DeniedRequestKind kind)
    {
        var decision = ApprovalDenyCatalog.Classify(kind);
        Assert.Equal(ApprovalDecisionKind.Deny, decision.Kind);
        Assert.Equal($"deny_catalog:{kind}", decision.Reason);
    }

    [Fact] // legal — the catalog covers its whole enum domain (no undefined gap to slip through)
    public void Coverage_spans_the_whole_enum_domain()
    {
        foreach (DeniedRequestKind kind in Enum.GetValues<DeniedRequestKind>())
        {
            Assert.True(ApprovalDenyCatalog.IsDeniedCategory(kind));
            Assert.Equal(ApprovalDecisionKind.Deny, ApprovalDenyCatalog.Classify(kind).Kind);
        }

        Assert.Equal(6, Enum.GetValues<DeniedRequestKind>().Length);
    }
}
