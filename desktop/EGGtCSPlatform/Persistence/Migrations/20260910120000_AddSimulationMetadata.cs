using Microsoft.EntityFrameworkCore.Migrations;

namespace EGGtCSPlatform.Persistence.Migrations;

public partial class AddSimulationMetadata : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "SimulationJson",
            table: "Experiments",
            type: "TEXT",
            nullable: true
        );
        migrationBuilder.AddColumn<string>(
            name: "EnvelopeJson",
            table: "StimulusParadigms",
            type: "TEXT",
            nullable: true
        );
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "SimulationJson", table: "Experiments");
        migrationBuilder.DropColumn(name: "EnvelopeJson", table: "StimulusParadigms");
    }
}
