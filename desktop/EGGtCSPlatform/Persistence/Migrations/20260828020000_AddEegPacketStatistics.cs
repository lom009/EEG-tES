using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EGGtCSPlatform.Persistence.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20260828020000_AddEegPacketStatistics")]
public partial class AddEegPacketStatistics : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<long>(
            "EegDuplicatePacketCount",
            "ExperimentRuns",
            "INTEGER",
            nullable: true
        );
        migrationBuilder.AddColumn<long>(
            "EegLateDiscardedPacketCount",
            "ExperimentRuns",
            "INTEGER",
            nullable: true
        );
        migrationBuilder.AddColumn<long>(
            "EegLostPacketCount",
            "ExperimentRuns",
            "INTEGER",
            nullable: true
        );
        migrationBuilder.AddColumn<long>(
            "EegOutOfOrderPacketCount",
            "ExperimentRuns",
            "INTEGER",
            nullable: true
        );
        migrationBuilder.AddColumn<bool>(
            "EegPacketReorderingEnabled",
            "ExperimentRuns",
            "INTEGER",
            nullable: true
        );
        migrationBuilder.AddColumn<long>(
            "EegReceivedPacketCount",
            "ExperimentRuns",
            "INTEGER",
            nullable: true
        );
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn("EegDuplicatePacketCount", "ExperimentRuns");
        migrationBuilder.DropColumn("EegLateDiscardedPacketCount", "ExperimentRuns");
        migrationBuilder.DropColumn("EegLostPacketCount", "ExperimentRuns");
        migrationBuilder.DropColumn("EegOutOfOrderPacketCount", "ExperimentRuns");
        migrationBuilder.DropColumn("EegPacketReorderingEnabled", "ExperimentRuns");
        migrationBuilder.DropColumn("EegReceivedPacketCount", "ExperimentRuns");
    }
}
