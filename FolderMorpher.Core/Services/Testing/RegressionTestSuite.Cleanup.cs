using System.Security.AccessControl;
using System.Security.Principal;

namespace FolderMorpher.Services.Testing;

public static partial class RegressionTestSuite
{
    private static void TestAclFixtureCleanup()
    {
        string root = Path.Combine(Path.GetTempPath(), "FM_RegTest_Cleanup_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            // Refuse both an ordinary temp directory and a fixture nested below another fixture.
            foreach (string unsafeRoot in new[] { Path.GetTempPath(), Path.Combine(root, "Nested"),
                Path.Combine(Path.GetTempPath(), "Unrelated_" + Guid.NewGuid().ToString("N")) })
            {
                bool refused = false;
                try { AclTestDirectoryCleanup.Delete(unsafeRoot); }
                catch (InvalidOperationException) { refused = true; }
                if (!refused || !Directory.Exists(root))
                    throw new InvalidOperationException("ACL fixture cleanup did not enforce its temp-root boundary.");
            }

            string blocked = Path.Combine(root, "ProtectedParent", "ProtectedChild");
            Directory.CreateDirectory(blocked);
            string file = Path.Combine(blocked, "readonly.txt");
            File.WriteAllText(file, "disposable regression fixture");
            File.SetAttributes(file, FileAttributes.ReadOnly);

            var emptyDacl = new DirectorySecurity();
            emptyDacl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            new DirectoryInfo(blocked).SetAccessControl(emptyDacl);

            using var identity = WindowsIdentity.GetCurrent();
            var sid = identity.User!;
            var denyDacl = new DirectorySecurity();
            denyDacl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            denyDacl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Deny));
            new DirectoryInfo(root).SetAccessControl(denyDacl);

            AclTestDirectoryCleanup.Delete(root);
            if (Directory.Exists(root))
                throw new InvalidOperationException("Deny/empty-DACL fixture remained after cleanup.");
            AclTestDirectoryCleanup.Delete(root); // An already removed fixture is harmless.
        }
        finally
        {
            AclTestDirectoryCleanup.Delete(root);
        }
    }
}
