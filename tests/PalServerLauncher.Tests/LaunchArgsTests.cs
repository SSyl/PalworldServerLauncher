using PalServerLauncher.Config;
using PalServerLauncher.Core;

namespace PalServerLauncher.Tests;

public class LaunchArgsTests
{
    [Fact]
    public void Defaults_include_perf_and_stdout_capture_but_not_optional_args()
    {
        var args = ServerController.BuildLaunchArgs(new LauncherConfig(), queryPort: 27015);

        Assert.Contains("-useperfthreads", args);
        Assert.Contains("-stdout", args);
        Assert.Contains("-FullStdOutLogOutput", args);
        Assert.Contains("-port=8211", args);
        Assert.Contains("-QueryPort=27015", args);
        // Optional args omitted at their unset values -> don't override the ini.
        Assert.DoesNotContain(args, a => a.StartsWith("-players"));
        Assert.DoesNotContain("-publiclobby", args);
        Assert.DoesNotContain(args, a => a.StartsWith("-NumberOfWorkerThreadsServer"));
    }

    [Fact]
    public void Optional_args_appear_when_set()
    {
        var config = new LauncherConfig
        {
            ServerPort = 8000,
            MaxPlayers = 16,
            WorkerThreads = 8,
            CommunityServer = true,
            PublicIp = "1.2.3.4",
            PublicPortArg = 9000,
            LogFormat = "Json",
            ExtraServerArgs = "-EpicApp=PalServer -custom",
        };

        var args = ServerController.BuildLaunchArgs(config, queryPort: 27016);

        Assert.Contains("-port=8000", args);
        Assert.Contains("-players=16", args);
        Assert.Contains("-NumberOfWorkerThreadsServer=8", args);
        Assert.Contains("-publiclobby", args);
        Assert.Contains("-publicip=1.2.3.4", args);
        Assert.Contains("-publicport=9000", args);
        Assert.Contains("-logformat=Json", args);
        Assert.Contains("-EpicApp=PalServer", args);
        Assert.Contains("-custom", args);
    }

    [Fact]
    public void Extra_args_split_on_any_whitespace()
    {
        var config = new LauncherConfig { ExtraServerArgs = "-a\n-b\t-c  -d" };
        var args = ServerController.BuildLaunchArgs(config, queryPort: 27015);

        Assert.Contains("-a", args);
        Assert.Contains("-b", args);
        Assert.Contains("-c", args);
        Assert.Contains("-d", args);
    }

    [Fact]
    public void Worker_threads_require_performance_threads()
    {
        var config = new LauncherConfig { PerformanceThreads = false, WorkerThreads = 8 };
        var args = ServerController.BuildLaunchArgs(config, queryPort: 27015);

        Assert.DoesNotContain("-useperfthreads", args);
        Assert.DoesNotContain(args, a => a.StartsWith("-NumberOfWorkerThreadsServer"));
        // stdout capture is still always present
        Assert.Contains("-stdout", args);
    }
}
