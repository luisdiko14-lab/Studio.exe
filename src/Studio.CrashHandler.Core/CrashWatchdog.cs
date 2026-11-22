using System.Diagnostics;
using Studio.CrashHandler.Diagnostics;

namespace Studio.CrashHandler.Core;

public static class CrashWatchdog
{
    public static int Watch(int parentProcessId, string applicationPath, string workingDirectory)
    {
        if (parentProcessId <= 0 || !Path.IsPathFullyQualified(applicationPath))
        {
            CrashLog.Write("Invalid watchdog arguments. The application will not be restarted.");
            return 2;
        }

        try
        {
            using var parent = Process.GetProcessById(parentProcessId);
            CrashLog.Write($"Watching Studio.exe process {parentProcessId}.");
            parent.WaitForExit();

            int exitCode;
            try { exitCode = parent.ExitCode; }
            catch (Exception ex)
            {
                CrashLog.Write("Could not read the Studio.exe exit code. No restart will be attempted.", ex);
                return 3;
            }

            if (exitCode == 0)
            {
                CrashLog.Write($"Studio.exe process {parentProcessId} closed normally. No restart needed.");
                return 0;
            }

            if (!File.Exists(applicationPath))
            {
                CrashLog.Write($"Studio.exe exited with code {exitCode}, but the application executable is missing: {applicationPath}");
                return 4;
            }

            CrashLog.Write($"Studio.exe exited unexpectedly with code {exitCode}. Attempting the single permitted restart.");
            var startInfo = new ProcessStartInfo
            {
                FileName = applicationPath,
                WorkingDirectory = Directory.Exists(workingDirectory)
                    ? workingDirectory
                    : Path.GetDirectoryName(applicationPath)!,
                UseShellExecute = true
            };
            startInfo.ArgumentList.Add("--crash-restart");

            var restarted = Process.Start(startInfo);
            if (restarted is null)
            {
                CrashLog.Write("Windows did not return a process handle for the restart attempt.");
                return 5;
            }

            int newProcessId = restarted.Id;
            restarted.Dispose();
            CrashLog.Write($"Studio.exe was restarted once successfully (PID {newProcessId}). Further automatic restarts are disabled for this launch.");
            return 0;
        }
        catch (ArgumentException ex)
        {
            CrashLog.Write($"The Studio.exe process {parentProcessId} could not be found. No restart was attempted.", ex);
            return 6;
        }
        catch (Exception ex)
        {
            CrashLog.Write("The crash watchdog failed.", ex);
            return 7;
        }
    }
}
