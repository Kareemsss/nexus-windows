using System;
using System.IO;
using System.Text;

namespace Nexus.Core.Diagnostics;

public enum LogLevel { Debug, Info, Warning, Error }

/// <summary>
/// Minimal, thread-safe rolling file logger. NEXUS deliberately logs no personal content
/// (no notification bodies, track titles or meeting subjects) – only component names and errors.
/// </summary>
public static class Log
{
    private static readonly object Gate = new();
    private static string? _path;
    private const long MaxBytes = 1_000_000;

    public static LogLevel MinimumLevel { get; set; } = LogLevel.Info;

    public static void Initialize(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            _path = Path.Combine(directory, "nexus.log");
            if (File.Exists(_path) && new FileInfo(_path).Length > MaxBytes)
            {
                File.Copy(_path, Path.Combine(directory, "nexus.old.log"), overwrite: true);
                File.Delete(_path);
            }
        }
        catch { _path = null; }
    }

    public static void Debug(string component, string message) => Write(LogLevel.Debug, component, message, null);
    public static void Info(string component, string message) => Write(LogLevel.Info, component, message, null);
    public static void Warn(string component, string message, Exception? ex = null) => Write(LogLevel.Warning, component, message, ex);
    public static void Error(string component, string message, Exception? ex = null) => Write(LogLevel.Error, component, message, ex);

    private static void Write(LogLevel level, string component, string message, Exception? ex)
    {
        if (level < MinimumLevel) return;
        var sb = new StringBuilder()
            .Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append(' ')
            .Append(level.ToString().ToUpperInvariant().PadRight(7)).Append(' ')
            .Append('[').Append(component).Append("] ").Append(message);
        if (ex is not null) sb.Append(" | ").Append(ex.GetType().Name).Append(": ").Append(ex.Message);
        var line = sb.ToString();
        System.Diagnostics.Debug.WriteLine(line);
        if (_path is null) return;
        lock (Gate)
        {
            try { File.AppendAllText(_path, line + Environment.NewLine); } catch { /* logging must never throw */ }
        }
    }
}
