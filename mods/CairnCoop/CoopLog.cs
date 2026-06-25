using System;
using System.Collections.Generic;

namespace CairnCoop;

/// <summary>
/// The mod's logging sink: console (MelonLoader logger) + a per-instance file + an in-memory tail the
/// F4 panel reads. <see cref="Log"/> is called from BOTH the game thread and the net-pump thread, so the
/// three sinks are serialised under one lock. MelonLoader's Latest.log interleaves when several instances
/// run, so each instance also writes its own file: MelonLoader/CairnCoop/&lt;start&gt;_&lt;pid&gt;.log.
/// </summary>
public sealed class CoopLog : IDisposable
{
    private readonly Action<string> _consoleSink; // LoggerInstance.Msg
    private readonly Action<string> _consoleWarn;  // LoggerInstance.Warning
    private readonly object _logLock = new(); // Log() is called from both the game and net threads
    private readonly Queue<string> _logTail = new();
    private System.IO.StreamWriter _fileLog;

    public CoopLog(Action<string> consoleSink, Action<string> consoleWarn)
    {
        _consoleSink = consoleSink;
        _consoleWarn = consoleWarn;
        OpenInstanceLog();
    }

    /// <summary>Read-only view of the in-memory tail for the F4 Log tab.</summary>
    public IEnumerable<string> Tail => _logTail;

    /// <summary>
    /// MelonLoader's Latest.log interleaves when several instances run, so each instance
    /// also writes its own file: MelonLoader/CairnCoop/&lt;start&gt;_&lt;pid&gt;.log.
    /// </summary>
    private void OpenInstanceLog()
    {
        try
        {
            var dir = System.IO.Path.Combine(MelonLoader.Utils.MelonEnvironment.MelonLoaderDirectory, "CairnCoop");
            System.IO.Directory.CreateDirectory(dir);
            var path = System.IO.Path.Combine(dir,
                $"{DateTime.Now:yyyyMMdd-HHmmss}_pid{Environment.ProcessId}.log");
            _fileLog = new System.IO.StreamWriter(path, append: false) { AutoFlush = true };
            _fileLog.WriteLine($"# CairnCoop instance log, pid {Environment.ProcessId}, " +
                $"autostart={Environment.GetEnvironmentVariable("CAIRNCOOP_AUTOHOST") == "1"}/{Environment.GetEnvironmentVariable("CAIRNCOOP_AUTOJOIN")}");
        }
        catch (Exception e)
        {
            _consoleWarn("instance log unavailable: " + e.Message);
        }
    }

    public void Log(string message)
    {
        // Called from both the game thread and the net-pump thread — serialise the shared sinks.
        lock (_logLock)
        {
            _consoleSink(message);
            _fileLog?.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {message}");
            _logTail.Enqueue($"[{DateTime.Now:HH:mm:ss}] {message}");
            while (_logTail.Count > 100)
                _logTail.Dequeue();
        }
    }

    /// <summary>Write a raw line to the file sink only (no console, no tail) — used for the 10 s perf
    /// snapshot, which already logs a separate summary to the console. Serialised against <see cref="Log"/>.</summary>
    public void WriteFileLine(string line)
    {
        lock (_logLock)
            _fileLog?.WriteLine(line);
    }

    public void Dispose()
    {
        _fileLog?.Dispose();
        _fileLog = null;
    }
}
