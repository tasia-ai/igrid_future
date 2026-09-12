#!/usr/bin/env bash
# =============================================================================
# Entrypoint for OpenSim Docker (multi-sim aware)
# =============================================================================
# Each sim creates its own diagnostic socket:
#   /dotnet-sockets/${SIM_NAME}/dotnet-monitor.sock
#
# The shared /dotnet-sockets/ volume is mounted by all sim containers
# AND by the dotnet-monitor container, which scans for .sock files.
# =============================================================================
set -e

# SIM_NAME must be unique per instance (set in docker-compose or --env)
SIM_NAME="${SIM_NAME:-opensim}"
OPENSIM_DIR="/src/bin"
SOCKET_DIR="/dotnet-sockets/${SIM_NAME}"
SOCKET_PATH="${SOCKET_DIR}/dotnet-monitor.sock"

echo "=========================================="
echo " OpenSim: ${SIM_NAME}"
echo " Runtime: .NET 8 (dotnet)"
echo " Socket:  ${SOCKET_PATH}"
echo "=========================================="

# Create socket directory for this sim
mkdir -p "$SOCKET_DIR"
mkdir -p "$OPENSIM_DIR"

# Generate WebStats.ini
if [ ! -f "$OPENSIM_DIR/WebStats.ini" ]; then
    cat > "$OPENSIM_DIR/WebStats.ini" <<EOF
[WebStats]
enabled = true
AuthEnabled = true
AuthUsername = admin
AuthPassword = ${SSTATS_PASSWORD:-sstats2026}
StatsSecret = ${SSTATS_SECRET:-}
EOF
    echo "[${SIM_NAME}] Generated WebStats.ini"
fi

# Report to stdout what monitor should look for
echo "[${SIM_NAME}] Waiting for dotnet-monitor to connect on ${SOCKET_PATH}"

# =============================================================================
# Start OpenSim
# =============================================================================
cd "$OPENSIM_DIR"

exec dotnet OpenSim.dll \
    -inifile=OpenSim.ini \
    "$@"
