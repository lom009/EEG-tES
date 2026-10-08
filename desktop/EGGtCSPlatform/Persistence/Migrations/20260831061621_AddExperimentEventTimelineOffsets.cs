using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EGGtCSPlatform.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddExperimentEventTimelineOffsets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "TimelineEndMilliseconds",
                table: "ExperimentEvents",
                type: "REAL",
                nullable: true
            );

            migrationBuilder.AddColumn<double>(
                name: "TimelineStartMilliseconds",
                table: "ExperimentEvents",
                type: "REAL",
                nullable: true
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "TimelineEndMilliseconds", table: "ExperimentEvents");

            migrationBuilder.DropColumn(
                name: "TimelineStartMilliseconds",
                table: "ExperimentEvents"
            );
        }
    }
}
