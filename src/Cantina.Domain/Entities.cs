namespace Cantina.Domain;

public enum MenuItemKind { Dish = 1, Drink = 2 }

public sealed class Store
{
    public Guid Id { get; set; }
    public string Slug { get; set; } = "";
    public string Name { get; set; } = "";
}

public sealed class MenuItem
{
    public Guid Id { get; set; }
    public Guid StoreId { get; set; }
    public MenuItemKind Kind { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public decimal Price { get; set; }
    public string ImageUrl { get; set; } = "";
    public ICollection<Rating> Ratings { get; set; } = new List<Rating>();
}

public sealed class Rating
{
    public Guid StoreId { get; set; }
    public Guid MenuItemId { get; set; }
    public string CustomerId { get; set; } = "";
    public int Score { get; set; }
}

public sealed class StoreMembership
{
    public Guid StoreId { get; set; }
    public string UserId { get; set; } = "";
}
