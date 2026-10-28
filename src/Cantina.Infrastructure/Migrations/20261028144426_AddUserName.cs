using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cantina.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddUserName : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Name",
                table: "AspNetUsers",
                type: "character varying(120)",
                maxLength: 120,
                nullable: false,
                defaultValue: "");

            // Accounts created before this column existed are backfilled from the email local part,
            // otherwise the non-blank constraint below could not be applied.
            migrationBuilder.Sql("""
                UPDATE "AspNetUsers"
                SET "Name" = left(coalesce(nullif(split_part(coalesce("Email", ''), '@', 1), ''), 'Customer'), 120)
                WHERE btrim("Name") = ''
                """);

            migrationBuilder.AddCheckConstraint(
                name: "CK_AspNetUsers_Name",
                table: "AspNetUsers",
                sql: "length(btrim(\"Name\")) > 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_AspNetUsers_Name",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "Name",
                table: "AspNetUsers");
        }
    }
}
