# =============================================================================
# PolicyFlow underwriting worker
#
# Built from the repository root:
#   docker build -f src/PolicyFlow.Worker/Dockerfile .
# =============================================================================

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY NuGet.config Directory.Build.props ./
COPY src/PolicyFlow.Domain/PolicyFlow.Domain.csproj                     src/PolicyFlow.Domain/
COPY src/PolicyFlow.Application/PolicyFlow.Application.csproj           src/PolicyFlow.Application/
COPY src/PolicyFlow.Infrastructure/PolicyFlow.Infrastructure.csproj     src/PolicyFlow.Infrastructure/
COPY src/PolicyFlow.Infrastructure.NoSql/PolicyFlow.Infrastructure.NoSql.csproj src/PolicyFlow.Infrastructure.NoSql/
COPY src/PolicyFlow.Worker/PolicyFlow.Worker.csproj                     src/PolicyFlow.Worker/

RUN dotnet restore src/PolicyFlow.Worker/PolicyFlow.Worker.csproj

COPY src/PolicyFlow.Domain/               src/PolicyFlow.Domain/
COPY src/PolicyFlow.Application/          src/PolicyFlow.Application/
COPY src/PolicyFlow.Infrastructure/       src/PolicyFlow.Infrastructure/
COPY src/PolicyFlow.Infrastructure.NoSql/ src/PolicyFlow.Infrastructure.NoSql/
COPY src/PolicyFlow.Worker/               src/PolicyFlow.Worker/

RUN dotnet publish src/PolicyFlow.Worker/PolicyFlow.Worker.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish

# -----------------------------------------------------------------------------
# Runtime. The `runtime` image, not `aspnet`: the worker has no HTTP surface, so
# shipping ASP.NET Core would be dead weight and extra attack surface.
# -----------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runtime
WORKDIR /app

USER app

ENV DOTNET_EnableDiagnostics=0

COPY --from=build /app/publish .

# No HEALTHCHECK. The worker exposes no endpoint to probe, and a process-liveness
# check would be meaningless — the container is up whether or not the Service Bus
# processor is actually consuming. The real signal is queue depth and the
# structured logs, which is what a real deployment would alert on.

ENTRYPOINT ["dotnet", "PolicyFlow.Worker.dll"]
