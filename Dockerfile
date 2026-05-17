# ── Stage 1: Build ────────────────────────────────────────────────────────────
# Use the full SDK image to restore, build, and publish.
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy project files first so NuGet restore is cached independently of source.
COPY DevMetrics.sln .
COPY DevMetrics.Core/DevMetrics.Core.csproj                   DevMetrics.Core/
COPY DevMetrics.Application/DevMetrics.Application.csproj     DevMetrics.Application/
COPY DevMetrics.Infrastructure/DevMetrics.Infrastructure.csproj DevMetrics.Infrastructure/
COPY DevMetrics.Api/DevMetrics.Api.csproj                     DevMetrics.Api/

# Restore for linux-x64 so LibGit2Sharp native binaries are included.
RUN dotnet restore DevMetrics.Api/DevMetrics.Api.csproj \
    --runtime linux-x64

# Copy the rest of the source and publish a Release build.
COPY . .

RUN dotnet publish DevMetrics.Api/DevMetrics.Api.csproj \
    --configuration Release \
    --runtime linux-x64 \
    --self-contained false \
    --no-restore \
    --output /app/publish


# ── Stage 2: Runtime ──────────────────────────────────────────────────────────
# Use the smaller ASP.NET runtime image (Debian-based — required for
# LibGit2Sharp's glibc-linked native library).
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

# Create the directories that will be mounted as volumes and grant ownership
# to the non-root "app" user that ships in the base image.
RUN mkdir -p /app/Data /app/Logs \
    && chown -R app:app /app

# Drop root privileges.
USER app

# Copy the published output from the build stage.
COPY --from=build --chown=app:app /app/publish .

# Declare mount points.
# docker-compose mounts named volumes here so SQLite and logs survive restarts.
VOLUME ["/app/Data", "/app/Logs"]

# Kestrel: bind to all interfaces on port 80 (mapped to host 5000 in compose).
ENV ASPNETCORE_URLS=http://+:80
ENV ASPNETCORE_ENVIRONMENT=Production

EXPOSE 80

ENTRYPOINT ["dotnet", "DevMetrics.Api.dll"]
