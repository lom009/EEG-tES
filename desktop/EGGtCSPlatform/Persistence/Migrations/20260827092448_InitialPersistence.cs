using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EGGtCSPlatform.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialPersistence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ApplicationSessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    StartedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    LastHeartbeatAtUtc = table.Column<DateTimeOffset>(
                        type: "TEXT",
                        nullable: false
                    ),
                    ClosedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApplicationSessions", x => x.Id);
                }
            );

            migrationBuilder.CreateTable(
                name: "Operators",
                columns: table => new
                {
                    Id = table
                        .Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Username = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    NormalizedUsername = table.Column<string>(
                        type: "TEXT",
                        maxLength: 64,
                        nullable: false
                    ),
                    PasswordSalt = table.Column<byte[]>(type: "BLOB", nullable: false),
                    PasswordHash = table.Column<byte[]>(type: "BLOB", nullable: false),
                    PasswordIterations = table.Column<int>(type: "INTEGER", nullable: false),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Operators", x => x.Id);
                }
            );

            migrationBuilder.CreateTable(
                name: "Subjects",
                columns: table => new
                {
                    Id = table
                        .Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SubjectCode = table.Column<string>(
                        type: "TEXT",
                        maxLength: 64,
                        nullable: false
                    ),
                    NormalizedSubjectCode = table.Column<string>(
                        type: "TEXT",
                        maxLength: 64,
                        nullable: false
                    ),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Subjects", x => x.Id);
                }
            );

            migrationBuilder.CreateTable(
                name: "Experiments",
                columns: table => new
                {
                    Id = table
                        .Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ExperimentCode = table.Column<string>(
                        type: "TEXT",
                        maxLength: 32,
                        nullable: false
                    ),
                    ScheduledAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    Remarks = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    OperatorId = table.Column<long>(type: "INTEGER", nullable: false),
                    SubjectId = table.Column<long>(type: "INTEGER", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Experiments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Experiments_Operators_OperatorId",
                        column: x => x.OperatorId,
                        principalTable: "Operators",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade
                    );
                    table.ForeignKey(
                        name: "FK_Experiments_Subjects_SubjectId",
                        column: x => x.SubjectId,
                        principalTable: "Subjects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade
                    );
                }
            );

            migrationBuilder.CreateTable(
                name: "ExperimentElectrodes",
                columns: table => new
                {
                    Id = table
                        .Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ExperimentId = table.Column<long>(type: "INTEGER", nullable: false),
                    SiteId = table.Column<string>(type: "TEXT", nullable: false),
                    AcquisitionUsage = table.Column<int>(type: "INTEGER", nullable: false),
                    IsStimulus = table.Column<bool>(type: "INTEGER", nullable: false),
                    AcquisitionPhysicalChannelId = table.Column<int>(
                        type: "INTEGER",
                        nullable: true
                    ),
                    StimulationPhysicalChannelId = table.Column<int>(
                        type: "INTEGER",
                        nullable: true
                    ),
                    StimulationRole = table.Column<string>(type: "TEXT", nullable: true),
                    TargetDisplayOrder = table.Column<int>(type: "INTEGER", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExperimentElectrodes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ExperimentElectrodes_Experiments_ExperimentId",
                        column: x => x.ExperimentId,
                        principalTable: "Experiments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade
                    );
                }
            );

            migrationBuilder.CreateTable(
                name: "ExperimentRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ExperimentId = table.Column<long>(type: "INTEGER", nullable: false),
                    ApplicationSessionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    RunMode = table.Column<string>(type: "TEXT", nullable: false),
                    CycleCount = table.Column<int>(type: "INTEGER", nullable: false),
                    SampleRateHz = table.Column<int>(type: "INTEGER", nullable: false),
                    AcquisitionDurationMilliseconds = table.Column<double>(
                        type: "REAL",
                        nullable: false
                    ),
                    BlankingDurationMilliseconds = table.Column<double>(
                        type: "REAL",
                        nullable: false
                    ),
                    StimulationDurationMilliseconds = table.Column<double>(
                        type: "REAL",
                        nullable: false
                    ),
                    RecoveryDurationMilliseconds = table.Column<double>(
                        type: "REAL",
                        nullable: false
                    ),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    StartedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    EndedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    LastHeartbeatAtUtc = table.Column<DateTimeOffset>(
                        type: "TEXT",
                        nullable: false
                    ),
                    LastKnownStage = table.Column<string>(type: "TEXT", nullable: true),
                    LastKnownCycle = table.Column<int>(type: "INTEGER", nullable: false),
                    LastKnownElapsedMilliseconds = table.Column<double>(
                        type: "REAL",
                        nullable: false
                    ),
                    Revision = table.Column<int>(type: "INTEGER", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExperimentRuns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ExperimentRuns_ApplicationSessions_ApplicationSessionId",
                        column: x => x.ApplicationSessionId,
                        principalTable: "ApplicationSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade
                    );
                    table.ForeignKey(
                        name: "FK_ExperimentRuns_Experiments_ExperimentId",
                        column: x => x.ExperimentId,
                        principalTable: "Experiments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade
                    );
                }
            );

            migrationBuilder.CreateTable(
                name: "ImpedanceSnapshots",
                columns: table => new
                {
                    Id = table
                        .Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ExperimentId = table.Column<long>(type: "INTEGER", nullable: false),
                    SiteId = table.Column<string>(type: "TEXT", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    KiloOhms = table.Column<double>(type: "REAL", nullable: true),
                    Passed = table.Column<bool>(type: "INTEGER", nullable: false),
                    MeasuredAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ImpedanceSnapshots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ImpedanceSnapshots_Experiments_ExperimentId",
                        column: x => x.ExperimentId,
                        principalTable: "Experiments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade
                    );
                }
            );

            migrationBuilder.CreateTable(
                name: "StimulusParadigms",
                columns: table => new
                {
                    Id = table
                        .Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ExperimentId = table.Column<long>(type: "INTEGER", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    ArrayMode = table.Column<string>(type: "TEXT", nullable: false),
                    Direction = table.Column<string>(type: "TEXT", nullable: false),
                    ShamMode = table.Column<string>(type: "TEXT", nullable: false),
                    RampSeconds = table.Column<double>(type: "REAL", nullable: false),
                    FrequencyHz = table.Column<double>(type: "REAL", nullable: false),
                    DutyPercent = table.Column<double>(type: "REAL", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StimulusParadigms", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StimulusParadigms_Experiments_ExperimentId",
                        column: x => x.ExperimentId,
                        principalTable: "Experiments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade
                    );
                }
            );

            migrationBuilder.CreateTable(
                name: "EegFiles",
                columns: table => new
                {
                    Id = table
                        .Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ExperimentRunId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Format = table.Column<int>(type: "INTEGER", nullable: false),
                    Location = table.Column<int>(type: "INTEGER", nullable: false),
                    State = table.Column<int>(type: "INTEGER", nullable: false),
                    Path = table.Column<string>(type: "TEXT", nullable: false),
                    SourceFileId = table.Column<long>(type: "INTEGER", nullable: true),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CleanedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EegFiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EegFiles_EegFiles_SourceFileId",
                        column: x => x.SourceFileId,
                        principalTable: "EegFiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict
                    );
                    table.ForeignKey(
                        name: "FK_EegFiles_ExperimentRuns_ExperimentRunId",
                        column: x => x.ExperimentRunId,
                        principalTable: "ExperimentRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade
                    );
                }
            );

            migrationBuilder.CreateTable(
                name: "ExperimentEvents",
                columns: table => new
                {
                    Id = table
                        .Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ExperimentRunId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    Cycle = table.Column<int>(type: "INTEGER", nullable: false),
                    Sequence = table.Column<int>(type: "INTEGER", nullable: false),
                    RequestedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    StartedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    EndedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    EndTimeAccuracy = table.Column<int>(type: "INTEGER", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExperimentEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ExperimentEvents_ExperimentRuns_ExperimentRunId",
                        column: x => x.ExperimentRunId,
                        principalTable: "ExperimentRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade
                    );
                }
            );

            migrationBuilder.CreateTable(
                name: "ExperimentIncidents",
                columns: table => new
                {
                    Id = table
                        .Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ExperimentRunId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    Source = table.Column<string>(type: "TEXT", nullable: false),
                    Message = table.Column<string>(type: "TEXT", nullable: false),
                    ExceptionType = table.Column<string>(type: "TEXT", nullable: true),
                    StackTrace = table.Column<string>(type: "TEXT", nullable: true),
                    DeviceStopSucceeded = table.Column<bool>(type: "INTEGER", nullable: true),
                    DeviceStopError = table.Column<string>(type: "TEXT", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExperimentIncidents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ExperimentIncidents_ExperimentRuns_ExperimentRunId",
                        column: x => x.ExperimentRunId,
                        principalTable: "ExperimentRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade
                    );
                }
            );

            migrationBuilder.CreateTable(
                name: "StimulusTargets",
                columns: table => new
                {
                    Id = table
                        .Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    StimulusParadigmId = table.Column<long>(type: "INTEGER", nullable: false),
                    DisplayOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    PeakCurrentMilliAmps = table.Column<double>(type: "REAL", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StimulusTargets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StimulusTargets_StimulusParadigms_StimulusParadigmId",
                        column: x => x.StimulusParadigmId,
                        principalTable: "StimulusParadigms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade
                    );
                }
            );

            migrationBuilder.CreateTable(
                name: "StimulusChannels",
                columns: table => new
                {
                    Id = table
                        .Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    StimulusTargetId = table.Column<long>(type: "INTEGER", nullable: false),
                    SiteId = table.Column<string>(type: "TEXT", nullable: false),
                    PhysicalChannelId = table.Column<int>(type: "INTEGER", nullable: false),
                    Role = table.Column<string>(type: "TEXT", nullable: false),
                    CurrentMilliAmps = table.Column<double>(type: "REAL", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StimulusChannels", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StimulusChannels_StimulusTargets_StimulusTargetId",
                        column: x => x.StimulusTargetId,
                        principalTable: "StimulusTargets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade
                    );
                }
            );

            migrationBuilder.CreateIndex(
                name: "IX_EegFiles_ExperimentRunId",
                table: "EegFiles",
                column: "ExperimentRunId"
            );

            migrationBuilder.CreateIndex(
                name: "IX_EegFiles_Path",
                table: "EegFiles",
                column: "Path"
            );

            migrationBuilder.CreateIndex(
                name: "IX_EegFiles_SourceFileId",
                table: "EegFiles",
                column: "SourceFileId"
            );

            migrationBuilder.CreateIndex(
                name: "IX_ExperimentElectrodes_ExperimentId_SiteId",
                table: "ExperimentElectrodes",
                columns: new[] { "ExperimentId", "SiteId" },
                unique: true
            );

            migrationBuilder.CreateIndex(
                name: "IX_ExperimentEvents_ExperimentRunId_Sequence",
                table: "ExperimentEvents",
                columns: new[] { "ExperimentRunId", "Sequence" },
                unique: true
            );

            migrationBuilder.CreateIndex(
                name: "IX_ExperimentIncidents_ExperimentRunId",
                table: "ExperimentIncidents",
                column: "ExperimentRunId"
            );

            migrationBuilder.CreateIndex(
                name: "IX_ExperimentRuns_ApplicationSessionId",
                table: "ExperimentRuns",
                column: "ApplicationSessionId"
            );

            migrationBuilder.CreateIndex(
                name: "IX_ExperimentRuns_ExperimentId",
                table: "ExperimentRuns",
                column: "ExperimentId"
            );

            migrationBuilder.CreateIndex(
                name: "IX_Experiments_ExperimentCode",
                table: "Experiments",
                column: "ExperimentCode",
                unique: true
            );

            migrationBuilder.CreateIndex(
                name: "IX_Experiments_OperatorId",
                table: "Experiments",
                column: "OperatorId"
            );

            migrationBuilder.CreateIndex(
                name: "IX_Experiments_SubjectId",
                table: "Experiments",
                column: "SubjectId"
            );

            migrationBuilder.CreateIndex(
                name: "IX_ImpedanceSnapshots_ExperimentId_SiteId_Kind",
                table: "ImpedanceSnapshots",
                columns: new[] { "ExperimentId", "SiteId", "Kind" },
                unique: true
            );

            migrationBuilder.CreateIndex(
                name: "IX_Operators_NormalizedUsername",
                table: "Operators",
                column: "NormalizedUsername",
                unique: true
            );

            migrationBuilder.CreateIndex(
                name: "IX_StimulusChannels_StimulusTargetId_PhysicalChannelId",
                table: "StimulusChannels",
                columns: new[] { "StimulusTargetId", "PhysicalChannelId" },
                unique: true
            );

            migrationBuilder.CreateIndex(
                name: "IX_StimulusParadigms_ExperimentId",
                table: "StimulusParadigms",
                column: "ExperimentId",
                unique: true
            );

            migrationBuilder.CreateIndex(
                name: "IX_StimulusTargets_StimulusParadigmId",
                table: "StimulusTargets",
                column: "StimulusParadigmId"
            );

            migrationBuilder.CreateIndex(
                name: "IX_Subjects_NormalizedSubjectCode",
                table: "Subjects",
                column: "NormalizedSubjectCode",
                unique: true
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "EegFiles");

            migrationBuilder.DropTable(name: "ExperimentElectrodes");

            migrationBuilder.DropTable(name: "ExperimentEvents");

            migrationBuilder.DropTable(name: "ExperimentIncidents");

            migrationBuilder.DropTable(name: "ImpedanceSnapshots");

            migrationBuilder.DropTable(name: "StimulusChannels");

            migrationBuilder.DropTable(name: "ExperimentRuns");

            migrationBuilder.DropTable(name: "StimulusTargets");

            migrationBuilder.DropTable(name: "ApplicationSessions");

            migrationBuilder.DropTable(name: "StimulusParadigms");

            migrationBuilder.DropTable(name: "Experiments");

            migrationBuilder.DropTable(name: "Operators");

            migrationBuilder.DropTable(name: "Subjects");
        }
    }
}
