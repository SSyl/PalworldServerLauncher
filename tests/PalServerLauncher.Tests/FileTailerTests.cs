using System.Collections.Concurrent;
using PalServerLauncher.Core;

namespace PalServerLauncher.Tests;

public class FileTailerTests
{
    [Fact]
    public async Task Tails_lines_appended_after_construction()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tail_{Guid.NewGuid():N}.log");
        File.WriteAllText(path, "old line before tailing\n"); // should NOT be captured (fromStart:false)
        var captured = new ConcurrentQueue<string>();

        using (var tailer = new FileTailer(path, captured.Enqueue, fromStart: false))
        {
            await Task.Delay(150);
            File.AppendAllText(path, "progress 1\nprogress 2\n");
            await WaitUntil(() => captured.Count >= 2, TimeSpan.FromSeconds(3));
        }

        Assert.Equal(new[] { "progress 1", "progress 2" }, captured.ToArray());
    }

    [Fact]
    public async Task FromStart_reads_existing_content()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tail_{Guid.NewGuid():N}.log");
        File.WriteAllText(path, "line A\nline B\n");
        var captured = new ConcurrentQueue<string>();

        using (new FileTailer(path, captured.Enqueue, fromStart: true))
        {
            await WaitUntil(() => captured.Count >= 2, TimeSpan.FromSeconds(3));
        }

        Assert.Contains("line A", captured);
        Assert.Contains("line B", captured);
        File.Delete(path);
    }

    [Fact]
    public async Task Missing_file_does_not_throw_and_picks_up_when_created()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tail_{Guid.NewGuid():N}.log");
        var captured = new ConcurrentQueue<string>();

        using (new FileTailer(path, captured.Enqueue, fromStart: true))
        {
            await Task.Delay(150);
            File.WriteAllText(path, "appeared later\n");
            await WaitUntil(() => captured.Count >= 1, TimeSpan.FromSeconds(3));
        }

        Assert.Contains("appeared later", captured);
        File.Delete(path);
    }

    private static async Task WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(50);
        }
    }
}
