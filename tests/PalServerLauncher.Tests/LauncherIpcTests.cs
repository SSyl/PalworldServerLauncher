using System.Diagnostics;
using System.IO;
using PalServerLauncher.Core;

namespace PalServerLauncher.Tests;

/// <summary>
/// The CLI-to-running-launcher pipe, exercised over a real named pipe rather than a mock: the framing, the
/// progress streaming and the "nothing is listening" answer are the whole point of the transport, and none of
/// them are observable without an actual pipe. Each test uses a pipe name derived from a unique fake data root.
/// </summary>
public class LauncherIpcTests
{
    private static string UniquePipeName() =>
        LauncherIpc.PipeNameFor(Path.Combine(Path.GetTempPath(), "pal-ipc-tests", Guid.NewGuid().ToString("N")));

    /// <summary>Wait for the listener's pipe to actually exist, so a test never races Start().</summary>
    private static async Task WaitForPipeAsync(string pipeName)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(5))
        {
            if (File.Exists(@"\\.\pipe\" + pipeName))
                return;
            await Task.Delay(20);
        }
        Assert.Fail($"Pipe {pipeName} never appeared.");
    }

    [Fact]
    public void Pipe_name_is_stable_and_scoped_to_the_data_root()
    {
        var a = LauncherIpc.PipeNameFor(@"C:\Games\LauncherA");
        var b = LauncherIpc.PipeNameFor(@"C:\Games\LauncherB");

        Assert.Equal(a, LauncherIpc.PipeNameFor(@"C:\Games\LauncherA"));
        Assert.NotEqual(a, b);
        Assert.StartsWith("PalworldServerLauncher.", a);
        Assert.DoesNotContain('\\', a); // a backslash would make it an invalid pipe name
    }

    [Theory]
    [InlineData(@"C:\Games\Launcher\")]
    [InlineData(@"c:\games\launcher")]
    [InlineData(@"C:\Games\Other\..\Launcher")]
    public void Pipe_name_ignores_case_trailing_separators_and_relative_segments(string variant)
    {
        // Both ends compute this independently, so equivalent spellings of one folder must agree.
        Assert.Equal(LauncherIpc.PipeNameFor(@"C:\Games\Launcher"), LauncherIpc.PipeNameFor(variant));
    }

    [Fact]
    public async Task Nothing_listening_reports_null_so_the_caller_can_fall_back()
    {
        var outcome = await LauncherIpcClient.SendAsync(UniquePipeName(), new StopRequest(StopKind.Graceful), _ => { });
        Assert.Null(outcome);
    }

    [Fact]
    public async Task A_request_reaches_the_handler_and_its_outcome_comes_back()
    {
        var pipeName = UniquePipeName();
        StopRequest? received = null;

        using var server = new LauncherIpcServer(pipeName, (request, report) =>
        {
            received = request;
            report("Saving and shutting down...");
            return Task.FromResult(new StopOutcome(true, "Server stopped."));
        }, _ => { });
        server.Start();
        await WaitForPipeAsync(pipeName);

        var progress = new List<string>();
        var outcome = await LauncherIpcClient.SendAsync(pipeName, new StopRequest(StopKind.Countdown, 60), progress.Add);

        Assert.Equal(new StopRequest(StopKind.Countdown, 60), received);
        Assert.Equal(new StopOutcome(true, "Server stopped."), outcome);
        Assert.Equal(["Saving and shutting down..."], progress);
    }

    [Fact]
    public async Task A_failed_stop_comes_back_as_a_failure_not_an_exception()
    {
        var pipeName = UniquePipeName();
        using var server = new LauncherIpcServer(pipeName,
            (_, _) => Task.FromResult(new StopOutcome(false, "No server is running.")),
            _ => { });
        server.Start();
        await WaitForPipeAsync(pipeName);

        var outcome = await LauncherIpcClient.SendAsync(pipeName, new StopRequest(StopKind.Kill), _ => { });

        Assert.NotNull(outcome);
        Assert.False(outcome.Succeeded);
        Assert.Equal("No server is running.", outcome.Message);
    }

    [Fact]
    public async Task A_multi_line_message_stays_on_one_line_so_framing_survives()
    {
        var pipeName = UniquePipeName();
        using var server = new LauncherIpcServer(pipeName, (_, report) =>
        {
            report("first\nsecond");
            return Task.FromResult(new StopOutcome(true, "done\r\nreally"));
        }, _ => { });
        server.Start();
        await WaitForPipeAsync(pipeName);

        var progress = new List<string>();
        var outcome = await LauncherIpcClient.SendAsync(pipeName, new StopRequest(StopKind.Graceful), progress.Add);

        Assert.Equal(["first second"], progress);
        Assert.Equal(new StopOutcome(true, "done really"), outcome);
    }

    [Fact]
    public async Task The_listener_serves_one_request_after_another()
    {
        var pipeName = UniquePipeName();
        var served = 0;
        using var server = new LauncherIpcServer(pipeName,
            (_, _) => Task.FromResult(new StopOutcome(true, $"stop #{Interlocked.Increment(ref served)}")),
            _ => { });
        server.Start();
        await WaitForPipeAsync(pipeName);

        var first = await LauncherIpcClient.SendAsync(pipeName, new StopRequest(StopKind.Graceful), _ => { });
        await WaitForPipeAsync(pipeName); // the loop re-creates the instance between connections
        var second = await LauncherIpcClient.SendAsync(pipeName, new StopRequest(StopKind.Graceful), _ => { });

        Assert.Equal("stop #1", first?.Message);
        Assert.Equal("stop #2", second?.Message);
    }

    [Fact]
    public async Task A_disposed_listener_stops_answering()
    {
        var pipeName = UniquePipeName();
        var server = new LauncherIpcServer(pipeName,
            (_, _) => Task.FromResult(new StopOutcome(true, "stopped")), _ => { });
        server.Start();
        await WaitForPipeAsync(pipeName);
        server.Dispose();

        // Give the cancelled accept loop a moment to tear the pipe instance down.
        await Task.Delay(200);
        Assert.Null(await LauncherIpcClient.SendAsync(pipeName, new StopRequest(StopKind.Graceful), _ => { }));
    }
}
