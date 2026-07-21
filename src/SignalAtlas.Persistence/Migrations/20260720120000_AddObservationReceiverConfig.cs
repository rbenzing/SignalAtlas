using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SignalAtlas.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddObservationReceiverConfig : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ReceiverConfig",
                table: "observations",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ReceiverConfig",
                table: "observations");
        }
    }
}
