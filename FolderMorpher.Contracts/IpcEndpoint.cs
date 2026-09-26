using System.Diagnostics;
using System.Security.Principal;

namespace FolderMorpher.Contracts;

/// <summary>Shared endpoint identity for this Windows logon session.</summary>
public static class IpcEndpoint
{
    private static string TestSuffix
    {
        get
        {
            if (!Environment.GetCommandLineArgs().Contains("--test-ipc")) return string.Empty;
            var id = Environment.GetEnvironmentVariable("FOLDERMORPHER_TEST_IPC_ID");
            return Guid.TryParseExact(id, "N", out _) ? $"_Test_{id}" : string.Empty;
        }
    }

    public static int SessionId => Process.GetCurrentProcess().SessionId;

    public static string UserSid => WindowsIdentity.GetCurrent().User?.Value
        ?? throw new InvalidOperationException("The current Windows user has no SID.");

    public static string PipeName => $"FolderMorpher_IPC_{UserSid}_{SessionId}{TestSuffix}";

    public static string MutexName => $@"Local\FolderMorpher_Host_{UserSid}_{SessionId}{TestSuffix}";
}
