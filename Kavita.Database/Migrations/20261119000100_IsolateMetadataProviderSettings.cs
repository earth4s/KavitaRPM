using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Kavita.Database.Migrations;

[DbContext(typeof(DataContext))]
[Migration("20261119000100_IsolateMetadataProviderSettings")]
public sealed class IsolateMetadataProviderSettings : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // This migration precedes ManagedImageStore. A database that already applied
        // that upstream migration uses key 46 for its composite-cover schedule.
        migrationBuilder.Sql("""
            UPDATE "ServerSetting" SET "Key" = 1000
            WHERE "Key" = 46 AND NOT EXISTS (
                SELECT 1 FROM "__EFMigrationsHistory"
                WHERE "MigrationId" = '20261120204102_ManagedImageStore'
            );
            UPDATE "ServerSetting" SET "Key" = 1001 WHERE "Key" = 47;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // On rollback to the metadata build, restore the original provider IDs.
        migrationBuilder.Sql("""
            DELETE FROM "ServerSetting" WHERE "Key" = 46
                AND EXISTS (SELECT 1 FROM "ServerSetting" WHERE "Key" = 1000);
            UPDATE "ServerSetting" SET "Key" = 46 WHERE "Key" = 1000;
            UPDATE "ServerSetting" SET "Key" = 47 WHERE "Key" = 1001;
            """);
    }
}
