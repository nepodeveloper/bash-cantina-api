using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cantina.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddStoreStatusRatingReviewsAndAudit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsActive",
                table: "Stores",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CreatedAtUtc",
                table: "Ratings",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.AddColumn<string>(
                name: "ReviewText",
                table: "Ratings",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "UpdatedAtUtc",
                table: "Ratings",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CreatedAtUtc",
                table: "MenuItems",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "UpdatedAtUtc",
                table: "MenuItems",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            // "MenuItems"."xmin" is mapped as a read-only concurrency token. PostgreSQL supplies that system
            // column on every table, so the scaffolded AddColumn/DropColumn statements were removed by hand.

            migrationBuilder.UpdateData(
                table: "MenuItems",
                keyColumn: "Id",
                keyValue: new Guid("a1111111-1111-1111-1111-111111111101"),
                columns: new[] { "CreatedAtUtc", "UpdatedAtUtc" },
                values: new object[] { new DateTimeOffset(new DateTime(2026, 10, 28, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), new DateTimeOffset(new DateTime(2026, 10, 28, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)) });

            migrationBuilder.UpdateData(
                table: "MenuItems",
                keyColumn: "Id",
                keyValue: new Guid("a1111111-1111-1111-1111-111111111102"),
                columns: new[] { "CreatedAtUtc", "UpdatedAtUtc" },
                values: new object[] { new DateTimeOffset(new DateTime(2026, 10, 28, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), new DateTimeOffset(new DateTime(2026, 10, 28, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)) });

            migrationBuilder.UpdateData(
                table: "MenuItems",
                keyColumn: "Id",
                keyValue: new Guid("a1111111-1111-1111-1111-111111111103"),
                columns: new[] { "CreatedAtUtc", "UpdatedAtUtc" },
                values: new object[] { new DateTimeOffset(new DateTime(2026, 10, 28, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), new DateTimeOffset(new DateTime(2026, 10, 28, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)) });

            migrationBuilder.UpdateData(
                table: "MenuItems",
                keyColumn: "Id",
                keyValue: new Guid("a1111111-1111-1111-1111-111111111104"),
                columns: new[] { "CreatedAtUtc", "UpdatedAtUtc" },
                values: new object[] { new DateTimeOffset(new DateTime(2026, 10, 28, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), new DateTimeOffset(new DateTime(2026, 10, 28, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)) });

            migrationBuilder.UpdateData(
                table: "MenuItems",
                keyColumn: "Id",
                keyValue: new Guid("a1111111-1111-1111-1111-111111111105"),
                columns: new[] { "CreatedAtUtc", "UpdatedAtUtc" },
                values: new object[] { new DateTimeOffset(new DateTime(2026, 10, 28, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), new DateTimeOffset(new DateTime(2026, 10, 28, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)) });

            migrationBuilder.UpdateData(
                table: "MenuItems",
                keyColumn: "Id",
                keyValue: new Guid("b2222222-2222-2222-2222-222222222201"),
                columns: new[] { "CreatedAtUtc", "UpdatedAtUtc" },
                values: new object[] { new DateTimeOffset(new DateTime(2026, 10, 28, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), new DateTimeOffset(new DateTime(2026, 10, 28, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)) });

            migrationBuilder.UpdateData(
                table: "MenuItems",
                keyColumn: "Id",
                keyValue: new Guid("b2222222-2222-2222-2222-222222222202"),
                columns: new[] { "CreatedAtUtc", "UpdatedAtUtc" },
                values: new object[] { new DateTimeOffset(new DateTime(2026, 10, 28, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), new DateTimeOffset(new DateTime(2026, 10, 28, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)) });

            migrationBuilder.UpdateData(
                table: "MenuItems",
                keyColumn: "Id",
                keyValue: new Guid("b2222222-2222-2222-2222-222222222203"),
                columns: new[] { "CreatedAtUtc", "UpdatedAtUtc" },
                values: new object[] { new DateTimeOffset(new DateTime(2026, 10, 28, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), new DateTimeOffset(new DateTime(2026, 10, 28, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)) });

            migrationBuilder.UpdateData(
                table: "MenuItems",
                keyColumn: "Id",
                keyValue: new Guid("b2222222-2222-2222-2222-222222222204"),
                columns: new[] { "CreatedAtUtc", "UpdatedAtUtc" },
                values: new object[] { new DateTimeOffset(new DateTime(2026, 10, 28, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), new DateTimeOffset(new DateTime(2026, 10, 28, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)) });

            migrationBuilder.UpdateData(
                table: "MenuItems",
                keyColumn: "Id",
                keyValue: new Guid("b2222222-2222-2222-2222-222222222205"),
                columns: new[] { "CreatedAtUtc", "UpdatedAtUtc" },
                values: new object[] { new DateTimeOffset(new DateTime(2026, 10, 28, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), new DateTimeOffset(new DateTime(2026, 10, 28, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)) });

            migrationBuilder.UpdateData(
                table: "Stores",
                keyColumn: "Id",
                keyValue: new Guid("11111111-1111-1111-1111-111111111111"),
                column: "IsActive",
                value: true);

            migrationBuilder.UpdateData(
                table: "Stores",
                keyColumn: "Id",
                keyValue: new Guid("22222222-2222-2222-2222-222222222222"),
                column: "IsActive",
                value: true);

            migrationBuilder.CreateIndex(
                name: "IX_Ratings_StoreId_MenuItemId_UpdatedAtUtc",
                table: "Ratings",
                columns: new[] { "StoreId", "MenuItemId", "UpdatedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Ratings_StoreId_MenuItemId_UpdatedAtUtc",
                table: "Ratings");

            migrationBuilder.DropColumn(
                name: "IsActive",
                table: "Stores");

            migrationBuilder.DropColumn(
                name: "CreatedAtUtc",
                table: "Ratings");

            migrationBuilder.DropColumn(
                name: "ReviewText",
                table: "Ratings");

            migrationBuilder.DropColumn(
                name: "UpdatedAtUtc",
                table: "Ratings");

            migrationBuilder.DropColumn(
                name: "CreatedAtUtc",
                table: "MenuItems");

            migrationBuilder.DropColumn(
                name: "UpdatedAtUtc",
                table: "MenuItems");
        }
    }
}
