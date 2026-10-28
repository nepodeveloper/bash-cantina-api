using Cantina.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Cantina.Infrastructure;

public sealed class ApplicationUser : IdentityUser
{
    public string Name { get; set; } = "";
}

public sealed class CantinaDbContext(DbContextOptions<CantinaDbContext> options)
    : IdentityDbContext<ApplicationUser>(options)
{
    public DbSet<Store> Stores => Set<Store>();
    public DbSet<StoreMembership> StoreMemberships => Set<StoreMembership>();
    public DbSet<MenuItem> MenuItems => Set<MenuItem>();
    public DbSet<Rating> Ratings => Set<Rating>();
    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.Entity<ApplicationUser>(entity =>
        {
            entity.HasIndex(x => x.NormalizedEmail).HasDatabaseName("EmailIndex").IsUnique();
            entity.Property(x => x.Name).HasMaxLength(120).IsRequired();
            entity.ToTable("AspNetUsers", table =>
                table.HasCheckConstraint("CK_AspNetUsers_Name", """length(btrim("Name")) > 0"""));
        });

        builder.Entity<Store>(entity =>
        {
            entity.ToTable("Stores", table =>
            {
                table.HasCheckConstraint("CK_Stores_Slug", """length(btrim("Slug")) > 0""");
                table.HasCheckConstraint("CK_Stores_Name", """length(btrim("Name")) > 0""");
            });
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Slug).HasMaxLength(80).IsRequired();
            entity.Property(x => x.Name).HasMaxLength(120).IsRequired();
            entity.Property(x => x.IsActive).HasDefaultValue(true);
            entity.HasIndex(x => x.Slug).IsUnique();
            entity.HasData(
                new Store { Id = SeedData.BashStoreId, Slug = "bash", Name = "Bash", IsActive = true },
                new Store { Id = SeedData.SportsceneStoreId, Slug = "sportscene", Name = "Sportscene", IsActive = true });
        });

        builder.Entity<StoreMembership>(entity =>
        {
            entity.ToTable("StoreMemberships");
            entity.HasKey(x => new { x.StoreId, x.UserId });
            entity.Property(x => x.UserId).HasMaxLength(450);
            entity.HasOne<Store>().WithMany().HasForeignKey(x => x.StoreId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<MenuItem>(entity =>
        {
            entity.HasData(
                Dish("a1111111-1111-1111-1111-111111111101", SeedData.BashStoreId, MenuItemKind.Dish, "Bash Burger", "Grilled beef burger with fresh salad.", 89.90m),
                Dish("a1111111-1111-1111-1111-111111111102", SeedData.BashStoreId, MenuItemKind.Dish, "Garden Bowl", "Seasonal vegetables, grains and lemon dressing.", 69.50m),
                Dish("a1111111-1111-1111-1111-111111111103", SeedData.BashStoreId, MenuItemKind.Dish, "Chicken Wrap", "Grilled chicken, greens and herb sauce.", 74.00m),
                Dish("a1111111-1111-1111-1111-111111111104", SeedData.BashStoreId, MenuItemKind.Drink, "Iced Coffee", "Chilled coffee served over ice.", 32.00m),
                Dish("a1111111-1111-1111-1111-111111111105", SeedData.BashStoreId, MenuItemKind.Drink, "Berry Smoothie", "Blended berries and yoghurt.", 39.90m),
                Dish("b2222222-2222-2222-2222-222222222201", SeedData.SportsceneStoreId, MenuItemKind.Dish, "Power Bowl", "Brown rice, chicken and roasted vegetables.", 84.50m),
                Dish("b2222222-2222-2222-2222-222222222202", SeedData.SportsceneStoreId, MenuItemKind.Dish, "Veggie Pita", "Roasted vegetables and hummus in a warm pita.", 64.00m),
                Dish("b2222222-2222-2222-2222-222222222203", SeedData.SportsceneStoreId, MenuItemKind.Dish, "Chicken Salad", "Crisp greens with grilled chicken.", 79.90m),
                Dish("b2222222-2222-2222-2222-222222222204", SeedData.SportsceneStoreId, MenuItemKind.Drink, "Citrus Cooler", "Fresh citrus juice with sparkling water.", 29.00m),
                Dish("b2222222-2222-2222-2222-222222222205", SeedData.SportsceneStoreId, MenuItemKind.Drink, "Banana Shake", "Banana, milk and a touch of cinnamon.", 38.00m));
            entity.ToTable("MenuItems", table =>
            {
                table.HasCheckConstraint("CK_MenuItems_Kind", "\"Kind\" IN (1, 2)");
                table.HasCheckConstraint("CK_MenuItems_Price", "\"Price\" >= 0 AND \"Price\" <= 99999999.99");
                table.HasCheckConstraint("CK_MenuItems_Name", """length(btrim("Name")) > 0""");
                table.HasCheckConstraint("CK_MenuItems_Description", """length(btrim("Description")) > 0""");
                table.HasCheckConstraint("CK_MenuItems_ImageUrl", "\"ImageUrl\" ILIKE 'https://%'");
            });
            entity.HasKey(x => x.Id);
            entity.HasAlternateKey(x => new { x.StoreId, x.Id });
            // PostgreSQL's hidden xmin system column is the row version; it adds no physical column.
            entity.Property<uint>("xmin").HasColumnName("xmin").ValueGeneratedOnAddOrUpdate().IsConcurrencyToken();
            entity.Property(x => x.Name).HasMaxLength(120).IsRequired();
            entity.Property(x => x.Description).HasMaxLength(2000).IsRequired();
            entity.Property(x => x.ImageUrl).HasMaxLength(2048).IsRequired();
            entity.Property(x => x.Price).HasPrecision(10, 2);
            entity.HasIndex(x => new { x.StoreId, x.Name, x.Id });
            entity.HasIndex(x => new { x.StoreId, x.Price, x.Id });
            entity.HasIndex(x => new { x.StoreId, x.Kind });
            entity.HasOne<Store>().WithMany().HasForeignKey(x => x.StoreId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<Rating>(entity =>
        {
            entity.ToTable("Ratings", table =>
                table.HasCheckConstraint("CK_Ratings_Score", "\"Score\" BETWEEN 1 AND 5"));
            entity.HasKey(x => new { x.StoreId, x.MenuItemId, x.CustomerId });
            entity.Property(x => x.CustomerId).HasMaxLength(450);
            entity.Property(x => x.ReviewText).HasMaxLength(1000);
            entity.HasIndex(x => new { x.StoreId, x.MenuItemId, x.UpdatedAtUtc });
            entity.HasOne<MenuItem>().WithMany(x => x.Ratings)
                .HasForeignKey(x => new { x.StoreId, x.MenuItemId })
                .HasPrincipalKey(x => new { x.StoreId, x.Id })
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<IdempotencyRecord>(entity =>
        {
            entity.ToTable("IdempotencyRecords");
            entity.HasKey(x => new { x.UserId, x.Key });
            entity.Property(x => x.UserId).HasMaxLength(450);
            entity.Property(x => x.Key).HasMaxLength(128);
            entity.Property(x => x.Fingerprint).HasMaxLength(64).IsRequired();
            entity.Property(x => x.Location).HasMaxLength(2048);
            entity.HasIndex(x => new { x.UserId, x.CreatedAtUtc });
            entity.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
    }

    private static MenuItem Dish(string id, Guid storeId, MenuItemKind kind, string name, string description, decimal price) =>
        new()
        {
            Id = Guid.Parse(id), StoreId = storeId, Kind = kind, Name = name,
            Description = description, Price = price,
            ImageUrl = $"https://images.example.com/menu/{id}.jpg",
            CreatedAtUtc = SeedData.SeedTimestamp, UpdatedAtUtc = SeedData.SeedTimestamp
        };
}
