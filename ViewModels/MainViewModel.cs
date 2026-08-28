using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using XXTouchController.Models;

namespace XXTouchController.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged
{
    private DeviceInfo? _selectedDevice;
    private DeviceInfo? _viewedDevice;
    private string? _luaFile;
    private string? _luaFileName;
    private string _luaSize = "-";
    private string _luaUpdated = "-";
    private bool _isScanning;
    private string _scanStatus = "Chưa quét";
    private string _batchStatus = "Sẵn sàng";
    private string _usernameBatchStatus = "Sẵn sàng";
    private string _pointResultStatus = "Sẵn sàng";

    public ObservableCollection<DeviceInfo> Devices { get; } = [];
    public ObservableCollection<LogEntry> Logs { get; } = [];
    public AppSettings Settings { get; set; } = new();

    public DeviceInfo? SelectedDevice { get => _selectedDevice; set => Set(ref _selectedDevice, value); }
    public DeviceInfo? ViewedDevice
    {
        get => _viewedDevice;
        set
        {
            if (Set(ref _viewedDevice, value)) Notify(nameof(ViewedDeviceName));
        }
    }
    public string ViewedDeviceName => ViewedDevice?.Name ?? "Chưa chọn thiết bị";
    public string? LuaFile
    {
        get => _luaFile;
        set
        {
            if (Set(ref _luaFile, value)) Notify(nameof(HasLua));
        }
    }
    public bool HasLua => !string.IsNullOrWhiteSpace(LuaFile);
    public string? LuaFileName { get => _luaFileName; set => Set(ref _luaFileName, value); }
    public string LuaSize { get => _luaSize; set => Set(ref _luaSize, value); }
    public string LuaUpdated { get => _luaUpdated; set => Set(ref _luaUpdated, value); }
    public bool IsScanning
    {
        get => _isScanning;
        set
        {
            if (!Set(ref _isScanning, value)) return;
            Notify(nameof(CanStartScan), nameof(CanStopScan));
        }
    }
    public bool CanStartScan => !IsScanning;
    public bool CanStopScan => IsScanning;
    public string ScanStatus { get => _scanStatus; set => Set(ref _scanStatus, value); }
    public string BatchStatus { get => _batchStatus; set => Set(ref _batchStatus, value); }
    public string UsernameBatchStatus
    {
        get => _usernameBatchStatus;
        set => Set(ref _usernameBatchStatus, value);
    }
    public string PointResultStatus
    {
        get => _pointResultStatus;
        set => Set(ref _pointResultStatus, value);
    }
    public int TotalCount => Devices.Count;
    public int OnlineCount => Devices.Count(d => d.ConnectionState == ConnectionState.Online);
    public int OfflineCount => Devices.Count(d => d.ConnectionState == ConnectionState.Offline);
    public int RunningCount => Devices.Count(d => d.ScriptState == ScriptState.Running);
    public int QueuedCount => Devices.Count(d =>
        d.ScriptState is ScriptState.Queued or ScriptState.Sending);
    public int CompletedCount => Devices.Count(d => d.ScriptState == ScriptState.Completed);
    public int StoppedCount => Devices.Count(d => d.ScriptState == ScriptState.Stopped);

    public void RefreshCounts() =>
        Notify(nameof(TotalCount), nameof(OnlineCount), nameof(OfflineCount),
            nameof(RunningCount), nameof(QueuedCount), nameof(CompletedCount),
            nameof(StoppedCount));

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    private void Notify(params string[] names)
    {
        foreach (var name in names)
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
