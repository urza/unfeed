using Microsoft.Extensions.Logging;
namespace Feed.Core.Infrastructure;
public sealed class LogSink(InstancePaths paths, string process, LogLevel minimum = LogLevel.Information, bool quiet = false) : ILoggerProvider
{
    public LogLevel Minimum => minimum;
    public ILogger CreateLogger(string categoryName) => new Sink(this, categoryName.Split('.').Last()); public void Dispose() { }
    public void Write(LogLevel level, string category, string message, Exception? error = null, bool emitConsole = true)
    {
        if (level < minimum) return;
        var code = level switch { LogLevel.Trace => "TRC", LogLevel.Debug => "DBG", LogLevel.Warning => "WRN", LogLevel.Error => "ERR", LogLevel.Critical => "CRT", _ => "INF" };
        if (error is not null) { message += $" | {error.GetType().Name}: {error.Message}"; if (error.GetBaseException() != error) message += " | cause: " + error.GetBaseException().Message; }
        message = message.Replace('\r', ' ').Replace('\n', ' ');
        try
        {
            if (emitConsole && (!quiet || level >= LogLevel.Warning)) Console.Error.WriteLine($"{DateTime.Now:HH:mm:ss} {code} {category}: {message}");
            var file = paths.Get("logs", $"feed-{process}.log");
            // A separate permanent inode coordinates rotation across CLI processes.
            ResourceLock? ownership = null;
            for (int i = 0; i < 20 && ownership is null; i++) { ownership = ResourceLock.Try(paths, "log-" + process); if (ownership is null) Thread.Sleep(10); }
            if (ownership is null) return;
            using (ownership) { if (File.Exists(file) && new FileInfo(file).Length >= 5000000) File.Move(file, file + ".1", true); File.AppendAllText(file, $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} {code} {category}: {message}\n"); }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
    sealed class Sink(LogSink owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => level >= owner.Minimum;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> formatter) => owner.Write(level, category, formatter(state, error), error);
    }
}
