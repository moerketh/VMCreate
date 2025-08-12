using System.Diagnostics;

namespace VMCreateTests
{
    [TestClass]
    public sealed class Init
    {
        [AssemblyInitialize]
        public static void AssemblyInit(TestContext context)
        {
            // This method is called once for the test assembly, before any tests are run.
            Trace.Listeners.Add(new TextWriterTraceListener(Console.Out));

        }

        [AssemblyCleanup]
        public static void AssemblyCleanup()
        {
            // This method is called once for the test assembly, after all tests are run.
        }
    }
}
