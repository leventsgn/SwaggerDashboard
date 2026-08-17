using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SwaggerDashboard.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CascadeDeleteApiChildRows : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Every API deleted before this migration left its rows behind, and the new
            // constraints cannot be created while those rows point at an API that is gone.
            // They are unreachable in the application either way: every screen reads them
            // through an API that no longer exists.
            migrationBuilder.Sql(
                "DELETE FROM [ApiRequestLogs] WHERE [ApiDefinitionId] NOT IN (SELECT [Id] FROM [ApiDefinitions]);");
            migrationBuilder.Sql(
                "DELETE FROM [FavoriteEndpoints] WHERE [ApiDefinitionId] NOT IN (SELECT [Id] FROM [ApiDefinitions]);");
            migrationBuilder.Sql(
                "DELETE FROM [SavedRequests] WHERE [ApiDefinitionId] NOT IN (SELECT [Id] FROM [ApiDefinitions]);");

            migrationBuilder.AddForeignKey(
                name: "FK_ApiRequestLogs_ApiDefinitions_ApiDefinitionId",
                table: "ApiRequestLogs",
                column: "ApiDefinitionId",
                principalTable: "ApiDefinitions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_FavoriteEndpoints_ApiDefinitions_ApiDefinitionId",
                table: "FavoriteEndpoints",
                column: "ApiDefinitionId",
                principalTable: "ApiDefinitions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_SavedRequests_ApiDefinitions_ApiDefinitionId",
                table: "SavedRequests",
                column: "ApiDefinitionId",
                principalTable: "ApiDefinitions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ApiRequestLogs_ApiDefinitions_ApiDefinitionId",
                table: "ApiRequestLogs");

            migrationBuilder.DropForeignKey(
                name: "FK_FavoriteEndpoints_ApiDefinitions_ApiDefinitionId",
                table: "FavoriteEndpoints");

            migrationBuilder.DropForeignKey(
                name: "FK_SavedRequests_ApiDefinitions_ApiDefinitionId",
                table: "SavedRequests");
        }
    }
}
