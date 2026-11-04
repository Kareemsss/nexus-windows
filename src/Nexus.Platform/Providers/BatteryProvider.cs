using System;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Core.Activities;
using Nexus.Core.Color;
using Nexus.Core.Content;
using Nexus.Core.Plugins;
using Nexus.Platform.Services;
using Windows.System.Power;
using WinBattery = Windows.Devices.Power.Battery;

namespace Nexus.Platform.Providers;

/// <summary>
/// Battery, charger and energy-saver state from Windows.System.Power.PowerManager events. Charge-time
/// estimates come from the aggregate battery report (charge rate vs. remaining capacity).
/// </summary>
public sealed class BatteryProvider : ProviderBase
{
    public const string StatusId = "battery.status";
    private const int LowThreshold = 20;
    private const int CriticalThreshold = 7;

    private readonly object _gate = new();
    private BatteryContent? _last;
    private bool _criticalAcknowledged;
    private bool _lowAnnounced;

    public override string Id => "battery";
    public override string DisplayName => "Battery & charging";
    public override string Description => "Charger connect/disconnect, battery level, low and critical warnings.";

    protected override Task OnStartAsync(CancellationToken ct)
    {
        PowerManager.BatteryStatusChanged += OnPowerEvent;
        PowerManager.PowerSupplyStatusChanged += OnPowerEvent;
        PowerManager.RemainingChargePercentChanged += OnPowerEvent;
        PowerManager.RemainingDischargeTimeChanged += OnPowerEvent;
        PowerManager.EnergySaverStatusChanged += OnPowerEvent;
        Evaluate(initial: true);
        if (_last?.State == PowerState.NoBattery)
            SetStatus(new ProviderStatus(ProviderState.Running, "No battery detected – showing AC power only."));
        return Task.CompletedTask;
    }

    protected override ValueTask OnStopAsync()
    {
        PowerManager.BatteryStatusChanged -= OnPowerEvent;
        PowerManager.PowerSupplyStatusChanged -= OnPowerEvent;
        PowerManager.RemainingChargePercentChanged -= OnPowerEvent;
        PowerManager.RemainingDischargeTimeChanged -= OnPowerEvent;
        PowerManager.EnergySaverStatusChanged -= OnPowerEvent;
        return ValueTask.CompletedTask;
    }

    private void OnPowerEvent(object? sender, object e) => Guard("power event", () => Evaluate(initial: false));

    private void Evaluate(bool initial)
    {
        BatteryContent next;
        BatteryContent? previous;
        lock (_gate)
        {
            next = Read();
            previous = _last;
            if (previous is not null && previous == next) return;
            _last = next;
        }

        PublishStatus(next);
        if (next.State == PowerState.NoBattery) return;

        // Re-arm warnings once the user has dealt with the situation.
        if (next.OnExternalPower || next.Percent > LowThreshold + 2) _lowAnnounced = false;
        if (next.OnExternalPower || next.Percent > CriticalThreshold + 2) _criticalAcknowledged = false;

        UpdateCritical(next);
        if (initial || previous is null) return;

        bool plugged = next.OnExternalPower && !previous.OnExternalPower;
        bool unplugged = !next.OnExternalPower && previous.OnExternalPower;

        if (plugged)
        {
            Pulse(new PulseSignal(SemanticColors.Energy, PulseStyle.Sweep));
            Post(new Activity
            {
                Id = "battery.event", SourceId = Id, Priority = ActivityPriority.Low, Category = ActivityCategory.Power,
                Duration = TimeSpan.FromSeconds(3.2), Content = next, Accent = SemanticColors.Energy,
            });
        }
        else if (unplugged)
        {
            Post(new Activity
            {
                Id = "battery.event", SourceId = Id, Priority = ActivityPriority.Low, Category = ActivityCategory.Power,
                Duration = TimeSpan.FromSeconds(2.6), Content = next,
                Accent = next.Band == BatteryBand.Normal ? SemanticColors.Neutral : SemanticColors.Amber,
            });
        }
        else if (next.Band == BatteryBand.Low && !_lowAnnounced && !next.OnExternalPower)
        {
            _lowAnnounced = true;
            Pulse(new PulseSignal(SemanticColors.Amber));
            Post(new Activity
            {
                Id = "battery.event", SourceId = Id, Priority = ActivityPriority.Medium, Category = ActivityCategory.Power,
                Duration = TimeSpan.FromSeconds(5), Content = next, Accent = SemanticColors.Amber,
                Actions = new[] { new ActivityAction("saver", "Energy saver", Glyphs.BatterySaver, OpenSaverSettings) },
            });
        }
    }

    private void UpdateCritical(BatteryContent b)
    {
        bool critical = b.Band == BatteryBand.Critical && !b.OnExternalPower;
        if (!critical || _criticalAcknowledged) { Remove("battery.critical"); return; }

        Post(new Activity
        {
            Id = "battery.critical",
            SourceId = Id,
            Priority = ActivityPriority.Critical,
            Category = ActivityCategory.Power,
            PreferExpanded = true,
            Content = b,
            Accent = SemanticColors.Critical,
            Actions = new[]
            {
                new ActivityAction("saver", "Energy saver", Glyphs.BatterySaver, OpenSaverSettings),
                new ActivityAction("dismiss", "Dismiss", Glyphs.Close, () =>
                {
                    _criticalAcknowledged = true;
                    Remove("battery.critical");
                    return Task.CompletedTask;
                }, IsPrimary: true),
            },
        });
    }

    private void PublishStatus(BatteryContent b)
    {
        // Normal state is dormant (Island Center / peek only). Low battery becomes a persistent satellite
        // indicator and slowly shifts the ambient light toward amber/red without taking over the island.
        bool lowIndicator = b.Band != BatteryBand.Normal && !b.OnExternalPower;
        var color = b.Band == BatteryBand.Critical ? SemanticColors.Critical : SemanticColors.Amber;
        Post(new Activity
        {
            Id = StatusId,
            SourceId = Id,
            Priority = ActivityPriority.Medium,
            Category = ActivityCategory.Power,
            Content = b,
            IsDormant = !lowIndicator,
            CanBeForeground = false,
            Indicator = lowIndicator ? new IndicatorSpec(Glyphs.Battery(b.Percent, false), color, $"Battery {b.Percent}%") : null,
            Accent = lowIndicator ? color : null,
        });
    }

    private static Task OpenSaverSettings()
    {
        ShellService.Open("ms-settings:batterysaver");
        return Task.CompletedTask;
    }

    private static BatteryContent Read()
    {
        var status = PowerManager.BatteryStatus;
        var supply = PowerManager.PowerSupplyStatus;
        int percent = Math.Clamp(PowerManager.RemainingChargePercent, 0, 100);
        bool saver = PowerManager.EnergySaverStatus == EnergySaverStatus.On;

        if (status == BatteryStatus.NotPresent)
            return new BatteryContent(100, PowerState.NoBattery, BatteryBand.Normal, null, saver);

        var state = status switch
        {
            BatteryStatus.Charging => PowerState.Charging,
            BatteryStatus.Discharging => PowerState.Discharging,
            _ when supply != PowerSupplyStatus.NotPresent => percent >= 99 ? PowerState.Full : PowerState.PluggedIn,
            _ => PowerState.Discharging,
        };

        var band = state is PowerState.Discharging
            ? percent <= CriticalThreshold ? BatteryBand.Critical : percent <= LowThreshold ? BatteryBand.Low : BatteryBand.Normal
            : BatteryBand.Normal;

        TimeSpan? remaining = null;
        if (state == PowerState.Discharging)
        {
            var t = PowerManager.RemainingDischargeTime;
            if (t > TimeSpan.Zero && t < TimeSpan.FromDays(2)) remaining = t;
        }
        else if (state == PowerState.Charging)
        {
            remaining = EstimateTimeToFull();
        }
        // Round to whole minutes so the activity does not change every few seconds.
        if (remaining is { } r) remaining = TimeSpan.FromMinutes(Math.Max(1, Math.Round(r.TotalMinutes)));
        return new BatteryContent(percent, state, band, remaining, saver);
    }

    private static TimeSpan? EstimateTimeToFull()
    {
        try
        {
            var report = WinBattery.AggregateBattery.GetReport();
            int? rate = report.ChargeRateInMilliwatts, full = report.FullChargeCapacityInMilliwattHours, now = report.RemainingCapacityInMilliwattHours;
            if (rate is > 0 && full is > 0 && now is >= 0 && full > now)
                return TimeSpan.FromHours((double)(full.Value - now.Value) / rate.Value);
        }
        catch { /* not all firmware reports rates */ }
        return null;
    }
}
