using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
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
    public async Task A_client_that_sends_nothing_does_not_kill_the_listener()
    {
        // The listener is single-instance, so losing the accept loop sends every later --stop-server down the
        // standalone path, which is the crash-relaunch this whole feature exists to avoid.
        var pipeName = UniquePipeName();
        using var server = new LauncherIpcServer(pipeName,
            (_, _) => Task.FromResult(new StopOutcome(true, "stopped")), _ => { },
            requestReadTimeout: TimeSpan.FromMilliseconds(300));
        server.Start();
        await WaitForPipeAsync(pipeName);

        await using (var silent = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
        {
            await silent.ConnectAsync(2000);
            await Task.Delay(600); // outlast the read timeout without sending a request
        }

        await WaitForPipeAsync(pipeName); // the loop must have come back around
        var outcome = await LauncherIpcClient.SendAsync(pipeName, new StopRequest(StopKind.Graceful), _ => { });
        Assert.Equal(new StopOutcome(true, "stopped"), outcome);
    }

    [Fact]
    public async Task A_client_that_stops_reading_costs_one_timeout_not_the_listener()
    {
        var pipeName = UniquePipeName();
        var handlerFinished = new TaskCompletionSource();
        using var server = new LauncherIpcServer(pipeName, (_, report) =>
        {
            // More progress than the pipe can hand to a client that never reads.
            for (var i = 0; i < 50; i++)
                report($"line {i}");
            handlerFinished.TrySetResult();
            return Task.FromResult(new StopOutcome(true, "stopped"));
        }, _ => { }, writeTimeout: TimeSpan.FromMilliseconds(300));
        server.Start();
        await WaitForPipeAsync(pipeName);

        await using (var deaf = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
        {
            await deaf.ConnectAsync(2000);
            var writer = new StreamWriter(deaf) { AutoFlush = true };
            await writer.WriteLineAsync("stop");

            // The handler must not block on a client that isn't draining its progress.
            await handlerFinished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }

        await WaitForPipeAsync(pipeName);
        var outcome = await LauncherIpcClient.SendAsync(pipeName, new StopRequest(StopKind.Graceful), _ => { });
        Assert.Equal(new StopOutcome(true, "stopped"), outcome);
    }

    [Fact]
    public async Task A_squatted_pipe_name_is_refused_rather_than_shared()
    {
        // \\.\pipe has no per-user namespace and the name is derivable from the install path, so the listener
        // must claim it outright instead of adding an instance next to someone else's.
        var pipeName = UniquePipeName();
        await using var squatter = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 2);

        var logged = new List<string>();
        using var server = new LauncherIpcServer(pipeName,
            (_, _) => Task.FromResult(new StopOutcome(true, "stopped")), logged.Add);
        server.Start();

        // It should give up rather than serve alongside the squatter.
        var deadline = Stopwatch.StartNew();
        while (logged.Count == 0 && deadline.Elapsed < TimeSpan.FromSeconds(5))
            await Task.Delay(20);

        Assert.Contains(logged, l => l.Contains("Couldn't claim", StringComparison.Ordinal));
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
