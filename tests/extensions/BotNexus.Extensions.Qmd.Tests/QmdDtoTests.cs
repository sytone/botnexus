using BotNexus.Extensions.Qmd;

namespace BotNexus.Extensions.Qmd.Tests;

public class QmdDtoTests
{
    [Fact]
    public void QmdSearchResult_record_equality()
    {
        var a = new QmdSearchResult("id1", "store", "/path", "Title", 0.95, "snippet");
        var b = new QmdSearchResult("id1", "store", "/path", "Title", 0.95, "snippet");

        Assert.Equal(a, b);
    }

    [Fact]
    public void QmdDocument_record_equality()
    {
        var a = new QmdDocument("id1", "store", "/path", "Title", "Content");
        var b = new QmdDocument("id1", "store", "/path", "Title", "Content");

        Assert.Equal(a, b);
    }

    [Fact]
    public void QmdStoreInfo_record_equality()
    {
        var ts = DateTimeOffset.UtcNow;
        var a = new QmdStoreInfo("vault", "/vault", "desc", 10, ts, true);
        var b = new QmdStoreInfo("vault", "/vault", "desc", 10, ts, true);

        Assert.Equal(a, b);
    }

    [Fact]
    public void QmdStoreInfo_allows_null_description_and_timestamp()
    {
        var info = new QmdStoreInfo("vault", "/vault", null, 0, null, false);

        Assert.Null(info.Description);
        Assert.Null(info.LastUpdated);
        Assert.False(info.Healthy);
    }

    [Theory]
    [InlineData(QmdSearchMode.Keyword)]
    [InlineData(QmdSearchMode.Semantic)]
    [InlineData(QmdSearchMode.Hybrid)]
    public void QmdSearchMode_enum_has_expected_values(QmdSearchMode mode)
    {
        Assert.True(Enum.IsDefined(mode));
    }
}
