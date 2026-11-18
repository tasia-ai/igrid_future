# Building and running Tranquillity

## Requirements

- Install the .NET 10 SDK selected by the repository's `global.json` to build.
- Framework-dependent RegionServer publishes require the .NET 10 runtime.
- Framework-dependent GridServer and MoneyServer publishes require the ASP.NET
  Core 10 runtime.
- Restore uses the NuGet.org source declared in the repository's `nuget.config`;
  this checkout does not require the former private OpenSim-NGC package feed.

## Build

From the repository root:

```sh
dotnet restore Tranquillity.sln
dotnet build Tranquillity.sln --configuration Release --no-restore
```

To publish a server for a specific runtime identifier:

```sh
dotnet publish Source/OpenSim.Server.RegionServer/OpenSim.Server.RegionServer.csproj \
  --configuration Release --runtime win-x64
```

Use `linux-x64` or another supported RID as appropriate. Review the server
project's publish settings before choosing framework-dependent or self-contained
deployment.

## Database-free tests

The integration branch GitHub Actions workflow builds on Ubuntu and Windows
x64, then runs the framework and ServerSideBaking unit tests. ServerSideBaking
uses SkiaSharp; its shared library now references the Linux native-asset
package directly so test output includes `libSkiaSharp.so`.

The EstateStore migration-resource tests inspect migration histories without
connecting to a database. They are not a substitute for the deferred runtime
migration checks in
[runtime-migration-validation.md](integration/runtime-migration-validation.md).
Do not run provider smoke tests that require a database until separately
authorized.

## Running published servers

Configure the relevant `.ini` files for the intended environment before
starting a server. From the publish directory, the server assemblies are
started with `dotnet`:

```sh
dotnet OpenSim.Server.RegionServer.dll
dotnet OpenSim.Server.GridServer.dll
dotnet OpenSim.Server.MoneyServer.dll
```

Start only the processes required by the deployment. Do not use these commands
as part of a build or unit-test workflow; they start server applications.

The integration audit, validation results, and deferred runtime procedures are
in [Docs/integration](integration/v1.1.2-report.md).
