using System.Windows;
using System.Windows.Threading;
using Nexus.App.Composition;
using Nexus.Core.Diagnostics;

namespace Nexus.App;

public partial class App : Application
{
    private NexusHost? _host;
    private SingleInstance? _instance;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Log.Initialize(System.IO.Path.Combine(NexusHost.DataDirectory, "logs"));

        _instance = SingleInstance.TryAcquire();
        if (_instance is null)
        {
            // Another NEXUS is running: it has been asked to open its settings. Leave quietly.
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Error("App", "Unhandled exception (terminating: " + args.IsTerminating + ")", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Warn("App", "Unobserved task exception", args.Exception);
            args.SetObserved();
        };

        try
        {
            _host = new NexusHost(this);
            _instance.ActivationRequested += (_, _) => Dispatcher.InvokeAsync(() => _host?.OpenSettings());
            await _host.StartAsync(openSettings: e.Args.Contains("--settings"));
        }
        catch (Exception ex)
        {
            Log.Error("App", "Fatal startup failure", ex);
            MessageBox.Show("NEXUS could not start:\n\n" + ex.Message, "NEXUS", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // A rendering or provider glitch must never take the shell overlay down with it.
        Log.Error("App", "UI exception (recovered)", e.Exception);
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Normal exits go through NexusHost.ExitAsync (providers stopped asynchronously). This is the safety net
        // for session end / unexpected shutdown: synchronous, never blocks on the dispatcher.
        try { _host?.EmergencyCleanup(); }
        catch (Exception ex) { Log.Warn("App", "Shutdown cleanup failed", ex); }
        _instance?.Dispose();
        base.OnExit(e);
    }
}
