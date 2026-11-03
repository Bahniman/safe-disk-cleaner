using System.Diagnostics;

namespace SafeDiskCleaner.Core;

/// <summary>Running processes, by exe name and full image path (Ctx in the reference).</summary>
public sealed class ProcessSnapshot
{
    private readonly HashSet<string> _names;
    private readonly List<string> _images;

    public ProcessSnapshot(IEnumerable<string> imagePaths, IEnumerable<string>? extraNames = null)
    {
        _images = imagePaths.Select(p => PathUtil.TryNorm(p) ?? p).ToList();
        _names = new HashSet<string>(_images.Select(Path.GetFileName).Where(n => !string.IsNullOrEmpty(n))!, StringComparer.OrdinalIgnoreCase);
        if (extraNames is not null)
        {
            _names.UnionWith(extraNames);
        }
    }

    public IReadOnlyCollection<string> Names => _names;

    public IReadOnlyList<string> Images => _images;

    public static ProcessSnapshot Take()
    {
        var images = new List<string>();
        var names = new List<string>();
        foreach (Process p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    names.Add(p.ProcessName + ".exe");
                }
                catch (InvalidOperationException)
                {
                    // exited meanwhile
                }
                try
                {
                    if (NativeMethods.TryGetImagePath(p.Id) is string path)
                    {
                        images.Add(path);
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                }
            }
        }
        return new ProcessSnapshot(images, names);
    }

    /// <summary>True when a process with one of these exe names runs, or one runs from inside one of these folders.</summary>
    public bool Running(IEnumerable<string>? names = null, IEnumerable<string>? dirs = null)
    {
        if (names is not null && names.Any(_names.Contains))
        {
            return true;
        }
        if (dirs is null)
        {
            return false;
        }
        foreach (string d in dirs)
        {
            if (!Directory.Exists(d) || PathUtil.TryNorm(d) is not string nd)
            {
                continue;
            }
            if (_images.Any(img => PathUtil.UnderNormalized(img, nd)))
            {
                return true;
            }
        }
        return false;
    }
}
