using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DigiChat.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Generations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Number = table.Column<int>(type: "int", nullable: false),
                    StartedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    EndedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    UndoneUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Generations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Lineages",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Slug = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    OrderIndex = table.Column<int>(type: "int", nullable: false),
                    Enabled = table.Column<bool>(type: "bit", nullable: false),
                    SourceMedia = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    Canonicality = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    AssetReadiness = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Lineages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ProcessedChatEvents",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    MessageId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ReceivedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProcessedChatEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "StreamSessions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Number = table.Column<int>(type: "int", nullable: false),
                    StartedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    EndedUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StreamSessions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Transitions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Type = table.Column<int>(type: "int", nullable: false),
                    OccurredUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    FromStage = table.Column<int>(type: "int", nullable: false),
                    ToStage = table.Column<int>(type: "int", nullable: false),
                    FromGenerationId = table.Column<int>(type: "int", nullable: true),
                    ToGenerationId = table.Column<int>(type: "int", nullable: true),
                    UndoneUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Transitions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Viewers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TwitchUserId = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Login = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    FirstSeenUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastSeenUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Viewers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DigimonForms",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    LineageId = table.Column<int>(type: "int", nullable: false),
                    Stage = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    AssetKey = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DigimonForms", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DigimonForms_Lineages_LineageId",
                        column: x => x.LineageId,
                        principalTable: "Lineages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AppStates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false),
                    CurrentGenerationId = table.Column<int>(type: "int", nullable: false),
                    CurrentStreamSessionId = table.Column<int>(type: "int", nullable: true),
                    CurrentStage = table.Column<int>(type: "int", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppStates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AppStates_Generations_CurrentGenerationId",
                        column: x => x.CurrentGenerationId,
                        principalTable: "Generations",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_AppStates_StreamSessions_CurrentStreamSessionId",
                        column: x => x.CurrentStreamSessionId,
                        principalTable: "StreamSessions",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "Assignments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ViewerId = table.Column<int>(type: "int", nullable: false),
                    GenerationId = table.Column<int>(type: "int", nullable: false),
                    LineageId = table.Column<int>(type: "int", nullable: true),
                    AssignedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Assignments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Assignments_Generations_GenerationId",
                        column: x => x.GenerationId,
                        principalTable: "Generations",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Assignments_Lineages_LineageId",
                        column: x => x.LineageId,
                        principalTable: "Lineages",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Assignments_Viewers_ViewerId",
                        column: x => x.ViewerId,
                        principalTable: "Viewers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Participants",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    StreamSessionId = table.Column<int>(type: "int", nullable: false),
                    ViewerId = table.Column<int>(type: "int", nullable: false),
                    JoinedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Participants", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Participants_StreamSessions_StreamSessionId",
                        column: x => x.StreamSessionId,
                        principalTable: "StreamSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Participants_Viewers_ViewerId",
                        column: x => x.ViewerId,
                        principalTable: "Viewers",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_AppStates_CurrentGenerationId",
                table: "AppStates",
                column: "CurrentGenerationId");

            migrationBuilder.CreateIndex(
                name: "IX_AppStates_CurrentStreamSessionId",
                table: "AppStates",
                column: "CurrentStreamSessionId");

            migrationBuilder.CreateIndex(
                name: "IX_Assignments_GenerationId_LineageId",
                table: "Assignments",
                columns: new[] { "GenerationId", "LineageId" },
                unique: true,
                filter: "[LineageId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Assignments_GenerationId_ViewerId",
                table: "Assignments",
                columns: new[] { "GenerationId", "ViewerId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Assignments_LineageId",
                table: "Assignments",
                column: "LineageId");

            migrationBuilder.CreateIndex(
                name: "IX_Assignments_ViewerId",
                table: "Assignments",
                column: "ViewerId");

            migrationBuilder.CreateIndex(
                name: "IX_DigimonForms_LineageId_Stage",
                table: "DigimonForms",
                columns: new[] { "LineageId", "Stage" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Generations_Number",
                table: "Generations",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Lineages_Slug",
                table: "Lineages",
                column: "Slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Participants_StreamSessionId_ViewerId",
                table: "Participants",
                columns: new[] { "StreamSessionId", "ViewerId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Participants_ViewerId",
                table: "Participants",
                column: "ViewerId");

            migrationBuilder.CreateIndex(
                name: "IX_ProcessedChatEvents_MessageId",
                table: "ProcessedChatEvents",
                column: "MessageId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProcessedChatEvents_ReceivedUtc",
                table: "ProcessedChatEvents",
                column: "ReceivedUtc");

            migrationBuilder.CreateIndex(
                name: "IX_StreamSessions_Number",
                table: "StreamSessions",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Transitions_OccurredUtc",
                table: "Transitions",
                column: "OccurredUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Viewers_TwitchUserId",
                table: "Viewers",
                column: "TwitchUserId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AppStates");

            migrationBuilder.DropTable(
                name: "Assignments");

            migrationBuilder.DropTable(
                name: "DigimonForms");

            migrationBuilder.DropTable(
                name: "Participants");

            migrationBuilder.DropTable(
                name: "ProcessedChatEvents");

            migrationBuilder.DropTable(
                name: "Transitions");

            migrationBuilder.DropTable(
                name: "Generations");

            migrationBuilder.DropTable(
                name: "Lineages");

            migrationBuilder.DropTable(
                name: "StreamSessions");

            migrationBuilder.DropTable(
                name: "Viewers");
        }
    }
}
