/*
 * Copyright (c) Contributors, http://opensimulator.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSimulator Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using System.Reflection;
using Nini.Config;

using Microsoft.Extensions.Logging;
using OpenSim.Framework;

namespace OpenSim.Services.Base;

public class ServiceBase
{
    private static readonly ILogger m_log = LoggerProvider.CreateLogger(MethodBase.GetCurrentMethod().DeclaringType);

    public T LoadPlugin<T>(string dllName) where T:class
    {
        return LoadPlugin<T>(dllName, new Object[0]);
    }

    public T LoadPlugin<T>(string dllName, Object[] args) where T:class
    {
        // The path:type separator : is unfortunate because it collides
        // with Windows paths like C:\...
        // When the path provided includes the drive, this fails.
        // Hence the root/noroot thing going on here.
        string pathRoot = Path.GetPathRoot(dllName);
        string noRoot = dllName.Substring(pathRoot.Length);
        string[] parts = noRoot.Split(new char[] {':'});


        dllName = pathRoot + parts[0];

        string className = string.Empty;

        if (parts.Length > 1)
            className = parts[1];

        return LoadPlugin<T>(dllName, className, args);
    }

    public T LoadPlugin<T>(string dllName, string className, Object[] args) where T:class
    {
        return StoragePluginLoader.Load<T>(dllName, className, args, m_log);
    }

    public ServiceBase(IConfigSource config)
    {
    }
}