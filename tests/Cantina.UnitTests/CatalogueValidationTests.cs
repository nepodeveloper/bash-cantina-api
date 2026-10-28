using Cantina.Application;
using Cantina.Domain;

namespace Cantina.UnitTests;

public sealed class CatalogueValidationTests
{
    private static MenuItemInput ValidItem => new(MenuItemKind.Dish, "Bash burger", "Grilled burger", 89.99m,
        "https://example.com/burger.jpg");

    [Fact]
    public void ValidDishAndDrinkAreAccepted()
    {
        CatalogueValidation.Validate(ValidItem);
        CatalogueValidation.Validate(ValidItem with { Kind = MenuItemKind.Drink, Price = 0 });
        CatalogueValidation.Validate(ValidItem with { ImageUrl = "HTTPS://example.com/image.jpg" });
    }

    [Fact]
    public void MissingFieldsReturnAllValidationErrors()
    {
        var exception = Assert.Throws<RequestValidationException>(() =>
            CatalogueValidation.Validate(new MenuItemInput(0, null!, null!, -1, null!)));
        Assert.Equal(5, exception.Errors.Count);
    }

    [Theory]
    [InlineData("http://example.com/image.jpg")]
    [InlineData("javascript:alert(1)")]
    [InlineData("/relative/image.jpg")]
    [InlineData("file:///etc/passwd")]
    public void ImageMustUseAbsoluteHttps(string imageUrl)
    {
        var exception = Assert.Throws<RequestValidationException>(() =>
            CatalogueValidation.Validate(ValidItem with { ImageUrl = imageUrl }));
        Assert.Contains("imageUrl", exception.Errors.Keys);
    }

    [Theory]
    [InlineData("-0.01")]
    [InlineData("100000000")]
    [InlineData("1.001")]
    public void PriceMustFitDatabasePrecision(string value)
    {
        var price = decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Throws<RequestValidationException>(() => CatalogueValidation.Validate(ValidItem with { Price = price }));
    }

    [Fact]
    public void InputLengthsAreBounded()
    {
        Assert.Throws<RequestValidationException>(() => CatalogueValidation.Validate(ValidItem with { Name = new string('a', 121) }));
        Assert.Throws<RequestValidationException>(() => CatalogueValidation.Validate(ValidItem with { Description = new string('a', 2001) }));
        Assert.Throws<RequestValidationException>(() => CatalogueValidation.Validate(ValidItem with { ImageUrl = "https://example.com/" + new string('a', 2048) }));
    }

    [Theory]
    [InlineData(0, 20)]
    [InlineData(1000001, 20)]
    [InlineData(1, 0)]
    [InlineData(1, 101)]
    public void InvalidPaginationIsRejected(int page, int pageSize) =>
        Assert.Throws<RequestValidationException>(() => CatalogueValidation.Validate(new CatalogueQuery(Page: page, PageSize: pageSize)));

    [Fact]
    public void QueryFiltersAndSortAreValidated()
    {
        Assert.Throws<RequestValidationException>(() => CatalogueValidation.Validate(new CatalogueQuery(MinPrice: 20, MaxPrice: 10)));
        Assert.Throws<RequestValidationException>(() => CatalogueValidation.Validate(new CatalogueQuery(MinPrice: -1)));
        Assert.Throws<RequestValidationException>(() => CatalogueValidation.Validate(new CatalogueQuery(Kind: (MenuItemKind)3)));
        Assert.Throws<RequestValidationException>(() => CatalogueValidation.Validate(new CatalogueQuery(Sort: "DROP TABLE")));
        Assert.Throws<RequestValidationException>(() => CatalogueValidation.Validate(new CatalogueQuery(Search: new string('a', 121))));
    }

    [Theory]
    [InlineData("name")]
    [InlineData("-name")]
    [InlineData("price")]
    [InlineData("-price")]
    [InlineData("rating")]
    public void SupportedSortsAreAccepted(string sort) =>
        CatalogueValidation.Validate(new CatalogueQuery(Sort: sort, Page: 1000000, PageSize: 100));

    [Theory]
    [InlineData(int.MinValue)]
    [InlineData(0)]
    [InlineData(6)]
    [InlineData(int.MaxValue)]
    public void RatingsAreBetweenOneAndFive(int score) =>
        Assert.Throws<RequestValidationException>(() => CatalogueValidation.Validate(new RatingInput(score)));

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    public void RatingBoundariesAreAccepted(int score) => CatalogueValidation.Validate(new RatingInput(score));

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("  Tasty.  ", "Tasty.")]
    public void ReviewTextIsTrimmedAndBlankBecomesNull(string? supplied, string? expected) =>
        Assert.Equal(expected, CatalogueValidation.Validate(new RatingInput(3, supplied)).ReviewText);

    [Fact]
    public void ReviewTextIsBoundedToTheColumnLength()
    {
        CatalogueValidation.Validate(new RatingInput(3, new string('a', 1000)));
        var exception = Assert.Throws<RequestValidationException>(() =>
            CatalogueValidation.Validate(new RatingInput(3, new string('a', 1001))));
        Assert.Contains("reviewText", exception.Errors.Keys);
    }

    [Fact]
    public void InvalidRatingReturnsEveryFieldError()
    {
        var exception = Assert.Throws<RequestValidationException>(() =>
            CatalogueValidation.Validate(new RatingInput(9, new string('a', 1001))));
        Assert.Equal(["reviewText", "score"], exception.Errors.Keys.Order());
    }

    [Theory]
    [InlineData(0, 20)]
    [InlineData(1, 0)]
    [InlineData(1, 101)]
    [InlineData(1000001, 20)]
    public void InvalidRatingPagingIsRejected(int page, int pageSize) =>
        Assert.Throws<RequestValidationException>(() => CatalogueValidation.Validate(new RatingsQuery(page, pageSize)));
}
