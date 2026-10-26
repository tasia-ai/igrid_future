namespace Phlox.ScriptEngine
{
    public partial class LSLSystemAPI
    {
        /// <summary>
        /// YEngine retains this deprecated LSL entry point for source compatibility. It does not load
        /// a script; callers are directed to llRemoteLoadScriptPin.
        /// </summary>
        public void llRemoteLoadScript(string target, string name, int running, int startParam)
        {
            Deprecated("llRemoteLoadScript");
            ScriptSleep(3000);
        }
    }
}
