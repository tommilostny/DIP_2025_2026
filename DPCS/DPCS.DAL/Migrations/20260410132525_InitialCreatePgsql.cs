using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DPCS.DAL.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreatePgsql : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CrackedPasswords",
                columns: table => new
                {
                    Hash = table.Column<string>(type: "text", nullable: false),
                    Plaintext = table.Column<string>(type: "text", nullable: false),
                    HashType = table.Column<int>(type: "integer", nullable: false),
                    CrackedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    TimeTaken = table.Column<TimeSpan>(type: "interval", nullable: false),
                    AttackMode = table.Column<int>(type: "integer", nullable: false),
                    JobId = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CrackedPasswords", x => x.Hash);
                });

            migrationBuilder.CreateTable(
                name: "JobRecords",
                columns: table => new
                {
                    JobId = table.Column<string>(type: "text", nullable: false),
                    AttackMode = table.Column<int>(type: "integer", nullable: false),
                    HashType = table.Column<int>(type: "integer", nullable: false),
                    StartTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EndTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Status = table.Column<string>(type: "text", nullable: false),
                    ProgressPercentage = table.Column<float>(type: "real", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobRecords", x => x.JobId);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CrackedPasswords");

            migrationBuilder.DropTable(
                name: "JobRecords");
        }
    }
}
