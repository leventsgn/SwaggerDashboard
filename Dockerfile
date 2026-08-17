# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Restore first so a source-only change does not re-download packages.
COPY Directory.Build.props SwaggerDashboard.sln ./
COPY src/SwaggerDashboard.Domain/SwaggerDashboard.Domain.csproj src/SwaggerDashboard.Domain/
COPY src/SwaggerDashboard.Application/SwaggerDashboard.Application.csproj src/SwaggerDashboard.Application/
COPY src/SwaggerDashboard.Infrastructure/SwaggerDashboard.Infrastructure.csproj src/SwaggerDashboard.Infrastructure/
COPY src/SwaggerDashboard.Web/SwaggerDashboard.Web.csproj src/SwaggerDashboard.Web/
COPY tests/SwaggerDashboard.Tests/SwaggerDashboard.Tests.csproj tests/SwaggerDashboard.Tests/
RUN dotnet restore SwaggerDashboard.sln

COPY . .
RUN dotnet publish src/SwaggerDashboard.Web/SwaggerDashboard.Web.csproj \
    -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

# The application runs unprivileged. The container still starts as root so the entrypoint
# can take ownership of a mounted volume, which platforms hand over owned by root; it drops
# to this account before the application starts.
RUN useradd --uid 10001 --create-home --shell /usr/sbin/nologin dashboard

COPY --from=build --chown=10001:10001 /app .
COPY --chmod=755 docker-entrypoint.sh /usr/local/bin/docker-entrypoint.sh

ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_NOLOGO=1 \
    DATA_DIR=/data

EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=10s --start-period=30s --retries=3 \
    CMD ["dotnet", "/app/SwaggerDashboard.Web.dll", "--healthcheck"]

ENTRYPOINT ["docker-entrypoint.sh", "dotnet", "SwaggerDashboard.Web.dll"]
