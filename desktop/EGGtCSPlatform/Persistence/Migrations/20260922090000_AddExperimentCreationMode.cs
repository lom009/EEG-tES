using Microsoft.EntityFrameworkCore.Migrations;

namespace EGGtCSPlatform.Persistence.Migrations;

public partial class AddExperimentCreationMode : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "CreationMode",
            table: "Experiments",
            type: "INTEGER",
            nullable: false,
            defaultValue: 2
        );
        migrationBuilder.AddColumn<string>(
            name: "ConfigurationJson",
            table: "ExperimentRuns",
            type: "TEXT",
            nullable: true
        );
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "CreationMode", table: "Experiments");
        migrationBuilder.DropColumn(name: "ConfigurationJson", table: "ExperimentRuns");
    }
}
