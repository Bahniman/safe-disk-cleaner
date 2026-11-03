namespace SafeDiskCleaner.Core;

/// <summary>What one delete did.</summary>
public sealed class DeleteReport
{
    public int FilesDeleted { get; internal set; }

    public int LinksRemoved { get; internal set; }

    /// <summary>OneDrive-style placeholders (RecallOnDataAccess, RecallOnOpen, Offline) left in place.</summary>
    public int PlaceholdersKept { get; internal set; }

    /// <summary>Other reparse points that are neither links nor placeholders, left in place.</summary>
    public int SpecialKept { get; internal set; }

    /// <summary>Files or folders that could not be removed (usually in use).</summary>
    public int Failed { get; internal set; }

    public int Kept => PlaceholdersKept + SpecialKept;
}

/// <summary>
/// The recursive delete, written out rather than Directory.Delete(recursive) so every rule is visible:
/// a junction or symlink is removed as a link and never descended into, read-only attributes are
/// cleared first, and cloud placeholder files are never deleted (or hydrated).
/// </summary>
public static class SafeDelete
{
    public const FileAttributes RecallOnOpen = (FileAttributes)0x00040000;
    public const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;

    private const FileAttributes Settable =
        FileAttributes.Hidden | FileAttributes.System | FileAttributes.Archive |
        FileAttributes.NotContentIndexed | FileAttributes.Temporary;

    public static bool IsCloudPlaceholder(FileAttributes a) =>
        (a & (RecallOnOpen | RecallOnDataAccess | FileAttributes.Offline)) != 0;

    public static DeleteReport Delete(string path)
    {
        var report = new DeleteReport();
        DeleteEntry(path, report);
        return report;
    }

    private static void DeleteEntry(string path, DeleteReport r)
    {
        FileAttributes a;
        try
        {
            a = File.GetAttributes(path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            r.Failed++;
            return;
        }

        // Placeholders first: they are often reparse points too, and touching them would download them.
        if (IsCloudPlaceholder(a))
        {
            r.PlaceholdersKept++;
            return;
        }

        bool isDir = (a & FileAttributes.Directory) != 0;
        if ((a & FileAttributes.ReparsePoint) != 0)
        {
            if (PathUtil.IsLinkReparsePoint(path, a))
            {
                RemoveLink(path, isDir, a, r);
            }
            else
            {
                r.SpecialKept++;
            }
            return;
        }

        if (isDir)
        {
            List<string> children;
            try
            {
                children = Directory.EnumerateFileSystemEntries(path, "*", ChildOptions).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                r.Failed++;
                return;
            }
            int problemsBefore = r.Failed + r.Kept;
            foreach (string child in children)
            {
                DeleteEntry(child, r);
            }
            try
            {
                ClearReadOnly(path, a);
                Directory.Delete(path, recursive: false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Usually not empty because something inside was kept or is in use, which is
                // already counted. Count it here only if nothing inside explains it.
                if (r.Failed + r.Kept == problemsBefore)
                {
                    r.Failed++;
                }
            }
        }
        else
        {
            try
            {
                ClearReadOnly(path, a);
                File.Delete(path);
                r.FilesDeleted++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                r.Failed++;
            }
        }
    }

    /// <summary>Deletes the link itself. RemoveDirectory on a junction or directory symlink never touches the target.</summary>
    private static void RemoveLink(string path, bool isDir, FileAttributes a, DeleteReport r)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                if (isDir)
                {
                    Directory.Delete(path, recursive: false);
                }
                else
                {
                    File.Delete(path);
                }
                r.LinksRemoved++;
                return;
            }
            catch (UnauthorizedAccessException) when (attempt == 0 && (a & FileAttributes.ReadOnly) != 0)
            {
                try
                {
                    ClearReadOnly(path, a);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    break;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                break;
            }
        }
        r.Failed++;
    }

    private static void ClearReadOnly(string path, FileAttributes a)
    {
        if ((a & FileAttributes.ReadOnly) == 0)
        {
            return;
        }
        FileAttributes keep = a & Settable;
        File.SetAttributes(path, keep == 0 ? FileAttributes.Normal : keep);
    }

    private static readonly EnumerationOptions ChildOptions = new()
    {
        RecurseSubdirectories = false,
        AttributesToSkip = 0,
        IgnoreInaccessible = false,
        ReturnSpecialDirectories = false,
    };
}
