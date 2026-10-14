using Xunit;

namespace Phalanx.Tests;

[CollectionDefinition("log", DisableParallelization = true)]
public class LogCollection { }

// spartan's Log @ 4c07baa: caller prefix, history, the logger slot, the file and its rotation
[Collection("log")]
public class LogTests : IDisposable
{
    private readonly string m_dir = Path.Combine(Path.GetTempPath(), "phalanx-log-" + Guid.NewGuid().ToString("N"));

    private sealed class ListLogger : ILogger
    {
        public readonly List<(string text, LogType type)> lines = new();
        public void Log(string text, LogType type) { lock (lines) lines.Add((text, type)); }
    }

    public LogTests()
    {
        Directory.CreateDirectory(m_dir);
        Log.SetLogger(null);
        Log.Initialize();
        Log.LogDirectory = m_dir;
        Log.SetLogToFile(true);
        Log.SetLogToConsole(false);
        Log.Clear();
    }

    // the log keeps its file open, read it the way another process would (windows refuses FileShare.Read here)
    private static string[] ReadLines(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }

    public void Dispose()
    {
        Log.SetLogger(null);
        Log.LogDirectory = null;
        Log.SetLogToConsole(true);
        Log.Clear();
        Directory.Delete(m_dir, recursive: true);
    }

    [Fact]
    public void Lines_carry_a_timestamp_and_the_caller()
    {
        Log.LogInfo("hello");
        LogCmd line = Log.GetRecentEntries(1).Single();
        Assert.Equal(LogType.Info, line.type);
        Assert.Matches(@"^\[\d\d:\d\d:\d\d\]: LogTests\.Lines_carry_a_timestamp_and_the_caller: hello$", line.text);
    }

    [Theory]
    [InlineData("/src/RHI/RHI_Device.Frame.cs", "RHI_Device")]
    [InlineData(@"C:\src\RHI\RHI_Queue.cs", "RHI_Queue")]
    [InlineData("Log.cs", "Log")]
    [InlineData("", "")]
    public void The_type_comes_from_the_file_name(string file, string type) => Assert.Equal(type, Log.GetTypeName(file));

    [Fact]
    public void Format_handles_a_missing_caller()
    {
        Assert.Equal("text", Log.Format("", "", "text"));
        Assert.Equal("Method: text", Log.Format("", "Method", "text"));
        Assert.Equal("Type: text", Log.Format("Type.cs", "", "text"));
        Assert.Equal("Type.Method: text", Log.Format("/a/Type.cs", "Method", "text"));
    }

    [Fact]
    public void A_logger_gets_the_lines_logged_before_it_was_attached_then_every_new_one()
    {
        Log.LogInfo("early");
        Log.LogWarning("also early");
        var logger = new ListLogger();
        Log.SetLogger(logger);
        Assert.Equal(new[] { LogType.Info, LogType.Warning }, logger.lines.Select(l => l.type));
        Assert.EndsWith("early", logger.lines[0].text);

        Log.LogError("late");
        Assert.Equal(3, logger.lines.Count);
        Assert.Equal(LogType.Error, logger.lines[2].type);

        // the replay buffer was emptied, a second logger only sees new lines
        var second = new ListLogger();
        Log.SetLogger(second);
        Assert.Empty(second.lines);
    }

    [Fact]
    public void The_event_sees_every_line()
    {
        var seen = new List<LogCmd>();
        void Handler(LogCmd cmd) => seen.Add(cmd);
        Log.LogEntry += Handler;
        try
        {
            Log.LogInfo("a");
            Log.LogError("b");
        }
        finally { Log.LogEntry -= Handler; }
        Log.LogInfo("c");
        Assert.Equal(2, seen.Count);
        Assert.Equal(LogType.Error, seen[1].type);
    }

    [Fact]
    public void The_history_is_bounded_and_returns_the_most_recent_lines()
    {
        for (int i = 0; i < 2100; i++)
            Log.LogInfo($"line {i}");
        Assert.Equal(2000, Log.GetRecentEntries(5000).Count);
        IReadOnlyList<LogCmd> last = Log.GetRecentEntries(2);
        Assert.EndsWith("line 2098", last[0].text);
        Assert.EndsWith("line 2099", last[1].text);
        Assert.Empty(Log.GetRecentEntries(0));
        Log.Clear();
        Assert.Empty(Log.GetRecentEntries(10));
    }

    [Fact]
    public void Lines_from_many_threads_are_neither_lost_nor_torn()
    {
        var logger = new ListLogger();
        Log.SetLogger(logger);
        const int threads = 8, per_thread = 200;
        Parallel.For(0, threads, new ParallelOptions { MaxDegreeOfParallelism = threads }, t =>
        {
            for (int i = 0; i < per_thread; i++)
                Log.LogInfo($"thread {t} line {i}");
        });
        Assert.Equal(threads * per_thread, logger.lines.Count);

        // each thread's lines arrive in order and the file has every line whole
        for (int t = 0; t < threads; t++)
        {
            int[] order = logger.lines.Select(l => l.text).Where(l => l.Contains($"thread {t} line ")).Select(l => int.Parse(l[(l.LastIndexOf(' ') + 1)..])).ToArray();
            Assert.Equal(Enumerable.Range(0, per_thread), order);
        }
        string[] file = ReadLines(Log.LogFilePath);
        Assert.Equal(threads * per_thread, file.Count(l => l.StartsWith("Info: [", StringComparison.Ordinal) && l.Contains(": thread ")));
    }

    [Fact]
    public void The_file_has_spartans_prefixes_and_the_previous_log_is_kept()
    {
        Log.LogInfo("first");
        Log.LogWarning("second");
        Log.LogError("third");
        string[] lines = ReadLines(Log.LogFilePath);
        Assert.Equal(3, lines.Length);
        Assert.StartsWith("Info: [", lines[0]);
        Assert.StartsWith("Warning: [", lines[1]);
        Assert.StartsWith("Error: [", lines[2]);
        Assert.Equal("log.txt", Path.GetFileName(Log.LogFilePath));

        // a new session moves the log to log_previous.txt on its first line
        Log.Initialize();
        Log.LogInfo("new session");
        Assert.Equal(lines, ReadLines(Log.LogFilePathPrevious));
        Assert.Single(ReadLines(Log.LogFilePath));

        // clearing truncates the log but leaves the previous one alone
        Log.Clear();
        Assert.Empty(ReadLines(Log.LogFilePath));
        Assert.Equal(lines, ReadLines(Log.LogFilePathPrevious));
    }

    [Fact]
    public void Headless_sessions_use_their_own_files()
    {
        Log.Initialize(headless: true);
        try
        {
            Log.LogInfo("headless");
            Assert.Equal("log_headless.txt", Path.GetFileName(Log.LogFilePath));
            Assert.Equal("log_headless_previous.txt", Path.GetFileName(Log.LogFilePathPrevious));
            Assert.True(File.Exists(Path.Combine(m_dir, "log_headless.txt")));
            Assert.DoesNotContain(ReadLines(Path.Combine(m_dir, "log.txt")), l => l.Contains("headless"));
        }
        finally { Log.Initialize(); }
    }

    [Fact]
    public void Without_a_logger_lines_go_to_the_file_even_when_file_logging_is_off()
    {
        // spartan: log_to_file || !logger (Log.cpp:187)
        Log.SetLogToFile(false);
        Log.LogInfo("still written");
        Assert.Single(ReadLines(Log.LogFilePath));

        Log.SetLogger(new ListLogger());
        Log.LogInfo("not written");
        Assert.Single(ReadLines(Log.LogFilePath));
        Log.SetLogToFile(true);
    }

    [Fact]
    public void Console_output_goes_to_stdout_and_stderr()
    {
        TextWriter out_previous = Console.Out, err_previous = Console.Error;
        var out_writer = new StringWriter();
        var err_writer = new StringWriter();
        Console.SetOut(out_writer);
        Console.SetError(err_writer);
        Log.SetLogToConsole(true);
        try
        {
            Log.LogInfo("to stdout");
            Log.LogWarning("to stderr");
            Log.LogError("also stderr");
        }
        finally
        {
            Log.SetLogToConsole(false);
            Console.SetOut(out_previous);
            Console.SetError(err_previous);
        }
        Assert.Contains("to stdout", out_writer.ToString());
        Assert.DoesNotContain(": to stderr", out_writer.ToString());
        Assert.DoesNotContain(": to stdout", err_writer.ToString());
        Assert.Contains("to stderr", err_writer.ToString());
        Assert.Contains("also stderr", err_writer.ToString());
    }
}
