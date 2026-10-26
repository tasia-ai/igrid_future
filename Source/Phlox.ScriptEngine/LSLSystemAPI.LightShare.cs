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

using System;
using OpenSim.Region.ScriptEngine.Shared.Api;
using LSLSharedList = OpenSim.Region.ScriptEngine.Shared.LSL_Types.list;
using LSLSharedFloat = OpenSim.Region.ScriptEngine.Shared.LSL_Types.LSLFloat;
using LSLSharedInteger = OpenSim.Region.ScriptEngine.Shared.LSL_Types.LSLInteger;
using LSLSharedString = OpenSim.Region.ScriptEngine.Shared.LSL_Types.LSLString;
using LSLSharedVector = OpenSim.Region.ScriptEngine.Shared.LSL_Types.Vector3;
using LSLSharedRotation = OpenSim.Region.ScriptEngine.Shared.LSL_Types.Quaternion;
using PhloxList = InWorldz.Phlox.Types.LSLList;
using OpenMetaverse;

namespace Phlox.ScriptEngine
{
    public partial class LSLSystemAPI
    {
        private LS_Api CreateLightShareApi()
        {
            LS_Api api = new();
            api.Initialize(m_ScriptEngine, m_host, GetInventorySelf());
            return api;
        }

        private static LSLSharedList ToSharedLightShareList(PhloxList values)
        {
            object[] converted = new object[values.Length];
            for (int i = 0; i < converted.Length; i++)
            {
                object value = values.Data[i];
                if (value is string text) converted[i] = new LSLSharedString(text);
                else if (value is int integer) converted[i] = new LSLSharedInteger(integer);
                else if (value is float number) converted[i] = new LSLSharedFloat(number);
                else if (value is Vector3 vector) converted[i] = new LSLSharedVector(vector);
                else if (value is Quaternion rotation) converted[i] = new LSLSharedRotation(rotation);
                else throw new InvalidOperationException($"Unsupported LightShare rule value type: {value?.GetType().Name ?? "null"}");
            }
            return new LSLSharedList(converted);
        }

        private static PhloxList ToPhloxLightShareList(LSLSharedList values)
        {
            var converted = new System.Collections.Generic.List<object>(values.Length);
            foreach (object value in values.Data)
            {
                if (value is LSLSharedString text) converted.Add((string)text);
                else if (value is LSLSharedInteger integer) converted.Add((int)integer);
                else if (value is LSLSharedFloat number) converted.Add((float)number);
                else if (value is LSLSharedVector vector) converted.Add(new Vector3((float)vector.x, (float)vector.y, (float)vector.z));
                else if (value is LSLSharedRotation rotation) converted.Add(new Quaternion((float)rotation.x, (float)rotation.y, (float)rotation.z, (float)rotation.s));
                else throw new InvalidOperationException($"Unsupported LightShare result type: {value?.GetType().Name ?? "null"}");
            }
            return new PhloxList(converted);
        }

        public InWorldz.Phlox.Types.LSLList lsGetWindlightScene(InWorldz.Phlox.Types.LSLList rules)
        {
            LSLSharedList sharedRules = ToSharedLightShareList(rules);
            LSLSharedList values = CreateLightShareApi().lsGetWindlightScene(sharedRules);
            return ToPhloxLightShareList(values);
        }

        public int lsSetWindlightScene(InWorldz.Phlox.Types.LSLList rules)
        {
            LSLSharedList sharedRules = ToSharedLightShareList(rules);
            return CreateLightShareApi().lsSetWindlightScene(sharedRules);
        }

        public int lsSetWindlightSceneTargeted(InWorldz.Phlox.Types.LSLList rules, string targetId)
        {
            LSLSharedList sharedRules = ToSharedLightShareList(rules);
            return CreateLightShareApi().lsSetWindlightSceneTargeted(sharedRules, new LSLSharedString(targetId));
        }

        public void lsClearWindlightScene()
        {
            CreateLightShareApi().lsClearWindlightScene();
        }
    }
}
