using System.Collections.Generic;

namespace VMCreate
{
    /// <summary>
    /// A downloaded VPN key ready for deployment to a VM.
    /// </summary>
    public class HtbVpnKey
    {
        // Populated programmatically (HtbApiClient); string members are always
        // set before use. null! satisfies nullable initialization analysis.
        public string Name { get; set; } = null!;
        public string OvpnContent { get; set; } = null!;
        public string GuestFileName { get; set; } = null!;
    }
}
