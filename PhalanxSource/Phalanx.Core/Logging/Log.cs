// Port of SpartanEngine source/logging/Log.h/.cpp @ 4c07baa.
// Every line is "[HH:mm:ss]: Caller.Method: text" like spartan's FormatBuffer/WriteBuffer, kept in a bounded history
// (GetRecentEntries), handed to the attached ILogger and/or written to log.txt, rotated to log_previous.txt on the
// first line of a session.
// Differences:
// - The caller comes from [CallerFilePath]/[CallerMemberName] (spartan: __FUNCTION__). The type is taken from the file
//   name, so partial class files (RHI_Device.Frame.cs) still read as RHI_Device.
// - No fixed buffers: spartan formats into 2048 byte buffers (16 of them, each behind a mutex), here the line is a string
//   and one lock serializes the history, the sinks and the file.
// - Lines also go to the console (warnings yellow, errors red) and to System.Diagnostics.Debug (the Visual Studio output
//   window). Spartan only writes to the file and the attached logger. Console output can be turned off.
// - LogEntry is an event besides ILogger, for listeners that don't want to own the single logger slot. Like ILogger it
//   runs under the log lock, so a handler must not log.
// - Clear doesn't rotate the log: spartan renames log.txt to log_previous.txt there too (Log.cpp:124), which replaces
//   the previous run's log, the one record the rotation exists to keep, with the current session's.
// - The file location can be set (LogFilePath) and Initialize takes the headless flag instead of reading the engine.
// - Error lines are flushed past the os file cache to the disk (spartan's writes stay in the cache): a gpu loss can
//   take the whole machine down right after it is logged.
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;

namespace Phalanx;

public enum LogType
{
    Info,
    Warning,
    Error
}

// spartan: LogCmd
public readonly record struct LogCmd(string text, LogType type);

public static class Log
{
    private const int history_max_count = 2000;

    private static readonly object s_mutex = new();
    private static readonly List<LogCmd> s_logs = new();       // lines waiting for a logger to be attached
    private static readonly Queue<LogCmd> s_history = new();
    private static ILogger? s_logger;
    private static bool s_log_to_file = true;
    private static bool s_log_to_console = true;
    private static bool s_is_first_log = true;
    private static StreamWriter? s_log_file;
    private static string s_log_file_name          = "log.txt";
    private static string s_log_file_name_previous = "log_previous.txt";
    private static string? s_log_directory;

    // raised for every line, under the log lock
    public static event Action<LogCmd>? LogEntry;

    // headless instances run next to the user's editor in the same folder and must not truncate its log
    public static void Initialize(bool headless = false)
    {
        lock (s_mutex)
        {
            CloseFile();
            s_log_file_name          = headless ? "log_headless.txt" : "log.txt";
            s_log_file_name_previous = headless ? "log_headless_previous.txt" : "log_previous.txt";
            s_is_first_log           = true;
        }
    }

    // where log.txt goes, the working directory by default like spartan
    public static string? LogDirectory
    {
        get { lock (s_mutex) return s_log_directory; }
        set { lock (s_mutex) { CloseFile(); s_log_directory = value; s_is_first_log = true; } }
    }

    public static string LogFilePath { get { lock (s_mutex) return GetPath(s_log_file_name); } }
    public static string LogFilePathPrevious { get { lock (s_mutex) return GetPath(s_log_file_name_previous); } }

    public static void SetLogger(ILogger? logger)
    {
        lock (s_mutex)
        {
            s_logger = logger;

            // flush the log buffer, if needed
            if (s_logger != null && s_logs.Count != 0)
            {
                foreach (LogCmd log in s_logs)
                    s_logger.Log(log.text, log.type);
                s_logs.Clear();
            }
        }
    }

    public static void SetLogToFile(bool log)
    {
        lock (s_mutex)
        {
            s_log_to_file = log;
            if (!log)
                CloseFile();
        }
    }

    public static void SetLogToConsole(bool log) { lock (s_mutex) s_log_to_console = log; }

    public static void Clear()
    {
        lock (s_mutex)
        {
            s_logs.Clear();
            s_history.Clear();
            if (s_log_to_file)
            {
                CloseFile();
                try { File.WriteAllText(GetPath(s_log_file_name), string.Empty); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }

    public static IReadOnlyList<LogCmd> GetRecentEntries(int count)
    {
        lock (s_mutex)
        {
            count = System.Math.Clamp(count, 0, s_history.Count);
            return s_history.Skip(s_history.Count - count).ToArray();
        }
    }

    public static void LogInfo(string message, [CallerMemberName] string caller = "", [CallerFilePath] string caller_file = "") =>
        Write(Format(caller_file, caller, message), LogType.Info);

    public static void LogWarning(string message, [CallerMemberName] string caller = "", [CallerFilePath] string caller_file = "") =>
        Write(Format(caller_file, caller, message), LogType.Warning);

    public static void LogError(string message, [CallerMemberName] string caller = "", [CallerFilePath] string caller_file = "") =>
        Write(Format(caller_file, caller, message), LogType.Error);

    // spartan: FormatBuffer, "function: text"
    public static string Format(string caller_file, string caller, string text)
    {
        string type = GetTypeName(caller_file);
        if (type.Length == 0 && caller.Length == 0)
            return text;
        if (type.Length == 0)
            return $"{caller}: {text}";
        return caller.Length == 0 ? $"{type}: {text}" : $"{type}.{caller}: {text}";
    }

    // RHI_Device.Frame.cs -> RHI_Device (both separators, a path recorded on windows can be read anywhere)
    public static string GetTypeName(string caller_file)
    {
        if (string.IsNullOrEmpty(caller_file))
            return string.Empty;
        int slash = System.Math.Max(caller_file.LastIndexOf('/'), caller_file.LastIndexOf('\\'));
        string name = caller_file[(slash + 1)..];
        int dot = name.IndexOf('.');
        return dot < 0 ? name : name[..dot];
    }

    // spartan: WriteBuffer
    public static void Write(string text, LogType type)
    {
        ArgumentNullException.ThrowIfNull(text);
        string line = $"[{DateTime.Now:HH:mm:ss}]: {text}";
        var cmd = new LogCmd(line, type);

        lock (s_mutex)
        {
            s_history.Enqueue(cmd);
            while (s_history.Count > history_max_count)
                s_history.Dequeue();

            // lines are only buffered for a logger that isn't attached yet, SetLogger replays and clears them
            if (s_logger == null)
                s_logs.Add(cmd);

            if (s_log_to_file || s_logger == null)
                WriteToFile(line, type);

            s_logger?.Log(line, type);
            LogEntry?.Invoke(cmd);

            if (s_log_to_console)
                WriteToConsole(line, type);
            Debug.WriteLine(GetPrefix(type) + line);
        }
    }

    private static string GetPrefix(LogType type) => type switch
    {
        LogType.Info    => "Info: ",
        LogType.Warning => "Warning: ",
        _               => "Error: "
    };

    private static string GetPath(string file_name) => s_log_directory == null ? file_name : Path.Combine(s_log_directory, file_name);

    private static void WriteToFile(string text, LogType type)
    {
        try
        {
            // the log being replaced here is the only record of why the last run died, so it is rotated rather than deleted
            if (s_is_first_log)
            {
                s_is_first_log = false;
                string path = GetPath(s_log_file_name), path_previous = GetPath(s_log_file_name_previous);
                if (File.Exists(path))
                    File.Move(path, path_previous, overwrite: true);
            }

            // the file stays open for the session, the flush keeps every line on disk in case the run dies
            if (s_log_file == null)
            {
                var stream = new FileStream(GetPath(s_log_file_name), FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                s_log_file = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
            }
            s_log_file.Write(GetPrefix(type));
            s_log_file.Write(text);
            s_log_file.Write('\n');
            if (type == LogType.Error)
                (s_log_file.BaseStream as FileStream)?.Flush(flushToDisk: true); // past the os cache: the run may take the machine down
        }
        catch (IOException) { }                 // logging must never throw or log
        catch (UnauthorizedAccessException) { }
    }

    private static void WriteToConsole(string text, LogType type)
    {
        try
        {
            if (type == LogType.Info)
            {
                Console.Out.WriteLine(text);
                return;
            }
            ConsoleColor previous = Console.ForegroundColor;
            Console.ForegroundColor = type == LogType.Warning ? ConsoleColor.Yellow : ConsoleColor.Red;
            Console.Error.WriteLine(text);
            Console.ForegroundColor = previous;
        }
        catch (IOException) { }
    }

    private static void CloseFile()
    {
        s_log_file?.Dispose();
        s_log_file = null;
    }
}
