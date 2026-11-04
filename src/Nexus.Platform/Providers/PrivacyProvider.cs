using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using Nexus.Core.Activities;
using Nexus.Core.Color;
using Nexus.Core.Content;
using Nexus.Core.Diagnostics;
using Nexus.Core.Plugins;
using Nexus.Platform.Interop;
using FileVersionInfo = System.Diagnostics.FileVersionInfo;

namespace Nexus.Platform.Providers;

/// <summary>
/// Microphone and camera usage, read from the same registry store Windows uses for its own privacy
/// indicators (CapabilityAccessManager\ConsentStore). An app is using a device while its LastUsedTimeStop
/// value is 0. Changes are delivered by RegNotifyChangeKeyValue – no polling.
///
/// Limitation: only usage by apps running as the current user is visible here (system services using the
/// microphone are recorded under HKLM and are not shown).
/// </summary>
public sealed partial class PrivacyProvider : ProviderBase
{
    private const string StoreRoot = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\";
    private static readonly ConcurrentDictionary<string, string> NameCache = new(StringComparer.OrdinalIgnoreCase);

    private Thread? _thread;
    private readonly Dictionary<PrivacyResource, IReadOnlyList<PrivacyUser>> _state = new();

    public override string Id => "privacy";
    public override string DisplayName => "Microphone & camera";
    public override string Description => "Persistent indicator while any app uses your microphone or camera, including which app.";

    [GeneratedRegex("(?<=[a-z])(?=[A-Z])")]
    private static partial Regex CamelSplit();

    protected override Task OnStartAsync(CancellationToken ct)
    {
        using (var probe = Registry.CurrentUser.OpenSubKey(StoreRoot + "microphone"))
        {
            if (probe is null)
            {
                SetStatus(new ProviderStatus(ProviderState.Unavailable, "This Windows build does not expose capability usage."));
                return Task.CompletedTask;
            }
        }
        Scan(PrivacyResource.Microphone, announce: false);
        Scan(PrivacyResource.Camera, announce: false);
        _thread = new Thread(() => WatchLoop(ct)) { IsBackground = true, Name = "NEXUS privacy watcher" };
        _thread.Start();
        return Task.CompletedTask;
    }

    /// <summary>
    /// RegNotifyChangeKeyValue registrations belong to the registering thread, so a dedicated thread arms
    /// one notification per key, waits, rescans the key that fired and re-arms it.
    /// </summary>
    private void WatchLoop(CancellationToken ct)
    {
        var resources = new[] { PrivacyResource.Microphone, PrivacyResource.Camera };
        var keys = new RegistryKey?[resources.Length];
        var events = new AutoResetEvent[resources.Length];
        try
        {
            for (int i = 0; i < resources.Length; i++)
            {
                keys[i] = Registry.CurrentUser.OpenSubKey(StoreRoot + KeyName(resources[i]), false);
                events[i] = new AutoResetEvent(false);
                Arm(keys[i], events[i]);
            }
            var handles = events.Cast<WaitHandle>().Append(ct.WaitHandle).ToArray();
            while (!ct.IsCancellationRequested)
            {
                int fired = WaitHandle.WaitAny(handles);
                if (fired == handles.Length - 1 || fired < 0) break;
                Thread.Sleep(150); // apps write Start and Stop values separately; let them land together
                Arm(keys[fired], events[fired]);
                Guard("scan", () => Scan(resources[fired], announce: true));
            }
        }
        catch (Exception ex) { Log.Error(Id, "Privacy watcher stopped", ex); }
        finally
        {
            foreach (var k in keys) k?.Dispose();
            foreach (var e in events) e?.Dispose();
        }
    }

    private static void Arm(RegistryKey? key, AutoResetEvent evt)
    {
        if (key is null) return;
        NativeMethods.RegNotifyChangeKeyValue(key.Handle, true,
            NativeMethods.REG_NOTIFY_CHANGE_LAST_SET | NativeMethods.REG_NOTIFY_CHANGE_NAME, evt.SafeWaitHandle, true);
    }

    private static string KeyName(PrivacyResource r) => r == PrivacyResource.Microphone ? "microphone" : "webcam";

    private void Scan(PrivacyResource resource, bool announce)
    {
        var users = ReadUsers(resource);
        IReadOnlyList<PrivacyUser> previous;
        lock (_state) { previous = _state.TryGetValue(resource, out var p) ? p : Array.Empty<PrivacyUser>(); _state[resource] = users; }

        string id = resource == PrivacyResource.Microphone ? "privacy.mic" : "privacy.cam";
        var color = resource == PrivacyResource.Microphone ? SemanticColors.Privacy : SemanticColors.Camera;
        var glyph = resource == PrivacyResource.Microphone ? Glyphs.Microphone : Glyphs.Camera;
        var noun = resource == PrivacyResource.Microphone ? "Microphone" : "Camera";

        if (users.Count == 0)
        {
            Remove(id);
            return;
        }

        var names = string.Join(", ", users.Select(u => u.AppName).Distinct());
        Post(new Activity
        {
            Id = id,
            SourceId = Id,
            Priority = ActivityPriority.High,
            Category = ActivityCategory.Privacy,
            CanBeForeground = false,
            Content = new PrivacyContent(resource, users),
            Accent = color,
            Indicator = new IndicatorSpec(glyph, color, $"{noun} · {names}"),
        });

        var newcomers = users.Where(u => previous.All(p => !string.Equals(p.AppName, u.AppName, StringComparison.OrdinalIgnoreCase))).ToList();
        if (announce && newcomers.Count > 0)
        {
            Pulse(new PulseSignal(color));
            Post(new Activity
            {
                Id = id + ".event",
                SourceId = Id,
                Priority = ActivityPriority.Medium,
                Category = ActivityCategory.Privacy,
                Duration = TimeSpan.FromSeconds(2.6),
                Content = new GlanceContent(glyph, newcomers[0].AppName, color, $"{noun} in use"),
                Accent = color,
            });
        }
    }

    private static IReadOnlyList<PrivacyUser> ReadUsers(PrivacyResource resource)
    {
        var list = new List<PrivacyUser>();
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(StoreRoot + KeyName(resource), false);
            if (root is null) return list;
            foreach (var sub in root.GetSubKeyNames())
            {
                if (sub.Equals("NonPackaged", StringComparison.OrdinalIgnoreCase))
                {
                    using var np = root.OpenSubKey(sub, false);
                    if (np is null) continue;
                    foreach (var exeKey in np.GetSubKeyNames())
                    {
                        using var k = np.OpenSubKey(exeKey, false);
                        if (InUse(k))
                        {
                            var path = exeKey.Replace('#', '\\');
                            list.Add(new PrivacyUser(FriendlyFromPath(path), path));
                        }
                    }
                }
                else
                {
                    using var k = root.OpenSubKey(sub, false);
                    if (InUse(k)) list.Add(new PrivacyUser(FriendlyFromPackage(sub), null));
                }
            }
        }
        catch (Exception ex) { Log.Debug("privacy", "Read failed: " + ex.Message); }
        return list;
    }

    private static bool InUse(RegistryKey? k)
    {
        if (k is null) return false;
        var start = k.GetValue("LastUsedTimeStart") is long s ? s : 0;
        var stop = k.GetValue("LastUsedTimeStop") is long e ? e : -1;
        return start > 0 && stop == 0;
    }

    private static string FriendlyFromPath(string path) => NameCache.GetOrAdd(path, p =>
    {
        try
        {
            if (File.Exists(p))
            {
                var desc = FileVersionInfo.GetVersionInfo(p).FileDescription;
                if (!string.IsNullOrWhiteSpace(desc) && desc.Length <= 40) return desc.Trim();
            }
        }
        catch { }
        return Path.GetFileNameWithoutExtension(p);
    });

    private static string FriendlyFromPackage(string packageFamily) => NameCache.GetOrAdd(packageFamily, p =>
    {
        var name = p.Split('_')[0];
        if (name.StartsWith("MSTeams", StringComparison.OrdinalIgnoreCase)) return "Microsoft Teams";
        if (name.Contains("WindowsCamera", StringComparison.OrdinalIgnoreCase)) return "Camera";
        if (name.Contains("WindowsSoundRecorder", StringComparison.OrdinalIgnoreCase)) return "Sound Recorder";
        var dot = name.LastIndexOf('.');
        if (dot >= 0 && dot < name.Length - 1) name = name[(dot + 1)..];
        return CamelSplit().Replace(name, " ");
    });
}
