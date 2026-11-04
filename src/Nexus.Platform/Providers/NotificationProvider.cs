using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Core.Activities;
using Nexus.Core.Color;
using Nexus.Core.Content;
using Nexus.Core.Diagnostics;
using Nexus.Core.Plugins;
using Windows.Foundation.Metadata;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace Nexus.Platform.Providers;

/// <summary>
/// Mirrors Windows toast notifications into the island through UserNotificationListener.
///
/// Windows only grants this API to apps with package identity (MSIX or a sparse package) after the user
/// approves access. When NEXUS runs unpackaged (e.g. `dotnet run`) the provider reports itself Unavailable
/// with an explanation instead of faking anything; NEXUS's own events still use the notification presentation.
/// </summary>
public sealed class NotificationProvider : ProviderBase
{
    private const int APPMODEL_ERROR_NO_PACKAGE = 15700;
    private readonly HashSet<uint> _seen = new();
    private UserNotificationListener? _listener;
    private Timer? _poll;

    public override string Id => "notifications";
    public override string DisplayName => "Notifications";
    public override string Description => "Shows incoming Windows notifications compactly, with details on hover or click.";

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref int length, char[]? name);

    public static bool HasPackageIdentity()
    {
        try { int len = 0; return GetCurrentPackageFullName(ref len, null) != APPMODEL_ERROR_NO_PACKAGE; }
        catch { return false; }
    }

    protected override async Task OnStartAsync(CancellationToken ct)
    {
        if (!ApiInformation.IsTypePresent("Windows.UI.Notifications.Management.UserNotificationListener"))
        {
            SetStatus(new ProviderStatus(ProviderState.Unavailable, "Notification listener API is not present on this Windows build."));
            return;
        }
        if (!HasPackageIdentity())
        {
            SetStatus(new ProviderStatus(ProviderState.Unavailable,
                "Windows grants notification access only to packaged (MSIX) apps. Run the packaged build to enable."));
            return;
        }

        _listener = UserNotificationListener.Current;
        var access = await _listener.RequestAccessAsync().AsTask(ct).ConfigureAwait(true);
        if (access != UserNotificationListenerAccessStatus.Allowed)
        {
            SetStatus(new ProviderStatus(ProviderState.Unavailable, "Notification access is off in Settings › Privacy & security › Notifications."));
            return;
        }

        foreach (var n in await _listener.GetNotificationsAsync(NotificationKinds.Toast).AsTask(ct).ConfigureAwait(true))
            _seen.Add(n.Id);

        try
        {
            _listener.NotificationChanged += OnNotificationChanged;
        }
        catch (Exception ex)
        {
            // Some builds only raise this event for UWP background tasks. Fall back to a light diff poll.
            Log.Info(Id, "NotificationChanged unsupported, polling every 3 s: " + ex.Message);
            _poll = new Timer(_ => GuardAsync("poll", PollAsync), null, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3));
        }
    }

    protected override ValueTask OnStopAsync()
    {
        try { if (_listener is not null) _listener.NotificationChanged -= OnNotificationChanged; } catch { }
        _poll?.Dispose();
        return ValueTask.CompletedTask;
    }

    private void OnNotificationChanged(UserNotificationListener sender, UserNotificationChangedEventArgs args)
    {
        if (args.ChangeKind == UserNotificationChangedKind.Added) GuardAsync("notification", () => HandleAsync(args.UserNotificationId));
        else if (args.ChangeKind == UserNotificationChangedKind.Removed) Remove("notif." + args.UserNotificationId);
    }

    private async Task PollAsync()
    {
        if (_listener is null) return;
        var list = await _listener.GetNotificationsAsync(NotificationKinds.Toast).AsTask(Stopping).ConfigureAwait(false);
        var ids = list.Select(n => n.Id).ToHashSet();
        List<uint> fresh;
        lock (_seen)
        {
            fresh = ids.Where(id => !_seen.Contains(id)).ToList();
            _seen.Clear();
            _seen.UnionWith(ids);
        }
        foreach (var id in fresh) await HandleAsync(id).ConfigureAwait(false);
    }

    private async Task HandleAsync(uint id)
    {
        if (_listener is null) return;
        lock (_seen) _seen.Add(id);
        var n = _listener.GetNotification(id);
        if (n is null) return;

        string app = Safe(() => n.AppInfo.DisplayInfo.DisplayName) ?? "Notification";
        var binding = n.Notification?.Visual?.GetBinding(KnownNotificationBindings.ToastGeneric);
        var texts = binding?.GetTextElements()?.Select(t => t.Text?.Trim() ?? string.Empty).Where(t => t.Length > 0).ToList() ?? new List<string>();
        string title = texts.FirstOrDefault() ?? app;
        string body = string.Join(" · ", texts.Skip(1));
        if (body.Length > 400) body = body[..400] + "…";
        if (!Settings.Behavior.ShowNotificationPreview) { title = app; body = "New notification"; }

        byte[]? icon = null;
        try
        {
            var logo = n.AppInfo.DisplayInfo.GetLogo(new Windows.Foundation.Size(64, 64));
            using var stream = await logo.OpenReadAsync().AsTask(Stopping).ConfigureAwait(false);
            if (stream.Size is > 0 and < 2_000_000)
            {
                using var s = stream.AsStreamForRead();
                using var ms = new MemoryStream();
                await s.CopyToAsync(ms).ConfigureAwait(false);
                icon = ms.ToArray();
            }
        }
        catch { /* icon is optional: the widget falls back to a glyph */ }

        Pulse(new PulseSignal(SemanticColors.Notification));
        Post(new Activity
        {
            Id = "notif." + id,
            SourceId = Id,
            Priority = ActivityPriority.Medium,
            Category = ActivityCategory.Notification,
            Duration = TimeSpan.FromSeconds(5.5),
            Content = new NotificationContent(app, icon, title, body, DateTimeOffset.Now, Glyphs.Ringer),
            Accent = SemanticColors.Notification,
            Actions = new[]
            {
                new ActivityAction("dismiss", "Dismiss", Glyphs.Close, () =>
                {
                    try { _listener?.RemoveNotification(id); } catch { }
                    Remove("notif." + id);
                    return Task.CompletedTask;
                }),
            },
        });
    }

    private static string? Safe(Func<string> f) { try { return f(); } catch { return null; } }
}
