using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace VMCreate
{
    public class VmSettings
    {
        public string VMName { get; internal set; }
        public int MemoryMB { get; internal set; }
        public int CPUCount { get; internal set; }
    }
}
