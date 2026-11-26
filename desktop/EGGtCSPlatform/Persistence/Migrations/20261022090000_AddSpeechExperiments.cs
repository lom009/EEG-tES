using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace EGGtCSPlatform.Persistence.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20261022090000_AddSpeechExperiments")]
public sealed partial class AddSpeechExperiments : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE TABLE "SpeechExperiments" (
            "Id" TEXT NOT NULL CONSTRAINT "PK_SpeechExperiments" PRIMARY KEY,
            "ExperimentId" INTEGER NOT NULL,
            "SourceJson" TEXT NOT NULL,
            "Version" INTEGER NOT NULL,
            "StartedAt" TEXT NOT NULL,
            "EndedAt" TEXT NULL,
            "Status" INTEGER NOT NULL,
            CONSTRAINT "FK_SpeechExperiments_Experiments_ExperimentId" FOREIGN KEY ("ExperimentId") REFERENCES "Experiments" ("Id") ON DELETE CASCADE
        );
        CREATE UNIQUE INDEX "IX_SpeechExperiments_ExperimentId" ON "SpeechExperiments" ("ExperimentId");
        CREATE TABLE "SpeechTrainingRounds" (
            "Id" TEXT NOT NULL CONSTRAINT "PK_SpeechTrainingRounds" PRIMARY KEY,
            "SpeechExperimentId" TEXT NOT NULL,
            "Number" INTEGER NOT NULL,
            "DelayMilliseconds" REAL NOT NULL,
            "EndedAt" TEXT NULL,
            "Status" INTEGER NOT NULL,
            "ResultJson" TEXT NOT NULL,
            CONSTRAINT "FK_SpeechTrainingRounds_SpeechExperiments_SpeechExperimentId" FOREIGN KEY ("SpeechExperimentId") REFERENCES "SpeechExperiments" ("Id") ON DELETE CASCADE
        );
        CREATE UNIQUE INDEX "IX_SpeechTrainingRounds_SpeechExperimentId_Number" ON "SpeechTrainingRounds" ("SpeechExperimentId", "Number");
        """);

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("SpeechTrainingRounds");
        migrationBuilder.DropTable("SpeechExperiments");
    }
}
