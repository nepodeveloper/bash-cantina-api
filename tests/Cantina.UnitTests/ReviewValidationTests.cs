using Cantina.Application;

namespace Cantina.UnitTests;

public sealed class ReviewValidationTests
{
    [Theory]
    [InlineData(0, 20)]
    [InlineData(1, 0)]
    [InlineData(1, 101)]
    [InlineData(1000001, 20)]
    public void PagingIsBounded(int page, int pageSize) =>
        Assert.Throws<RequestValidationException>(() =>
            ReviewValidation.Validate(new ReviewQuery(Page: page, PageSize: pageSize)));

    [Theory]
    [InlineData(0, null)]
    [InlineData(6, null)]
    [InlineData(null, 0)]
    [InlineData(null, 6)]
    [InlineData(4, 2)]
    public void ScoreBoundsMustBeOrderedAndInRange(int? min, int? max)
    {
        var exception = Assert.Throws<RequestValidationException>(() =>
            ReviewValidation.Validate(new ReviewQuery(MinScore: min, MaxScore: max)));
        Assert.Contains("score", exception.Errors.Keys);
    }

    [Fact]
    public void ScoreBoundaryValuesAreAccepted() =>
        ReviewValidation.Validate(new ReviewQuery(MinScore: 1, MaxScore: 5));

    [Fact]
    public void DateWindowMustBeOrdered()
    {
        var later = DateTimeOffset.UtcNow;
        var exception = Assert.Throws<RequestValidationException>(() =>
            ReviewValidation.Validate(new ReviewQuery(FromUtc: later, ToUtc: later.AddDays(-1))));
        Assert.Contains("date", exception.Errors.Keys);
    }

    [Fact]
    public void DateBoundsAreNormalizedToUtcWithoutChangingTheInstant()
    {
        var from = new DateTimeOffset(2026, 10, 1, 10, 0, 0, TimeSpan.FromHours(2));
        var to = new DateTimeOffset(2026, 10, 1, 7, 0, 0, TimeSpan.FromHours(-3));
        var query = ReviewValidation.Validate(new ReviewQuery(FromUtc: from, ToUtc: to));
        Assert.Equal(from, query.FromUtc);
        Assert.Equal(to, query.ToUtc);
        Assert.Equal(TimeSpan.Zero, query.FromUtc!.Value.Offset);
        Assert.Equal(TimeSpan.Zero, query.ToUtc!.Value.Offset);
    }

    [Theory]
    [InlineData("updated")]
    [InlineData("-updated")]
    [InlineData("created")]
    [InlineData("-created")]
    [InlineData("score")]
    [InlineData("-score")]
    public void SupportedSortsAreAccepted(string sort) =>
        ReviewValidation.Validate(new ReviewQuery(Sort: sort));

    [Theory]
    [InlineData("rating")]
    [InlineData("DROP TABLE")]
    [InlineData("")]
    public void UnsupportedSortsAreRejected(string sort) =>
        Assert.Throws<RequestValidationException>(() => ReviewValidation.Validate(new ReviewQuery(Sort: sort)));

    [Fact]
    public void SearchIsBoundedAndTrimmed()
    {
        Assert.Throws<RequestValidationException>(() =>
            ReviewValidation.Validate(new ReviewQuery(Search: new string('a', 121))));
        Assert.Equal("sith", ReviewValidation.Validate(new ReviewQuery(Search: "  sith  ")).Search);
        Assert.Null(ReviewValidation.Validate(new ReviewQuery(Search: "   ")).Search);
    }
}
