#!/usr/bin/env bash
# Repeatable review: complete compile, dependency audit, PHP lint and real regression suites.
set -euo pipefail
repo_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
cd "$repo_root"
: "${IGRID_PGSQL_TEST_CONNECTION:?Use a disposable LOCAL igrid_provider_test PostgreSQL database}"
quality_dir=${QUALITY_RESULTS_DIR:-"$repo_root/artifacts/quality"}
quality_rid=${QUALITY_RID:-linux-x64}
mkdir -p "$quality_dir"
git diff --check
# Compile everything without duplicating every native NuGet runtime into every test directory.
# Each test below then builds its COMPLETE runnable dependency set for one platform.
dotnet build Tranquillity.sln -c Release -m:4 -p:EnableNETAnalyzers=true -p:CopyLocalLockFileAssemblies=false > "$quality_dir/build.log" 2>&1 || { tail -80 "$quality_dir/build.log"; exit 1; }
dotnet list Tranquillity.sln package --vulnerable --include-transitive --no-restore --format json > "$quality_dir/vulnerabilities.json"
python3 tools/quality-report.py --audit "$quality_dir/vulnerabilities.json"
"${PHP_BINARY:-php}" -l Addons/TasiaAddons.WebScripts/tools/sign-script.php
projects=(
  OpenSim.Data.PGSQL.Tests
  TasiaAddons.WebScripts.Tests
  InWorldz.Phlox.Tests
  OpenSim.Framework.Tests
  OpenSim.Server.Base.Tests
  OpenSim.PluginLoadContext.Tests
  OpenSim.Framework.PluginMigration.Tests
  OpenSim.TrustedHypergrid.Tests
  OpenSim.Services.HypergridService.Tests
  OpenSim.Region.ScriptEngine.Tests
  OpenSim.Region.CoreModules.Tests
  WebRtcJanusService.Tests
  WebRtcVoiceRegionModule.Tests
)
for project in "${projects[@]}"; do
  printf 'Checking %s\n' "$project"
  # A new result name and remove only OUR previous result so a stale TRX cannot count as a pass.
  rm -f -- "$quality_dir/$project.trx"
  dotnet test "Tests/$project" -c Release -r "$quality_rid" -m:4 \
    --logger "trx;LogFileName=$project.trx" --results-directory "$quality_dir" \
    > "$quality_dir/$project.log" 2>&1 || { tail -90 "$quality_dir/$project.log"; exit 1; }
  python3 tools/quality-report.py --trx "$quality_dir/$project.trx"
done
python3 tools/quality-report.py --inventory "$quality_dir/implementation-inventory.json"
printf 'Quality review complete: %s\n' "$quality_dir"
