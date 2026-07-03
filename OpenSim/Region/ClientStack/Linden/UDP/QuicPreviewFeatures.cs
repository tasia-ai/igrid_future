/*
 * Copyright (c) Contributors, http://opensimulator.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 */

using System.Runtime.Versioning;

// System.Net.Quic is still marked preview by this .NET 8 runtime/targeting pack.
// Directory.Build.props disables generated assembly info, so EnablePreviewFeatures
// alone does not emit the required assembly-level opt-in attribute.
[assembly: RequiresPreviewFeatures]
