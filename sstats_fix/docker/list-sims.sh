#!/usr/bin/env bash
# =============================================================================
# list-sims.sh — Show all active sim sockets and their status
# =============================================================================
# Run from host to see what's available for dotnet-monitor
# =============================================================================
set -e

SOCKET_DIR="${1:-/var/lib/docker/volumes/sstats_fix_dotnet-sockets/_data}"

echo "=========================================="
echo " OpenSim Sockets — dotnet-monitor targets"
echo "=========================================="
echo ""

if [ ! -d "$SOCKET_DIR" ]; then
    echo "[ERROR] Socket directory not found: $SOCKET_DIR"
    echo ""
    echo "For docker-compose, try:"
    echo "  docker exec dotnet-monitor ls -la /dotnet-sockets/"
    echo ""
    exit 1
fi

FOUND=0
for sim_dir in "$SOCKET_DIR"/sim-*; do
    [ -d "$sim_dir" ] || continue
    SIM_NAME=$(basename "$sim_dir")
    SOCKET="$sim_dir/dotnet-monitor.sock"

    if [ -S "$SOCKET" ]; then
        echo "  [OK]   $SIM_NAME → $SOCKET"
        FOUND=$((FOUND + 1))
    else
        echo "  [--]   $SIM_NAME → (no socket — sim may be offline)"
    fi
done

echo ""
echo "Total: $FOUND active socket(s)"
echo ""

# Also show from inside the monitor container if available
if docker ps --format '{{.Names}}' 2>/dev/null | grep -q dotnet-monitor; then
    echo "--- From dotnet-monitor container ---"
    docker exec dotnet-monitor ls -la /dotnet-sockets/*/dotnet-monitor.sock 2>/dev/null || echo "  (no sockets found)"
    echo ""
fi
