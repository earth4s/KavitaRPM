using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Kavita.Database.Migrations;

[DbContext(typeof(DataContext))]
[Migration("20261118000100_GcdMetadataIds")]
public sealed class GcdMetadataIds : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>("GcdId", "Series", type: "INTEGER", nullable: true);
        migrationBuilder.AddColumn<int>("GcdId", "Chapter", type: "INTEGER", nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn("GcdId", "Series");
        migrationBuilder.DropColumn("GcdId", "Chapter");
    }
}
