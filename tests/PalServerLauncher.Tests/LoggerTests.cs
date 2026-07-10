using PalServerLauncher.Logging;

namespace PalServerLauncher.Tests;

public class LoggerTests
{
    [Fact]
    public void EchoToConsole_writes_lines_to_stdout()
    {
        var original = Console.Out;
        var captured = new StringWriter();
        Console.SetOut(captured);
        try
        {
            var logger = new Logger(verbose: false, echoToConsole: true);
            logger.Info("cli status line");

            var text = captured.ToString();
            Assert.Contains("cli status line", text);
            Assert.Contains("[INFO", text); // includes the formatted tag
        }
        finally
        {
            Console.SetOut(original);
        }
    }

    [Fact]
    public void Without_echo_nothing_goes_to_stdout()
    {
        var original = Console.Out;
        var captured = new StringWriter();
        Console.SetOut(captured);
        try
        {
            var logger = new Logger(verbose: false, echoToConsole: false);
            logger.Info("should not appear on stdout");

            Assert.DoesNotContain("should not appear on stdout", captured.ToString());
        }
        finally
        {
            Console.SetOut(original);
        }
    }
}
