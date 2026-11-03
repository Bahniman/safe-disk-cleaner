using System.Diagnostics;
using System.Security.Principal;
using System.Text.RegularExpressions;

namespace SafeDiskCleaner.Core;

/// <summary>Small system helpers: admin state, free space, hidden child processes, nvidia-smi.</summary>
public static partial class Tools
{
    public static bool IsAdmin()
    {
        try
        {
            using WindowsIdentity id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    /// <summary>Free bytes on the drive holding <paramref name="path"/>, or null if it cannot be read.</summary>
    public static long? FreeBytes(string path)
    {
        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(path));
            return string.IsNullOrEmpty(root) ? null : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Total and free bytes of the system drive, for the header.</summary>
    public static (long Total, long Free)? SystemDriveUsage()
    {
        try
        {
            var d = new DriveInfo(KnownPaths.SystemDrive);
            return (d.TotalSize, d.AvailableFreeSpace);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Runs a program without a window and returns its exit code and output. Kills it on timeout.</summary>
    public static (int ExitCode, string Output) RunHidden(string exe, string arguments, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo(exe, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        using Process p = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {Path.GetFileName(exe)}.");
        Task<string> stdout = p.StandardOutput.ReadToEndAsync();
        Task<string> stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(timeout))
        {
            try
            {
                p.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }
            throw new TimeoutException($"{Path.GetFileName(exe)} did not finish in {timeout.TotalMinutes:0} minutes.");
        }
        p.WaitForExit();
        return (p.ExitCode, stdout.Result + stderr.Result);
    }

    public static string System32(string exe) => Path.Combine(KnownPaths.Windows, "System32", exe);

    /// <summary>Runs a PowerShell command by full path (never from PATH) and throws if it fails.</summary>
    public static void PowerShell(string command, TimeSpan timeout)
    {
        string ps = Path.Combine(KnownPaths.Windows, "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        (int code, string output) = RunHidden(ps, $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"{command}\"", timeout);
        if (code != 0)
        {
            throw new InvalidOperationException(FirstLine(output) ?? $"PowerShell exited with code {code}.");
        }
    }

    /// <summary>
    /// The installed NVIDIA driver version from nvidia-smi, or null without an NVIDIA GPU.
    /// Looks only in System32 and the old NVSMI folder, never on PATH.
    /// </summary>
    public static VersionKey? NvidiaDriverVersion()
    {
        string[] candidates =
        [
            System32("nvidia-smi.exe"),
            Path.Combine(KnownPaths.ProgramFiles, "NVIDIA Corporation", "NVSMI", "nvidia-smi.exe"),
        ];
        foreach (string exe in candidates)
        {
            if (!File.Exists(exe))
            {
                continue;
            }
            try
            {
                (int code, string output) = RunHidden(exe, "--query-gpu=driver_version --format=csv,noheader", TimeSpan.FromSeconds(20));
                string? line = FirstLine(output);
                if (code == 0 && line is not null && VersionLike().IsMatch(line))
                {
                    return VersionKey.Parse(line);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException or IOException)
            {
            }
        }
        return null;
    }

    private static string? FirstLine(string text) =>
        text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();

    [GeneratedRegex(@"^\d+(\.\d+)+$")]
    private static partial Regex VersionLike();
}
