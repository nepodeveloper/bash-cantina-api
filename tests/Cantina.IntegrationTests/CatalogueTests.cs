using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Cantina.Application;
using Cantina.Domain;
using Cantina.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cantina.IntegrationTests;

[Collection("api")]
public sealed class CatalogueTests(ApiFixture fixture)
{
    [Fact]
    public async Task Stores_and_seeded_catalogues_are_public_and_store_scoped()
    {
        using var client = fixture.CreateClient();
        var stores = (await client.GetFromJsonAsync<List<StoreDto>>("/api/v1/stores"))!;
        Assert.Contains(stores, x => x.Id == ApiFixture.Bash && x.Slug == "bash");
        Assert.Contains(stores, x => x.Id == ApiFixture.Sportscene && x.Slug == "sportscene");
        foreach (var store in new[] { ApiFixture.Bash, ApiFixture.Sportscene })
        {
            var page = (await client.GetFromJsonAsync<Page<MenuItemDto>>(ApiFixture.Menu(store)))!;
            Assert.True(page.TotalCount >= 5);
            Assert.All(page.Items, item => Assert.Equal(store, item.StoreId));
        }
    }

    [Fact]
    public async Task Anonymous_writes_and_ratings_require_authentication()
    {
        using var admin = await fixture.StaffAsync("Admin");
        var item = await ApiFixture.CreateItemAsync(admin, ApiFixture.Bash);
        using var client = fixture.CreateClient();
        var url = $"{ApiFixture.Menu(ApiFixture.Bash)}/{item.Id}";
        await ApiFixture.ExpectAsync(await client.PostAsJsonAsync(ApiFixture.Menu(ApiFixture.Bash), ApiFixture.Input()), HttpStatusCode.Unauthorized);
        await ApiFixture.ExpectAsync(await client.PutAsJsonAsync(url, ApiFixture.Input()), HttpStatusCode.Unauthorized);
        await ApiFixture.ExpectAsync(await client.DeleteAsync(url), HttpStatusCode.Unauthorized);
        await ApiFixture.ExpectAsync(await client.PutAsJsonAsync(url + "/rating", new { score = 4 }), HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Customers_cannot_create_edit_or_delete_catalogue_items()
    {
        using var admin = await fixture.StaffAsync("Admin");
        var item = await ApiFixture.CreateItemAsync(admin, ApiFixture.Bash);
        var (_, tokens) = await fixture.RegisterAsync();
        using var customer = fixture.CreateClient(tokens.AccessToken);
        var url = $"{ApiFixture.Menu(ApiFixture.Bash)}/{item.Id}";
        await ApiFixture.ExpectAsync(await customer.PostAsJsonAsync(ApiFixture.Menu(ApiFixture.Bash), ApiFixture.Input()), HttpStatusCode.Forbidden);
        await ApiFixture.ExpectAsync(await customer.PutAsJsonAsync(url, ApiFixture.Input()), HttpStatusCode.Forbidden);
        await ApiFixture.ExpectAsync(await customer.DeleteAsync(url), HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Managers_can_manage_only_stores_to_which_they_belong()
    {
        using var manager = await fixture.StaffAsync("StoreManager", ApiFixture.Bash);
        var item = await ApiFixture.CreateItemAsync(manager, ApiFixture.Bash);
        var url = $"{ApiFixture.Menu(ApiFixture.Bash)}/{item.Id}";
        await ApiFixture.ExpectAsync(await manager.PutAsJsonAsync(url, ApiFixture.Input()), HttpStatusCode.OK);
        using var admin = await fixture.StaffAsync("Admin");
        var otherItem = await ApiFixture.CreateItemAsync(admin, ApiFixture.Sportscene);
        var otherUrl = $"{ApiFixture.Menu(ApiFixture.Sportscene)}/{otherItem.Id}";
        await ApiFixture.ExpectAsync(await manager.PostAsJsonAsync(ApiFixture.Menu(ApiFixture.Sportscene), ApiFixture.Input()), HttpStatusCode.Forbidden);
        await ApiFixture.ExpectAsync(await manager.PutAsJsonAsync(otherUrl, ApiFixture.Input()), HttpStatusCode.Forbidden);
        await ApiFixture.ExpectAsync(await manager.DeleteAsync(otherUrl), HttpStatusCode.Forbidden);
        await ApiFixture.ExpectAsync(await manager.DeleteAsync(url), HttpStatusCode.NoContent);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    public async Task Admin_can_create_read_update_delete_both_kinds_in_either_store(int kind, bool sportscene)
    {
        var store = sportscene ? ApiFixture.Sportscene : ApiFixture.Bash;
        using var admin = await fixture.StaffAsync("Admin");
        var input = ApiFixture.Input(kind: (MenuItemKind)kind);
        var item = await ApiFixture.CreateItemAsync(admin, store, input);
        Assert.Equal(input.Name, item.Name);
        Assert.Equal(input.Kind, item.Kind);
        Assert.Equal(input.Price, item.Price);
        var url = $"{ApiFixture.Menu(store)}/{item.Id}";
        Assert.Equal(item, await admin.GetFromJsonAsync<MenuItemDto>(url));
        var changed = input with { Name = $"Updated-{Guid.NewGuid():N}", Price = 71.25m };
        var update = await admin.PutAsJsonAsync(url, changed);
        await ApiFixture.ExpectAsync(update, HttpStatusCode.OK);
        var actual = (await update.Content.ReadFromJsonAsync<MenuItemDto>())!;
        Assert.Equal(changed.Name, actual.Name);
        Assert.Equal(changed.Price, actual.Price);
        await ApiFixture.ExpectAsync(await admin.DeleteAsync(url), HttpStatusCode.NoContent);
        await ApiFixture.ExpectAsync(await admin.GetAsync(url), HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData(MenuItemKind.Dish)]
    [InlineData(MenuItemKind.Drink)]
    public async Task Https_image_scheme_is_case_insensitive_during_create_and_update(MenuItemKind kind)
    {
        using var admin = await fixture.StaffAsync("Admin");
        var input = ApiFixture.Input(kind: kind) with { ImageUrl = "HTTPS://images.example.com/uppercase-create.jpg" };
        var created = await ApiFixture.CreateItemAsync(admin, ApiFixture.Bash, input);
        Assert.Equal(input.ImageUrl, created.ImageUrl);
        var url = $"{ApiFixture.Menu(ApiFixture.Bash)}/{created.Id}";
        var changed = input with { ImageUrl = "HTTPS://images.example.com/uppercase-update.jpg" };
        await ApiFixture.ExpectAsync(await admin.PutAsJsonAsync(url, changed), HttpStatusCode.OK);
        Assert.Equal(changed.ImageUrl, (await admin.GetFromJsonAsync<MenuItemDto>(url))!.ImageUrl);
        await ApiFixture.ExpectAsync(await admin.DeleteAsync(url), HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Cross_store_item_identifiers_cannot_read_edit_delete_or_rate_other_store_items()
    {
        using var admin = await fixture.StaffAsync("Admin");
        var item = await ApiFixture.CreateItemAsync(admin, ApiFixture.Bash);
        var wrongUrl = $"{ApiFixture.Menu(ApiFixture.Sportscene)}/{item.Id}";
        await ApiFixture.ExpectAsync(await admin.GetAsync(wrongUrl), HttpStatusCode.NotFound);
        await ApiFixture.ExpectAsync(await admin.PutAsJsonAsync(wrongUrl, ApiFixture.Input()), HttpStatusCode.NotFound);
        await ApiFixture.ExpectAsync(await admin.DeleteAsync(wrongUrl), HttpStatusCode.NotFound);
        var (_, tokens) = await fixture.RegisterAsync();
        using var customer = fixture.CreateClient(tokens.AccessToken);
        await ApiFixture.ExpectAsync(await customer.PutAsJsonAsync(wrongUrl + "/rating", new { score = 5 }), HttpStatusCode.NotFound);
        Assert.Equal(item, await admin.GetFromJsonAsync<MenuItemDto>($"{ApiFixture.Menu(ApiFixture.Bash)}/{item.Id}"));
    }

    [Fact]
    public async Task Validation_returns_problem_details_with_correlation_identifier()
    {
        using var admin = await fixture.StaffAsync("Admin");
        const string correlation = "integration-validation-correlation";
        admin.DefaultRequestHeaders.Add("X-Correlation-ID", correlation);
        var response = await admin.PostAsJsonAsync(ApiFixture.Menu(ApiFixture.Bash),
            new { kind = 99, name = "", description = "", price = -1, imageUrl = "http://insecure.example.com/image" });
        var problem = await AssertProblemAsync(response, HttpStatusCode.BadRequest);
        Assert.True(problem.TryGetProperty("errors", out var errors));
        Assert.True(errors.EnumerateObject().Count() >= 4);
        Assert.True(response.Headers.TryGetValues("X-Correlation-ID", out var values));
        Assert.Contains(correlation, values!);
        Assert.Contains(correlation, problem.GetRawText());
    }

    [Fact]
    public async Task Malformed_json_returns_problem_details_instead_of_html_or_empty_response()
    {
        using var admin = await fixture.StaffAsync("Admin");
        var response = await admin.PostAsync(ApiFixture.Menu(ApiFixture.Bash),
            new StringContent("{\"name\":", Encoding.UTF8, "application/json"));
        await AssertProblemAsync(response, HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData("-0.01")]
    [InlineData("12.345")]
    [InlineData("100000000")]
    public async Task Invalid_prices_are_not_silently_rounded_or_persisted(string price)
    {
        using var admin = await fixture.StaffAsync("Admin");
        var input = ApiFixture.Input() with { Price = decimal.Parse(price, System.Globalization.CultureInfo.InvariantCulture) };
        await AssertProblemAsync(await admin.PostAsJsonAsync(ApiFixture.Menu(ApiFixture.Bash), input), HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Staff_roles_cannot_submit_customer_ratings()
    {
        using var admin = await fixture.StaffAsync("Admin");
        using var manager = await fixture.StaffAsync("StoreManager", ApiFixture.Bash);
        var item = await ApiFixture.CreateItemAsync(admin, ApiFixture.Bash);
        var url = $"{ApiFixture.Menu(ApiFixture.Bash)}/{item.Id}/rating";
        await ApiFixture.ExpectAsync(await admin.PutAsJsonAsync(url, new { score = 4 }), HttpStatusCode.Forbidden);
        await ApiFixture.ExpectAsync(await manager.PutAsJsonAsync(url, new { score = 4 }), HttpStatusCode.Forbidden);
    }

    [Theory]
    [InlineData("page=0")]
    [InlineData("pageSize=101")]
    [InlineData("minPrice=20&maxPrice=10")]
    [InlineData("kind=99")]
    [InlineData("sort=unsupported")]
    public async Task Invalid_catalogue_queries_return_bad_request(string query)
    {
        using var client = fixture.CreateClient();
        await AssertProblemAsync(await client.GetAsync(ApiFixture.Menu(ApiFixture.Bash) + "?" + query), HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Search_kind_price_sort_and_paging_are_composable_and_isolated_to_store()
    {
        using var admin = await fixture.StaffAsync("Admin");
        var prefix = $"Query-{Guid.NewGuid():N}";
        await ApiFixture.CreateItemAsync(admin, ApiFixture.Bash, ApiFixture.Input(prefix + " A", MenuItemKind.Dish, 10m));
        await ApiFixture.CreateItemAsync(admin, ApiFixture.Bash, ApiFixture.Input(prefix + " B", MenuItemKind.Dish, 20m));
        await ApiFixture.CreateItemAsync(admin, ApiFixture.Bash, ApiFixture.Input(prefix + " C", MenuItemKind.Dish, 30m));
        await ApiFixture.CreateItemAsync(admin, ApiFixture.Bash, ApiFixture.Input(prefix + " D", MenuItemKind.Drink, 25m));
        await ApiFixture.CreateItemAsync(admin, ApiFixture.Sportscene, ApiFixture.Input(prefix + " E", MenuItemKind.Dish, 25m));
        var query = ApiFixture.Menu(ApiFixture.Bash) + $"?search={prefix.ToLowerInvariant()}&kind=1&minPrice=15&maxPrice=35&sort=-price&pageSize=1";
        var first = (await admin.GetFromJsonAsync<Page<MenuItemDto>>(query + "&page=1"))!;
        var second = (await admin.GetFromJsonAsync<Page<MenuItemDto>>(query + "&page=2"))!;
        Assert.Equal(2, first.TotalCount);
        Assert.Equal(1, first.PageNumber);
        Assert.Equal(1, first.PageSize);
        Assert.Equal(prefix + " C", Assert.Single(first.Items).Name);
        Assert.Equal(prefix + " B", Assert.Single(second.Items).Name);
        Assert.Equal(2, second.PageNumber);
        Assert.Equal(2, second.TotalCount);
    }

    [Fact]
    public async Task Rating_is_an_upsert_and_invalidates_already_cached_catalogue_page()
    {
        using var admin = await fixture.StaffAsync("Admin");
        var item = await ApiFixture.CreateItemAsync(admin, ApiFixture.Bash);
        var query = ApiFixture.Menu(ApiFixture.Bash) + $"?search={item.Name}";
        var empty = (await admin.GetFromJsonAsync<Page<MenuItemDto>>(query))!;
        Assert.Equal(0, Assert.Single(empty.Items).RatingCount);
        var (email, tokens) = await fixture.RegisterAsync();
        using var customer = fixture.CreateClient(tokens.AccessToken);
        var url = $"{ApiFixture.Menu(ApiFixture.Bash)}/{item.Id}/rating";
        await ApiFixture.ExpectAsync(await customer.PutAsJsonAsync(url, new { score = 2 }), HttpStatusCode.OK, HttpStatusCode.NoContent);
        var rated = Assert.Single((await customer.GetFromJsonAsync<Page<MenuItemDto>>(query))!.Items);
        Assert.Equal(1, rated.RatingCount);
        Assert.Equal(2d, rated.AverageRating);
        await ApiFixture.ExpectAsync(await customer.PutAsJsonAsync(url, new { score = 5 }), HttpStatusCode.OK, HttpStatusCode.NoContent);
        var updated = Assert.Single((await customer.GetFromJsonAsync<Page<MenuItemDto>>(query))!.Items);
        Assert.Equal(1, updated.RatingCount);
        Assert.Equal(5d, updated.AverageRating);
        var (_, secondTokens) = await fixture.RegisterAsync();
        using var secondCustomer = fixture.CreateClient(secondTokens.AccessToken);
        await ApiFixture.ExpectAsync(await secondCustomer.PutAsJsonAsync(url, new { score = 3 }), HttpStatusCode.OK, HttpStatusCode.NoContent);
        var averaged = Assert.Single((await customer.GetFromJsonAsync<Page<MenuItemDto>>(query))!.Items);
        Assert.Equal(2, averaged.RatingCount);
        Assert.Equal(4d, averaged.AverageRating);
        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CantinaDbContext>();
        Assert.Equal(2, await db.Ratings.CountAsync(x => x.MenuItemId == item.Id));
        Assert.Equal(5, (await db.Ratings.SingleAsync(x => x.MenuItemId == item.Id &&
            x.CustomerId == db.Users.Where(u => u.Email == email).Select(u => u.Id).First())).Score);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    public async Task Rating_outside_one_to_five_is_rejected(int score)
    {
        using var admin = await fixture.StaffAsync("Admin");
        var item = await ApiFixture.CreateItemAsync(admin, ApiFixture.Bash);
        var (_, tokens) = await fixture.RegisterAsync();
        using var customer = fixture.CreateClient(tokens.AccessToken);
        await AssertProblemAsync(await customer.PutAsJsonAsync(
            $"{ApiFixture.Menu(ApiFixture.Bash)}/{item.Id}/rating", new { score }), HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Catalogue_mutations_invalidate_cached_pages()
    {
        using var admin = await fixture.StaffAsync("Admin");
        var input = ApiFixture.Input();
        var query = ApiFixture.Menu(ApiFixture.Bash) + $"?search={input.Name}";
        Assert.Empty((await admin.GetFromJsonAsync<Page<MenuItemDto>>(query))!.Items);
        var item = await ApiFixture.CreateItemAsync(admin, ApiFixture.Bash, input);
        Assert.Single((await admin.GetFromJsonAsync<Page<MenuItemDto>>(query))!.Items);
        var url = $"{ApiFixture.Menu(ApiFixture.Bash)}/{item.Id}";
        await ApiFixture.ExpectAsync(await admin.PutAsJsonAsync(url, input with { Price = 90m }), HttpStatusCode.OK);
        Assert.Equal(90m, Assert.Single((await admin.GetFromJsonAsync<Page<MenuItemDto>>(query))!.Items).Price);
        await ApiFixture.ExpectAsync(await admin.DeleteAsync(url), HttpStatusCode.NoContent);
        Assert.Empty((await admin.GetFromJsonAsync<Page<MenuItemDto>>(query))!.Items);
    }

    [Fact]
    public async Task Cached_pages_are_store_separated_even_for_identical_query_and_item_names()
    {
        using var admin = await fixture.StaffAsync("Admin");
        var name = $"Shared-{Guid.NewGuid():N}";
        var bash = await ApiFixture.CreateItemAsync(admin, ApiFixture.Bash, ApiFixture.Input(name, price: 12m));
        var sportscene = await ApiFixture.CreateItemAsync(admin, ApiFixture.Sportscene, ApiFixture.Input(name, price: 34m));
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var bashPage = (await admin.GetFromJsonAsync<Page<MenuItemDto>>(ApiFixture.Menu(ApiFixture.Bash) + $"?search={name}"))!;
            var sportscenePage = (await admin.GetFromJsonAsync<Page<MenuItemDto>>(ApiFixture.Menu(ApiFixture.Sportscene) + $"?search={name}"))!;
            Assert.Equal(bash, Assert.Single(bashPage.Items));
            Assert.Equal(sportscene, Assert.Single(sportscenePage.Items));
        }
        await ApiFixture.ExpectAsync(await admin.PutAsJsonAsync($"{ApiFixture.Menu(ApiFixture.Bash)}/{bash.Id}",
            ApiFixture.Input(name, price: 56m)), HttpStatusCode.OK);
        Assert.Equal(56m, Assert.Single((await admin.GetFromJsonAsync<Page<MenuItemDto>>(
            ApiFixture.Menu(ApiFixture.Bash) + $"?search={name}"))!.Items).Price);
        Assert.Equal(sportscene, Assert.Single((await admin.GetFromJsonAsync<Page<MenuItemDto>>(
            ApiFixture.Menu(ApiFixture.Sportscene) + $"?search={name}"))!.Items));
    }

    [Fact]
    public async Task Unknown_store_is_not_an_empty_catalogue_and_unknown_item_returns_not_found()
    {
        using var client = fixture.CreateClient();
        var missingStore = Guid.NewGuid();
        var missingItem = Guid.NewGuid();
        var empty = await client.GetAsync(ApiFixture.Menu(ApiFixture.Bash) + $"?search={Guid.NewGuid():N}");
        await ApiFixture.ExpectAsync(empty, HttpStatusCode.OK);
        Assert.Empty((await empty.Content.ReadFromJsonAsync<Page<MenuItemDto>>())!.Items);
        await AssertProblemAsync(await client.GetAsync(ApiFixture.Menu(missingStore)), HttpStatusCode.NotFound);
        await AssertProblemAsync(await client.GetAsync($"{ApiFixture.Menu(ApiFixture.Bash)}/{missingItem}"), HttpStatusCode.NotFound);
        await AssertProblemAsync(await client.GetAsync($"{ApiFixture.Menu(missingStore)}/{missingItem}"), HttpStatusCode.NotFound);
        using var admin = await fixture.StaffAsync("Admin");
        await AssertProblemAsync(await admin.PostAsJsonAsync(ApiFixture.Menu(missingStore), ApiFixture.Input()), HttpStatusCode.NotFound);
        await AssertProblemAsync(await admin.PutAsJsonAsync($"{ApiFixture.Menu(ApiFixture.Bash)}/{missingItem}", ApiFixture.Input()), HttpStatusCode.NotFound);
        await AssertProblemAsync(await admin.DeleteAsync($"{ApiFixture.Menu(ApiFixture.Bash)}/{missingItem}"), HttpStatusCode.NotFound);
    }

    private static async Task<JsonElement> AssertProblemAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        await ApiFixture.ExpectAsync(response, expected);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal((int)expected, body.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("title").GetString()));
        return body;
    }
}
