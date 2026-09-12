# =============================================================================
# PolicyFlow API
#
# Multi-stage build. The SDK image (~800MB) compiles; only the published output
# is copied into the runtime image (~220MB). Shipping the SDK would mean shipping
# a compiler, NuGet credentials and the full source tree into production.
#
# Built from the repository root so project references resolve:
#   docker build -f src/PolicyFlow.Api/Dockerfile .
# =============================================================================

# -----------------------------------------------------------------------------
# Stage 1: restore and publish
# -----------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy only the project files first. Docker caches this layer, so `restore` is
# re-run only when a dependency actually changes — not on every source edit.
COPY NuGet.config Directory.Build.props ./
COPY src/PolicyFlow.Domain/PolicyFlow.Domain.csproj                     src/PolicyFlow.Domain/
COPY src/PolicyFlow.Application/PolicyFlow.Application.csproj           src/PolicyFlow.Application/
COPY src/PolicyFlow.Infrastructure/PolicyFlow.Infrastructure.csproj     src/PolicyFlow.Infrastructure/
COPY src/PolicyFlow.Infrastructure.NoSql/PolicyFlow.Infrastructure.NoSql.csproj src/PolicyFlow.Infrastructure.NoSql/
COPY src/PolicyFlow.Api/PolicyFlow.Api.csproj                           src/PolicyFlow.Api/

RUN dotnet restore src/PolicyFlow.Api/PolicyFlow.Api.csproj

# Now the sources. A change here invalidates only from this layer down.
COPY src/PolicyFlow.Domain/               src/PolicyFlow.Domain/
COPY src/PolicyFlow.Application/          src/PolicyFlow.Application/
COPY src/PolicyFlow.Infrastructure/       src/PolicyFlow.Infrastructure/
COPY src/PolicyFlow.Infrastructure.NoSql/ src/PolicyFlow.Infrastructure.NoSql/
COPY src/PolicyFlow.Api/                  src/PolicyFlow.Api/

RUN dotnet publish src/PolicyFlow.Api/PolicyFlow.Api.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish

# -----------------------------------------------------------------------------
# Stage 2: runtime
# -----------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# curl is needed for the container health check: the aspnet runtime image ships no
# HTTP client, and Compose gates dependent containers on this probe. Installed as
# root, then the image drops to an unprivileged user below.
RUN apt-get update \
    && apt-get install --no-install-recommends -y curl \
    # Remove the package lists: they are ~40MB and would ship in the final layer.
    && rm -rf /var/lib/apt/lists/*

# Non-root. The base image ships an `app` user (uid 1654) for exactly this. A
# container process running as root that is compromised is trivially a host
# escalation attempt; an unprivileged one is not.
USER app

# Kestrel listens on 8080. Ports below 1024 require elevated privileges, which is
# the whole reason the .NET images moved off port 80.
ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_EnableDiagnostics=0

EXPOSE 8080

COPY --from=build /app/publish .

# Readiness, not liveness. Compose uses this probe to decide when the API is able
# to serve traffic, which requires SQL Server and the queue to be reachable — so
# it must be /health/ready. start-period covers migrations plus catalog seeding on
# a cold start.
HEALTHCHECK --interval=10s --timeout=5s --start-period=90s --retries=10 \
    CMD curl --fail --silent http://127.0.0.1:8080/health/ready || exit 1

ENTRYPOINT ["dotnet", "PolicyFlow.Api.dll"]
