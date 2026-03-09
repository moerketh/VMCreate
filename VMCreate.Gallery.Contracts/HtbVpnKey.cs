using System.Collections.Generic;

namespace VMCreate
{
    /// <summary>
    /// A downloaded VPN key ready for deployment to a VM.
    /// </summary>
    public class HtbVpnKey
    {
        public string Name { get; set; }
        public string OvpnContent { get; set; }
        public string GuestFileName { get; set; }
    }
}
