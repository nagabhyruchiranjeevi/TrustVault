using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TrustVault.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddAuditHashChainColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CorrelationId",
                table: "AuditEvents",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CurrentHash",
                table: "AuditEvents",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "PreviousHash",
                table: "AuditEvents",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<long>(
                name: "SequenceNumber",
                table: "AuditEvents",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_SequenceNumber",
                table: "AuditEvents",
                column: "SequenceNumber",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AuditEvents_SequenceNumber",
                table: "AuditEvents");

            migrationBuilder.DropColumn(
                name: "CorrelationId",
                table: "AuditEvents");

            migrationBuilder.DropColumn(
                name: "CurrentHash",
                table: "AuditEvents");

            migrationBuilder.DropColumn(
                name: "PreviousHash",
                table: "AuditEvents");

            migrationBuilder.DropColumn(
                name: "SequenceNumber",
                table: "AuditEvents");
        }
    }
}
