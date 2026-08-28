using Microsoft.Win32;
using System.Collections.Concurrent;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using XXTouchController.Models;
using XXTouchController.Services;
using XXTouchController.ViewModels;

namespace XXTouchController;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new();
    private readonly XXTouchClient _client = new();
    private readonly SettingsService _settingsService = new();
    private readonly DeviceDiscoveryService _discovery;
    private readonly LogService _log;
    private readonly OpenAiErrorAnalysisService _aiAnalysisService = new();
    private readonly WindowsCredentialService _credentialService = new();
    private readonly AgentKeepAliveService _agentKeepAliveService = new();
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _deviceOperationGates = new();
    private readonly ConcurrentDictionary<DeviceInfo, CancellationTokenSource>
        _scriptRunCancellations = new();
    private readonly SemaphoreSlim _usernameResultSaveGate = new(1, 1);
    private readonly SemaphoreSlim _pointResultSaveGate = new(1, 1);
    private readonly SemaphoreSlim _homeReadyFilterGate = new(1, 1);
    private CancellationTokenSource? _scanCts;
    private CancellationTokenSource? _snapshotCts;
    private CancellationTokenSource? _aiAnalysisCts;
    private CancellationTokenSource? _usernameCollectionCts;
    private readonly DispatcherTimer _autoScanTimer;
    private readonly DispatcherTimer _snapshotTimer;
    private readonly DispatcherTimer _logScrollTimer;
    private readonly DispatcherTimer _scriptStatusTimer;
    private readonly DispatcherTimer _deviceKeepAliveTimer;
    private bool _snapshotBusy;
    private bool _scriptStatusBusy;
    private bool _heartbeatBusy;
    private bool _agentUpdateBusy;
    private bool _usernameBusy;
    private bool _pointResultBusy;
    private int _scriptStatusTick;
    private byte[]? _latestSnapshotJpeg;
    private bool _loaded;
    private int _heartbeatCycle;
    private ICollectionView? _deviceView;

    private sealed record HomeReadyFilterResult(
        IReadOnlyList<DeviceInfo> Ready,
        int Total,
        int Unsupported,
        int Unreachable)
    {
        public int Skipped => Total - Ready.Count;
    }

    private static readonly string[] TikTokUsernameAssetNames =
    [
        "tur-profile-tab.png",
        "tur-profile-tab-ja-light.png",
        "tur-profile-tab-en-light.png",
        "tur-ask-not-track.png",
        "tur-dont-allow.png",
        "tur-find-contacts-dont-allow-en.png",
        "tur-black-action-button.png",
        "tur-contacts-deny-ja.png",
        "tur-not-now.png",
        "tur-photo-picker-close.png",
        "tur-modal-close-x.png",
        "tur-security-check-close-x.png"
    ];

    private static string TikTokUsernameResultsDirectory =>
        Path.Combine(AppContext.BaseDirectory, "Ket-qua-username");

    private static string TikTokPointResultsDirectory =>
        Path.Combine(AppContext.BaseDirectory, "Ket-qua-TikTok-Point");

    // Uses OCR rather than find_image so the installer works on older Agents too.
    // It exits cleanly when TrollStore is configured to install without confirmation.
    private const string TrollStoreInstallScript = """
        screen.init(0)
        local points = {{510, 1048}, {1020, 2096}, {1814, 1179}}
        local function press_install()
            for _, point in ipairs(points) do
                touch.on(point[1], point[2]):move(point[1] + 2, point[2]):step_delay(1):off()
                sys.msleep(700)
            end
        end
        for attempt = 1, 180 do
            local text = screen.ocr()
            if text then
                local is_trollvnc = text:find("TrollVNC", 1, true) or text:find("TrolIVNC", 1, true)
                local has_bundle = text:find("com.82flex", 1, true) and text:find("TrollVNCApp", 1, true)
                if is_trollvnc and has_bundle and text:find("Install", 1, true) then
                    nLog("UPDATE_INSTALL_DIALOG_FOUND")
                    press_install()
                    sys.msleep(1500)
                    nLog("UPDATE_INSTALL_PRESSED")
                    return
                end
            end
            sys.msleep(1000)
        end
        nLog("UPDATE_INSTALL_DIALOG_NOT_FOUND")
        """;

    public MainWindow()
    {
        InitializeComponent();
        var assemblyVersion = typeof(MainWindow).Assembly.GetName().Version;
        VersionText.Text = assemblyVersion is null
            ? "Phiên bản không xác định"
            : $"Phiên bản {assemblyVersion.Major}.{assemblyVersion.Minor}.{assemblyVersion.Build}";
        DataContext = _vm;
        _deviceView = CollectionViewSource.GetDefaultView(_vm.Devices);
        _deviceView.Filter = DeviceMatchesSearch;
        RefreshDeviceSearch();
        _discovery = new DeviceDiscoveryService(_client);
        _log = new LogService(Dispatcher);
        LogList.ItemsSource = _log.Entries;
        _logScrollTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(120)
        };
        _logScrollTimer.Tick += (_, _) =>
        {
            _logScrollTimer.Stop();
            if (_log.Entries.Count > 0)
            {
                // Keep the log itself pinned to its newest row without letting
                // WPF drag the outer compact-layout scrollbar down to Log.
                var mainOffset = MainContentScrollViewer.VerticalOffset;
                LogList.ScrollIntoView(_log.Entries[^1]);
                _ = Dispatcher.BeginInvoke(DispatcherPriority.Render,
                    () => MainContentScrollViewer.ScrollToVerticalOffset(mainOffset));
            }
        };
        _log.Entries.CollectionChanged += (_, _) =>
        {
            _logScrollTimer.Stop();
            _logScrollTimer.Start();
        };
        _client.Diagnostic += OnDiagnostic;
        _agentKeepAliveService.HeartbeatReceived += OnAgentKeepAlive;

        _autoScanTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _autoScanTimer.Tick += async (_, _) =>
        {
            if (_vm.IsScanning) return;
            _heartbeatCycle++;
            if (_heartbeatCycle % 6 == 0) await ScanAsync();
            else await HeartbeatAsync();
        };
        _snapshotTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _snapshotTimer.Tick += async (_, _) => await RefreshSnapshotAsync();
        _scriptStatusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _scriptStatusTimer.Tick += async (_, _) =>
        {
            foreach (var device in _vm.Devices)
                device.RefreshScriptClock();

            _scriptStatusTick++;
            if (_scriptStatusTick % 2 == 0)
                await RefreshActiveScriptStatusesAsync();
        };
        _deviceKeepAliveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _deviceKeepAliveTimer.Tick += async (_, _) => await HeartbeatAsync();
        _vm.Devices.CollectionChanged += Devices_CollectionChanged;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            var loaded = await _settingsService.LoadAsync(_lifetimeCts.Token);
            _vm.Settings = loaded.Settings;
            foreach (var device in loaded.Devices) AddToCollection(device);
            ApplySettingsToUi();
            _agentKeepAliveService.Start();
            if (!string.IsNullOrWhiteSpace(_vm.Settings.LastLuaFile) &&
                File.Exists(_vm.Settings.LastLuaFile))
                await SelectLuaFileAsync(_vm.Settings.LastLuaFile, false);

            // SelectLuaFileAsync updates controls near the bottom of the page.
            // Restore the initial viewport before the potentially long device
            // health check so a compact window opens at Kết nối/Danh sách.
            MainContentScrollViewer.ScrollToTop();
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded,
                () => MainContentScrollViewer.ScrollToTop());

            _log.Add("Ứng dụng", "-", "Khởi động.", "Sẵn sàng.", LogLevel.Success);
            if (_vm.Devices.Count > 0)
                await RunBatchAsync(_vm.Devices, device => CheckDeviceAsync(device));
            if (_vm.Settings.AutoScan) _autoScanTimer.Start();
            if (_vm.Settings.AutoSnapshot) _snapshotTimer.Start();
            _scriptStatusTimer.Start();
            _deviceKeepAliveTimer.Start();
            // Loading devices and refreshing the virtualized table can ask WPF
            // to bring a child into view. On short windows that used to leave
            // the connection controls above the visible viewport at startup.
            _ = Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle,
                () => MainContentScrollViewer.ScrollToTop());
        }
        catch (Exception ex)
        {
            _log.Add("Ứng dụng", "-", "Khởi động.", "Không đọc được cấu hình.",
                LogLevel.Error, ex.Message);
        }
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        _autoScanTimer.Stop();
        _snapshotTimer.Stop();
        _logScrollTimer.Stop();
        _scriptStatusTimer.Stop();
        _deviceKeepAliveTimer.Stop();
        _scanCts?.Cancel();
        _snapshotCts?.Cancel();
        _aiAnalysisCts?.Cancel();
        _usernameCollectionCts?.Cancel();
        foreach (var cancellation in _scriptRunCancellations.Values)
            cancellation.Cancel();
        _lifetimeCts.Cancel();
        _client.Dispose();
        _agentKeepAliveService.Dispose();
        _aiAnalysisCts?.Dispose();
        _usernameCollectionCts?.Dispose();
        _lifetimeCts.Dispose();
    }

    private void ApplySettingsToUi()
    {
        PortTextBox.Text = _vm.Settings.DefaultPort.ToString();
        ScanIntervalTextBox.Text = _vm.Settings.ScanIntervalSeconds.ToString();
        AutoScanCheckBox.IsChecked = _vm.Settings.AutoScan;
        SnapshotIntervalTextBox.Text = _vm.Settings.SnapshotIntervalSeconds.ToString();
        AutoSnapshotCheckBox.IsChecked = _vm.Settings.AutoSnapshot;
        ConcurrencyTextBox.Text = _vm.Settings.ConcurrencyLimit.ToString();
        BatchDelayTextBox.Text = _vm.Settings.BatchDelaySeconds.ToString();
        OnlyHomeReadyCheckBox.IsChecked = _vm.Settings.OnlyRunHomeReady;
        DiagnosticsCheckBox.IsChecked = _vm.Settings.Diagnostics;
        AiModelTextBox.Text = string.IsNullOrWhiteSpace(_vm.Settings.AiModel)
            ? "gpt-5.6-terra"
            : _vm.Settings.AiModel;
        AiIncludeSnapshotCheckBox.IsChecked = _vm.Settings.AiIncludeSnapshot;
        AiRedactNetworkCheckBox.IsChecked = _vm.Settings.AiRedactNetworkData;
        try
        {
            AiStatusText.Text = string.IsNullOrWhiteSpace(_credentialService.ReadApiKey())
                ? "Chưa lưu API key"
                : "API key đã lưu an toàn";
        }
        catch
        {
            AiStatusText.Text = "Không đọc được API key";
        }
        UpdateTimerIntervals();
    }

    private void Devices_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
            foreach (DeviceInfo device in e.NewItems)
                device.PropertyChanged += Device_PropertyChanged;
        if (e.OldItems is not null)
            foreach (DeviceInfo device in e.OldItems)
                device.PropertyChanged -= Device_PropertyChanged;
        RenumberDevices();
        _vm.RefreshCounts();
        RefreshDeviceSearch();
    }

    private void RenumberDevices()
    {
        for (var index = 0; index < _vm.Devices.Count; index++)
            _vm.Devices[index].DisplayIndex = index + 1;
    }

    private void Device_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DeviceInfo.ConnectionState) or nameof(DeviceInfo.ScriptState))
            _vm.RefreshCounts();
        if (e.PropertyName is nameof(DeviceInfo.DisplayIndex) or nameof(DeviceInfo.DeviceId) or
            nameof(DeviceInfo.Name) or nameof(DeviceInfo.Ip) or nameof(DeviceInfo.Port) or
            nameof(DeviceInfo.TikTokUsername))
            RefreshDeviceSearch();
    }

    private bool DeviceMatchesSearch(object item)
    {
        if (item is not DeviceInfo device) return false;
        var query = DeviceSearchTextBox?.Text.Trim();
        if (string.IsNullOrWhiteSpace(query)) return true;

        var searchable = string.Join(' ',
            device.DisplayIndex.ToString(),
            device.DeviceId ?? string.Empty,
            device.Name,
            device.Ip,
            device.Port.ToString(),
            device.TikTokUsername ?? string.Empty);
        return query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .All(term => searchable.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private void RefreshDeviceSearch()
    {
        if (_deviceView is null || DeviceSearchResultText is null) return;
        _deviceView.Refresh();
        DeviceSearchResultText.Text =
            $"Hiển thị {_deviceView.Cast<object>().Count()}/{_vm.Devices.Count}";
    }

    private void DeviceSearchTextBox_TextChanged(object sender, TextChangedEventArgs e) =>
        RefreshDeviceSearch();

    private void ClearDeviceSearch_Click(object sender, RoutedEventArgs e)
    {
        DeviceSearchTextBox.Clear();
        DeviceSearchTextBox.Focus();
    }

    private void OnAgentKeepAlive(IPAddress address)
    {
        Dispatcher.BeginInvoke(() =>
        {
            var device = _vm.Devices.FirstOrDefault(
                item => string.Equals(
                    item.Ip, address.ToString(), StringComparison.OrdinalIgnoreCase));
            if (device is null) return;
            device.ConsecutiveConnectionFailures = 0;
            device.ConnectionState = ConnectionState.Online;
            device.LastUpdated = DateTime.Now;
        });
    }

    private void AddToCollection(DeviceInfo device) => _vm.Devices.Add(device);

    private async void AddDevice_Click(object sender, RoutedEventArgs e)
    {
        if (!TryReadEndpoint(out var ip, out var port)) return;
        if (FindByEndpoint(ip, port) is not null)
        {
            MessageBox.Show("Thiết bị có cùng IP và cổng đã tồn tại.", "Trùng thiết bị",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        DeviceInfo device;
        try
        {
            device = await _client.GetDeviceInfoAsync(ip, port, _lifetimeCts.Token)
                     ?? throw new InvalidOperationException("Không có dữ liệu thiết bị.");
            var duplicate = FindByDeviceId(device.DeviceId);
            if (duplicate is not null)
            {
                MessageBox.Show($"Device ID đã tồn tại dưới tên {duplicate.Name}.", "Trùng thiết bị",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            _log.Add(device.Name, device.Ip, "Thêm thiết bị.", "Kết nối thành công.", LogLevel.Success);
        }
        catch (Exception ex)
        {
            device = new DeviceInfo
            {
                Name = $"iPhone {ip}",
                Ip = ip,
                Port = port,
                ConnectionState = ConnectionState.Offline
            };
            _log.Add(device.Name, ip, "Thêm thủ công.", "Đã lưu ở trạng thái Offline.",
                LogLevel.Warning, ex.Message);
        }
        AddToCollection(device);
        _vm.SelectedDevice = device;
        await SaveAsync();
    }

    private async void EditDevice_Click(object sender, RoutedEventArgs e)
    {
        var device = _vm.SelectedDevice;
        if (device is null)
        {
            MessageBox.Show("Hãy chọn một thiết bị cần sửa.", "XXTouch Controller",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!TryReadEndpoint(out var ip, out var port)) return;
        var duplicate = FindByEndpoint(ip, port);
        if (duplicate is not null && !ReferenceEquals(duplicate, device))
        {
            MessageBox.Show("IP và cổng này đã thuộc một thiết bị khác.", "Trùng thiết bị",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        device.Ip = ip;
        device.Port = port;
        await CheckDeviceAsync(device);
        await SaveAsync();
    }

    private async void DeleteDevice_Click(object sender, RoutedEventArgs e)
    {
        var targets = SelectedDevices();
        if (targets.Count == 0 && _vm.SelectedDevice is { } currentRow)
            targets.Add(currentRow);

        if (targets.Count == 0)
        {
            MessageBox.Show("Hãy đánh dấu thiết bị cần xóa, hoặc bôi một dòng trong danh sách.",
                "XXTouch Controller", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var confirmation = targets.Count == 1
            ? $"Xóa {targets[0].Name} ({targets[0].Endpoint})?"
            : $"Xóa toàn bộ {targets.Count} thiết bị đã đánh dấu?";
        if (MessageBox.Show(confirmation, "Xác nhận xóa",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        if (_vm.ViewedDevice is not null && targets.Contains(_vm.ViewedDevice))
        {
            _snapshotCts?.Cancel();
            _vm.ViewedDevice = null;
            SnapshotImage.Source = null;
        }

        var removed = 0;
        foreach (var device in targets.ToArray())
        {
            CancelScriptSchedule(device);
            device.IsSelected = false;
            if (!_vm.Devices.Remove(device)) continue;
            _deviceOperationGates.TryRemove(device.Endpoint, out _);
            removed++;
        }

        if (_vm.SelectedDevice is not null && targets.Contains(_vm.SelectedDevice))
            _vm.SelectedDevice = null;

        _log.Add("Danh sách", "-", "Xóa thiết bị.",
            $"Đã xóa {removed}/{targets.Count} thiết bị đã chọn.", LogLevel.Warning);
        await SaveAsync();
    }

    private async void CheckConnection_Click(object sender, RoutedEventArgs e)
    {
        if (!TryReadEndpoint(out var ip, out var port)) return;
        var existing = FindByEndpoint(ip, port);
        if (existing is not null)
        {
            await CheckDeviceAsync(existing);
            return;
        }
        try
        {
            _log.Add(ip, ip, $"Kiểm tra {ip}:{port}...", "Đang xử lý.");
            var info = await _client.GetDeviceInfoAsync(ip, port, _lifetimeCts.Token);
            _log.Add(info?.Name ?? ip, ip, "Kiểm tra kết nối.",
                $"Thành công — {info?.Model}, iOS {info?.IosVersion}, XXTouch {info?.XXTouchVersion}.",
                LogLevel.Success);
        }
        catch (Exception ex)
        {
            _log.Add(ip, ip, "Kiểm tra kết nối.", "Không kết nối được.", LogLevel.Error, ex.Message);
        }
    }

    private void DeviceGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_vm.SelectedDevice is not { } device) return;
        IpTextBox.Text = device.Ip;
        PortTextBox.Text = device.Port.ToString();
    }

    private async void StartScan_Click(object sender, RoutedEventArgs e) => await ScanAsync();

    private void StopScan_Click(object sender, RoutedEventArgs e)
    {
        _scanCts?.Cancel();
        _vm.ScanStatus = "Đang dừng...";
    }

    private async Task ScanAsync()
    {
        if (_vm.IsScanning) return;
        _scanCts?.Dispose();
        _scanCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
        _vm.IsScanning = true;
        _vm.ScanStatus = "Đang quét UDP + subnet";
        _log.Add("LAN", "-", "UDP 46953 + dò nhanh TCP 46952.",
            "Đang tìm thiết bị trong subnet cục bộ...");
        try
        {
            var scan = await _discovery.ScanDevicesAsync(_scanCts.Token);
            foreach (var incoming in scan.Devices)
            {
                var existing = FindByDeviceId(incoming.DeviceId) ??
                               FindByEndpoint(incoming.Ip, incoming.Port);
                if (existing is null)
                {
                    AddToCollection(incoming);
                    _log.Add(incoming.Name, incoming.Ip, "LAN discovery.",
                        "Đã tìm thấy và xác minh.", LogLevel.Success);
                }
                else existing.Apply(incoming);
            }

            // Verify every existing endpoint before cleaning up legacy rows.
            // This preserves a manually added device if its Agent is reachable,
            // even when UDP/subnet discovery did not return it.
            await RunBatchAsync(
                _vm.Devices.ToArray(), device => CheckDeviceAsync(device, logEveryCheck: false));

            // Version 3.8.22 accidentally persisted TCP-open endpoints even
            // when /deviceinfo rejected them. A proxy can stop accepting those
            // ports later, so RejectedEndpoints alone is not enough to find all
            // old ghosts. After the explicit HTTP verification above, remove
            // only Offline/default rows which still contain no Agent identity,
            // device metadata, or user data. This cleanup only runs after a
            // scan found at least one real Agent, avoiding cleanup during a
            // total LAN outage.
            var legacyGhosts = scan.Devices.Count == 0
                ? Array.Empty<DeviceInfo>()
                : _vm.Devices.Where(device =>
                    device.ConnectionState == ConnectionState.Offline &&
                    device.Name.Equals("iPhone", StringComparison.OrdinalIgnoreCase) &&
                    string.IsNullOrWhiteSpace(device.DeviceId) &&
                    string.IsNullOrWhiteSpace(device.Model) &&
                    string.IsNullOrWhiteSpace(device.IosVersion) &&
                    string.IsNullOrWhiteSpace(device.XXTouchVersion) &&
                    string.IsNullOrWhiteSpace(device.TikTokUsername) &&
                    string.IsNullOrWhiteSpace(device.TikTokUsernameResult) &&
                    device.TikTokPointBalance is null &&
                    string.IsNullOrWhiteSpace(device.TikTokPointPlan) &&
                    string.IsNullOrWhiteSpace(device.TikTokPointStatus) &&
                    string.IsNullOrWhiteSpace(device.TikTokPointLink)).ToArray();
            foreach (var ghost in legacyGhosts)
            {
                CancelScriptSchedule(ghost);
                ghost.IsSelected = false;
                _vm.Devices.Remove(ghost);
                _deviceOperationGates.TryRemove(ghost.Endpoint, out _);
            }
            if (legacyGhosts.Length > 0)
            {
                _log.Add("LAN", "-", "Dọn kết quả quét cũ.",
                    $"Đã xóa {legacyGhosts.Length} dòng ảo chưa từng xác minh.",
                    LogLevel.Warning);
            }
            _vm.ScanStatus = scan.RejectedCount > 0
                ? $"Hoàn tất ({scan.Devices.Count} thật, bỏ {scan.RejectedCount} giả)"
                : $"Hoàn tất ({scan.Devices.Count} thiết bị thật)";
            _log.Add("LAN", "-", "Quét UDP + subnet.",
                $"Hoàn tất: {scan.Devices.Count} thiết bị thật; " +
                $"loại {scan.RejectedCount}/{scan.CandidateCount} phản hồi không phải Agent.",
                LogLevel.Success);
            await SaveAsync();
        }
        catch (OperationCanceledException)
        {
            _vm.ScanStatus = "Đã dừng";
            _log.Add("LAN", "-", "Quét UDP + subnet.", "Đã hủy.", LogLevel.Warning);
        }
        catch (Exception ex)
        {
            _vm.ScanStatus = "Lỗi quét";
            _log.Add("LAN", "-", "Quét UDP + subnet.",
                "Thất bại.", LogLevel.Error, ex.Message);
        }
        finally
        {
            _vm.IsScanning = false;
        }
    }

    private async Task HeartbeatAsync()
    {
        if (_heartbeatBusy || _vm.IsScanning) return;
        _heartbeatBusy = true;
        try
        {
        var devices = _vm.Devices.ToArray();
        using var semaphore = new SemaphoreSlim(32, 32);
        var tasks = devices.Select(async device =>
        {
            await semaphore.WaitAsync(_lifetimeCts.Token);
            try
            {
                var wasOnline = device.IsOnline;
                var previousState = device.ScriptState;
                try
                {
                    var health = await _client.GetHealthAsync(
                        device.Ip, device.Port, _lifetimeCts.Token);
                    device.ConsecutiveConnectionFailures = 0;
                    device.ConnectionState = ConnectionState.Online;
                    device.LastUpdated = DateTime.Now;
                    if (!string.IsNullOrWhiteSpace(health.Version))
                        device.XXTouchVersion = health.Version;
                    if (health.Running == true)
                    {
                        device.ScriptStartedAt ??= DateTime.Now;
                        device.ScriptFinishedAt = null;
                        device.ScriptState = ScriptState.Running;
                    }
                    else if (previousState is ScriptState.Running or
                             ScriptState.Sending or ScriptState.Queued)
                    {
                        device.ScriptFinishedAt ??= DateTime.Now;
                        device.ScriptState = ScriptState.Stopped;
                    }
                    if (!wasOnline)
                        _log.Add(device.Name, device.Ip, "Heartbeat.",
                            "Thiết bị Online trở lại.", LogLevel.Success);
                }
                catch
                {
                    if (_agentKeepAliveService.IsConnected(device.Ip))
                    {
                        device.ConsecutiveConnectionFailures = 0;
                        device.ConnectionState = ConnectionState.Online;
                        device.LastUpdated = DateTime.Now;
                    }
                    else
                    {
                        await CheckDeviceAsync(device, logEveryCheck: false);
                    }
                }
            }
            finally
            {
                semaphore.Release();
            }
        });
        await Task.WhenAll(tasks);
        _vm.RefreshCounts();
        }
        finally
        {
            _heartbeatBusy = false;
        }
    }

    private async Task RefreshActiveScriptStatusesAsync()
    {
        if (_scriptStatusBusy) return;
        var active = _vm.Devices
            .Where(device => device.IsOnline &&
                device.ScriptState is ScriptState.Running or
                    ScriptState.Sending or ScriptState.Queued)
            .ToArray();
        if (active.Length == 0) return;

        _scriptStatusBusy = true;
        var pointResultChanged = 0;
        try
        {
            using var semaphore = new SemaphoreSlim(32, 32);
            var tasks = active.Select(async device =>
            {
                await semaphore.WaitAsync(_lifetimeCts.Token);
                try
                {
                    var previousState = device.ScriptState;
                    bool? running;
                    if (device.SupportsTikTokPoint)
                    {
                        var agentLogs = await _client.GetAgentLogsAsync(
                            device.Ip, device.Port, _lifetimeCts.Token);
                        running = agentLogs.Running;
                        if (ApplyTikTokPointEvents(device, agentLogs.Logs))
                            Interlocked.Exchange(ref pointResultChanged, 1);
                    }
                    else
                    {
                        var health = await _client.GetHealthAsync(
                            device.Ip, device.Port, _lifetimeCts.Token);
                        running = health.Running;
                    }
                    if (running == true)
                    {
                        device.ScriptStartedAt ??= DateTime.Now;
                        device.ScriptFinishedAt = null;
                        device.ScriptState = ScriptState.Running;
                        device.LastUpdated = DateTime.Now;
                        return;
                    }

                    if (running != false) return;

                    var updated = await _client.GetDeviceInfoAsync(
                        device.Ip, device.Port, _lifetimeCts.Token);
                    if (updated is null) return;
                    device.Apply(updated);
                    if (device.ScriptState == ScriptState.Completed)
                        device.ScriptState = ScriptState.Stopped;
                    if ((previousState is ScriptState.Running or
                            ScriptState.Sending or ScriptState.Queued) &&
                        (device.ScriptState is ScriptState.Stopped or ScriptState.Error))
                    {
                        _log.Add(device.Name, device.Ip, "Lua.",
                            device.ScriptState == ScriptState.Error
                                ? "Kết thúc do lỗi."
                                : $"Đã dừng sau {device.ScriptText.Replace("Đã dừng ", "")}.",
                            device.ScriptState == ScriptState.Error
                                ? LogLevel.Error
                                : LogLevel.Success,
                            device.LastScriptError);
                    }
                }
                catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested) { }
                catch
                {
                    if (SupportsPresenceCommands(device))
                    {
                        var health = await _agentKeepAliveService.GetHealthAsync(
                            device.Ip, _lifetimeCts.Token);
                        if (health?.Running == false)
                        {
                            device.ScriptFinishedAt ??= DateTime.Now;
                            device.ScriptState = ScriptState.Stopped;
                        }
                    }
                }
                finally
                {
                    semaphore.Release();
                }
            });
            await Task.WhenAll(tasks);
            if (pointResultChanged != 0)
                await PersistTikTokPointResultsAsync(_lifetimeCts.Token);
            _vm.RefreshCounts();
        }
        finally
        {
            _scriptStatusBusy = false;
        }
    }

    private async void CheckDevice_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is DeviceInfo device) await CheckDeviceAsync(device);
    }

    private async Task CheckDeviceAsync(DeviceInfo device, bool logEveryCheck = true)
    {
        var wasOnline = device.IsOnline;
        if (logEveryCheck)
            device.ConnectionState = ConnectionState.Checking;
        if (logEveryCheck)
            _log.Add(device.Name, device.Ip, $"Kiểm tra {device.Endpoint}...", "Đang xử lý.");
        try
        {
            var updated = await _client.GetDeviceInfoAsync(
                device.Ip, device.Port, _lifetimeCts.Token);
            if (updated is null) throw new InvalidOperationException("Thiếu data trong response.");
            var duplicate = FindByDeviceId(updated.DeviceId);
            if (duplicate is not null && !ReferenceEquals(duplicate, device))
                throw new InvalidOperationException($"Device ID trùng với {duplicate.Name}.");
            device.Apply(updated);
            device.ConsecutiveConnectionFailures = 0;
            if (logEveryCheck || !wasOnline)
                _log.Add(device.Name, device.Ip, "Kiểm tra kết nối.",
                    $"Online — {device.Model}, iOS {device.IosVersion}, XXTouch {device.XXTouchVersion}.",
                    LogLevel.Success);
            if (!string.IsNullOrWhiteSpace(device.LastScriptError))
                _log.Add(device.Name, device.Ip, "Lua runtime.", "Script lỗi.",
                    LogLevel.Error, device.LastScriptError);
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested) { }
        catch (Exception ex)
        {
            // Never reuse a stale positive home_ready after an HTTP failure.
            device.ClearHomeReadiness();
            if (_agentKeepAliveService.IsConnected(device.Ip))
            {
                device.ConsecutiveConnectionFailures = 0;
                device.ConnectionState = ConnectionState.Online;
                device.LastUpdated = DateTime.Now;
                if (logEveryCheck)
                    _log.Add(device.Name, device.Ip, "Kênh nền.",
                        "Agent còn kết nối; HTTP đang ngủ.", LogLevel.Warning);
                return;
            }
            device.ConsecutiveConnectionFailures++;
            device.LastUpdated = DateTime.Now;
            var confirmedOffline = device.ConsecutiveConnectionFailures >= 3;
            if (confirmedOffline)
            {
                device.ConnectionState = ConnectionState.Offline;
                device.ScriptState = ScriptState.Unknown;
                device.ClearHomeReadiness();
            }
            else
            {
                device.ConnectionState = wasOnline
                    ? ConnectionState.Online
                    : ConnectionState.Offline;
            }
            if (confirmedOffline && (logEveryCheck || wasOnline))
                _log.Add(device.Name, device.Ip, "Kiểm tra kết nối.", "Offline.",
                    LogLevel.Error, ex.Message);
        }
    }

    private async void StartDevice_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not DeviceInfo device) return;
        if (OnlyHomeReadyCheckBox.IsChecked == true)
        {
            var filter = await CheckHomeReadyAsync([device], updateSelection: false);
            if (filter.Ready.Count == 0)
            {
                device.SetRepeatProgress(0, 0);
                _log.Add(device.Name, device.Ip, "Bỏ qua Start.",
                    device.HomeReadyDetail, LogLevel.Warning);
                MessageBox.Show(
                    $"{device.Name} chưa sẵn sàng ở Home.\n\n{device.HomeReadyDetail}",
                    "Không chạy Lua", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
        }
        await StartScriptRepeatedAsync(device, true);
    }

    private Task StartScriptOnDeviceAsync(
        DeviceInfo device, bool askIfRunning, CancellationToken scheduleToken) =>
        RunDeviceExclusiveAsync(
            device, () => StartScriptOnDeviceCoreAsync(device, askIfRunning),
            scheduleToken);

    private async Task StartScriptRepeatedAsync(DeviceInfo device, bool askIfRunning)
    {
        var scheduleCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
        _scriptRunCancellations.AddOrUpdate(
            device,
            scheduleCts,
            (_, previous) =>
            {
                previous.Cancel();
                return scheduleCts;
            });

        var scheduleToken = scheduleCts.Token;
        var repeat = ReadBounded(LuaRepeatTextBox.Text, 1, 1, 1000);
        device.SetRepeatProgress(1, repeat);
        try
        {
            for (var pass = 1; pass <= repeat; pass++)
            {
                scheduleToken.ThrowIfCancellationRequested();
                device.SetRepeatProgress(pass, repeat);
                _log.Add(device.Name, device.Ip, "Lặp Lua.",
                    $"Bắt đầu lần {pass}/{repeat}.", LogLevel.Info);
                await StartScriptOnDeviceAsync(
                    device, askIfRunning && pass == 1, scheduleToken);
                scheduleToken.ThrowIfCancellationRequested();
                if (pass == repeat) break;

                var deadline = DateTime.UtcNow + TimeSpan.FromHours(24);
                while (DateTime.UtcNow < deadline)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), scheduleToken);
                    await CheckDeviceAsync(device);
                    scheduleToken.ThrowIfCancellationRequested();
                    if (device.StoppedByUser)
                    {
                        _log.Add(device.Name, device.Ip, "Lặp Lua.",
                            $"Hủy các lần còn lại sau khi Stop ({pass}/{repeat}).",
                            LogLevel.Warning);
                        return;
                    }
                    if (device.ScriptState is
                        ScriptState.Stopped or ScriptState.Completed or ScriptState.Error)
                        break;
                }

                if (device.ScriptState == ScriptState.Error)
                {
                    _log.Add(device.Name, device.Ip, "Lặp Lua.",
                        $"Dừng các lần còn lại vì lần {pass}/{repeat} bị lỗi.",
                        LogLevel.Error, device.LastScriptError);
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (scheduleToken.IsCancellationRequested)
        {
            _log.Add(device.Name, device.Ip, "Lặp Lua.",
                "Đã hủy hàng đợi lặp.", LogLevel.Warning);
        }
        finally
        {
            var entry = new KeyValuePair<DeviceInfo, CancellationTokenSource>(
                device, scheduleCts);
            ((ICollection<KeyValuePair<DeviceInfo, CancellationTokenSource>>)
                _scriptRunCancellations).Remove(entry);
            scheduleCts.Dispose();
        }
    }

    private async Task<bool> UploadLuaAssetsAsync(DeviceInfo device, string luaContent)
    {
        if (string.IsNullOrWhiteSpace(_vm.LuaFile)) return true;
        var scriptDirectory = Path.GetDirectoryName(_vm.LuaFile);
        if (string.IsNullOrWhiteSpace(scriptDirectory)) return true;
        var imageDirectory = Path.Combine(scriptDirectory, "images");
        if (!Directory.Exists(imageDirectory)) return true;

        var referencedNames = Regex.Matches(
                luaContent, @"(?i)(?<name>[A-Za-z0-9_.-]+\.(?:png|jpe?g))")
            .Select(match => Path.GetFileName(match.Groups["name"].Value))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (referencedNames.Count == 0) return true;

        foreach (var path in Directory.EnumerateFiles(imageDirectory)
                     .Where(path => referencedNames.Contains(Path.GetFileName(path))))
        {
            var name = Path.GetFileName(path);
            try
            {
                var bytes = await File.ReadAllBytesAsync(path, _lifetimeCts.Token);
                var upload = await _client.UploadAssetAsync(
                    device.Ip, device.Port, name, bytes, _lifetimeCts.Token);
                if (!upload.Success)
                {
                    _log.Add(device.Name, device.Ip, "Upload ảnh Lua.",
                        $"Không gửi được {name}: {upload.Message}", LogLevel.Error);
                    return false;
                }
                _log.Add(device.Name, device.Ip, "Upload ảnh Lua.",
                    $"Đã gửi {name} ({bytes.Length:N0} byte).", LogLevel.Success);
            }
            catch (Exception ex)
            {
                _log.Add(device.Name, device.Ip, "Upload ảnh Lua.",
                    $"Lỗi {name}: {ex.Message}", LogLevel.Error);
                return false;
            }
        }
        return true;
    }

    private async Task StartScriptOnDeviceCoreAsync(DeviceInfo device, bool askIfRunning)
    {
        if (!device.IsOnline && !_agentKeepAliveService.IsConnected(device.Ip))
        {
            _log.Add(device.Name, device.Ip, "Start script.", "Thiết bị Offline.", LogLevel.Warning);
            return;
        }
        if (device.ScriptState == ScriptState.Running && askIfRunning &&
            MessageBox.Show($"{device.Name} đang chạy script. Gửi script mới?", "Xác nhận Start",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        string content;
        try { content = await ReadLuaContentAsync(); }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "File Lua không hợp lệ",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        if (!await UploadLuaAssetsAsync(device, content))
        {
            device.ScriptState = ScriptState.Error;
            return;
        }

        device.StoppedByUser = false;
        device.ScriptState = ScriptState.Sending;
        _log.Add(device.Name, device.Ip, $"Đang gửi {_vm.LuaFileName}...", "POST /spawn.");
        ApiResult result;
        if (SupportsPresenceCommands(device))
        {
            _log.Add(device.Name, device.Ip, "Kênh nền.",
                "Gửi Lua qua TCP 46955.", LogLevel.Info);
            result = await _agentKeepAliveService.RunLuaAsync(
                device.Ip, content, _lifetimeCts.Token);
        }
        else
        {
            result = await _client.StartScriptAsync(
                device.Ip, device.Port, content, _lifetimeCts.Token);
        }
        if (!result.Success)
        {
            device.ScriptState = ScriptState.Error;
            _log.Add(device.Name, device.Ip, "/spawn.", "Thất bại.",
                LogLevel.Error, $"code={result.Code?.ToString() ?? "null"}; {result.Message}");
            return;
        }
        device.RunId = result.RunId;
        device.ScriptStartedAt = DateTime.Now;
        device.ScriptFinishedAt = null;
        _log.Add(device.Name, device.Ip, "/spawn.",
            $"code={result.Code}; {result.Message}; run_id={result.RunId ?? "-"}.",
            LogLevel.Success);
        await Task.Delay(350, _lifetimeCts.Token);
        await CheckDeviceAsync(device);
    }

    private bool SupportsPresenceCommands(DeviceInfo device) =>
        _agentKeepAliveService.IsConnected(device.Ip) &&
        device.XXTouchVersion?.StartsWith(
            "LuaAgent ", StringComparison.OrdinalIgnoreCase) == true;

    private async void StopDevice_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is DeviceInfo device) await StopScriptOnDeviceAsync(device);
    }

    private Task StopScriptOnDeviceAsync(DeviceInfo device)
    {
        if (CancelScriptSchedule(device))
            _log.Add(device.Name, device.Ip, "Stop script.",
                "Đã hủy các lần Lua đang chờ/lặp.", LogLevel.Warning);
        return RunDeviceExclusiveAsync(device, () => StopScriptOnDeviceCoreAsync(device));
    }

    private bool CancelScriptSchedule(DeviceInfo device)
    {
        if (!_scriptRunCancellations.TryGetValue(device, out var cancellation) ||
            cancellation.IsCancellationRequested)
            return false;
        cancellation.Cancel();
        return true;
    }

    private async Task StopScriptOnDeviceCoreAsync(DeviceInfo device)
    {
        // Never block Stop because of cached state. /recycle is idempotent and
        // the Agent may still be reachable while a large batch has not yet
        // refreshed the Online badge or presence session.
        if (!device.IsOnline && !_agentKeepAliveService.IsConnected(device.Ip))
        {
            _log.Add(device.Name, device.Ip, "Stop script.",
                "Trạng thái đang Offline; vẫn thử gửi lệnh dừng.", LogLevel.Warning);
        }
        _log.Add(device.Name, device.Ip, "Đang dừng script...", "POST /recycle.");
        var result = await _client.StopScriptAsync(
            device.Ip, device.Port, _lifetimeCts.Token);
        if (!result.Success)
        {
            // /recycle joins the Lua thread before replying.  On a busy 6s/7
            // that response can time out although the Agent has already
            // received the cancel request.  Confirm the real state before
            // reporting an error or leaving the Stop button stuck.
            if (!await WaitUntilScriptStoppedAsync(device))
            {
                _log.Add(device.Name, device.Ip, "/recycle.", "Thất bại.",
                    LogLevel.Error,
                    $"code={result.Code?.ToString() ?? "null"}; {result.Message}. " +
                    "Chưa xác nhận được trạng thái; có thể bấm Stop lại.");
                return;
            }

            device.ScriptFinishedAt ??= DateTime.Now;
            device.StoppedByUser = true;
            device.ScriptState = ScriptState.Stopped;
            _log.Add(device.Name, device.Ip, "/recycle.",
                "Agent đã dừng (xác nhận sau timeout HTTP).", LogLevel.Success);
            return;
        }
        _log.Add(device.Name, device.Ip, "/recycle.",
            $"code={result.Code}; {result.Message}.", LogLevel.Success);
        device.ScriptFinishedAt ??= DateTime.Now;
        device.StoppedByUser = true;
        device.ScriptState = ScriptState.Stopped;
        await Task.Delay(250, _lifetimeCts.Token);
        await CheckDeviceAsync(device);
    }

    private async Task<bool> WaitUntilScriptStoppedAsync(DeviceInfo device)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            if (_agentKeepAliveService.IsConnected(device.Ip))
            {
                try
                {
                    using var presenceCts =
                        CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
                    presenceCts.CancelAfter(TimeSpan.FromSeconds(2));
                    var presenceHealth = await _agentKeepAliveService.GetHealthAsync(
                        device.Ip, presenceCts.Token);
                    if (presenceHealth?.Running == false) return true;
                }
                catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    // Fall through to the HTTP health endpoint.
                }
            }

            try
            {
                using var probeCts =
                    CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
                probeCts.CancelAfter(TimeSpan.FromSeconds(2));
                var health = await _client.GetHealthAsync(
                    device.Ip, device.Port, probeCts.Token);
                if (health.Running == false) return true;
            }
            catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // A transient HTTP failure is expected while /recycle is
                // joining the script thread; keep polling before declaring a
                // real failure.
            }
            await Task.Delay(500, _lifetimeCts.Token);
        }
        return false;
    }

    private async void UpdateAgent_Click(object sender, RoutedEventArgs e)
    {
        if (_agentUpdateBusy)
        {
            MessageBox.Show("Một đợt cập nhật Agent đang chạy.", "Cập nhật Agent",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var selected = SelectedDevices();
        if (selected.Count == 0) { ShowNoSelection(); return; }

        var dialog = new OpenFileDialog
        {
            Title = "Chọn TIPA/IPA cập nhật LuaAgent",
            Filter = "TrollStore package (*.tipa;*.ipa)|*.tipa;*.ipa",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true) return;

        var info = new FileInfo(dialog.FileName);
        if (info.Length is < 1024 or > 500 * 1024 * 1024)
        {
            MessageBox.Show("Kích thước TIPA/IPA không hợp lệ.", "Cập nhật Agent",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var confirmation =
            $"Phát {info.Name} ({FormatSize(info.Length)}) qua LAN tới " +
            $"{selected.Count} thiết bị?\n\n" +
            "Controller sẽ mở TrollStore và tự bấm Install qua VNC. " +
            "Ứng dụng KHÔNG tự khởi động lại Agent sau khi cài. Nếu thiết bị Offline, " +
            "hãy mở TrollVNC hoặc bấm Apply trên iPhone.";
        if (MessageBox.Show(confirmation, "Xác nhận cập nhật Agent",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        _agentUpdateBusy = true;
        var updateButton = sender as Button;
        if (updateButton is not null) updateButton.IsEnabled = false;
        var accepted = new ConcurrentDictionary<string, DeviceInfo>();

        try
        {
            await using var server = new TipaDistributionServer(dialog.FileName);
            server.DownloadCompleted += address => Dispatcher.BeginInvoke(() =>
                _log.Add("Cập nhật", address.ToString(), "Tải TIPA.",
                    "Thiết bị đã tải xong file.", LogLevel.Success));
            server.Start();
            _log.Add("Cập nhật", "-", "Máy chủ TIPA.",
                $"Đang nghe cổng {server.Port}; file {info.Name}.", LogLevel.Success);

            await RunBatchAsync(selected, async device =>
            {
                if (!device.IsOnline)
                {
                    _log.Add(device.Name, device.Ip, "Cập nhật Agent.",
                        "Bỏ qua vì thiết bị Offline.", LogLevel.Warning);
                    return;
                }
                var uri = server.GetDownloadUri(device.Ip);
                var result = await _client.InstallAgentUpdateAsync(
                    device.Ip, device.Port, uri, _lifetimeCts.Token);
                if (!result.Success)
                {
                    _log.Add(device.Name, device.Ip, "Cập nhật Agent.",
                        "Thiết bị từ chối API; không mở Safari/Magnifier.", LogLevel.Error,
                        $"HTTP={result.HttpStatus}; code={result.Code}; {result.Message}");
                    return;
                }
                // Always click through RFB. TrollStore's preference cache is
                // not reliable across the supported iOS/TrollStore versions.
                await Task.Delay(TimeSpan.FromSeconds(5), _lifetimeCts.Token);
                var vncInstall = await VncPointerClient.ClickInstallAsync(
                    device.Ip, 5901, _lifetimeCts.Token);
                _log.Add(device.Name, device.Ip, "Auto Install qua VNC.",
                    vncInstall.Message,
                    vncInstall.Success ? LogLevel.Success : LogLevel.Warning);
                if (!vncInstall.Success)
                {
                    var install = await _client.StartScriptAsync(
                        device.Ip, device.Port, TrollStoreInstallScript,
                        _lifetimeCts.Token);
                    if (!install.Success)
                    {
                        _log.Add(device.Name, device.Ip, "Auto Install.",
                            "Không click được bằng VNC hoặc Lua.", LogLevel.Error,
                            $"HTTP={install.HttpStatus}; code={install.Code}; {install.Message}");
                        return;
                    }
                }
                accepted[device.Ip] = device;
                _log.Add(device.Name, device.Ip, "Mở TrollStore.",
                    $"Đã gửi URL {uri}.", LogLevel.Success);
            }, trackProgress: true);

            if (accepted.IsEmpty)
            {
                MessageBox.Show(
                    "Không thiết bị nào nhận được lệnh cập nhật trực tiếp hoặc qua VNC. " +
                    "Kiểm tra URL Scheme của TrollStore, VNC cổng 5901 và trạng thái màn hình.",
                    "Cập nhật Agent", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(2), _lifetimeCts.Token);
            _vm.BatchStatus = $"Đã tự bấm Install: {accepted.Count}/{selected.Count}";
            await SaveAsync();
            MessageBox.Show(
                $"Đã gửi và tự bấm Install: {accepted.Count}/{selected.Count}.\n" +
                $"TrollStore đã tải file: {server.DownloadCount} lượt.\n\n" +
                "Không tự khởi động lại Agent. Nếu máy Offline, hãy mở TrollVNC hoặc bấm Apply.",
                "Cập nhật Agent", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _log.Add("Cập nhật", "-", "Cập nhật Agent.", "Thất bại.",
                LogLevel.Error, ex.Message);
            MessageBox.Show(ex.Message, "Cập nhật Agent",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _agentUpdateBusy = false;
            if (updateButton is not null) updateButton.IsEnabled = true;
        }
    }

    private async void FilterHomeReady_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.Devices.Count == 0) return;
        try
        {
            var result = await CheckHomeReadyAsync(
                _vm.Devices.ToArray(), updateSelection: true);
            _log.Add("Lọc Home", "-", "Lọc máy sẵn sàng.",
                $"Đã chọn {result.Ready.Count}/{result.Total}; bỏ qua {result.Skipped}; " +
                $"Agent cũ {result.Unsupported}; không liên lạc {result.Unreachable}.",
                result.Ready.Count > 0 ? LogLevel.Success : LogLevel.Warning);
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested) { }
    }

    private async Task<HomeReadyFilterResult> CheckHomeReadyAsync(
        IEnumerable<DeviceInfo> devices, bool updateSelection)
    {
        var deviceList = devices.Distinct().ToArray();
        if (deviceList.Length == 0)
            return new HomeReadyFilterResult([], 0, 0, 0);

        await _homeReadyFilterGate.WaitAsync(_lifetimeCts.Token);
        FilterHomeReadyButton.IsEnabled = false;
        try
        {
            using var semaphore = new SemaphoreSlim(
                Math.Min(32, deviceList.Length), Math.Min(32, deviceList.Length));
            var readySet = new ConcurrentDictionary<DeviceInfo, byte>();
            var completed = 0;
            var unsupported = 0;
            var unreachable = 0;
            _vm.BatchStatus = $"Đang lọc Home 0/{deviceList.Length} · sẵn sàng 0";

            var tasks = deviceList.Select(async device =>
            {
                await semaphore.WaitAsync(_lifetimeCts.Token);
                try
                {
                    if (!device.IsOnline && !_agentKeepAliveService.IsConnected(device.Ip))
                    {
                        device.ClearHomeReadiness();
                        if (updateSelection) device.IsSelected = false;
                        Interlocked.Increment(ref unreachable);
                        return;
                    }

                    var first = await _client.GetDeviceInfoAsync(
                        device.Ip, device.Port, _lifetimeCts.Token);
                    if (first is null)
                        throw new InvalidOperationException("Thiếu data từ /deviceinfo.");
                    device.Apply(first);

                    if (!device.SupportsReliableHomeReady)
                    {
                        device.ClearHomeReadiness();
                        if (updateSelection) device.IsSelected = false;
                        Interlocked.Increment(ref unsupported);
                        return;
                    }

                    // A second positive sample prevents an app transition or a
                    // briefly visible SpringBoard frame from entering the queue.
                    if (device.HomeReady == true)
                    {
                        await Task.Delay(450, _lifetimeCts.Token);
                        var second = await _client.GetDeviceInfoAsync(
                            device.Ip, device.Port, _lifetimeCts.Token);
                        if (second is null)
                            throw new InvalidOperationException("Thiếu mẫu xác nhận home_ready.");
                        device.Apply(second);
                    }

                    if (device.HomeReady == true)
                        readySet.TryAdd(device, 0);
                    else if (device.HomeReady is null)
                        Interlocked.Increment(ref unsupported);

                    if (updateSelection) device.IsSelected = device.HomeReady == true;
                }
                catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested) { }
                catch (Exception ex)
                {
                    device.ClearHomeReadiness();
                    if (updateSelection) device.IsSelected = false;
                    Interlocked.Increment(ref unreachable);
                    _log.Add(device.Name, device.Ip, "Kiểm tra Home.",
                        "Không đọc được trạng thái sẵn sàng.", LogLevel.Warning, ex.Message);
                }
                finally
                {
                    var current = Interlocked.Increment(ref completed);
                    _vm.BatchStatus =
                        $"Đang lọc Home {current}/{deviceList.Length} · " +
                        $"sẵn sàng {readySet.Count}";
                    semaphore.Release();
                }
            });

            await Task.WhenAll(tasks);
            var ready = deviceList.Where(readySet.ContainsKey).ToArray();
            var result = new HomeReadyFilterResult(
                ready, deviceList.Length, unsupported, unreachable);
            _vm.BatchStatus =
                $"Sẵn sàng {ready.Length}/{deviceList.Length} · bỏ qua {result.Skipped}";
            return result;
        }
        finally
        {
            FilterHomeReadyButton.IsEnabled = true;
            _homeReadyFilterGate.Release();
        }
    }

    private void LogSkippedHomeDevices(
        IEnumerable<DeviceInfo> requested, IReadOnlyCollection<DeviceInfo> ready)
    {
        var readySet = ready.ToHashSet();
        foreach (var device in requested.Where(device => !readySet.Contains(device)))
        {
            device.SetRepeatProgress(0, 0);
            _log.Add(device.Name, device.Ip, "Bỏ qua Start.",
                device.HomeReadyDetail, LogLevel.Warning);
        }
    }

    private async void StartSelected_Click(object sender, RoutedEventArgs e)
    {
        var requested = SelectedDevices();
        if (requested.Count == 0) { ShowNoSelection(); return; }

        var selected = requested;
        var skipped = 0;
        if (OnlyHomeReadyCheckBox.IsChecked == true)
        {
            var filter = await CheckHomeReadyAsync(requested, updateSelection: false);
            selected = filter.Ready.ToList();
            skipped = filter.Skipped;
            LogSkippedHomeDevices(requested, filter.Ready);
            if (selected.Count == 0)
            {
                MessageBox.Show(
                    "Không có máy nào đồng thời sáng màn hình, đã mở khóa và đang ở Home.\n\n" +
                    "Máy hiện 'Cần Agent 3.0' phải cập nhật Agent trước.",
                    "Không có máy sẵn sàng", MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }
        }

        if (selected.Any(d => d.ScriptState == ScriptState.Running) &&
            MessageBox.Show("Một số thiết bị đang chạy script. Gửi script mới tới các thiết bị đã chọn?",
                "Xác nhận Start hàng loạt", MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        var repeat = ReadBounded(LuaRepeatTextBox.Text, 1, 1, 1000);
        foreach (var device in selected)
        {
            device.SetRepeatProgress(1, repeat);
            device.ScriptState = ScriptState.Queued;
        }
        await RunBatchAsync(
            selected, d => StartScriptRepeatedAsync(d, false),
            applyBatchDelay: true, trackProgress: true);
        if (skipped > 0)
            _vm.BatchStatus = $"Hoàn tất {selected.Count}/{selected.Count} · bỏ qua {skipped}";
    }

    private async void StopSelected_Click(object sender, RoutedEventArgs e)
    {
        var selected = SelectedDevices();
        if (selected.Count == 0) { ShowNoSelection(); return; }
        await RunBatchAsync(selected, StopScriptOnDeviceAsync);
    }

    private async void CheckSelected_Click(object sender, RoutedEventArgs e)
    {
        var selected = SelectedDevices();
        if (selected.Count == 0) { ShowNoSelection(); return; }
        await RunBatchAsync(selected, device => CheckDeviceAsync(device));
        await SaveAsync();
    }

    private async void CollectTikTokUsernames_Click(object sender, RoutedEventArgs e)
    {
        if (_usernameBusy)
        {
            MessageBox.Show("Đang có một lượt lấy TikTok username.", "Đang xử lý",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var selected = SelectedDevices();
        if (selected.Count == 0) { ShowNoSelection(); return; }

        string luaContent;
        try
        {
            luaContent = Encoding.UTF8.GetString(
                ReadEmbeddedTikTokUsernameResource("read-tiktok-username.lua"));
            foreach (var assetName in TikTokUsernameAssetNames)
                _ = ReadEmbeddedTikTokUsernameResource(assetName);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Thiếu tài nguyên lấy username",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        _usernameCollectionCts?.Dispose();
        _usernameCollectionCts = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetimeCts.Token);
        var token = _usernameCollectionCts.Token;
        _usernameBusy = true;
        CollectTikTokUsernamesButton.IsEnabled = false;
        try
        {
            foreach (var device in selected)
                device.TikTokUsernameResult = "Đang chờ";

            await RunTikTokUsernameBatchAsync(selected, luaContent, token);

            var batchPath = Path.Combine(
                TikTokUsernameResultsDirectory,
                $"TikTok-usernames-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
            await WriteTikTokUsernameCsvAsync(selected, batchPath, token);
            _vm.UsernameBatchStatus = $"Hoàn tất {selected.Count} máy · {Path.GetFileName(batchPath)}";
            _log.Add("TikTok username", "-", "Xuất kết quả.", batchPath,
                LogLevel.Success);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            _vm.UsernameBatchStatus = "Đã hủy";
        }
        catch (Exception ex)
        {
            _vm.UsernameBatchStatus = "Lỗi lượt lấy username";
            _log.Add("TikTok username", "-", "Lấy hàng loạt.", "Thất bại.",
                LogLevel.Error, ex.Message);
            MessageBox.Show(ex.Message, "Lỗi lấy TikTok username",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _usernameBusy = false;
            CollectTikTokUsernamesButton.IsEnabled = true;
        }
    }

    private async Task RunTikTokUsernameBatchAsync(
        IReadOnlyCollection<DeviceInfo> devices,
        string luaContent,
        CancellationToken cancellationToken)
    {
        var deviceList = devices.ToArray();
        var limit = ReadBounded(ConcurrencyTextBox.Text, 5, 1, 20);
        using var semaphore = new SemaphoreSlim(limit, limit);
        var completed = 0;
        _vm.UsernameBatchStatus = $"Đang xử lý 0/{deviceList.Length}";

        var tasks = deviceList.Select(async device =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                await RunDeviceExclusiveAsync(
                    device,
                    () => CollectTikTokUsernameOnDeviceAsync(
                        device, luaContent, cancellationToken),
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                device.TikTokUsernameResult = "Đã hủy";
            }
            catch (Exception ex)
            {
                await CompleteTikTokUsernameAsync(
                    device, null, $"Lỗi: {ex.Message}", LogLevel.Error,
                    saveSnapshot: true, clearUsername: true);
            }
            finally
            {
                var current = Interlocked.Increment(ref completed);
                _vm.UsernameBatchStatus = $"Đang xử lý {current}/{deviceList.Length}";
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks);
    }

    private async Task CollectTikTokUsernameOnDeviceAsync(
        DeviceInfo device,
        string luaContent,
        CancellationToken cancellationToken)
    {
        device.TikTokUsernameResult = "Đang kiểm tra Agent";
        DeviceInfo current;
        try
        {
            current = await _client.GetDeviceInfoAsync(
                device.Ip, device.Port, cancellationToken)
                ?? throw new InvalidOperationException("Không có phản hồi /deviceinfo.");
            device.Apply(current);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await CompleteTikTokUsernameAsync(
                device, null, "Bỏ qua: Offline", LogLevel.Warning,
                saveSnapshot: false, clearUsername: false);
            return;
        }

        var agentReportsRunning = current.ScriptState == ScriptState.Running;
        if (!agentReportsRunning)
        {
            try
            {
                agentReportsRunning = await _client.GetRunningStatusAsync(
                    device.Ip, device.Port, cancellationToken) == true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await CompleteTikTokUsernameAsync(
                    device, null, "Bỏ qua: không xác minh được Agent đang rảnh",
                    LogLevel.Warning, saveSnapshot: false, clearUsername: false);
                return;
            }
        }

        if (agentReportsRunning)
        {
            await CompleteTikTokUsernameAsync(
                device, null, "Bỏ qua: Agent đang chạy Lua", LogLevel.Warning,
                saveSnapshot: false, clearUsername: false);
            return;
        }

        if (string.IsNullOrWhiteSpace(current.XXTouchVersion) ||
            !current.XXTouchVersion.StartsWith("LuaAgent ", StringComparison.OrdinalIgnoreCase))
        {
            await CompleteTikTokUsernameAsync(
                device, null, "Bỏ qua: cần LuaAgent", LogLevel.Warning,
                saveSnapshot: false, clearUsername: false);
            return;
        }

        device.TikTokUsername = null;
        device.TikTokUsernameResult = "Đang gửi ảnh nhận dạng";
        foreach (var assetName in TikTokUsernameAssetNames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bytes = ReadEmbeddedTikTokUsernameResource(assetName);
            var upload = await _client.UploadAssetAsync(
                device.Ip, device.Port, assetName, bytes, cancellationToken);
            if (!upload.Success)
            {
                await CompleteTikTokUsernameAsync(
                    device, null, $"Lỗi gửi ảnh {assetName}", LogLevel.Error,
                    saveSnapshot: false, clearUsername: true);
                return;
            }
        }

        device.TikTokUsernameResult = "Đang mở TikTok";
        var start = await _client.StartScriptAsync(
            device.Ip, device.Port, luaContent, cancellationToken);
        if (!start.Success)
        {
            await CompleteTikTokUsernameAsync(
                device, null, $"Lỗi Start: {start.Message}", LogLevel.Error,
                saveSnapshot: true, clearUsername: true);
            return;
        }

        device.RunId = start.RunId;
        device.ScriptStartedAt = DateTime.Now;
        device.ScriptFinishedAt = null;
        device.ScriptState = ScriptState.Running;
        _log.Add(device.Name, device.Ip, "Lấy TikTok username.",
            $"Đã chạy Lua; run_id={start.RunId ?? "-"}.", LogLevel.Info);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(150);
        var runIdDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        var consecutiveLogFailures = 0;
        string? expectedRunId = start.RunId;
        await Task.Delay(450, cancellationToken);

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AgentLogInfo agentLogs;
            try
            {
                agentLogs = await _client.GetAgentLogsAsync(
                    device.Ip, device.Port, cancellationToken);
                consecutiveLogFailures = 0;
            }
            catch (OperationCanceledException) when (
                !cancellationToken.IsCancellationRequested && consecutiveLogFailures < 3)
            {
                consecutiveLogFailures++;
                device.TikTokUsernameResult =
                    $"Đang chờ log ({consecutiveLogFailures}/3)";
                await Task.Delay(900, cancellationToken);
                continue;
            }
            catch (Exception) when (consecutiveLogFailures < 3)
            {
                consecutiveLogFailures++;
                device.TikTokUsernameResult =
                    $"Đang chờ log ({consecutiveLogFailures}/3)";
                await Task.Delay(900, cancellationToken);
                continue;
            }

            if (string.IsNullOrWhiteSpace(expectedRunId) &&
                !string.IsNullOrWhiteSpace(agentLogs.RunId))
                expectedRunId = agentLogs.RunId;

            var sameRun = string.IsNullOrWhiteSpace(expectedRunId) ||
                          string.Equals(expectedRunId, agentLogs.RunId,
                              StringComparison.OrdinalIgnoreCase);
            if (!sameRun)
            {
                if (DateTime.UtcNow >= runIdDeadline)
                {
                    await CompleteTikTokUsernameAsync(
                        device, null, "Lỗi: Agent trả log của lượt khác",
                        LogLevel.Error, saveSnapshot: true, clearUsername: true);
                    return;
                }
                await Task.Delay(700, cancellationToken);
                continue;
            }

            var terminal = FindTerminalTikTokUsernameEvent(agentLogs.Logs);
            if (terminal is not null)
            {
                await ApplyTikTokUsernameEventAsync(
                    device, terminal.Value.Event, terminal.Value.Detail);
                return;
            }

            var progress = FindLatestTikTokUsernameEvent(agentLogs.Logs);
            if (progress is not null)
                UpdateTikTokUsernameProgress(device, progress.Value.Event);

            if (!agentLogs.Running)
            {
                var detail = string.IsNullOrWhiteSpace(agentLogs.LastError)
                    ? "Lua kết thúc nhưng không trả USERNAME_FOUND"
                    : agentLogs.LastError;
                await CompleteTikTokUsernameAsync(
                    device, null, $"Lỗi: {detail}", LogLevel.Error,
                    saveSnapshot: true, clearUsername: true);
                return;
            }

            await Task.Delay(1200, cancellationToken);
        }

        await CompleteTikTokUsernameAsync(
            device, null, "Lỗi: quá 150 giây chưa có kết quả", LogLevel.Error,
            saveSnapshot: true, clearUsername: true);
    }

    private static (string Event, string Detail)? FindLatestTikTokUsernameEvent(
        IReadOnlyList<string> logs)
    {
        const string marker = "[TUR] EVENT|";
        for (var index = logs.Count - 1; index >= 0; index--)
        {
            var line = logs[index] ?? string.Empty;
            var markerIndex = line.IndexOf(marker, StringComparison.Ordinal);
            if (markerIndex < 0) continue;
            var payload = line[(markerIndex + marker.Length)..];
            var separator = payload.IndexOf('|');
            return separator < 0
                ? (payload.Trim(), string.Empty)
                : (payload[..separator].Trim(), payload[(separator + 1)..].Trim());
        }
        return null;
    }

    private static (string Event, string Detail)? FindTerminalTikTokUsernameEvent(
        IReadOnlyList<string> logs)
    {
        var terminalNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "USERNAME_FOUND",
            "SKIP_SCREEN_OFF",
            "APP_NOT_INSTALLED",
            "APP_FOREGROUND_FAILED",
            "NOT_LOGGED_IN",
            "POPUP_UNSUPPORTED",
            "PROFILE_IMAGE_NOT_FOUND",
            "USERNAME_NOT_FOUND",
            "KNOWN_PROMPT_IMAGE_MISSING",
            "OCR_ERROR"
        };

        const string marker = "[TUR] EVENT|";
        for (var index = logs.Count - 1; index >= 0; index--)
        {
            var line = logs[index] ?? string.Empty;
            var markerIndex = line.IndexOf(marker, StringComparison.Ordinal);
            if (markerIndex < 0) continue;
            var payload = line[(markerIndex + marker.Length)..];
            var separator = payload.IndexOf('|');
            var eventName = (separator < 0 ? payload : payload[..separator]).Trim();
            if (!terminalNames.Contains(eventName)) continue;
            var detail = separator < 0 ? string.Empty : payload[(separator + 1)..].Trim();
            return (eventName, detail);
        }
        return null;
    }

    private void UpdateTikTokUsernameProgress(DeviceInfo device, string eventName)
    {
        var text = eventName switch
        {
            "SCREEN_ON_CONFIRMED" => "Màn hình sáng · đang mở TikTok",
            "SCREEN_VISUAL_CONFIRMED" => "Đã xác nhận màn hình qua OCR · đang mở TikTok",
            "APP_OPENED" => "Đã mở TikTok · đang ổn định",
            "SETTINGS_RECOVERY" => "TikTok đang ở Settings · đang mở lại ứng dụng",
            "HOME_RECOVERY" => "TikTok về màn hình chính · đang mở lại ứng dụng",
            "APP_REOPENED" => "Đã mở lại TikTok · đang ổn định",
            "PROFILE_REOPEN_RETRY" => "Màn hình TikTok khác · đang khởi động lại một lần",
            "PROFILE_ALREADY_OPEN" => "Profile đã mở · đang đọc username",
            "IMAGE_TAPPED" => "Đã vào Profile · đang kiểm tra popup",
            "PROFILE_STABILIZING" => "Đang chờ Profile ổn định",
            "USERNAME_CANDIDATE" => "Đang xác minh username",
            "USERNAME_VERIFY_RETRY" => "Đang xác minh lại username",
            _ => null
        };
        if (text is not null) device.TikTokUsernameResult = text;
    }

    private async Task ApplyTikTokUsernameEventAsync(
        DeviceInfo device, string eventName, string detail)
    {
        switch (eventName)
        {
            case "USERNAME_FOUND":
            {
                var username = detail.Trim();
                if (!Regex.IsMatch(username, @"^@[A-Za-z0-9._]{2,32}$",
                        RegexOptions.CultureInvariant))
                {
                    await CompleteTikTokUsernameAsync(
                        device, null, "Lỗi: username trả về không hợp lệ",
                        LogLevel.Error, saveSnapshot: true, clearUsername: true);
                    return;
                }
                await CompleteTikTokUsernameAsync(
                    device, username, "Đã lấy thành công", LogLevel.Success,
                    saveSnapshot: false, clearUsername: true);
                return;
            }
            case "SKIP_SCREEN_OFF":
                await CompleteTikTokUsernameAsync(
                    device, null, "Bỏ qua: màn hình tắt", LogLevel.Warning,
                    saveSnapshot: false, clearUsername: true);
                return;
            case "APP_NOT_INSTALLED":
                await CompleteTikTokUsernameAsync(
                    device, null, "Lỗi: chưa cài TikTok", LogLevel.Error,
                    saveSnapshot: false, clearUsername: true);
                return;
            case "APP_FOREGROUND_FAILED":
                await CompleteTikTokUsernameAsync(
                    device, null, "Lỗi: không đưa được TikTok ra màn hình",
                    LogLevel.Error, saveSnapshot: true, clearUsername: true);
                return;
            case "NOT_LOGGED_IN":
                await CompleteTikTokUsernameAsync(
                    device, null, "Lỗi: TikTok chưa đăng nhập", LogLevel.Warning,
                    saveSnapshot: true, clearUsername: true);
                return;
            case "POPUP_UNSUPPORTED":
                await CompleteTikTokUsernameAsync(
                    device, null, "Lỗi: popup chưa hỗ trợ", LogLevel.Error,
                    saveSnapshot: true, clearUsername: true);
                return;
            case "PROFILE_IMAGE_NOT_FOUND":
                await CompleteTikTokUsernameAsync(
                    device, null, "Lỗi: không tìm thấy nút Profile", LogLevel.Error,
                    saveSnapshot: true, clearUsername: true);
                return;
            case "KNOWN_PROMPT_IMAGE_MISSING":
                await CompleteTikTokUsernameAsync(
                    device, null, "Lỗi: popup đúng nhưng thiếu ảnh nút", LogLevel.Error,
                    saveSnapshot: true, clearUsername: true);
                return;
            case "OCR_ERROR":
                await CompleteTikTokUsernameAsync(
                    device, null, "Lỗi OCR trên iPhone", LogLevel.Error,
                    saveSnapshot: true, clearUsername: true);
                return;
            default:
                await CompleteTikTokUsernameAsync(
                    device, null, "Lỗi: không đọc được username", LogLevel.Error,
                    saveSnapshot: true, clearUsername: true);
                return;
        }
    }

    private async Task CompleteTikTokUsernameAsync(
        DeviceInfo device,
        string? username,
        string result,
        LogLevel level,
        bool saveSnapshot,
        bool clearUsername)
    {
        if (clearUsername) device.TikTokUsername = null;
        if (!string.IsNullOrWhiteSpace(username)) device.TikTokUsername = username;
        device.TikTokUsernameResult = result;
        device.TikTokUsernameUpdatedAt = DateTime.Now;
        if (device.ScriptState == ScriptState.Running)
        {
            device.ScriptState = ScriptState.Completed;
            device.ScriptFinishedAt = DateTime.Now;
        }
        _log.Add(device.Name, device.Ip, "Lấy TikTok username.",
            string.IsNullOrWhiteSpace(username) ? result : $"{result}: {username}", level);

        if (saveSnapshot)
        {
            try
            {
                var path = await SaveTikTokUsernameSnapshotAsync(device, _lifetimeCts.Token);
                _log.Add(device.Name, device.Ip, "Lưu ảnh lỗi username.", path,
                    LogLevel.Warning);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.Add(device.Name, device.Ip, "Lưu ảnh lỗi username.", "Thất bại.",
                    LogLevel.Error, ex.Message);
            }
        }

        await PersistTikTokUsernameResultsAsync(_lifetimeCts.Token);
    }

    private async Task<string> SaveTikTokUsernameSnapshotAsync(
        DeviceInfo device, CancellationToken cancellationToken)
    {
        var directory = Path.Combine(TikTokUsernameResultsDirectory, "snapshots");
        Directory.CreateDirectory(directory);
        var safeIp = device.Ip.Replace(':', '-').Replace('.', '-');
        var path = Path.Combine(
            directory, $"{DateTime.Now:yyyyMMdd-HHmmss-fff}_{safeIp}.jpg");
        var bytes = await _client.GetSnapshotAsync(
            device.Ip, device.Port, cancellationToken);
        await File.WriteAllBytesAsync(path, bytes, cancellationToken);
        return path;
    }

    private async Task PersistTikTokUsernameResultsAsync(
        CancellationToken cancellationToken)
    {
        await _usernameResultSaveGate.WaitAsync(cancellationToken);
        try
        {
            await SaveAsync();
            var latestPath = Path.Combine(
                TikTokUsernameResultsDirectory, "TikTok-usernames-latest.csv");
            await WriteTikTokUsernameCsvAsync(
                _vm.Devices, latestPath, cancellationToken);
        }
        finally
        {
            _usernameResultSaveGate.Release();
        }
    }

    private async void ExportTikTokUsernames_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(TikTokUsernameResultsDirectory);
            var selected = SelectedDevices();
            var devices = selected.Count > 0 ? selected : _vm.Devices.ToList();
            var dialog = new SaveFileDialog
            {
                Title = "Xuất TikTok username",
                Filter = "CSV (*.csv)|*.csv",
                DefaultExt = ".csv",
                AddExtension = true,
                InitialDirectory = TikTokUsernameResultsDirectory,
                FileName = $"TikTok-usernames-{DateTime.Now:yyyyMMdd-HHmmss}.csv"
            };
            if (dialog.ShowDialog(this) != true) return;
            await WriteTikTokUsernameCsvAsync(
                devices, dialog.FileName, _lifetimeCts.Token);
            _log.Add("TikTok username", "-", "Xuất CSV.", dialog.FileName,
                LogLevel.Success);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Không xuất được CSV",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static async Task WriteTikTokUsernameCsvAsync(
        IEnumerable<DeviceInfo> devices,
        string path,
        CancellationToken cancellationToken)
    {
        var parent = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(parent)) Directory.CreateDirectory(parent);
        var builder = new StringBuilder();
        builder.AppendLine("STT,Tên thiết bị,Địa chỉ IP,Cổng,TikTok username,Kết quả,Cập nhật");
        foreach (var device in devices.OrderBy(device => device.DisplayIndex))
        {
            builder.Append(device.DisplayIndex).Append(',')
                .Append(CsvCell(device.Name)).Append(',')
                .Append(CsvCell(device.Ip)).Append(',')
                .Append(device.Port).Append(',')
                .Append(CsvCell(device.TikTokUsername)).Append(',')
                .Append(CsvCell(device.TikTokUsernameResult)).Append(',')
                .Append(CsvCell(device.TikTokUsernameUpdatedAt?.ToString(
                    "yyyy-MM-dd HH:mm:ss")))
                .AppendLine();
        }
        await File.WriteAllTextAsync(
            path, builder.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
            cancellationToken);
    }

    private static string CsvCell(string? value) =>
        $"\"{(value ?? string.Empty).Replace("\"", "\"\"")}\"";

    private static byte[] ReadEmbeddedTikTokUsernameResource(string fileName)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var suffix = $".TikTokUsername.{fileName}";
        var resourceName = assembly.GetManifestResourceNames()
            .SingleOrDefault(name => name.EndsWith(suffix, StringComparison.Ordinal));
        if (resourceName is null)
            throw new FileNotFoundException(
                $"Không tìm thấy tài nguyên tích hợp {fileName} trong EXE.");
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidDataException($"Không đọc được tài nguyên {fileName}.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private void OpenTikTokUsernameFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(TikTokUsernameResultsDirectory);
        Process.Start(new ProcessStartInfo(TikTokUsernameResultsDirectory)
        {
            UseShellExecute = true
        });
    }

    private async void RefreshTikTokPointResults_Click(object sender, RoutedEventArgs e)
    {
        if (_pointResultBusy) return;
        var selected = SelectedDevices();
        if (selected.Count == 0)
        {
            MessageBox.Show("Chưa đánh dấu thiết bị nào.", "TikTok Point",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _pointResultBusy = true;
        RefreshTikTokPointResultsButton.IsEnabled = false;
        _vm.PointResultStatus = $"Đang nạp 0/{selected.Count}";
        var completed = 0;
        var received = 0;
        var unsupported = 0;
        var failed = 0;
        var changed = 0;
        try
        {
            using var semaphore = new SemaphoreSlim(Math.Min(32, selected.Count));
            var tasks = selected.Select(async device =>
            {
                await semaphore.WaitAsync(_lifetimeCts.Token);
                try
                {
                    if (!device.IsOnline || !device.SupportsTikTokPoint)
                    {
                        Interlocked.Increment(ref unsupported);
                        return;
                    }
                    var agentLogs = await _client.GetAgentLogsAsync(
                        device.Ip, device.Port, _lifetimeCts.Token);
                    if (ApplyTikTokPointEvents(device, agentLogs.Logs))
                        Interlocked.Exchange(ref changed, 1);
                    Interlocked.Increment(ref received);
                }
                catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested) { }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref failed);
                    _log.Add(device.Name, device.Ip, "Nạp kết quả Point.",
                        "Không đọc được /logs.", LogLevel.Error, ex.Message);
                }
                finally
                {
                    var current = Interlocked.Increment(ref completed);
                    _vm.PointResultStatus = $"Đang nạp {current}/{selected.Count}";
                    semaphore.Release();
                }
            });
            await Task.WhenAll(tasks);
            if (changed != 0)
                await PersistTikTokPointResultsAsync(_lifetimeCts.Token);
            _vm.PointResultStatus =
                $"Đã nạp {received}/{selected.Count}; cần Agent 3.1/Offline {unsupported}; lỗi {failed}";
        }
        finally
        {
            _pointResultBusy = false;
            RefreshTikTokPointResultsButton.IsEnabled = true;
        }
    }

    private static IEnumerable<(string EventName, string Detail)> ReadTikTokPointEvents(
        IReadOnlyList<string> logs)
    {
        const string marker = "[TIKTOK_POINT] EVENT|";
        foreach (var rawLine in logs)
        {
            var line = rawLine ?? string.Empty;
            var markerIndex = line.IndexOf(marker, StringComparison.Ordinal);
            if (markerIndex < 0) continue;
            var payload = line[(markerIndex + marker.Length)..];
            var separator = payload.IndexOf('|');
            var eventName = (separator < 0 ? payload : payload[..separator]).Trim();
            var detail = separator < 0 ? string.Empty : payload[(separator + 1)..].Trim();
            if (eventName.Length > 0)
                yield return (eventName, detail);
        }
    }

    private static bool TryReadPointNumber(string text, out long value)
    {
        var digits = new string((text ?? string.Empty).Where(char.IsDigit).ToArray());
        return long.TryParse(digits, out value) && value is >= 0 and <= 1_000_000_000_000;
    }

    private static string CleanPointDetail(string detail, int maximumLength = 240)
    {
        var value = Regex.Replace(detail ?? string.Empty, @"[\r\n\t]+", " ").Trim();
        return value.Length <= maximumLength ? value : value[..maximumLength];
    }

    private static bool TryNormalizePointLink(string detail, out string link)
    {
        link = string.Empty;
        var candidate = CleanPointDetail(detail, 2048);
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            string.IsNullOrWhiteSpace(uri.Host))
            return false;
        link = uri.AbsoluteUri;
        return true;
    }

    private static string FormatPointPlan(string detail)
    {
        var raw = CleanPointDetail(detail).Split('|', 2)[0].Trim();
        if (raw.Equals("ALL", StringComparison.OrdinalIgnoreCase)) return "Tất cả";
        return TryReadPointNumber(raw, out var amount)
            ? amount.ToString("N0", System.Globalization.CultureInfo.GetCultureInfo("vi-VN"))
            : raw;
    }

    private bool ApplyTikTokPointEvents(DeviceInfo device, IReadOnlyList<string> logs)
    {
        var balance = device.TikTokPointBalance;
        var plan = device.TikTokPointPlan;
        var status = device.TikTokPointStatus;
        var link = device.TikTokPointLink;
        var recognizedAny = false;

        foreach (var pointEvent in ReadTikTokPointEvents(logs))
        {
            switch (pointEvent.EventName)
            {
                case "START":
                    recognizedAny = true;
                    balance = null;
                    plan = null;
                    link = null;
                    status = "Đang đọc số điểm";
                    break;
                case "BALANCE":
                    recognizedAny = true;
                    if (TryReadPointNumber(pointEvent.Detail, out var parsedBalance))
                    {
                        balance = parsedBalance;
                        status = "Đã đọc số điểm";
                    }
                    else
                    {
                        status = "Lỗi: số điểm không hợp lệ";
                    }
                    break;
                case "PLAN":
                    recognizedAny = true;
                    plan = FormatPointPlan(pointEvent.Detail);
                    status = "Đã xác định mức dự kiến";
                    break;
                case "STATUS":
                    recognizedAny = true;
                    status = CleanPointDetail(pointEvent.Detail);
                    break;
                case "LINK":
                    recognizedAny = true;
                    if (TryNormalizePointLink(pointEvent.Detail, out var normalizedLink))
                    {
                        link = normalizedLink;
                        status = "Đã nhận link · chưa xác nhận rút";
                    }
                    else
                    {
                        link = null;
                        status = "Lỗi: link trả về không hợp lệ";
                    }
                    break;
                case "READY":
                    recognizedAny = true;
                    status = "Sẵn sàng · dừng trước xác nhận rút";
                    break;
                case "SKIP":
                    recognizedAny = true;
                    link = null;
                    status = $"Bỏ qua: {CleanPointDetail(pointEvent.Detail)}";
                    break;
                case "ERROR":
                    recognizedAny = true;
                    status = $"Lỗi: {CleanPointDetail(pointEvent.Detail)}";
                    break;
                default:
                    break;
            }
        }

        if (!recognizedAny ||
            balance == device.TikTokPointBalance &&
            string.Equals(plan, device.TikTokPointPlan, StringComparison.Ordinal) &&
            string.Equals(status, device.TikTokPointStatus, StringComparison.Ordinal) &&
            string.Equals(link, device.TikTokPointLink, StringComparison.Ordinal))
            return false;

        device.TikTokPointBalance = balance;
        device.TikTokPointPlan = plan;
        device.TikTokPointStatus = status;
        device.TikTokPointLink = link;
        device.TikTokPointUpdatedAt = DateTime.Now;
        return true;
    }

    private async Task PersistTikTokPointResultsAsync(CancellationToken cancellationToken)
    {
        await _pointResultSaveGate.WaitAsync(cancellationToken);
        try
        {
            await SaveAsync();
            var latestPath = Path.Combine(
                TikTokPointResultsDirectory, "TikTok-Point-latest.csv");
            await WriteTikTokPointCsvAsync(_vm.Devices, latestPath, cancellationToken);
        }
        finally
        {
            _pointResultSaveGate.Release();
        }
    }

    private async void ExportTikTokPointResults_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(TikTokPointResultsDirectory);
            var selected = SelectedDevices();
            var devices = selected.Count > 0 ? selected : _vm.Devices.ToList();
            var dialog = new SaveFileDialog
            {
                Title = "Xuất kết quả TikTok Point",
                Filter = "CSV (*.csv)|*.csv",
                DefaultExt = ".csv",
                AddExtension = true,
                InitialDirectory = TikTokPointResultsDirectory,
                FileName = $"TikTok-Point-{DateTime.Now:yyyyMMdd-HHmmss}.csv"
            };
            if (dialog.ShowDialog(this) != true) return;
            await WriteTikTokPointCsvAsync(devices, dialog.FileName, _lifetimeCts.Token);
            _log.Add("TikTok Point", "-", "Xuất CSV.", dialog.FileName,
                LogLevel.Success);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Không xuất được CSV Point",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static async Task WriteTikTokPointCsvAsync(
        IEnumerable<DeviceInfo> devices,
        string path,
        CancellationToken cancellationToken)
    {
        var parent = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(parent)) Directory.CreateDirectory(parent);
        var builder = new StringBuilder();
        builder.AppendLine("STT,Tên thiết bị,Địa chỉ IP,Cổng,TikTok username,Số Point,Mức dự kiến,Trạng thái,Link Point,Cập nhật");
        foreach (var device in devices.OrderBy(device => device.DisplayIndex))
        {
            builder.Append(device.DisplayIndex).Append(',')
                .Append(CsvCell(device.Name)).Append(',')
                .Append(CsvCell(device.Ip)).Append(',')
                .Append(device.Port).Append(',')
                .Append(CsvCell(device.TikTokUsername)).Append(',')
                .Append(device.TikTokPointBalance?.ToString()).Append(',')
                .Append(CsvCell(device.TikTokPointPlan)).Append(',')
                .Append(CsvCell(device.TikTokPointStatus)).Append(',')
                .Append(CsvCell(device.TikTokPointLink)).Append(',')
                .Append(CsvCell(device.TikTokPointUpdatedAt?.ToString(
                    "yyyy-MM-dd HH:mm:ss")))
                .AppendLine();
        }
        await File.WriteAllTextAsync(
            path, builder.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
            cancellationToken);
    }

    private void OpenTikTokPointFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(TikTokPointResultsDirectory);
        Process.Start(new ProcessStartInfo(TikTokPointResultsDirectory)
        {
            UseShellExecute = true
        });
    }

    private async Task RunBatchAsync(
        IEnumerable<DeviceInfo> devices, Func<DeviceInfo, Task> operation,
        bool applyBatchDelay = false, bool trackProgress = false)
    {
        var deviceList = devices.ToArray();
        var limit = ReadBounded(ConcurrencyTextBox.Text, 5, 1, 50);
        var delaySeconds = applyBatchDelay
            ? ReadBounded(BatchDelayTextBox.Text, 0, 0, 3600)
            : 0;
        using var semaphore = new SemaphoreSlim(limit, limit);
        var completed = 0;
        if (trackProgress) _vm.BatchStatus = $"Đang xử lý 0/{deviceList.Length}";
        var tasks = deviceList.Select(async device =>
        {
            await semaphore.WaitAsync(_lifetimeCts.Token);
            try { await operation(device); }
            catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested) { }
            catch (Exception ex)
            {
                _log.Add(device.Name, device.Ip, "Thao tác hàng loạt.", "Lỗi độc lập.",
                    LogLevel.Error, ex.Message);
            }
            finally
            {
                try
                {
                    if (delaySeconds > 0 && !_lifetimeCts.IsCancellationRequested)
                        await Task.Delay(
                            TimeSpan.FromSeconds(delaySeconds), _lifetimeCts.Token);
                }
                catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested) { }
                finally
                {
                    if (trackProgress)
                    {
                        var current = Interlocked.Increment(ref completed);
                        _vm.BatchStatus = $"Đã xử lý {current}/{deviceList.Length}";
                    }
                    semaphore.Release();
                }
            }
        });
        await Task.WhenAll(tasks);
        if (trackProgress) _vm.BatchStatus = $"Hoàn tất {deviceList.Length}/{deviceList.Length}";
    }

    private async Task RunDeviceExclusiveAsync(
        DeviceInfo device, Func<Task> operation)
    {
        await RunDeviceExclusiveAsync(device, operation, _lifetimeCts.Token);
    }

    private async Task RunDeviceExclusiveAsync(
        DeviceInfo device, Func<Task> operation, CancellationToken cancellationToken)
    {
        var gate = _deviceOperationGates.GetOrAdd(
            device.Endpoint, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            await operation();
        }
        finally
        {
            gate.Release();
        }
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        var visibleDevices = string.IsNullOrWhiteSpace(DeviceSearchTextBox.Text) || _deviceView is null
            ? _vm.Devices.AsEnumerable()
            : _deviceView.Cast<DeviceInfo>();
        foreach (var device in visibleDevices) device.IsSelected = true;
    }

    private void ClearSelection_Click(object sender, RoutedEventArgs e)
    {
        foreach (var device in _vm.Devices) device.IsSelected = false;
    }

    private void DeviceSelection_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox checkBox && checkBox.DataContext is DeviceInfo device)
            device.IsSelected = checkBox.IsChecked == true;
    }

    private async void ChooseLua_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Chọn file Lua",
            Filter = "Lua script (*.lua)|*.lua",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) == true)
            await SelectLuaFileAsync(dialog.FileName, true);
    }

    private async Task SelectLuaFileAsync(string path, bool showErrors)
    {
        try
        {
            if (!File.Exists(path)) throw new FileNotFoundException("File không tồn tại.", path);
            if (!string.Equals(Path.GetExtension(path), ".lua", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Chỉ cho phép file có phần mở rộng .lua.");
            var content = await File.ReadAllTextAsync(
                path, new UTF8Encoding(false, true), _lifetimeCts.Token);
            if (string.IsNullOrWhiteSpace(content)) throw new InvalidDataException("File Lua đang rỗng.");
            var info = new FileInfo(path);
            _vm.LuaFile = info.FullName;
            _vm.LuaFileName = info.Name;
            _vm.LuaSize = FormatSize(info.Length);
            _vm.LuaUpdated = info.LastWriteTime.ToString("dd/MM/yyyy HH:mm:ss");
            _vm.Settings.LastLuaFile = info.FullName;
            _log.Add("Lua", "-", "Chọn file.", info.Name, LogLevel.Success);
            await SaveAsync();
        }
        catch (Exception ex)
        {
            if (showErrors)
                MessageBox.Show(ex.Message, "Không đọc được file Lua",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            _log.Add("Lua", "-", "Chọn file.", "Thất bại.", LogLevel.Error, ex.Message);
        }
    }

    private async Task<string> ReadLuaContentAsync()
    {
        if (string.IsNullOrWhiteSpace(_vm.LuaFile))
            throw new InvalidOperationException("Chưa chọn file Lua.");
        if (!File.Exists(_vm.LuaFile))
            throw new FileNotFoundException("File Lua đã bị di chuyển hoặc xóa.", _vm.LuaFile);
        if (!string.Equals(Path.GetExtension(_vm.LuaFile), ".lua", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("File được chọn không có phần mở rộng .lua.");
        var content = await File.ReadAllTextAsync(
            _vm.LuaFile, new UTF8Encoding(false, true), _lifetimeCts.Token);
        if (string.IsNullOrWhiteSpace(content)) throw new InvalidDataException("File Lua đang rỗng.");
        return content;
    }

    private void OpenLuaFolder_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_vm.LuaFile) || !File.Exists(_vm.LuaFile)) return;
        Process.Start(new ProcessStartInfo("explorer.exe",
            $"/select,\"{_vm.LuaFile}\"") { UseShellExecute = true });
    }

    private async void ViewScreen_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not DeviceInfo device) return;
        _snapshotCts?.Cancel();
        _snapshotCts?.Dispose();
        _snapshotCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
        _vm.ViewedDevice = device;
        await RefreshSnapshotAsync();
    }

    private async void RefreshSnapshot_Click(object sender, RoutedEventArgs e) =>
        await RefreshSnapshotAsync();

    private async Task RefreshSnapshotAsync()
    {
        var device = _vm.ViewedDevice;
        if (device is null || !device.IsOnline || _snapshotBusy) return;
        _snapshotBusy = true;
        SnapshotProgress.Visibility = Visibility.Visible;
        var token = _snapshotCts?.Token ?? _lifetimeCts.Token;
        try
        {
            var bytes = await _client.GetSnapshotAsync(device.Ip, device.Port, token);
            using var stream = new MemoryStream(bytes, writable: false);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            if (!token.IsCancellationRequested && ReferenceEquals(_vm.ViewedDevice, device))
            {
                _latestSnapshotJpeg = bytes;
                SnapshotImage.Source = null;
                SnapshotImage.Source = bitmap;
                _log.Add(device.Name, device.Ip, "Snapshot.", $"{bytes.Length:N0} byte.",
                    LogLevel.Success);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log.Add(device.Name, device.Ip, "Snapshot.", "Thất bại.", LogLevel.Error, ex.Message);
        }
        finally
        {
            SnapshotProgress.Visibility = Visibility.Collapsed;
            _snapshotBusy = false;
        }
    }

    private async void SettingsChanged_Click(object sender, RoutedEventArgs e)
    {
        ReadSettingsFromUi();
        UpdateTimerIntervals();
        if (_vm.Settings.AutoScan) _autoScanTimer.Start(); else _autoScanTimer.Stop();
        if (_vm.Settings.AutoSnapshot) _snapshotTimer.Start(); else _snapshotTimer.Stop();
        await SaveAsync();
    }

    private void ReadSettingsFromUi()
    {
        _vm.Settings.DefaultPort = ReadBounded(PortTextBox.Text, 46952, 1, 65535);
        _vm.Settings.ScanIntervalSeconds = ReadBounded(ScanIntervalTextBox.Text, 10, 3, 3600);
        _vm.Settings.AutoScan = AutoScanCheckBox.IsChecked == true;
        _vm.Settings.SnapshotIntervalSeconds = ReadBounded(SnapshotIntervalTextBox.Text, 2, 1, 3600);
        _vm.Settings.AutoSnapshot = AutoSnapshotCheckBox.IsChecked == true;
        _vm.Settings.ConcurrencyLimit = ReadBounded(ConcurrencyTextBox.Text, 5, 1, 50);
        _vm.Settings.BatchDelaySeconds = ReadBounded(BatchDelayTextBox.Text, 0, 0, 3600);
        _vm.Settings.OnlyRunHomeReady = OnlyHomeReadyCheckBox.IsChecked == true;
        _vm.Settings.Diagnostics = DiagnosticsCheckBox.IsChecked == true;
        _vm.Settings.AiModel = string.IsNullOrWhiteSpace(AiModelTextBox.Text)
            ? "gpt-5.6-terra"
            : AiModelTextBox.Text.Trim();
        _vm.Settings.AiIncludeSnapshot = AiIncludeSnapshotCheckBox.IsChecked == true;
        _vm.Settings.AiRedactNetworkData = AiRedactNetworkCheckBox.IsChecked == true;
    }

    private void UpdateTimerIntervals()
    {
        _autoScanTimer.Interval = TimeSpan.FromSeconds(Math.Max(3, _vm.Settings.ScanIntervalSeconds));
        _snapshotTimer.Interval = TimeSpan.FromSeconds(Math.Max(1, _vm.Settings.SnapshotIntervalSeconds));
    }

    private void OnDiagnostic(HttpDiagnostic diagnostic)
    {
        if (!_vm.Settings.Diagnostics) return;
        var detail = $"URL={diagnostic.Url}; HTTP={diagnostic.HttpStatus?.ToString() ?? "-"}; " +
                     $"Content-Type={diagnostic.ContentType ?? "-"}; {diagnostic.ElapsedMilliseconds}ms; " +
                     $"JSON code={diagnostic.JsonCode?.ToString() ?? "-"}; " +
                     $"message={diagnostic.JsonMessage ?? "-"}; exception={diagnostic.ExceptionType ?? "-"}";
        _log.Add("HTTP", "-", diagnostic.Method, detail,
            diagnostic.ExceptionType is null ? LogLevel.Info : LogLevel.Error);
    }

    private async Task SaveAsync()
    {
        if (!_loaded || _lifetimeCts.IsCancellationRequested) return;
        ReadSettingsFromUi();
        try
        {
            await _settingsService.SaveAsync(
                _vm.Settings, _vm.Devices, _lifetimeCts.Token);
        }
        catch (Exception ex)
        {
            _log.Add("Ứng dụng", "-", "Lưu cấu hình.", "Thất bại.", LogLevel.Error, ex.Message);
        }
    }

    private bool TryReadEndpoint(out string ip, out int port)
    {
        ip = IpTextBox.Text.Trim();
        port = 0;
        if (string.IsNullOrWhiteSpace(ip) ||
            (!IPAddress.TryParse(ip, out _) &&
             !Uri.CheckHostName(ip).Equals(UriHostNameType.Dns)))
        {
            MessageBox.Show("IP hoặc tên máy không hợp lệ.", "Dữ liệu không hợp lệ",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        if (!int.TryParse(PortTextBox.Text, out port) || port is < 1 or > 65535)
        {
            MessageBox.Show("Cổng phải nằm trong khoảng 1–65535.", "Dữ liệu không hợp lệ",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        return true;
    }

    private DeviceInfo? FindByEndpoint(string ip, int port) =>
        _vm.Devices.FirstOrDefault(d =>
            string.Equals(d.Ip, ip, StringComparison.OrdinalIgnoreCase) && d.Port == port);

    private DeviceInfo? FindByDeviceId(string? id) =>
        string.IsNullOrWhiteSpace(id) ? null :
        _vm.Devices.FirstOrDefault(d =>
            string.Equals(d.DeviceId, id, StringComparison.OrdinalIgnoreCase));

    private List<DeviceInfo> SelectedDevices()
    {
        // Commit any pending checkbox edit before a toolbar button reads the
        // model. This matters when the user ticks a device and immediately
        // starts/stops/deletes a batch.
        DeviceGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        DeviceGrid.CommitEdit(DataGridEditingUnit.Row, true);
        return _vm.Devices.Where(device => device.IsSelected).ToList();
    }

    private static int ReadBounded(string text, int fallback, int min, int max) =>
        int.TryParse(text, out var value) ? Math.Clamp(value, min, max) : fallback;

    private static string FormatSize(long bytes) =>
        bytes < 1024 ? $"{bytes} B" : $"{bytes / 1024d:0.##} KB";

    private static void ShowNoSelection() =>
        MessageBox.Show("Chưa đánh dấu thiết bị nào.", "XXTouch Controller",
            MessageBoxButton.OK, MessageBoxImage.Information);

    private void ClearLog_Click(object sender, RoutedEventArgs e) => _log.Clear();

    private void SaveAiKey_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var key = AiApiKeyBox.Password.Trim();
            if (key.Length < 20 || key.Any(char.IsWhiteSpace))
                throw new InvalidDataException("API key không đúng định dạng.");
            _credentialService.SaveApiKey(key);
            AiApiKeyBox.Clear();
            AiStatusText.Text = "API key đã lưu an toàn";
            _log.Add("AI", "-", "Lưu API key.",
                "Đã lưu trong Windows Credential Manager.", LogLevel.Success);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Không lưu được API key",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void DeleteAiKey_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(
                "Xóa OpenAI API key đã lưu trên máy tính này?",
                "Xác nhận xóa API key",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        try
        {
            _credentialService.DeleteApiKey();
            AiApiKeyBox.Clear();
            AiStatusText.Text = "Chưa lưu API key";
            _log.Add("AI", "-", "Xóa API key.", "Đã xóa.", LogLevel.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Không xóa được API key",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void AiSettingsChanged_Click(object sender, RoutedEventArgs e)
    {
        ReadSettingsFromUi();
        await SaveAsync();
    }

    private async void AnalyzeError_Click(object sender, RoutedEventArgs e)
    {
        if (_aiAnalysisCts is not null) return;

        string? key;
        try
        {
            key = _credentialService.ReadApiKey();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Không đọc được API key",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        if (string.IsNullOrWhiteSpace(key))
        {
            MessageBox.Show(
                "Nhập OpenAI API key rồi bấm “Lưu API key” trước khi phân tích.",
                "Chưa có API key", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        string? lua = null;
        try
        {
            if (_vm.HasLua) lua = await ReadLuaContentAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Không đọc được Lua",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        ReadSettingsFromUi();
        await SaveAsync();
        var logs = string.Join(Environment.NewLine,
            _log.Entries.TakeLast(150).Select(entry => entry.Display));
        var device = _vm.SelectedDevice ?? _vm.ViewedDevice;
        var deviceContext = device is null
            ? $"Tổng thiết bị: {_vm.TotalCount}; Online: {_vm.OnlineCount}; " +
              $"Đang chạy: {_vm.RunningCount}; Lỗi: " +
              $"{_vm.Devices.Count(item => item.ScriptState == ScriptState.Error)}"
            : $"Tên: {device.Name}; model: {device.Model ?? "không rõ"}; " +
              $"iOS: {device.IosVersion ?? "không rõ"}; agent: " +
              $"{device.XXTouchVersion ?? "không rõ"}; trạng thái: " +
              $"{device.ConnectionText}/{device.ScriptText}; runtime: " +
              $"{device.LastScriptError ?? "không có"}";

        if (_vm.Settings.AiRedactNetworkData)
        {
            logs = RedactNetworkData(logs);
            deviceContext = RedactNetworkData(deviceContext);
            if (lua is not null) lua = RedactNetworkData(lua);
        }

        var snapshot = _vm.Settings.AiIncludeSnapshot ? _latestSnapshotJpeg : null;
        if (_vm.Settings.AiIncludeSnapshot && snapshot is null)
        {
            var answer = MessageBox.Show(
                "Chưa có ảnh màn hình. Tiếp tục phân tích chỉ với log và Lua?",
                "Không có snapshot", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) return;
        }

        _aiAnalysisCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
        AnalyzeErrorButton.IsEnabled = false;
        CancelAnalysisButton.IsEnabled = true;
        SaveFixedLuaButton.IsEnabled = false;
        AiReportTextBox.Clear();
        AiFixedLuaTextBox.Clear();
        AiStatusText.Text = "Đang phân tích...";
        _log.Add("AI", "-", "Phân tích lỗi.", "Đang gửi dữ liệu đã chọn.");

        try
        {
            var request = new AiAnalysisRequest(
                logs,
                _vm.LuaFileName,
                Truncate(lua, 100_000),
                deviceContext,
                snapshot is { Length: <= 5_000_000 } ? snapshot : null);
            var result = await _aiAnalysisService.AnalyzeAsync(
                key, _vm.Settings.AiModel, request, _aiAnalysisCts.Token);
            AiReportTextBox.Text = FormatAiReport(result);
            AiFixedLuaTextBox.Text = result.FixedLua;
            SaveFixedLuaButton.IsEnabled = !string.IsNullOrWhiteSpace(result.FixedLua);
            AiStatusText.Text = $"Hoàn tất — độ tin cậy {result.Confidence}";
            _log.Add("AI", "-", "Phân tích lỗi.", "Hoàn tất.", LogLevel.Success);
        }
        catch (OperationCanceledException)
        {
            AiStatusText.Text = "Đã hủy";
            _log.Add("AI", "-", "Phân tích lỗi.", "Đã hủy.", LogLevel.Warning);
        }
        catch (Exception ex)
        {
            AiStatusText.Text = "Phân tích thất bại";
            AiReportTextBox.Text = $"Không thể phân tích lỗi.\r\n\r\n{ex.Message}";
            _log.Add("AI", "-", "Phân tích lỗi.", "Thất bại.", LogLevel.Error, ex.Message);
        }
        finally
        {
            _aiAnalysisCts?.Dispose();
            _aiAnalysisCts = null;
            AnalyzeErrorButton.IsEnabled = true;
            CancelAnalysisButton.IsEnabled = false;
        }
    }

    private void CancelAnalysis_Click(object sender, RoutedEventArgs e) =>
        _aiAnalysisCts?.Cancel();

    private void CopyAiReport_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(AiReportTextBox.Text))
            Clipboard.SetText(AiReportTextBox.Text);
    }

    private async void SaveFixedLua_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(AiFixedLuaTextBox.Text)) return;
        var sourceName = string.IsNullOrWhiteSpace(_vm.LuaFileName)
            ? "script"
            : Path.GetFileNameWithoutExtension(_vm.LuaFileName);
        var dialog = new SaveFileDialog
        {
            Title = "Lưu Lua đã sửa thành file mới",
            Filter = "Lua script (*.lua)|*.lua",
            FileName = $"{sourceName}.ai-fixed.lua",
            AddExtension = true,
            DefaultExt = ".lua",
            OverwritePrompt = true
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            await File.WriteAllTextAsync(
                dialog.FileName,
                AiFixedLuaTextBox.Text,
                new UTF8Encoding(false),
                _lifetimeCts.Token);
            _log.Add("AI", "-", "Lưu Lua đã sửa.",
                Path.GetFileName(dialog.FileName), LogLevel.Success);
            MessageBox.Show(
                "Đã lưu thành file mới. Ứng dụng không tự chạy file này.",
                "Đã lưu bản sửa", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Không lưu được bản sửa",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static string FormatAiReport(AiErrorAnalysis result)
    {
        static string Section(string title, IEnumerable<string> items)
        {
            var values = items.Where(item => !string.IsNullOrWhiteSpace(item)).ToArray();
            return values.Length == 0
                ? ""
                : $"{title}\r\n{string.Join("\r\n", values.Select((item, index) => $"{index + 1}. {item}"))}\r\n\r\n";
        }

        var builder = new StringBuilder();
        builder.AppendLine($"TÓM TẮT (độ tin cậy: {result.Confidence})");
        builder.AppendLine(result.Summary);
        builder.AppendLine();
        builder.AppendLine("NGUYÊN NHÂN");
        builder.AppendLine(result.RootCause);
        builder.AppendLine();
        builder.Append(Section("BẰNG CHỨNG", result.Evidence));
        builder.Append(Section("CÁCH KHẮC PHỤC", result.Steps));
        builder.Append(Section("CẢNH BÁO", result.Warnings));
        builder.AppendLine(string.IsNullOrWhiteSpace(result.FixedLua)
            ? "Không tạo bản Lua sửa vì chưa đủ bằng chứng hoặc lỗi không nằm trong Lua."
            : "Đã tạo Lua sửa đề xuất ở khung bên dưới; hãy kiểm tra trước khi sử dụng.");
        return builder.ToString().Trim();
    }

    private static string RedactNetworkData(string value) =>
        Regex.Replace(value, @"\b(?:\d{1,3}\.){3}\d{1,3}\b", "[IP đã che]");

    private static string? Truncate(string? value, int maximumLength) =>
        string.IsNullOrEmpty(value) || value.Length <= maximumLength
            ? value
            : value[..maximumLength] + "\n-- [Đã cắt bớt vì file quá dài]";
}
