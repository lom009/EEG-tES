using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EGGtCSPlatform.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddInterruptRequest : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "InterruptRequestSource",
                table: "ExperimentRuns",
                type: "TEXT",
                nullable: true
            );

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "InterruptRequestedAtUtc",
                table: "ExperimentRuns",
                type: "TEXT",
                nullable: true
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "InterruptRequestSource", table: "ExperimentRuns");

            migrationBuilder.DropColumn(name: "InterruptRequestedAtUtc", table: "ExperimentRuns");
        }
    }
}
