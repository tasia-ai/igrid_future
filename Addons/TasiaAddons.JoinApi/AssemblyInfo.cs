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
// Nerdbank.GitVersioning (pulled in by Directory.Build.props) generates
// AssemblyCompany/Configuration/Product/Title and the version attributes into
// obj\, so this file must not repeat them - a duplicate is a CS0579 build
// break. Only the description, which nothing else generates, is declared here.
using System.Reflection;

[assembly: AssemblyDescription("JSON /1-join account registration endpoint with API-key authentication")]
