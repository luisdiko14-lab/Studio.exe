using System.Diagnostics;

namespace StudioApp.Services;

/// <summary>Persistent real shell process using redirected input/output. Not a ConPTY terminal.</summary>
public sealed class TerminalSession : IDisposable
{
    private readonly object _writeLock = new();
    private Process? _process;
    public event Action<string>? OutputReceived;
    public bool IsRunning => _process is { HasExited: false };

    public void Start(string workingDirectory, string shell = "PowerShell")
    {
        Stop();
        var useCmd = shell.Equals("Command Prompt", StringComparison.OrdinalIgnoreCase);
        var startInfo = new ProcessStartInfo
        {
            FileName = useCmd ? "cmd.exe" : FindPowerShell(),
            WorkingDirectory = Directory.Exists(workingDirectory) ? workingDirectory : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8, StandardErrorEncoding = System.Text.Encoding.UTF8
        };
        if (useCmd) startInfo.ArgumentList.Add("/Q");
        else
        {
            startInfo.ArgumentList.Add("-NoLogo");
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-Command");
            startInfo.ArgumentList.Add("-");
        }

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) OutputReceived?.Invoke(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) OutputReceived?.Invoke(e.Data); };
        process.Exited += (_, _) => OutputReceived?.Invoke("[shell process exited]");
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        _process = process;
        WriteLine(useCmd ? "echo Studio terminal connected." : "Write-Output 'Studio terminal connected.'");
    }

    public void WriteLine(string command)
    {
        lock (_writeLock)
        {
            if (_process is not { HasExited: false }) throw new InvalidOperationException("The terminal is not running.");
            _process.StandardInput.WriteLine(command);
            _process.StandardInput.Flush();
        }
    }

    public void Stop()
    {
        try
        {
            if (_process is { HasExited: false })
            {
                _process.StandardInput.Close();
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(1500);
            }
        }
        catch { }
        finally { _process?.Dispose(); _process = null; }
    }

    private static string FindPowerShell()
    {
        var programFiles = Environment.GetEnvironmentVariable("ProgramFiles") ?? @"C:\Program Files";
        var candidate = Path.Combine(programFiles, "PowerShell", "7", "pwsh.exe");
        return File.Exists(candidate) ? candidate : "powershell.exe";
    }

    public void Dispose() => Stop();
}
