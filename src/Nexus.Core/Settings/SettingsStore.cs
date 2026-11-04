using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using Nexus.Core.Diagnostics;

namespace Nexus.Core.Settings;

/// <summary>
/// Owns the single <see cref="NexusSettings"/> instance. Mutations go through <see cref="Update"/>, which raises
/// <see cref="Changed"/> immediately (live preview) and persists with a short debounce using an atomic write.
/// </summary>
public sealed class SettingsStore : IDisposable
{
    private readonly string _path;
    private readonly Timer _saveTimer;
    private readonly object _gate = new();

    public SettingsStore(string path)
    {
        _path = path;
        Current = Load(path);
        _saveTimer = new Timer(_ => SaveNow(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public NexusSettings Current { get; }
    public string FilePath => _path;

    /// <summary>Raised on the calling thread after every update. Argument is the changed section name.</summary>
    public event EventHandler<string>? Changed;

    public void Update(string section, Action<NexusSettings> mutate)
    {
        lock (_gate)
        {
            mutate(Current);
            Current.Normalize();
        }
        Changed?.Invoke(this, section);
        _saveTimer.Change(400, Timeout.Infinite);
    }

    public void SaveNow()
    {
        try
        {
            string json;
            lock (_gate) json = JsonSerializer.Serialize(Current, NexusSettings.JsonOptions);
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _path, overwrite: true); // atomic replace on NTFS: never leaves a half-written file
        }
        catch (Exception ex)
        {
            Log.Error("Settings", "Failed to save settings", ex);
        }
    }

    private static NexusSettings Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var s = JsonSerializer.Deserialize<NexusSettings>(File.ReadAllText(path), NexusSettings.JsonOptions);
                if (s is not null) { s.Normalize(); return s; }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Settings", "Settings file was unreadable; using defaults (a backup was kept).", ex);
            try { File.Copy(path, path + ".corrupt", overwrite: true); } catch { /* best effort */ }
        }
        var d = new NexusSettings();
        d.Normalize();
        return d;
    }

    public void Dispose()
    {
        _saveTimer.Dispose();
        SaveNow();
    }
}
