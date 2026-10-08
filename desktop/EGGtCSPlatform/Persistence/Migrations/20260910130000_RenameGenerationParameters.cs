using Microsoft.EntityFrameworkCore.Migrations;

namespace EGGtCSPlatform.Persistence.Migrations;

public partial class RenameGenerationParameters : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.RenameColumn(
            name: "SimulationJson",
            table: "Experiments",
            newName: "GenerationJson"
        );
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.RenameColumn(
            name: "GenerationJson",
            table: "Experiments",
            newName: "SimulationJson"
        );
    }
}
