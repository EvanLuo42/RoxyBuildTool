using Xunit;

namespace RoxyBuildTool.IntegrationTests;

public sealed class WorkspaceGenerationLockTests
{
    [Fact]
    public async Task AcquisitionWaitsUntilTheCurrentWorkspaceWriterReleasesTheLock()
    {
        var workspaceRoot = Path.Combine(Path.GetTempPath(), $"RoxyGenerationLock-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspaceRoot);
        try
        {
            await using var first = await WorkspaceGenerationLock.AcquireAsync(
                workspaceRoot, "workspace", TestContext.Current.CancellationToken);
            var secondTask = WorkspaceGenerationLock.AcquireAsync(
                workspaceRoot, "workspace", TestContext.Current.CancellationToken).AsTask();

            await Task.Yield();
            Assert.False(secondTask.IsCompleted);

            await first.DisposeAsync();
            await using var second = await secondTask;
        }
        finally
        {
            Directory.Delete(workspaceRoot, recursive: true);
        }
    }
}