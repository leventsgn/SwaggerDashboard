using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SwaggerDashboard.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ApiDefinitions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    RouteName = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    TargetKey = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    BaseUrl = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    SwaggerUrl = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    SwaggerUrlNormalized = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    SwaggerVersion = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    ApiVersion = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    DashboardJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    DashboardSchemaVersion = table.Column<int>(type: "int", nullable: false),
                    RawSwaggerJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SwaggerHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    EndpointCount = table.Column<int>(type: "int", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    IsAutoProvisioned = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastSwaggerCheckAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastDashboardBuildAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CreatedBy = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    UpdatedBy = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    AllowedRoles = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApiDefinitions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ApiRequestLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ApiDefinitionId = table.Column<int>(type: "int", nullable: false),
                    ApiEndpointId = table.Column<int>(type: "int", nullable: true),
                    UserId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    RequestUrl = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    HttpMethod = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    RequestHeadersJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RequestBody = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RequestBodyTruncated = table.Column<bool>(type: "bit", nullable: false),
                    ResponseStatusCode = table.Column<int>(type: "int", nullable: false),
                    ResponseHeadersJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ResponseBody = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ResponseBodyTruncated = table.Column<bool>(type: "bit", nullable: false),
                    DurationMilliseconds = table.Column<long>(type: "bigint", nullable: false),
                    ClientIp = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    IsSuccess = table.Column<bool>(type: "bit", nullable: false),
                    ErrorMessage = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApiRequestLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "FavoriteEndpoints",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ApiDefinitionId = table.Column<int>(type: "int", nullable: false),
                    EndpointSlug = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                    UserId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FavoriteEndpoints", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SavedRequests",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ApiDefinitionId = table.Column<int>(type: "int", nullable: false),
                    EndpointSlug = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                    UserId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Note = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    PayloadJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SavedRequests", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Users",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UserName = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    PasswordHash = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    PasswordSalt = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    PasswordIterations = table.Column<int>(type: "int", nullable: false),
                    Role = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastLoginAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ApiEndpoints",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ApiDefinitionId = table.Column<int>(type: "int", nullable: false),
                    Slug = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                    Path = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    HttpMethod = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    OperationId = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    Summary = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Tag = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    RequestSchemaJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ResponseSchemaJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SecuritySchemaJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    IsDeprecated = table.Column<bool>(type: "bit", nullable: false),
                    RequiresAuthentication = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApiEndpoints", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ApiEndpoints_ApiDefinitions_ApiDefinitionId",
                        column: x => x.ApiDefinitionId,
                        principalTable: "ApiDefinitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ApiEnvironments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ApiDefinitionId = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    BaseUrl = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    IsDefault = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApiEnvironments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ApiEnvironments_ApiDefinitions_ApiDefinitionId",
                        column: x => x.ApiDefinitionId,
                        principalTable: "ApiDefinitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ApiUrlAliases",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ApiDefinitionId = table.Column<int>(type: "int", nullable: false),
                    UrlKey = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Url = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApiUrlAliases", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ApiUrlAliases_ApiDefinitions_ApiDefinitionId",
                        column: x => x.ApiDefinitionId,
                        principalTable: "ApiDefinitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ApiDefinitions_RouteName",
                table: "ApiDefinitions",
                column: "RouteName",
                unique: true,
                filter: "[RouteName] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ApiDefinitions_TargetKey",
                table: "ApiDefinitions",
                column: "TargetKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ApiEndpoints_ApiDefinitionId_Slug",
                table: "ApiEndpoints",
                columns: new[] { "ApiDefinitionId", "Slug" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ApiEndpoints_ApiDefinitionId_Tag",
                table: "ApiEndpoints",
                columns: new[] { "ApiDefinitionId", "Tag" });

            migrationBuilder.CreateIndex(
                name: "IX_ApiEnvironments_ApiDefinitionId_Name",
                table: "ApiEnvironments",
                columns: new[] { "ApiDefinitionId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ApiRequestLogs_ApiDefinitionId_CreatedAt",
                table: "ApiRequestLogs",
                columns: new[] { "ApiDefinitionId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ApiRequestLogs_CreatedAt",
                table: "ApiRequestLogs",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_ApiUrlAliases_ApiDefinitionId",
                table: "ApiUrlAliases",
                column: "ApiDefinitionId");

            migrationBuilder.CreateIndex(
                name: "IX_ApiUrlAliases_UrlKey",
                table: "ApiUrlAliases",
                column: "UrlKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FavoriteEndpoints_ApiDefinitionId_EndpointSlug_UserId",
                table: "FavoriteEndpoints",
                columns: new[] { "ApiDefinitionId", "EndpointSlug", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SavedRequests_ApiDefinitionId_EndpointSlug_UserId",
                table: "SavedRequests",
                columns: new[] { "ApiDefinitionId", "EndpointSlug", "UserId" });

            migrationBuilder.CreateIndex(
                name: "IX_Users_UserName",
                table: "Users",
                column: "UserName",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ApiEndpoints");

            migrationBuilder.DropTable(
                name: "ApiEnvironments");

            migrationBuilder.DropTable(
                name: "ApiRequestLogs");

            migrationBuilder.DropTable(
                name: "ApiUrlAliases");

            migrationBuilder.DropTable(
                name: "FavoriteEndpoints");

            migrationBuilder.DropTable(
                name: "SavedRequests");

            migrationBuilder.DropTable(
                name: "Users");

            migrationBuilder.DropTable(
                name: "ApiDefinitions");
        }
    }
}
