using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Logging;

namespace SolidWorksMcp.Server.Hosting;

/// <summary>
/// A tiny daily-rolling file logger (Microsoft.Extensions.Logging has none). Claude Code hides server stderr unless run
/// with --debug, so this file is where problems can be investigated: %LOCALAPPDATA%\solidworks-mcp\logs.
/// Keeps 7 days.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _dir;
    private readonly LogLevel _min;
    private readonly BlockingCollection<string> _queue = new(10_000);
    private readonly Thread _writer;

    public FileLoggerProvider(string dir, LogLevel min)
    {
        _dir = dir;
        _min = min;
        Directory.CreateDirectory(dir);
        Cleanup();
        _writer = new Thread(WriteLoop) { IsBackground = true, Name = "log writer" };
        _writer.Start();
    }

    public string CurrentFile => Path.Combine(_dir, $"solidworks-mcp-{DateTime.Now:yyyyMMdd}.log");

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    private void Enqueue(string line) => _queue.TryAdd(line);

    private void WriteLoop()
    {
        foreach (var line in _queue.GetConsumingEnumerable())
        {
            try { File.AppendAllText(CurrentFile, line + Environment.NewLine, Encoding.UTF8); }
            catch { /* never let logging break the server */ }
        }
    }

    private void Cleanup()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(_dir, "solidworks-mcp-*.log"))
                if (File.GetLastWriteTime(f) < DateTime.Now.AddDays(-7)) File.Delete(f);
        }
        catch { /* best effort */ }
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        _writer.Join(TimeSpan.FromSeconds(2));
    }

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= provider._min && logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var sb = new StringBuilder();
            sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture))
              .Append(' ').Append(logLevel.ToString().ToUpperInvariant()[..4]).Append(' ').Append(category).Append(": ")
              .Append(formatter(state, exception));
            if (exception is not null) sb.Append(Environment.NewLine).Append(exception);
            provider.Enqueue(sb.ToString());
        }
    }
}
