using System.Security.AccessControl;
using System.Security.Principal;

namespace FolderMorpher.Services.Testing;

// Only regression fixtures created immediately under the current user's temp directory.
// This must never be used to repair permissions on a search target.
internal static class AclTestDirectoryCleanup
{
    private static readonly string[] FixturePrefixes =
    [
        "FM_RegTest_Acl_", "FM_RegTest_AclFlags_", "FM_RegTest_EffAccess_",
        "FM_RegTest_InheritedOrder_", "FM_RegTest_Rollback_", "FM_RegTest_AclFailSnap_",
        "FM_RegTest_Cleanup_", "FM_DeltaApply_Test_", "FolderMorpher_Regression_Test21_",
        "FolderMorpher_Regression_Test22_", "FolderMorpher_ChangePlanTest_",
        "Test29_EffAccessReal_", "Test29_LinkFix_"
    ];

    internal static string ValidateRoot(string path)
    {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        string temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        string name = Path.GetFileName(root);
        if (!string.Equals(Path.GetDirectoryName(root), temp, StringComparison.OrdinalIgnoreCase) ||
            !FixturePrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal) &&
                Guid.TryParseExact(name[prefix.Length..], "N", out _)))
            throw new InvalidOperationException($"Refusing cleanup outside a named ACL test fixture: {root}");
        return root;
    }

    internal static void Delete(string path)
    {
        string root = ValidateRoot(path);
        RestoreAccess(root);
        try { Directory.Delete(root, recursive: true); }
        catch (DirectoryNotFoundException) { }
        if (Directory.Exists(root))
            throw new IOException($"ACL test fixture remained after cleanup: {root}");
        // Any other failure propagates to the regression gate instead of leaving silent debris.
    }

    // Also used by one-off recovery of known old fixtures before sending them to the Recycle Bin.
    internal static void RestoreAccess(string path)
    {
        string root = ValidateRoot(path);
        FileAttributes rootAttributes;
        try { rootAttributes = File.GetAttributes(root); }
        catch (FileNotFoundException) { return; }
        catch (DirectoryNotFoundException) { return; }
        RejectReparsePoint(root, rootAttributes);

        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User ?? throw new InvalidOperationException("Missing test runner SID.");
        var directories = new Stack<string>();
        directories.Push(root);
        while (directories.TryPop(out string? directory))
        {
            EnsureInsideRoot(root, directory);
            RejectReparsePoint(directory, File.GetAttributes(directory));
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            // No inheritance: Windows must not propagate this ACL through a child junction.
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
            new DirectoryInfo(directory).SetAccessControl(security);

            foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
            {
                EnsureInsideRoot(root, entry);
                var attributes = File.GetAttributes(entry);
                RejectReparsePoint(entry, attributes);
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    directories.Push(entry);
                }
                else
                {
                    var fileSecurity = new FileSecurity();
                    fileSecurity.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
                    fileSecurity.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
                    new FileInfo(entry).SetAccessControl(fileSecurity);
                    if ((attributes & FileAttributes.ReadOnly) != 0)
                        File.SetAttributes(entry, attributes & ~FileAttributes.ReadOnly);
                }
            }
        }
    }

    private static void EnsureInsideRoot(string root, string path)
    {
        string absolute = Path.GetFullPath(path);
        if (!string.Equals(absolute, root, StringComparison.OrdinalIgnoreCase) &&
            !absolute.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Fixture entry escaped its cleanup root: {absolute}");
    }

    private static void RejectReparsePoint(string path, FileAttributes attributes)
    {
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException($"Refusing to follow a reparse point during ACL test cleanup: {path}");
    }
}
