using CreateVM;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Threading;
using System.Threading.Tasks;

namespace VMCreate
{
    /// <summary>
    /// Composite interface that inherits all role-specific Hyper-V manager interfaces.
    /// Consumers should prefer depending on the narrower role interfaces
    /// (<see cref="IVmLifecycleManager"/>, <see cref="IVmDiskManager"/>, etc.) whenever possible.
    /// </summary>
    public interface IHyperVManager : IVmLifecycleManager, IVmDiskManager, IVmBootManager, IVmNetworkManager, IVmConfigManager
    {
    }
}
