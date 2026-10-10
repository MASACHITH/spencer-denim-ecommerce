# syntax=docker/dockerfile:1.7
#
# Spencer Denim backend images (W0-15, docs/15-deployment-runbook.md). Build from the repository root:
#
#   docker build -f deploy/docker/backend.Dockerfile --target api      -t spencer-denim/api:<tag> .
#   docker build -f deploy/docker/backend.Dockerfile --target migrator -t spencer-denim/migrator:<tag> .
#
# The build context is filtered by backend.Dockerfile.dockerignore (an allow-list), so secrets, reference/,
# client-progress/, worktrees, bin/obj and the frontend never enter it.
#
# Base images are pinned to exact tags; bump them deliberately (docs/15 §8).

ARG DOTNET_SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:10.0.401-alpine3.24
ARG DOTNET_ASPNET_IMAGE=mcr.microsoft.com/dotnet/aspnet:10.0.12-alpine3.24

# ---------------------------------------------------------------------------------------------------------------
# Restore: only the files restore needs, so the layer is reused until a package or project reference changes.
# ---------------------------------------------------------------------------------------------------------------
FROM ${DOTNET_SDK_IMAGE} AS restore
ENV DOTNET_NOLOGO=true \
    DOTNET_CLI_TELEMETRY_OPTOUT=true \
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE=true
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/backend/SpencerDenim.Domain/SpencerDenim.Domain.csproj                 src/backend/SpencerDenim.Domain/
COPY src/backend/SpencerDenim.Application/SpencerDenim.Application.csproj       src/backend/SpencerDenim.Application/
COPY src/backend/SpencerDenim.Infrastructure/SpencerDenim.Infrastructure.csproj src/backend/SpencerDenim.Infrastructure/
COPY src/backend/SpencerDenim.Api/SpencerDenim.Api.csproj                       src/backend/SpencerDenim.Api/
RUN --mount=type=cache,id=sd-nuget,target=/root/.nuget/packages \
    dotnet restore src/backend/SpencerDenim.Api/SpencerDenim.Api.csproj -r linux-musl-x64

# ---------------------------------------------------------------------------------------------------------------
# Build: the API publish output and the EF Core migrations bundle.
# ---------------------------------------------------------------------------------------------------------------
FROM restore AS build
COPY src/backend/ src/backend/
RUN --mount=type=cache,id=sd-nuget,target=/root/.nuget/packages \
    dotnet publish src/backend/SpencerDenim.Api/SpencerDenim.Api.csproj \
      --configuration Release \
      --runtime linux-musl-x64 \
      --self-contained false \
      --no-restore \
      -p:UseAppHost=false \
      -o /out/api \
 && rm -f /out/api/appsettings.Development.json

# The dotnet-ef version matches the EF Core packages in Directory.Packages.props.
ARG DOTNET_EF_VERSION=10.0.12
RUN --mount=type=cache,id=sd-nuget,target=/root/.nuget/packages \
    dotnet tool install dotnet-ef --version ${DOTNET_EF_VERSION} --tool-path /tools

# Framework-dependent: it runs on the same runtime layers as the API image, so on the server it costs only the
# bundle itself. It reads the connection string from ConnectionStrings__Database like the API does (the bundle
# builds the API's host to find the DbContext).
RUN --mount=type=cache,id=sd-nuget,target=/root/.nuget/packages \
    /tools/dotnet-ef migrations bundle \
      --project src/backend/SpencerDenim.Infrastructure/SpencerDenim.Infrastructure.csproj \
      --startup-project src/backend/SpencerDenim.Api/SpencerDenim.Api.csproj \
      --configuration Release \
      --runtime linux-musl-x64 \
      --output /out/migrator/efbundle \
      --verbose

# ---------------------------------------------------------------------------------------------------------------
# runtime: shared by api and migrator, so the two images share every layer below their own files.
# ---------------------------------------------------------------------------------------------------------------
FROM ${DOTNET_ASPNET_IMAGE} AS runtime
# The projects run with InvariantGlobalization=false (Directory.Build.props), so ICU must be present; tzdata for
# Asia/Colombo conversions.
RUN apk add --no-cache icu-libs icu-data-full tzdata
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_NOLOGO=true \
    DOTNET_CLI_TELEMETRY_OPTOUT=true \
    TZ=UTC
WORKDIR /app

# ---------------------------------------------------------------------------------------------------------------
# api: ASP.NET Core on Alpine, non-root, read-only root filesystem friendly.
# ---------------------------------------------------------------------------------------------------------------
FROM runtime AS api
ENV ASPNETCORE_HTTP_PORTS=8080
COPY --from=build --chown=root:root /out/api/ ./
# Mount points for the volumes the compose file attaches (docs/15 §2): the Data Protection key ring (#96), the
# public and private uploads and the upload spool (W1-04). Owned by app so a fresh named volume inherits it.
RUN mkdir -p /var/lib/spencer-denim/dataprotection-keys /var/lib/spencer-denim/media-public \
             /var/lib/spencer-denim/media-private /var/lib/spencer-denim/upload-spool \
 && chown -R app:app /var/lib/spencer-denim
USER app
EXPOSE 8080
HEALTHCHECK --interval=15s --timeout=5s --start-period=20s --retries=5 \
  CMD wget -q -O /dev/null http://127.0.0.1:8080/health || exit 1
ENTRYPOINT ["dotnet", "SpencerDenim.Api.dll"]

# ---------------------------------------------------------------------------------------------------------------
# migrator: one-shot. Applies every pending migration, then exits 0. The API waits for it
# (depends_on: service_completed_successfully). EF Core takes an application lock on the database while
# migrating, so two bundles started at once cannot interleave.
# ---------------------------------------------------------------------------------------------------------------
FROM runtime AS migrator
COPY --from=build --chown=root:root /out/migrator/ ./
USER app
ENTRYPOINT ["/app/efbundle"]
