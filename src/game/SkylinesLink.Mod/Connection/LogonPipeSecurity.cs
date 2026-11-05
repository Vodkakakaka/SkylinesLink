using System.IO.Pipes;
using System.Security.AccessControl;

namespace SkylinesLink.Mod.Connection
{
    public static class LogonPipeSecurity
    {
        public static PipeSecurity Create()
        {
            var security = new PipeSecurity();
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new PipeAccessRule(WindowsTokenIdentity.CurrentLogon(), PipeAccessRights.FullControl, AccessControlType.Allow));
            return security;
        }
    }
}
