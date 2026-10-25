#!/usr/bin/env bash
# Local disposable database only. Does not load production configuration.
set -euo pipefail
repo_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
program_home=$(realpath "${1:?Usage: tools/run-pgsql-smoke-grid.sh PUBLISHED_GRID_DIRECTORY}")
test -f "$program_home/OpenSim.Server.GridServer.dll"
python3 - "$repo_root" "$program_home" <<'PY'
import pathlib, re, sys
source = pathlib.Path(sys.argv[1]) / 'Source/OpenSim.Server.GridServer/AppData/GridServer.ini.example'
text = source.read_text()
keep = ['InventoryInConnector','GridServiceConnector','AuthenticationServiceConnector',
        'AvatarServiceConnector','LLLoginServiceInConnector','PresenceServiceConnector',
        'UserAccountServiceConnector','GridUserServiceConnector','AgentPreferencesServiceConnector',
        'FriendsServiceConnector']
block = text[text.index('[ServiceList]'):text.index('[Network]')]
lines = [line for line in block.splitlines()
         if any(re.match(r'\s*' + key + r'\s*=', line) for key in keep)]
assert len(lines) == len(keep), 'Service list changed; update the smoke configuration.'
text = re.sub(r'\[ServiceList\].*?(?=\[Network\])', '[ServiceList]\n' + '\n'.join(lines) + '\n\n', text, flags=re.S)
text = text.replace('PublicPort = "8002"', 'PublicPort = "58002"').replace('PrivatePort = "8003"', 'PrivatePort = "58003"')
text = re.sub(r'^    StorageProvider = .*$', '    StorageProvider = "OpenSim.Data.PGSQL.dll"', text, flags=re.M)
text = re.sub(r'^    ConnectionString = .*$', '    ConnectionString = "Host=127.0.0.1;Port=55432;Database=igrid_provider_test;Username=postgres"', text, flags=re.M)
target = pathlib.Path(sys.argv[2]) / 'pgsql-smoke.ini'
if target.exists() and target.read_text() != text:
    raise SystemExit('Existing smoke configuration differs; preserve it and choose a new publish directory.')
target.write_text(text)
PY
cd "$program_home"
exec dotnet OpenSim.Server.GridServer.dll --inimaster "$program_home/pgsql-smoke.ini" --inidirectory "$program_home/empty-smoke-config" --console mock
