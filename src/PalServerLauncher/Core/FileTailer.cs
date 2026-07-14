using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PalServerLauncher.Core;

/// <summary>
/// Polls a growing text file and reports newly-appended lines, used to mirror SteamCMD's
/// console_log.txt into its UI tab while SteamCMD keeps the file open. (The game server's output is
/// captured live from its stdout, not a file, Palworld writes no log file.) Opens shared read/write,
/// tolerates the file not existing yet, and resets if the file is truncated/rotated. Best-effort:
/// transient IO errors are ignored.
/// </summary>
public sealed class FileTailer : IDisposable
{
    private const int MaxReadChunk = 256 * 1024; // bound per-tick reads; catch up over subsequent ticks

    private readonly string _path;
    private readonly Action<string> _onLine;
    private readonly CancellationTokenSource _cts = new();
    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
    private long _position;
    private string _remainder = "";

    /// <param name="fromStart">Read the whole existing file first (true) or only content appended after construction (false).</param>
    public FileTailer(string path, Action<string> onLine, bool fromStart = true)
    {
        _path = path;
        _onLine = onLine;
        _position = !fromStart && File.Exists(path) ? new FileInfo(path).Length : 0;
        _ = LoopAsync(_cts.Token);
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                ReadNewContent();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // File locked, mid-write, or a transient access denial: retry next tick rather than faulting
                // this fire-and-forget loop (which would silently stop tailing).
            }

            try
            {
                await Task.Delay(500, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void ReadNewContent()
    {
        if (!File.Exists(_path))
            return;

        using var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (fs.Length < _position)
        {
            _position = 0; // truncated or rotated
            _remainder = "";
            _decoder.Reset();
        }
        if (fs.Length <= _position)
            return;

        fs.Seek(_position, SeekOrigin.Begin);
        // Bounded read: never allocate the whole outstanding delta at once (and never overflow int).
        var toRead = (int)Math.Min(fs.Length - _position, MaxReadChunk);
        var buffer = new byte[toRead];
        var read = fs.Read(buffer, 0, toRead);
        _position += read;

        // A stateful Decoder holds any partial multibyte UTF-8 sequence across chunk/tick boundaries.
        var chars = new char[_decoder.GetCharCount(buffer, 0, read, flush: false)];
        var charCount = _decoder.GetChars(buffer, 0, read, chars, 0, flush: false);
        _remainder += new string(chars, 0, charCount);

        var parts = _remainder.Split('\n');
        for (var i = 0; i < parts.Length - 1; i++)
            _onLine(parts[i].TrimEnd('\r'));
        _remainder = parts[^1]; // hold the trailing partial line until more arrives
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
