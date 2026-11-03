using System.Diagnostics;
using SafeDiskCleaner.Core;

// The PATH test changes the process environment; keep tests sequential so nothing races it.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace SafeDiskCleaner.Core.Tests;

/// <summary>A fresh folder under %TEMP% that the test owns, removed afterwards. Never a real user folder.</summary>
public sealed class TempDir : IDisposable
{
    public TempDir()
    {
        string raw = Path.Combine(Path.GetTempPath(), "SafeDiskCleanerTests", Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(raw);
        // GitHub's runners report %TEMP% with an 8.3 name (RUNNER~1); work with the long form.
        Root = PathUtil.Norm(raw);
    }

    public string Root { get; }

    public string Dir(params string[] parts)
    {
        string p = Path.Combine([Root, .. parts]);
        Directory.CreateDirectory(p);
        return p;
    }

    public string File(string text, params string[] parts)
    {
        string p = Path.Combine([Root, .. parts]);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        System.IO.File.WriteAllText(p, text);
        return p;
    }

    /// <summary>A guard whose only allowed root (and only top-level folder) is this temp folder.</summary>
    public Guard Guard(IReadOnlyList<string>? protectedPaths = null, IReadOnlyList<string>? inUse = null) => new(new GuardConfig
    {
        AllowedRoots = [Root],
        NeverExact = [Root],
        Protected = protectedPaths ?? [],
        InUse = inUse ?? [],
    });

    public void Dispose()
    {
        try
        {
            foreach (string f in Directory.EnumerateFiles(Root, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true }))
            {
                System.IO.File.SetAttributes(f, FileAttributes.Normal);
            }
            // .NET's recursive delete removes junctions without following them.
            Directory.Delete(Root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}

public static class Links
{
    /// <summary>Creates a directory junction the way the spec asks: cmd /c mklink /J.</summary>
    public static void Junction(string link, string target)
    {
        var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using Process p = Process.Start(psi)!;
        string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, "mklink /J failed: " + output);
        Assert.True(PathUtil.IsLink(link), "the junction was not created");
    }

    /// <summary>Symbolic links need admin or Developer Mode; skip the test where they are not allowed.</summary>
    public static void SymlinkOrSkip(Action create)
    {
        try
        {
            create();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Xunit.Skip.If(true, "Symbolic links are not allowed for this user: " + ex.Message);
        }
    }
}
