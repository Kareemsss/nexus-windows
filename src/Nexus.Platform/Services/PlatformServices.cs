using System;
using System.Threading.Tasks;
using Nexus.Core.Diagnostics;

namespace Nexus.Platform.Services;

/// <summary>Shared, lazily created Windows services used by more than one provider. Disposed once at shutdown.</summary>
public sealed class PlatformServices : IDisposable
{
    private readonly Lazy<AudioEndpointMonitor?> _render;
    private readonly Lazy<AudioEndpointMonitor?> _capture;

    public PlatformServices()
    {
        _render = new Lazy<AudioEndpointMonitor?>(() => CreateAudio(capture: false));
        _capture = new Lazy<AudioEndpointMonitor?>(() => CreateAudio(capture: true));
    }

    /// <summary>Default output endpoint, or null if Core Audio is unavailable (no audio service, RDP edge cases).</summary>
    public AudioEndpointMonitor? RenderAudio => _render.Value;
    /// <summary>Default capture endpoint (microphone).</summary>
    public AudioEndpointMonitor? CaptureAudio => _capture.Value;

    private static AudioEndpointMonitor? CreateAudio(bool capture)
    {
        try
        {
            // Create the COM objects on an MTA pool thread. Core Audio calls back on its own MTA threads; keeping
            // the objects out of the UI STA avoids cross-apartment marshalling and the deadlocks it can cause.
            return Task.Run(() => new AudioEndpointMonitor(capture)).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Log.Error("Audio", $"Core Audio unavailable ({(capture ? "capture" : "render")})", ex);
            return null;
        }
    }

    public void Dispose()
    {
        if (_render.IsValueCreated) _render.Value?.Dispose();
        if (_capture.IsValueCreated) _capture.Value?.Dispose();
    }
}
