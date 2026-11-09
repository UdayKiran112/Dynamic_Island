using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using DynamicIsland.Core;

namespace DynamicIsland.Services;

public sealed record SystemStats(
    double CpuPercent,
    double MemoryPercent,
    ulong UsedMemoryBytes,
    ulong TotalMemoryBytes,
    double DownloadBytesPerSecond,
    double UploadBytesPerSecond,
    int? BatteryPercent,
    bool IsCharging,
    bool HasBattery);

/// <summary>
/// Samples CPU (GetSystemTimes), RAM (GlobalMemoryStatusEx), battery
/// (GetSystemPowerStatus) and network throughput (NetworkInterface).
/// The timer only runs while the stats panel is visible.
/// </summary>
public sealed class StatsService : IDisposable
{
    private readonly DispatcherTimer _timer;
    private ulong _prevIdle;
    private ulong _prevKernel;
    private ulong _prevUser;
    private long _prevReceived;
    private long _prevSent;
    private DateTime _prevSampleUtc;
    private bool _hasPrevious;

    public event EventHandler<SystemStats>? StatsUpdated;

    public StatsService(Dispatcher dispatcher)
    {
        _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _timer.Tick += (_, _) => SampleAndPublish();
    }

    public bool IsRunning => _timer.IsEnabled;

    public void Start()
    {
        if (_timer.IsEnabled)
            return;

        _hasPrevious = false;
        _prevSampleUtc = DateTime.UtcNow;
        _timer.Start();
        SampleAndPublish();
    }

    public void Stop() => _timer.Stop();

    private void SampleAndPublish()
    {
        try
        {
            var stats = Sample();
            StatsUpdated?.Invoke(this, stats);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Stats: sample failed: {ex.Message}");
        }
    }

    private SystemStats Sample()
    {
        var now = DateTime.UtcNow;
        var elapsed = Math.Max(0.2, (now - _prevSampleUtc).TotalSeconds);
        _prevSampleUtc = now;

        var cpu = SampleCpu();
        var (usedMemory, totalMemory, memoryPercent) = SampleMemory();
        var (received, sent) = SampleNetwork();
        var (batteryPercent, charging, hasBattery) = SampleBattery();

        double downloadRate = 0;
        double uploadRate = 0;

        if (_hasPrevious)
        {
            downloadRate = Math.Max(0, received - _prevReceived) / elapsed;
            uploadRate = Math.Max(0, sent - _prevSent) / elapsed;
        }

        _prevReceived = received;
        _prevSent = sent;
        _hasPrevious = true;

        return new SystemStats(
            cpu,
            memoryPercent,
            usedMemory,
            totalMemory,
            downloadRate,
            uploadRate,
            batteryPercent,
            charging,
            hasBattery);
    }

    private double SampleCpu()
    {
        if (!NativeMethods.GetSystemTimes(out var idle, out var kernel, out var user))
            return 0;

        var idleTime = idle.ToUInt64();
        var kernelTime = kernel.ToUInt64();
        var userTime = user.ToUInt64();

        var cpu = 0.0;
        if (_hasPrevious)
        {
            var idleDelta = idleTime - _prevIdle;
            var kernelDelta = kernelTime - _prevKernel;
            var userDelta = userTime - _prevUser;
            var total = kernelDelta + userDelta;

            if (total > 0)
            {
                var busy = total > idleDelta ? total - idleDelta : 0;
                cpu = Math.Clamp(busy * 100.0 / total, 0, 100);
            }
        }

        _prevIdle = idleTime;
        _prevKernel = kernelTime;
        _prevUser = userTime;
        return cpu;
    }

    private static (ulong Used, ulong Total, double Percent) SampleMemory()
    {
        var status = new NativeMethods.MEMORYSTATUSEX
        {
            dwLength = (uint)Marshal.SizeOf<NativeMethods.MEMORYSTATUSEX>()
        };

        if (!NativeMethods.GlobalMemoryStatusEx(ref status) || status.ullTotalPhys == 0)
            return (0, 0, 0);

        var used = status.ullTotalPhys - status.ullAvailPhys;
        return (used, status.ullTotalPhys, status.dwMemoryLoad);
    }

    private static (long Received, long Sent) SampleNetwork()
    {
        long received = 0;
        long sent = 0;

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up ||
                nic.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel)
            {
                continue;
            }

            try
            {
                var stats = nic.GetIPStatistics();
                received += stats.BytesReceived;
                sent += stats.BytesSent;
            }
            catch
            {
                // Some virtual adapters throw while being removed.
            }
        }

        return (received, sent);
    }

    private static (int? Percent, bool Charging, bool HasBattery) SampleBattery()
    {
        if (!NativeMethods.GetSystemPowerStatus(out var status))
            return (null, false, false);

        var hasBattery = status.BatteryLifePercent != 255 && (status.BatteryFlag & 128) == 0;
        var percent = status.BatteryLifePercent == 255 ? null : (int?)status.BatteryLifePercent;
        var charging = status.ACLineStatus == 1;
        return (hasBattery ? percent : null, charging, hasBattery);
    }

    public void Dispose()
    {
        _timer.Stop();
    }
}
