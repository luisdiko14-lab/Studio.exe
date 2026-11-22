using Studio.CrashHandler.Core;
using Studio.CrashHandler.Diagnostics;

namespace Studio.CrashHandler;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length != 3 ||
                !args[0].Equals("--watch", StringComparison.OrdinalIgnoreCase) ||
                !int.TryParse(args[1], out int parentProcessId))
            {
                CrashLog.Write("Crash handler received invalid arguments. Expected: --watch <pid> <studio-exe> <working-directory>.");
                return 2;
            }

            return CrashWatchdog.Watch(parentProcessId, args[2], args.Length > 3 ? args[3] : Path.GetDirectoryName(args[2])!);
        }
        catch (Exception ex)
        {
            CrashLog.Write("Fatal error in Studio Crash Handler.", ex);
            return 10;
        }
    }
}
