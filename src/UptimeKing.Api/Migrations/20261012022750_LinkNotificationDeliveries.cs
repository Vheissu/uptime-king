using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace UptimeKing.Api.Migrations
{
    /// <inheritdoc />
    public partial class LinkNotificationDeliveries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddForeignKey(
                name: "FK_Deliveries_Incidents_IncidentId",
                table: "Deliveries",
                column: "IncidentId",
                principalTable: "Incidents",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Deliveries_Incidents_IncidentId",
                table: "Deliveries"
            );
        }
    }
}
