using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
namespace EGGtCSPlatform.Persistence.Migrations;
[DbContext(typeof(AppDbContext))]
[Migration("20261030090000_AddToleranceTests")]
public sealed partial class AddToleranceTests : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE TABLE "ToleranceConfigurations" (
            "Id" TEXT NOT NULL CONSTRAINT "PK_ToleranceConfigurations" PRIMARY KEY,
            "SubjectId" INTEGER NOT NULL,
            "ConfigurationKey" TEXT NOT NULL,
            "ConfigurationJson" TEXT NOT NULL,
            "Kind" INTEGER NOT NULL,
            CONSTRAINT "FK_ToleranceConfigurations_Subjects_SubjectId" FOREIGN KEY ("SubjectId") REFERENCES "Subjects" ("Id") ON DELETE RESTRICT
        );
        CREATE UNIQUE INDEX "IX_ToleranceConfigurations_SubjectId_ConfigurationKey" ON "ToleranceConfigurations" ("SubjectId", "ConfigurationKey");
        CREATE TABLE "ToleranceSnapshots" (
            "Id" TEXT NOT NULL CONSTRAINT "PK_ToleranceSnapshots" PRIMARY KEY,
            "ConfigurationId" TEXT NOT NULL,
            "OperatorId" INTEGER NOT NULL,
            "SavedAtUtcTicks" INTEGER NOT NULL,
            "SnapshotJson" TEXT NOT NULL,
            CONSTRAINT "FK_ToleranceSnapshots_ToleranceConfigurations_ConfigurationId" FOREIGN KEY ("ConfigurationId") REFERENCES "ToleranceConfigurations" ("Id") ON DELETE CASCADE,
            CONSTRAINT "FK_ToleranceSnapshots_Operators_OperatorId" FOREIGN KEY ("OperatorId") REFERENCES "Operators" ("Id") ON DELETE RESTRICT
        );
        CREATE INDEX "IX_ToleranceSnapshots_ConfigurationId_SavedAtUtcTicks" ON "ToleranceSnapshots" ("ConfigurationId", "SavedAtUtcTicks");
        CREATE INDEX "IX_ToleranceSnapshots_OperatorId" ON "ToleranceSnapshots" ("OperatorId");
        """);
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("ToleranceSnapshots");
        migrationBuilder.DropTable("ToleranceConfigurations");
    }
}
