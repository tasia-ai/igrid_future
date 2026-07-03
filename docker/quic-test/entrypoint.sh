#!/usr/bin/env bash
set -euo pipefail

cd /src

echo "[quic-test] .NET SDK: $(dotnet --version)"

if ldconfig -p 2>/dev/null | grep -q msquic; then
  echo "[quic-test] libmsquic: found"
else
  echo "[quic-test] WARNING: libmsquic not found by ldconfig"
fi

case "${1:-bash}" in
  build)
    shift || true
    echo "[quic-test] Building OpenSim.sln ($*)"
    exec dotnet build OpenSim.sln -c "${CONFIGURATION:-Release}" "$@"
    ;;
  build-opensim)
    shift || true
    echo "[quic-test] Building OpenSim simulator"
    exec dotnet build OpenSim/Region/Application/OpenSim.csproj -c "${CONFIGURATION:-Release}" "$@"
    ;;
  build-robust)
    shift || true
    echo "[quic-test] Building ROBUST"
    exec dotnet build OpenSim/Server/Robust.csproj -c "${CONFIGURATION:-Release}" "$@"
    ;;
  run-opensim)
    shift || true
    if [ ! -f build/OpenSim.dll ]; then
      echo "[quic-test] build/OpenSim.dll missing; building simulator first"
      dotnet build OpenSim/Region/Application/OpenSim.csproj -c "${CONFIGURATION:-Release}"
    fi
    echo "[quic-test] Starting OpenSim"
    exec dotnet build/OpenSim.dll "$@"
    ;;
  run-robust)
    shift || true
    if [ ! -f build/Robust.dll ]; then
      echo "[quic-test] build/Robust.dll missing; building ROBUST first"
      dotnet build OpenSim/Server/Robust.csproj -c "${CONFIGURATION:-Release}"
    fi
    echo "[quic-test] Starting ROBUST"
    exec dotnet build/Robust.dll "$@"
    ;;
  check-quic)
    shift || true
    dotnet --info
    ldconfig -p | grep msquic || true
    exec dotnet build OpenSim/Region/ClientStack/Linden/UDP/OpenSim.Region.ClientStack.LindenUDP.csproj -c "${CONFIGURATION:-Release}" "$@"
    ;;
  run-all-in-one)
    shift || true
    exec quic-test-run-all-in-one "$@"
    ;;
  bash|sh)
    exec "$@"
    ;;
  *)
    exec "$@"
    ;;
esac
