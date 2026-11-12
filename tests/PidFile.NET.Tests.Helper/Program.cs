namespace PidFile.NET.Tests.Helper {
    /// <summary>
    /// Acquires the PID file given as the first argument and holds it until
    /// stdin is closed (graceful exit) or the process is killed (crash).
    /// </summary>
    internal static class Program {
        private const int EXIT_OK = 0;
        private const int EXIT_BUSY = 2;
        private const int EXIT_USAGE = 64;

        private static int Main(string[] args) {
            if (args.Length != 1) {
                return EXIT_USAGE;
            }

            using (PidFile pidFile = new PidFile(args[0])) {
                if (!pidFile.TryAcquire()) {
                    Console.WriteLine("BUSY");
                    return EXIT_BUSY;
                }

                Console.WriteLine("READY");
                Console.Out.Flush();

                // Blocks until the parent writes a line or closes stdin.
                Console.In.ReadLine();
                return EXIT_OK;
            }
        }
    }
}
