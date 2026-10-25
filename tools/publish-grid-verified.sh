#!/usr/bin/env bash
# Build a complete grid deployment into a NEW directory; never overlay a live deployment.
set -euo pipefail
repo_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
cd "$repo_root"
output=${1:?Usage: tools/publish-grid-verified.sh NEW_OUTPUT_DIRECTORY [linux-x64|win-x64]}
runtime=${2:-linux-x64}
if [[ -e "$output" ]]; then
    echo 'Output already exists; choose a new directory. Live deployments must never be overlaid.' >&2
    exit 1
fi
if [[ -n $(git status --porcelain) ]]; then
    echo 'Commit the source first so every assembly has a reproducible commit identity.' >&2
    exit 1
fi
: "${IGRID_PGSQL_TEST_CONNECTION:?Start the disposable local PostgreSQL test database and set IGRID_PGSQL_TEST_CONNECTION first}"
commit=$(git rev-parse HEAD)
dotnet test Tests/OpenSim.Data.PGSQL.Tests -c Release -m:4
# A clean staging directory avoids stale DLLs from a previous framework or dependency set.
dotnet publish Source/OpenSim.Server.GridServer -c Release -r "$runtime" --self-contained false -m:4 -o "$output"
python3 - "$output" "$commit" "$runtime" <<'PY'
import hashlib, json, pathlib, sys
root = pathlib.Path(sys.argv[1])
deps = json.loads((root / 'OpenSim.Server.GridServer.deps.json').read_text())
for name in ('OpenSim.Data', 'OpenSim.Data.PGSQL', 'Npgsql'):
    assert (root / (name + '.dll')).is_file(), name
    assert any(key.startswith(name + '/') for key in deps['libraries']), name
manifest = {'commit':sys.argv[2], 'runtime':sys.argv[3], 'framework':'net10.0',
            'sha256':{str(p.relative_to(root)):hashlib.sha256(p.read_bytes()).hexdigest()
                      for p in sorted(root.rglob('*')) if p.is_file()}}
(root / 'deployment-manifest.json').write_text(json.dumps(manifest, indent=2) + '\n')
print('Complete deployment verified:', sys.argv[2], 'files:', len(manifest['sha256']))
PY
