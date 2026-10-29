using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cantina.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddIdempotencyOwnership : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "Owner",
                table: "IdempotencyRecords",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Owner",
                table: "IdempotencyRecords");
        }
    }
}
