# =============================================================================
# Dockerfile for OpenSim with SStats Module
# =============================================================================
# OpenSim targets .NET 8.0 — runs via `dotnet OpenSim.dll` (NOT mono!)
# dotnet-monitor runs as a separate sidecar container (see docker-compose.yml)
#
# Build:
#   docker build -f sstats_fix/docker/Dockerfile -t opensim-sstats:latest .
# =============================================================================

# =============================================================================
# Stage 1: Build
# =============================================================================
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build

ENV DEBIAN_FRONTEND=noninteractive \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1

WORKDIR /src

# Copy full source
COPY . .

# Apply SStats fixes
COPY sstats_fix/fixed-sources/WebStatsModule.cs \
     OpenSim/Region/OptionalModules/UserStatistics/WebStatsModule.cs
COPY sstats_fix/fixed-sources/Updater_distributor.cs \
     OpenSim/Region/OptionalModules/UserStatistics/Updater_distributor.cs
COPY sstats_fix/fixed-sources/RegionStatsHandler.cs \
     OpenSim/Region/Framework/Scenes/RegionStatsHandler.cs

# Build
RUN dotnet build OpenSim/Region/Application/OpenSim.csproj \
    -c Release \
    -o /app/publish

# =============================================================================
# Stage 2: Runtime — .NET 8 only (monitor is a separate container)
# =============================================================================
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime

ENV DEBIAN_FRONTEND=noninteractive

RUN apt-get update && apt-get install -y --no-install-recommends \
    ca-certificates \
    curl \
    sqlite3 \
    libgdiplus \
    procps \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /src

# Copy build output
COPY --from=build /app/publish ./bin/

# Copy config + entrypoint
COPY sstats_fix/WebStats.ini.example ./bin/WebStats.ini
COPY sstats_fix/docker/entrypoint.sh /usr/local/bin/entrypoint.sh
RUN chmod +x /usr/local/bin/entrypoint.sh

# Create diagnostic socket directory (shared volume mount point)
RUN mkdir -p /dotnet

# OpenSim ports:
#   8002  HTTP (SStats dashboard)
#   9000  TCP+UDP (region)
#   9001  UDP (alternate)
EXPOSE 8002/tcp 9000/tcp 9000/udp 9001/udp

ENTRYPOINT ["/usr/local/bin/entrypoint.sh"]
CMD ["opensim"]
