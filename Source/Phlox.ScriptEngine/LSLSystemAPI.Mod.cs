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
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.ScriptEngine.Shared;
using OpenSim.Region.ScriptEngine.Shared.ScriptBase;
using static Phlox.ScriptEngine.SlConst;
using LSL_Float = OpenSim.Region.ScriptEngine.Shared.LSL_Types.LSLFloat;
using LSL_Integer = OpenSim.Region.ScriptEngine.Shared.LSL_Types.LSLInteger;
using LSL_Key = OpenSim.Region.ScriptEngine.Shared.LSL_Types.LSLString;
using LSL_List = OpenSim.Region.ScriptEngine.Shared.LSL_Types.list;
using LSL_Rotation = OpenSim.Region.ScriptEngine.Shared.LSL_Types.Quaternion;
using LSL_String = OpenSim.Region.ScriptEngine.Shared.LSL_Types.LSLString;
using LSL_Vector = OpenSim.Region.ScriptEngine.Shared.LSL_Types.Vector3;

namespace Phlox.ScriptEngine
{
    public partial class LSLSystemAPI
    {
        private bool MODFunctionsEnabled
        {
            get
            {
                IConfig config = m_ScriptEngine?.ConfigSource?.Configs["OSSL"];
                if (config == null)
                    config = m_ScriptEngine?.Config;

                return config != null && config.GetBoolean("AllowMODFunctions", false) &&
                       World?.RequestModuleInterface<IScriptModuleComms>() != null;
            }
        }

        private static void MODError(string message) =>
            throw new ScriptException("MOD Runtime Error: " + message);

        private void MODShoutError(string message)
        {
            if (message.Length > 1023)
                message = message.Substring(0, 1023);

            World.SimChat(Utils.StringToBytes(message), ChatTypeEnum.Shout, DEBUG_CHANNEL,
                m_host.ParentGroup.RootPart.AbsolutePosition, m_host.Name, m_host.UUID, false);
            World.RequestModuleInterface<IWorldComm>()?.DeliverMessage(
                ChatTypeEnum.Shout, DEBUG_CHANNEL, m_host.Name, m_host.UUID, message);
        }

        public void modInvokeN(string fname, object[] parms)
        {
            IScriptModuleComms comms = World.RequestModuleInterface<IScriptModuleComms>();
            if (comms.LookupReturnType(fname) != typeof(void))
                MODError(string.Format("return type mismatch for {0}", fname));
            modInvoke(fname, parms, comms);
        }

        public string modInvokeS(string fname, object[] parms)
        {
            IScriptModuleComms comms = World.RequestModuleInterface<IScriptModuleComms>();
            if (comms.LookupReturnType(fname) != typeof(string))
                MODError(string.Format("return type mismatch for {0}", fname));
            return (string)modInvoke(fname, parms, comms);
        }

        public int modInvokeI(string fname, object[] parms)
        {
            IScriptModuleComms comms = World.RequestModuleInterface<IScriptModuleComms>();
            if (comms.LookupReturnType(fname) != typeof(int))
                MODError(string.Format("return type mismatch for {0}", fname));
            return (int)modInvoke(fname, parms, comms);
        }

        public float modInvokeF(string fname, object[] parms)
        {
            IScriptModuleComms comms = World.RequestModuleInterface<IScriptModuleComms>();
            if (comms.LookupReturnType(fname) != typeof(float))
                MODError(string.Format("return type mismatch for {0}", fname));
            return (float)modInvoke(fname, parms, comms);
        }

        public string modInvokeK(string fname, object[] parms)
        {
            IScriptModuleComms comms = World.RequestModuleInterface<IScriptModuleComms>();
            if (comms.LookupReturnType(fname) != typeof(UUID))
                MODError(string.Format("return type mismatch for {0}", fname));
            return ((UUID)modInvoke(fname, parms, comms)).ToString();
        }

        public Vector3 modInvokeV(string fname, object[] parms)
        {
            IScriptModuleComms comms = World.RequestModuleInterface<IScriptModuleComms>();
            if (comms.LookupReturnType(fname) != typeof(Vector3))
                MODError(string.Format("return type mismatch for {0}", fname));
            return (Vector3)modInvoke(fname, parms, comms);
        }

        public Quaternion modInvokeR(string fname, object[] parms)
        {
            IScriptModuleComms comms = World.RequestModuleInterface<IScriptModuleComms>();
            if (comms.LookupReturnType(fname) != typeof(Quaternion))
                MODError(string.Format("return type mismatch for {0}", fname));
            return (Quaternion)modInvoke(fname, parms, comms);
        }

        public InWorldz.Phlox.Types.LSLList modInvokeL(string fname, object[] parms)
        {
            IScriptModuleComms comms = World.RequestModuleInterface<IScriptModuleComms>();
            if (comms.LookupReturnType(fname) != typeof(object[]))
                MODError(string.Format("return type mismatch for {0}", fname));

            object[] result = (object[])modInvoke(fname, parms, comms);
            var list = new System.Collections.Generic.List<object>(result.Length);
            for (int i = 0; i < result.Length; i++)
            {
                if (result[i] is string s) list.Add(s);
                else if (result[i] is int n) list.Add(n);
                else if (result[i] is float f) list.Add(f);
                else if (result[i] is double d) list.Add((float)d);
                else if (result[i] is UUID id) list.Add(id.ToString());
                else if (result[i] is Vector3 v) list.Add(v);
                else if (result[i] is Quaternion q) list.Add(q);
                else MODError(string.Format("unknown list element {1} returned by {0}", fname, result[i].GetType().Name));
            }

            return new InWorldz.Phlox.Types.LSLList(list);
        }

        private object modInvoke(string fname, object[] parms, IScriptModuleComms comms)
        {
            if (!MODFunctionsEnabled)
            {
                MODShoutError("Module command functions not enabled");
                return "";
            }

            Type[] signature = comms.LookupTypeSignature(fname);
            if (signature.Length != parms.Length)
                MODError(string.Format("wrong number of parameters to function {0}", fname));

            object[] converted = new object[parms.Length];
            for (int i = 0; i < parms.Length; i++)
                converted[i] = ConvertFromLSL(parms[i], signature[i], fname);

            try
            {
                object result = comms.InvokeOperation(m_host.UUID, m_itemID, fname, converted);
                if (result != null)
                    return result;
                if (comms.LookupReturnType(fname) == typeof(void))
                    return null;
                MODError(string.Format("Invocation of {0} failed; null return value", fname));
            }
            catch (Exception e)
            {
                MODError(string.Format("Invocation of {0} failed; {1}", fname, e.Message));
            }

            return null;
        }

        public string modSendCommand(string module, string command, string k)
        {
            if (!MODFunctionsEnabled)
            {
                MODShoutError("Module command functions not enabled");
                return UUID.Zero.ToString();
            }

            IScriptModuleComms comms = World.RequestModuleInterface<IScriptModuleComms>();
            string request = UUID.Random().ToString();
            comms.RaiseEvent(m_itemID, request, module, command, k);
            return request;
        }

        private static object ConvertFromLSL(object value, Type type, string fname)
        {
            if (value.GetType() == type)
                return value;
            if (value is string rawText)
            {
                if (type == typeof(string)) return rawText;
                if (type == typeof(UUID)) return new UUID(rawText);
            }
            else if (value is int rawInteger)
            {
                if (type == typeof(int)) return rawInteger;
                if (type == typeof(float)) return (float)rawInteger;
            }
            else if (value is float rawFloat && type == typeof(float)) return rawFloat;
            else if (value is InWorldz.Phlox.Types.LSLList phloxList && type == typeof(object[]))
            {
                var converted = new object[phloxList.Length];
                for (int i = 0; i < converted.Length; i++)
                {
                    object item = phloxList.Data[i];
                    if (item is string s) converted[i] = s;
                    else if (item is int n) converted[i] = n;
                    else if (item is float f) converted[i] = f;
                    else if (item is Vector3 v) converted[i] = v;
                    else if (item is Quaternion q) converted[i] = q;
                    else MODError(string.Format("{0}: unknown LSL list element type", fname));
                }
                return converted;
            }
            if (value is LSL_String text)
            {
                if (type == typeof(string)) return (string)text;
                if (type == typeof(UUID)) return new UUID((string)text);
            }
            else if (value is LSL_Integer integer)
            {
                if (type == typeof(int) || type == typeof(float)) return (int)integer;
            }
            else if (value is LSL_Float number)
            {
                if (type == typeof(float)) return (float)number;
            }
            else if (value is LSL_Key key)
            {
                if (type == typeof(UUID)) return new UUID((string)key);
            }
            else if (value is LSL_Rotation rotation)
            {
                if (type == typeof(Quaternion)) return (Quaternion)rotation;
            }
            else if (value is LSL_Vector vector)
            {
                if (type == typeof(Vector3)) return (Vector3)vector;
            }
            else if (value is LSL_List lslList && type == typeof(object[]))
            {
                object[] items = lslList.Data;
                object[] converted = new object[items.Length];
                for (int i = 0; i < items.Length; i++)
                {
                    if (items[i] is LSL_String s) converted[i] = (string)s;
                    else if (items[i] is LSL_Integer n) converted[i] = (int)n;
                    else if (items[i] is int) converted[i] = items[i];
                    else if (items[i] is LSL_Float f) converted[i] = (float)f;
                    else if (items[i] is LSL_Key k) converted[i] = new UUID((string)k);
                    else if (items[i] is LSL_Rotation r) converted[i] = (Quaternion)r;
                    else if (items[i] is LSL_Vector v) converted[i] = (Vector3)v;
                    else MODError(string.Format("{0}: unknown LSL list element type", fname));
                }
                return converted;
            }

            MODError(string.Format("{0}: parameter type mismatch; expecting {1}, type(parm)={2}",
                fname, type.Name, value.GetType()));
            return null;
        }
    }
}
