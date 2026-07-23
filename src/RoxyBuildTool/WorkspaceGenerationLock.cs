namespace RoxyBuildTool;

/// <summary>Serializes generation for one workspace so snapshots and owned outputs are committed together.</summary>
internal sealed class WorkspaceGenerationLock : IAsyncDisposable
{
    private readonly FileStream _stream;

    private WorkspaceGenerationLock(FileStream stream) => _stream = stream;

    public ValueTask DisposeAsync() => _stream.DisposeAsync();

    public static async ValueTask<WorkspaceGenerationLock> AcquireAsync(
        string workspaceRoot,
        string workspaceId,
        CancellationToken cancellationToken)
    {
        var lockDirectory = Path.Combine(workspaceRoot, ".roxy", "locks");
        Directory.CreateDirectory(lockDirectory);
        var lockPath = Path.Combine(lockDirectory, $"generate-{workspaceId}.lock");

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new WorkspaceGenerationLock(new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.Asynchronous));
            }
            catch (IOException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
            }
        }
    }
}
