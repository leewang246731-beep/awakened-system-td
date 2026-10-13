using EraCore.Persistence;
using Xunit;

namespace EraCore.Tests.Persistence;

// [C-04] K-7 save backend: corruption signal carries the stored version (T-041)
public class SaveCorruptExceptionTests
{
    // ------------------------------------------------------------ legal

    [Fact]
    public void Constructor_carries_message_stored_version_and_inner()
    {
        var inner = new InvalidOperationException("parse died");

        var ex = new SaveCorruptException("payload damaged", 3, inner);

        Assert.Equal("payload damaged", ex.Message);
        Assert.Equal(3, ex.StoredVersion);
        Assert.Same(inner, ex.InnerException);
    }

    // ------------------------------------------------------------ boundary

    [Fact]
    public void Stored_version_zero_is_valid_for_unreadable_stores()
    {
        // A database whose version row cannot even be located reports 0 —
        // "unknown", still distinguishable from any real format version.
        var ex = new SaveCorruptException("no schema_version table", 0);

        Assert.Equal(0, ex.StoredVersion);
        Assert.Null(ex.InnerException);
    }
}
