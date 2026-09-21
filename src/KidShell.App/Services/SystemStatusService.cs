using KidShell.Core.Diagnostics;
using KidShell.Core.Mvvm;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Windows.Networking.Connectivity;

namespace KidShell.App.Services;

/// <summary>
/// The clock, battery and network readouts along the top of both screens.
///
/// Read-only system state only. KidShell changes nothing about power or
/// networking, and falls back to a neutral display if anything is unavailable
/// (a desktop with no battery, a locked-down network stack, and so on).
/// </summary>
public interface ISystemStatusService
{
    string Time { get; }

    int? BatteryPercent { get; }

    bool HasBattery { get; }

    bool IsOnline { get; }

    void Start();

    void Stop();
}

public sealed class SystemStatusService : ObservableObject, ISystemStatusService
{
    private readonly IKidShellLogger _logger;
    private readonly DispatcherQueue _dispatcherQueue;
    private DispatcherTimer? _timer;

    private string _time = string.Empty;
    private int? _batteryPercent;
    private bool _hasBattery;
    private bool _isOnline;

    public SystemStatusService(IKidShellLogger logger)
    {
        _logger = logger;
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
    }

    public string Time
    {
        get => _time;
        private set => SetProperty(ref _time, value);
    }

    public int? BatteryPercent
    {
        get => _batteryPercent;
        private set => SetProperty(ref _batteryPercent, value);
    }

    public bool HasBattery
    {
        get => _hasBattery;
        private set => SetProperty(ref _hasBattery, value);
    }

    public bool IsOnline
    {
        get => _isOnline;
        private set => SetProperty(ref _isOnline, value);
    }

    public void Start()
    {
        if (_timer is not null)
        {
            return;
        }

        Refresh();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();

        try
        {
            NetworkInformation.NetworkStatusChanged += OnNetworkStatusChanged;
        }
        catch (Exception ex)
        {
            _logger.Warning("Status", "Could not subscribe to network status changes.", ex);
        }
    }

    public void Stop()
    {
        _timer?.Stop();
        _timer = null;

        try
        {
            NetworkInformation.NetworkStatusChanged -= OnNetworkStatusChanged;
        }
        catch
        {
            // Unsubscribing is best effort.
        }
    }

    private void OnNetworkStatusChanged(object sender) =>
        _dispatcherQueue.TryEnqueue(() => IsOnline = ReadIsOnline());

    private void Refresh()
    {
        Time = DateTime.Now.ToString("HH:mm");
        IsOnline = ReadIsOnline();

        var percent = ReadBatteryPercent();
        HasBattery = percent.HasValue;
        BatteryPercent = percent;
    }

    private int? ReadBatteryPercent()
    {
        try
        {
            var status = Windows.System.Power.PowerManager.BatteryStatus;
            if (status == Windows.System.Power.BatteryStatus.NotPresent)
            {
                return null;
            }

            return Math.Clamp(Windows.System.Power.PowerManager.RemainingChargePercent, 0, 100);
        }
        catch (Exception ex)
        {
            _logger.Debug("Status", $"Battery unavailable: {ex.GetType().Name}");
            return null;
        }
    }

    private bool ReadIsOnline()
    {
        try
        {
            var profile = NetworkInformation.GetInternetConnectionProfile();
            return profile?.GetNetworkConnectivityLevel()
                is NetworkConnectivityLevel.InternetAccess
                or NetworkConnectivityLevel.ConstrainedInternetAccess
                or NetworkConnectivityLevel.LocalAccess;
        }
        catch (Exception ex)
        {
            _logger.Debug("Status", $"Network state unavailable: {ex.GetType().Name}");
            return false;
        }
    }
}
