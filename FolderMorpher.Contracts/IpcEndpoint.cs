using System.Diagnostics;
using System.Security.Principal;

namespace FolderMorpher.Contracts;

/// <summary>Shared endpoint identity for this Windows logon session.</summary>
public static class IpcEndpoint
{
    public static int SessionId => Process.GetCurrentProcess().SessionId;

    public static string UserSid => WindowsIdentity.GetCurrent().User?.Value
        ?? throw new InvalidOperationException("The current Windows user has no SID.");

    public static string PipeName => $"FolderMorpher_IPC_{UserSid}_{SessionId}";

    public static string MutexName => $@"Local\FolderMorpher_Host_{UserSid}_{SessionId}";
}
