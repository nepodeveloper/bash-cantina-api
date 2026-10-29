using System.Net;
using System.Net.Http.Json;
using Cantina.Application;

namespace Cantina.IntegrationTests;

[Collection("api")]
public sealed class ReviewDashboardTests(ApiFixture fixture)
{
    [Theory]
    [InlineData("")]
    [InlineData("/summary")]
    public async Task Dashboard_rejects_anonymous_customers_and_cross_store_managers(string suffix)
    {
        var route = $"/api/v1/stores/{ApiFixture.BashSlug}/reviews{suffix}";
        using var anonymous = fixture.CreateClient();
        using var customer = await fixture.CustomerAsync();
        using var otherManager = await fixture.StaffAsync("StoreManager", ApiFixture.Sportscene);
        await ApiFixture.ExpectAsync(await anonymous.GetAsync(route), HttpStatusCode.Unauthorized);
        await ApiFixture.ExpectAsync(await customer.GetAsync(route), HttpStatusCode.Forbidden);
        await ApiFixture.ExpectAsync(await otherManager.GetAsync(route), HttpStatusCode.Forbidden);
    }

    [Theory]
    [InlineData("Admin")]
    [InlineData("StoreManager")]
    public async Task Authorized_list_and_summary_are_store_scoped_and_accept_offset_dates(string role)
    {
        using var admin = await fixture.StaffAsync("Admin");
        using var staff = await fixture.StaffAsync(role, ApiFixture.Bash, ApiFixture.Sportscene);
        var marker = $"Dashboard-{Guid.NewGuid():N}";
        var bash = await ApiFixture.CreateItemAsync(admin, ApiFixture.BashSlug);
        var sportscene = await ApiFixture.CreateItemAsync(admin, ApiFixture.SportsceneSlug);
        var (bashEmail, bashTokens) = await fixture.RegisterAsync("Bash Reviewer");
        var (sportsceneEmail, sportsceneTokens) = await fixture.RegisterAsync("Sportscene Reviewer");
        using var bashCustomer = fixture.CreateClient(bashTokens.AccessToken);
        using var sportsceneCustomer = fixture.CreateClient(sportsceneTokens.AccessToken);
        await ApiFixture.ExpectAsync(await bashCustomer.PutAsJsonAsync(
            $"{ApiFixture.Menu(ApiFixture.BashSlug)}/{bash.Id}/rating", new { score = 2, reviewText = marker }),
            HttpStatusCode.NoContent);
        await ApiFixture.ExpectAsync(await sportsceneCustomer.PutAsJsonAsync(
            $"{ApiFixture.Menu(ApiFixture.SportsceneSlug)}/{sportscene.Id}/rating", new { score = 5, reviewText = marker }),
            HttpStatusCode.NoContent);

        var from = DateTimeOffset.UtcNow.AddDays(-1);
        var to = DateTimeOffset.UtcNow.AddDays(1);
        var dates = $"&fromUtc={Uri.EscapeDataString(from.ToOffset(TimeSpan.FromHours(2)).ToString("O"))}"
            + $"&toUtc={Uri.EscapeDataString(to.ToOffset(TimeSpan.FromHours(-3)).ToString("O"))}";
        foreach (var (slug, item, email, name, score) in new[]
        {
            (ApiFixture.BashSlug, bash, bashEmail, "Bash Reviewer", 2),
            (ApiFixture.SportsceneSlug, sportscene, sportsceneEmail, "Sportscene Reviewer", 5)
        })
        {
            var route = $"/api/v1/stores/{slug}/reviews";
            var query = $"?search={marker}";
            var listResponse = await staff.GetAsync(route + query + dates);
            await ApiFixture.ExpectAsync(listResponse, HttpStatusCode.OK);
            var page = (await listResponse.Content.ReadFromJsonAsync<Page<ReviewDto>>())!;
            var review = Assert.Single(page.Items);
            Assert.Equal(1, page.TotalCount);
            Assert.Equal(item.Id, review.MenuItemId);
            Assert.Equal(email, review.CustomerEmail);
            Assert.Equal(name, review.CustomerName);
            Assert.Equal(score, review.Score);
            var summaryResponse = await staff.GetAsync(route + "/summary" + query + dates);
            await ApiFixture.ExpectAsync(summaryResponse, HttpStatusCode.OK);
            var summary = (await summaryResponse.Content.ReadFromJsonAsync<ReviewSummary>())!;
            Assert.Equal(1, summary.TotalReviews);
            Assert.Equal(1, summary.DistinctReviewers);
            Assert.Equal((double)score, summary.AverageScore);
            Assert.Equal(email, Assert.Single(summary.MostActiveReviewers).CustomerEmail);
            Assert.Equal(item.Id, Assert.Single(summary.LowestRatedItems).MenuItemId);
            Assert.Equal(1, Assert.Single(summary.Distribution, bucket => bucket.Score == score).Count);

            var utcQuery = query + $"&fromUtc={Uri.EscapeDataString(from.ToString("O"))}"
                + $"&toUtc={Uri.EscapeDataString(to.ToString("O"))}";
            Assert.Equal(await staff.GetStringAsync(route + utcQuery), await listResponse.Content.ReadAsStringAsync());
            Assert.Equal(await staff.GetStringAsync(route + "/summary" + utcQuery),
                await summaryResponse.Content.ReadAsStringAsync());

            var excluded = query + $"&fromUtc={Uri.EscapeDataString(to.ToOffset(TimeSpan.FromHours(2)).ToString("O"))}";
            Assert.Empty((await staff.GetFromJsonAsync<Page<ReviewDto>>(route + excluded))!.Items);
            Assert.Equal(0, (await staff.GetFromJsonAsync<ReviewSummary>(route + "/summary" + excluded))!.TotalReviews);
        }
    }
}