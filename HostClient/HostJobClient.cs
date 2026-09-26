using FolderMorpher.Contracts;

namespace FolderMorpher.HostClient;

public static class HostJobClient
{
    public static async Task<HostJobStatusDto> RunAsync(
        HostJobRequestDto request,
        Action<HostJobStatusDto>? onProgress,
        CancellationToken ct)
    {
        var service = await FolderMorpherHostClient.Instance.GetServiceAsync(ct);
        var jobId = await service.StartJobAsync(request);
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                service = await FolderMorpherHostClient.Instance.GetServiceAsync(ct);
                var status = await service.GetJobStatusAsync(jobId);
                onProgress?.Invoke(status);
                switch (status.State)
                {
                    case HostJobState.Completed:
                        await ReleaseBestEffortAsync(service, jobId);
                        return status;
                    case HostJobState.Canceled:
                        await ReleaseBestEffortAsync(service, jobId);
                        throw new OperationCanceledException("Host job was canceled.", ct);
                    case HostJobState.Failed:
                        await ReleaseBestEffortAsync(service, jobId);
                        throw new InvalidOperationException(status.Error ?? "Host job failed.");
                }
                await Task.Delay(250, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            try
            {
                service = await FolderMorpherHostClient.Instance.GetServiceAsync();
                await service.CancelJobAsync(jobId);
            }
            catch { /* The Host retains its job if this client is already disconnecting. */ }
            throw;
        }
    }

    private static async Task ReleaseBestEffortAsync(IFolderMorpherHostService service, Guid jobId)
    {
        try { await service.ReleaseJobAsync(jobId); }
        catch { /* A disconnected UI must not lose an already fetched result. */ }
    }
}
