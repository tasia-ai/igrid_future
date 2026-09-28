// Assembly metadata for TasiaAddons.JoinApi.
//
// NOTE: there is deliberately NO [assembly: Addin] attribute here.
// This is a Robust IServiceConnector, not an OpenSim application plugin.
// Robust does not load application addins at all - Server/ServerMain.cs builds
// the server from the [ServiceList] section via
// ServerUtils.LoadPlugin<IServiceConnector>(conn, args).  It is registered with:
//
//     [ServiceList]
//     JoinApi = "${Const|PrivatePort}/TasiaAddons.JoinApi.dll:JoinApiConnector"
//
// The class name after the colon must match a public type in this assembly.
//
// Directory.Build.props sets GenerateAssemblyInfo=false, so it is declared here.

using System.Reflection;

[assembly: AssemblyTitle("TasiaAddons.JoinApi")]
[assembly: AssemblyDescription("JSON /1-join account registration endpoint with API-key authentication")]
[assembly: AssemblyProduct("TasiaAddons")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]
