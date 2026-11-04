using System.IO;
using System.Reflection;
using Nexus.Core.Activities;
using Nexus.Core.Diagnostics;
using Nexus.Core.Plugins;
using Nexus.Core.Settings;

namespace Nexus.App.Composition;

/// <summary>
/// The plugin manager. Owns provider lifetimes: starts enabled modules, stops/starts them when the user toggles
/// modules, and isolates failures – a provider that throws is marked Faulted and the rest of NEXUS keeps running.
/// </summary>
public sealed class ProviderManager : IAsyncDisposable
{
    private readonly List<INexusProvider> _providers = new();
    private readonly HashSet<string> _running = new(StringComparer.OrdinalIgnoreCase);
    private readonly ActivityHub _hub;
    private readonly SettingsStore _settings;
    private readonly ProviderContext _context;
    private readonly CancellationTokenSource _shutdown = new();

    public ProviderManager(ActivityHub hub, SettingsStore settings, SynchronizationContext ui)
    {
        _hub = hub;
        _settings = settings;
        _context = new ProviderContext(hub, settings.Current, ui);
    }

    public IReadOnlyList<INexusProvider> Providers => _providers;
    public event EventHandler? StatusChanged;

    public void Register(INexusProvider provider)
    {
        if (_providers.Any(p => p.Id.Equals(provider.Id, StringComparison.OrdinalIgnoreCase)))
        {
            Log.Warn("Providers", $"Duplicate provider id '{provider.Id}' ignored");
            return;
        }
        provider.StatusChanged += (_, _) => StatusChanged?.Invoke(this, EventArgs.Empty);
        _providers.Add(provider);
    }

    public async Task StartEnabledAsync()
    {
        foreach (var p in _providers.Where(p => _settings.Current.Modules.IsEnabled(p.Id)).ToList())
            await StartAsync(p);
    }

    public async Task SetEnabledAsync(string id, bool enabled)
    {
        _settings.Update("modules", s => s.Modules.Enabled[id] = enabled);
        var p = _providers.FirstOrDefault(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (p is null) return;
        if (enabled) await StartAsync(p);
        else await StopAsync(p);
    }

    public async Task RestartAsync(string id)
    {
        var p = _providers.FirstOrDefault(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (p is null || !_running.Contains(p.Id)) return;
        await StopAsync(p);
        await StartAsync(p);
    }

    private async Task StartAsync(INexusProvider p)
    {
        if (!_running.Add(p.Id)) return;
        try
        {
            await p.StartAsync(_context, _shutdown.Token);
            Log.Info("Providers", $"{p.Id}: {p.Status.State}{(p.Status.Message is null ? "" : " – " + p.Status.Message)}");
        }
        catch (Exception ex)
        {
            Log.Error("Providers", $"{p.Id} failed to start", ex);
        }
    }

    private async Task StopAsync(INexusProvider p)
    {
        if (!_running.Remove(p.Id)) return;
        try { await p.DisposeAsync(); }
        catch (Exception ex) { Log.Warn("Providers", $"{p.Id} failed to stop", ex); }
        _hub.RemoveSource(p.Id);
    }

    /// <summary>
    /// Loads third-party providers from %LOCALAPPDATA%\Nexus\plugins (opt-in). A plugin is any public,
    /// non-abstract INexusProvider with a parameterless constructor in an assembly referencing Nexus.Core.
    /// </summary>
    public void LoadPlugins(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            foreach (var dll in Directory.EnumerateFiles(folder, "*.dll"))
            {
                try
                {
                    var asm = Assembly.LoadFrom(dll);
                    foreach (var type in asm.GetExportedTypes().Where(t => typeof(INexusProvider).IsAssignableFrom(t) && !t.IsAbstract))
                    {
                        if (type.GetConstructor(System.Type.EmptyTypes) is null) continue;
                        if (Activator.CreateInstance(type) is INexusProvider provider)
                        {
                            Register(provider);
                            Log.Info("Plugins", $"Loaded {provider.Id} from {Path.GetFileName(dll)}");
                        }
                    }
                }
                catch (Exception ex) { Log.Warn("Plugins", $"Skipped {Path.GetFileName(dll)}", ex); }
            }
        }
        catch (Exception ex) { Log.Warn("Plugins", "Plugin folder unavailable", ex); }
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        foreach (var p in _providers.Where(p => _running.Contains(p.Id)).ToList())
        {
            try
            {
                var stop = p.DisposeAsync().AsTask();
                await Task.WhenAny(stop, Task.Delay(1500)); // never let one provider hang shutdown
            }
            catch (Exception ex) { Log.Warn("Providers", $"{p.Id} dispose failed", ex); }
        }
        _running.Clear();
        _shutdown.Dispose();
    }

    private sealed class ProviderContext : IProviderContext
    {
        public ProviderContext(ActivityHub hub, NexusSettings settings, SynchronizationContext ui)
        {
            Activities = hub;
            Feed = hub;
            Settings = settings;
            UiContext = ui;
        }

        public IActivitySink Activities { get; }
        public IActivityFeed Feed { get; }
        public NexusSettings Settings { get; }
        public SynchronizationContext UiContext { get; }
    }
}
