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

# The application does not need to write to its own directory, so it runs unprivileged.
RUN useradd --uid 10001 --create-home --shell /usr/sbin/nologin dashboard
USER 10001

COPY --from=build --chown=10001:10001 /app .

ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_NOLOGO=1

EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=10s --start-period=30s --retries=3 \
    CMD ["dotnet", "SwaggerDashboard.Web.dll", "--healthcheck"]

ENTRYPOINT ["dotnet", "SwaggerDashboard.Web.dll"]
