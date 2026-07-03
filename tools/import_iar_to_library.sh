#!/usr/bin/env bash

set -euo pipefail

COMPOSE_DIR="/home/marty/opensim"
SERVICE="grid-main"
TMUX_SESSION="opensim_session"
IAR_PATH=""
FIRST_NAME=""
LAST_NAME=""
TARGET_PATH="/"
IAR_IN_CONTAINER="/home/grid/opensim/bin/import.iar"
CMD_TEMPLATE='load iar --merge "{FIRST}" "{LAST}" "{TARGET_PATH}" "{IAR_PATH}"'
EXECUTE=false
BUMP_LIBRARY_VERSION=true
LIBRARIES_XML="/home/marty/opensim/Library/inventory/Libraries.xml"

usage() {
  cat <<'EOF'
Usage:
  import_iar_to_library.sh --iar /abs/path/file.iar --first Shared --last Inventory [options]

Required:
  --iar PATH              Absolute path to .iar file on host
  --first NAME            Avatar/account first name
  --last NAME             Avatar/account last name

Optional:
  --target-path PATH      Inventory target path in account (default: /)
  --compose-dir PATH      Docker compose project dir (default: /home/marty/opensim)
  --service NAME          Compose service with OpenSim console (default: grid-main)
  --tmux-session NAME     Tmux session name for console (default: opensim_session)
  --container-iar PATH    Target path inside container (default: /home/grid/opensim/bin/import.iar)
  --cmd-template STR      Import command template (default uses load iar --merge)
                          Placeholders: {FIRST} {LAST} {TARGET_PATH} {IAR_PATH}
  --no-bump-library       Do not bump RootVersion in Libraries.xml
  --libraries-xml PATH    Libraries.xml path to bump (default: /home/marty/opensim/Library/inventory/Libraries.xml)
  --execute               Actually run docker cp + tmux send-keys + version bump

Notes:
  - Default mode is dry-run (prints planned actions only).
  - After import, RootVersion in Libraries.xml is incremented by 1 (unless disabled).
EOF
}

while [ $# -gt 0 ]; do
  case "$1" in
    --iar) IAR_PATH="$2"; shift 2 ;;
    --first) FIRST_NAME="$2"; shift 2 ;;
    --last) LAST_NAME="$2"; shift 2 ;;
    --target-path) TARGET_PATH="$2"; shift 2 ;;
    --compose-dir) COMPOSE_DIR="$2"; shift 2 ;;
    --service) SERVICE="$2"; shift 2 ;;
    --tmux-session) TMUX_SESSION="$2"; shift 2 ;;
    --container-iar) IAR_IN_CONTAINER="$2"; shift 2 ;;
    --cmd-template) CMD_TEMPLATE="$2"; shift 2 ;;
    --no-bump-library) BUMP_LIBRARY_VERSION=false; shift ;;
    --libraries-xml) LIBRARIES_XML="$2"; shift 2 ;;
    --execute) EXECUTE=true; shift ;;
    -h|--help) usage; exit 0 ;;
    *) echo "Unknown option: $1"; usage; exit 1 ;;
  esac
done

if [ -z "$IAR_PATH" ] || [ -z "$FIRST_NAME" ] || [ -z "$LAST_NAME" ]; then
  echo "ERROR: --iar, --first, --last are required"
  usage
  exit 1
fi

if [ ! -f "$IAR_PATH" ]; then
  echo "ERROR: IAR file not found: $IAR_PATH"
  exit 1
fi

if [ ! -d "$COMPOSE_DIR" ]; then
  echo "ERROR: compose dir not found: $COMPOSE_DIR"
  exit 1
fi

CMD="$CMD_TEMPLATE"
CMD="${CMD//\{FIRST\}/$FIRST_NAME}"
CMD="${CMD//\{LAST\}/$LAST_NAME}"
CMD="${CMD//\{TARGET_PATH\}/$TARGET_PATH}"
CMD="${CMD//\{IAR_PATH\}/$IAR_IN_CONTAINER}"

echo "Plan:"
echo "  compose dir:   $COMPOSE_DIR"
echo "  service:       $SERVICE"
echo "  tmux session:  $TMUX_SESSION"
echo "  iar host path: $IAR_PATH"
echo "  iar in cont:   $IAR_IN_CONTAINER"
echo "  import cmd:    $CMD"
echo "  bump library:  $BUMP_LIBRARY_VERSION"

if [ "$EXECUTE" != "true" ]; then
  echo ""
  echo "Dry-run only. Re-run with --execute to apply."
  exit 0
fi

CID="$(docker compose -f "$COMPOSE_DIR/docker-compose.yml" ps -q "$SERVICE")"
if [ -z "$CID" ]; then
  echo "ERROR: service not running or not found: $SERVICE"
  exit 1
fi

docker exec "$CID" tmux has-session -t "$TMUX_SESSION" 2>/dev/null || {
  echo "ERROR: tmux session '$TMUX_SESSION' not found in $SERVICE"
  exit 1
}

echo "Copying IAR into container..."
docker cp "$IAR_PATH" "$CID:$IAR_IN_CONTAINER"

echo "Sending import command to OpenSim console..."
docker exec "$CID" tmux send-keys -t "$TMUX_SESSION" "$CMD" Enter

if [ "$BUMP_LIBRARY_VERSION" = "true" ]; then
  if [ -f "$LIBRARIES_XML" ]; then
    python3 - "$LIBRARIES_XML" <<'PY'
import re
import sys
from pathlib import Path

p = Path(sys.argv[1])
s = p.read_text(encoding='utf-8')
m = re.search(r'RootVersion="(\d+)"', s)
if not m:
    print(f"WARN: RootVersion not found in {p}")
    raise SystemExit(0)

old = int(m.group(1))
new = old + 1
s2 = s[:m.start(1)] + str(new) + s[m.end(1):]
p.write_text(s2, encoding='utf-8')
print(f"Libraries.xml RootVersion bumped: {old} -> {new}")
PY
  else
    echo "WARN: Libraries.xml not found, skip bump: $LIBRARIES_XML"
  fi
fi

echo "Done. Import command sent."
