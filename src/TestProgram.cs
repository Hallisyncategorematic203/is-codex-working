using System;

namespace IsCodexWorking
{
    internal static class TestProgram
    {
        [STAThread]
        private static void Main(string[] args)
        {
            if (args != null && args.Length > 0 && string.Equals(args[0], "--self-test", StringComparison.OrdinalIgnoreCase))
            {
                Environment.ExitCode = SelfTests.RunAll();
                return;
            }
            if (args != null && args.Length > 0 && string.Equals(args[0], "--stress", StringComparison.OrdinalIgnoreCase))
            {
                Environment.ExitCode = StressTests.RunAll();
                return;
            }
            if (args != null && args.Length > 0 && string.Equals(args[0], "--idle-smoke", StringComparison.OrdinalIgnoreCase))
            {
                Environment.ExitCode = StressTests.RunIdleSmoke();
                return;
            }

            Environment.ExitCode = 2;
            Console.Error.WriteLine("Specify --self-test, --stress, or --idle-smoke.");
        }
    }
}
