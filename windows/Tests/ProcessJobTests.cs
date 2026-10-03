using System.Diagnostics;
using Xunit;

namespace R2T2CrispASR.Tests;

public class ProcessJobTests
{
    [Fact]
    public async Task ClosingJobTerminatesItsNativeChild()
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "ping.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        foreach (string argument in new[] { "-n", "30", "127.0.0.1" }) start.ArgumentList.Add(argument);
        using var child = Process.Start(start)!;
        try
        {
            using (var job = new ProcessJob(child)) Assert.False(child.HasExited);
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(child.HasExited);
        }
        finally { if (!child.HasExited) child.Kill(); }
    }
}
