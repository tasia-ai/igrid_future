#!/usr/bin/env bash
# =============================================================================
# generate-sims.sh — Generate docker-compose sim blocks for N sims
# =============================================================================
# Usage:
#   ./generate-sims.sh 31          # Generate 31 sim blocks
#   ./generate-sims.sh 50 > sims.yml
#
# Each sim gets:
#   - Unique container name: sim-1, sim-2, ... sim-N
#   - Unique socket path:    /dotnet-sockets/sim-N/dotnet-monitor.sock
#   - Unique port mapping:   9000, 9010, 9020, ... 9000+(N-1)*10
# =============================================================================
set -e

NUM_SIMS="${1:-3}"

cat <<'HEADER'
  # =========================================================================
  # Generated sim blocks — DO NOT EDIT MANUALLY
  # Regenerate with: ./generate-sims.sh <NUMBER_OF_SIMS>
  # =========================================================================

HEADER

for i in $(seq 1 "$NUM_SIMS"); do
  # Port mapping: 9000, 9010, 9020, ...
  HTTP_PORT=$((9000 + (i - 1) * 10))

  cat <<EOF
  sim-${i}:
    <<: *sim-defaults
    container_name: sim-${i}
    environment:
      <<: *sim-env
      SIM_NAME: sim-${i}
      DOTNET_DiagnosticPorts: "/dotnet-sockets/sim-${i}/dotnet-monitor.sock,connect,nosuspend"
    ports:
      - "${HTTP_PORT}:9000/tcp"
      - "${HTTP_PORT}:9000/udp"
    networks:
      - simnet

EOF
done

cat <<FOOTER
  # =========================================================================
  # End of generated sims (${NUM_SIMS} total)
  # Ports: 9000 to $((9000 + (NUM_SIMS - 1) * 10))
  # =========================================================================
FOOTER
