using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CreateVM
{
    public static class BooleanOnOffAdapter
    {
        public static string ToOnOff(this bool input)
        {
            return input ? "On" : "Off";
        }
    }
}
