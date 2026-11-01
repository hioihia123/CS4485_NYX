using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace NyxInsightsWeb.Migrations
{
    /// <inheritdoc />
    public partial class AddHealthPredictions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "HealthPredictions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Bpm = table.Column<double>(type: "double precision", nullable: false),
                    MindfulnessSession = table.Column<double>(type: "double precision", nullable: false),
                    ExertionPointsPercentage = table.Column<double>(type: "double precision", nullable: false),
                    VeryActiveMinutes = table.Column<double>(type: "double precision", nullable: false),
                    ModeratelyActiveMinutes = table.Column<double>(type: "double precision", nullable: false),
                    LightlyActiveMinutes = table.Column<double>(type: "double precision", nullable: false),
                    SedentaryMinutes = table.Column<double>(type: "double precision", nullable: false),
                    MinutesInDefaultZone1 = table.Column<double>(type: "double precision", nullable: false),
                    MinutesInDefaultZone2 = table.Column<double>(type: "double precision", nullable: false),
                    MinutesInDefaultZone3 = table.Column<double>(type: "double precision", nullable: false),
                    Steps = table.Column<double>(type: "double precision", nullable: false),
                    Calories = table.Column<double>(type: "double precision", nullable: false),
                    StressScore = table.Column<double>(type: "double precision", nullable: false),
                    SleepEfficiencyNextNight = table.Column<double>(type: "double precision", nullable: false),
                    AiExplanation = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HealthPredictions", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "HealthPredictions");
        }
    }
}
