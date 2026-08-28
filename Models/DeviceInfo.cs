using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace XXTouchController.Models;

public enum ConnectionState { Checking, Online, Offline }
public enum ScriptState { Queued, Sending, Running, Completed, Stopped, Unknown, Error }

public sealed class DeviceInfo : INotifyPropertyChanged
{
    private bool _isSelected;
    private int _displayIndex;
    private int _consecutiveConnectionFailures;
    private string _name = "iPhone";
    private string _ip = "";
    private int _port = 46952;
    private string? _deviceId;
    private string? _model;
    private string? _iosVersion;
    private string? _xxTouchVersion;
    private string? _tikTokUsername;
    private string? _tikTokUsernameResult;
    private DateTime? _tikTokUsernameUpdatedAt;
    private long? _tikTokPointBalance;
    private string? _tikTokPointPlan;
    private string? _tikTokPointStatus;
    private string? _tikTokPointLink;
    private DateTime? _tikTokPointUpdatedAt;
    private string? _lastScriptError;
    private string? _runId;
    private bool _stoppedByUser;
    private bool? _screenOn;
    private bool? _locked;
    private string? _frontmostApp;
    private bool? _homeReady;
    private int _repeatCurrent;
    private int _repeatTotal;
    private ConnectionState _connectionState = ConnectionState.Offline;
    private ScriptState _scriptState = ScriptState.Unknown;
    private DateTime? _lastUpdated;
    private DateTime? _scriptStartedAt;
    private DateTime? _scriptFinishedAt;

    [JsonIgnore]
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }
    [JsonIgnore]
    public int DisplayIndex { get => _displayIndex; set => Set(ref _displayIndex, value); }
    [JsonIgnore]
    public int ConsecutiveConnectionFailures
    {
        get => _consecutiveConnectionFailures;
        set => Set(ref _consecutiveConnectionFailures, value);
    }
    public string Name { get => _name; set => Set(ref _name, value); }
    public string Ip { get => _ip; set => Set(ref _ip, value); }
    public int Port { get => _port; set => Set(ref _port, value); }
    public string? DeviceId
    {
        get => _deviceId;
        set
        {
            if (Set(ref _deviceId, value))
                Notify(nameof(DeviceIdDisplay), nameof(DeviceIdShortDisplay));
        }
    }
    public string? Model { get => _model; set => Set(ref _model, value); }
    public string? IosVersion { get => _iosVersion; set => Set(ref _iosVersion, value); }
    public string? XXTouchVersion
    {
        get => _xxTouchVersion;
        set
        {
            if (Set(ref _xxTouchVersion, value))
                Notify(nameof(SupportsReliableHomeReady), nameof(SupportsTikTokPoint),
                    nameof(HomeReadyText), nameof(HomeReadyDetail));
        }
    }
    public string? TikTokUsername
    {
        get => _tikTokUsername;
        set
        {
            if (Set(ref _tikTokUsername, value)) Notify(nameof(TikTokUsernameDisplay));
        }
    }
    public string? TikTokUsernameResult
    {
        get => _tikTokUsernameResult;
        set
        {
            if (Set(ref _tikTokUsernameResult, value))
                Notify(nameof(TikTokUsernameStatusDisplay));
        }
    }
    public DateTime? TikTokUsernameUpdatedAt
    {
        get => _tikTokUsernameUpdatedAt;
        set
        {
            if (Set(ref _tikTokUsernameUpdatedAt, value))
                Notify(nameof(TikTokUsernameUpdatedText));
        }
    }
    public long? TikTokPointBalance
    {
        get => _tikTokPointBalance;
        set
        {
            if (Set(ref _tikTokPointBalance, value))
                Notify(nameof(TikTokPointBalanceDisplay));
        }
    }
    public string? TikTokPointPlan
    {
        get => _tikTokPointPlan;
        set
        {
            if (Set(ref _tikTokPointPlan, value))
                Notify(nameof(TikTokPointPlanDisplay));
        }
    }
    public string? TikTokPointStatus
    {
        get => _tikTokPointStatus;
        set
        {
            if (Set(ref _tikTokPointStatus, value))
                Notify(nameof(TikTokPointStatusDisplay));
        }
    }
    public string? TikTokPointLink
    {
        get => _tikTokPointLink;
        set
        {
            if (Set(ref _tikTokPointLink, value))
                Notify(nameof(TikTokPointLinkDisplay));
        }
    }
    public DateTime? TikTokPointUpdatedAt
    {
        get => _tikTokPointUpdatedAt;
        set
        {
            if (Set(ref _tikTokPointUpdatedAt, value))
                Notify(nameof(TikTokPointUpdatedText));
        }
    }
    [JsonIgnore]
    public string? RunId { get => _runId; set => Set(ref _runId, value); }
    [JsonIgnore]
    public bool StoppedByUser { get => _stoppedByUser; set => Set(ref _stoppedByUser, value); }
    [JsonIgnore]
    public bool? ScreenOn
    {
        get => _screenOn;
        set
        {
            if (Set(ref _screenOn, value))
                Notify(nameof(HomeReadyText), nameof(HomeReadyDetail));
        }
    }
    [JsonIgnore]
    public bool? Locked
    {
        get => _locked;
        set
        {
            if (Set(ref _locked, value))
                Notify(nameof(HomeReadyText), nameof(HomeReadyDetail));
        }
    }
    [JsonIgnore]
    public string? FrontmostApp
    {
        get => _frontmostApp;
        set
        {
            if (Set(ref _frontmostApp, value))
                Notify(nameof(HomeReadyText), nameof(HomeReadyDetail));
        }
    }
    [JsonIgnore]
    public bool? HomeReady
    {
        get => _homeReady;
        set
        {
            if (Set(ref _homeReady, value))
                Notify(nameof(HomeReadyText), nameof(HomeReadyDetail));
        }
    }
    [JsonIgnore]
    public int RepeatCurrent => _repeatCurrent;
    [JsonIgnore]
    public int RepeatTotal => _repeatTotal;
    [JsonIgnore]
    public string? LastScriptError
    {
        get => _lastScriptError;
        set => Set(ref _lastScriptError, value);
    }

    [JsonIgnore]
    public ConnectionState ConnectionState
    {
        get => _connectionState;
        set
        {
            if (!Set(ref _connectionState, value)) return;
            Notify(
                nameof(ConnectionText), nameof(IsOnline), nameof(CanStart),
                nameof(CanStop), nameof(CanView), nameof(HomeReadyText),
                nameof(HomeReadyDetail));
        }
    }

    [JsonIgnore]
    public ScriptState ScriptState
    {
        get => _scriptState;
        set
        {
            if (!Set(ref _scriptState, value)) return;
            Notify(nameof(ScriptText), nameof(ScriptDisplayText), nameof(CanStop));
        }
    }

    [JsonIgnore]
    public DateTime? LastUpdated
    {
        get => _lastUpdated;
        set
        {
            if (Set(ref _lastUpdated, value)) Notify(nameof(LastUpdatedText));
        }
    }

    [JsonIgnore]
    public DateTime? ScriptStartedAt
    {
        get => _scriptStartedAt;
        set
        {
            if (Set(ref _scriptStartedAt, value))
                Notify(nameof(ScriptText), nameof(ScriptDisplayText));
        }
    }

    [JsonIgnore]
    public DateTime? ScriptFinishedAt
    {
        get => _scriptFinishedAt;
        set
        {
            if (Set(ref _scriptFinishedAt, value))
                Notify(nameof(ScriptText), nameof(ScriptDisplayText));
        }
    }

    [JsonIgnore] public bool IsOnline => ConnectionState == ConnectionState.Online;
    [JsonIgnore] public bool CanStart => IsOnline;
    // /recycle is idempotent, so Stop is intentionally always available.
    // Cached Online/script state can lag behind the real Agent, especially
    // when 100+ devices are being checked at once.
    [JsonIgnore] public bool CanStop => true;
    [JsonIgnore] public bool CanView => IsOnline;
    [JsonIgnore] public string Endpoint => $"{Ip}:{Port}";
    [JsonIgnore] public string DeviceIdDisplay =>
        string.IsNullOrWhiteSpace(DeviceId) ? "-" : DeviceId;
    [JsonIgnore] public string DeviceIdShortDisplay
    {
        get
        {
            if (string.IsNullOrWhiteSpace(DeviceId)) return "-";
            var value = DeviceId.Trim();
            return value.Length <= 16
                ? value
                : $"{value[..8]}…{value[^4..]}";
        }
    }
    [JsonIgnore] public bool SupportsReliableHomeReady => IsAgentVersionAtLeast(3, 0);
    [JsonIgnore] public bool SupportsTikTokPoint => IsAgentVersionAtLeast(3, 1);
    [JsonIgnore] public string TikTokUsernameDisplay =>
        string.IsNullOrWhiteSpace(TikTokUsername) ? "-" : TikTokUsername;
    [JsonIgnore] public string TikTokUsernameStatusDisplay =>
        string.IsNullOrWhiteSpace(TikTokUsernameResult) ? "Chưa lấy" : TikTokUsernameResult;
    [JsonIgnore] public string TikTokUsernameUpdatedText =>
        TikTokUsernameUpdatedAt?.ToString("dd/MM HH:mm:ss") ?? "-";
    [JsonIgnore] public string TikTokPointBalanceDisplay => TikTokPointBalance is null
        ? "-"
        : TikTokPointBalance.Value.ToString("N0", CultureInfo.GetCultureInfo("vi-VN"));
    [JsonIgnore] public string TikTokPointPlanDisplay =>
        string.IsNullOrWhiteSpace(TikTokPointPlan) ? "-" : TikTokPointPlan;
    [JsonIgnore] public string TikTokPointStatusDisplay =>
        string.IsNullOrWhiteSpace(TikTokPointStatus) ? "Chưa có" : TikTokPointStatus;
    [JsonIgnore] public string TikTokPointLinkDisplay
    {
        get
        {
            if (string.IsNullOrWhiteSpace(TikTokPointLink)) return "-";
            var value = TikTokPointLink.Trim();
            return value.Length <= 34 ? value : $"{value[..31]}…";
        }
    }
    [JsonIgnore] public string TikTokPointUpdatedText =>
        TikTokPointUpdatedAt?.ToString("dd/MM HH:mm:ss") ?? "-";
    [JsonIgnore] public string ConnectionText => ConnectionState switch
    {
        ConnectionState.Checking => "Đang kiểm tra",
        ConnectionState.Online => "Online",
        _ => "Offline"
    };
    [JsonIgnore] public string ScriptText => ScriptState switch
    {
        ScriptState.Running => $"Đang chạy {FormatElapsed(GetElapsed())}",
        ScriptState.Queued => "Đang chờ",
        ScriptState.Sending => "Đang gửi",
        ScriptState.Completed => $"Đã dừng {FormatElapsed(GetElapsed())}",
        ScriptState.Stopped => $"Đã dừng {FormatElapsed(GetElapsed())}",
        ScriptState.Error => "Lỗi",
        _ => "Không xác định"
    };
    [JsonIgnore] public string ScriptDisplayText => RepeatTotal > 0
        ? $"{ScriptText} \u00B7 V\u00F2ng {RepeatCurrent}/{RepeatTotal}"
        : ScriptText;
    [JsonIgnore] public string LastUpdatedText => LastUpdated?.ToString("HH:mm:ss") ?? "-";
    [JsonIgnore] public string HomeReadyText
    {
        get
        {
            if (IsOnline && !SupportsReliableHomeReady) return "Cần Agent 3.0";
            return HomeReady switch
            {
                true => "Sẵn sàng",
                false when ScreenOn == false => "Màn hình tắt",
                false when Locked == true => "Đang khóa",
                false when !string.IsNullOrWhiteSpace(FrontmostApp) => "Không ở Home",
                false => "Không sẵn sàng",
                null when IsOnline => "Chưa có trạng thái",
                _ => "Chưa kiểm tra"
            };
        }
    }
    [JsonIgnore] public string HomeReadyDetail
    {
        get
        {
            if (IsOnline && !SupportsReliableHomeReady)
                return "Cần cập nhật LuaAgent 3.0 để đọc đúng trạng thái màn hình.";
            return HomeReady switch
            {
                true => "Màn hình sáng, đã mở khóa và đang ở Home.",
                false when ScreenOn == false => "Màn hình đang tắt.",
                false when Locked == true => "Thiết bị đang ở màn hình khóa.",
                false when !string.IsNullOrWhiteSpace(FrontmostApp) =>
                    $"App đang mở: {FrontmostApp}.",
                false => "Thiết bị chưa ở trạng thái Home sẵn sàng.",
                null when IsOnline => "Agent 3.0 chưa trả đủ trạng thái Home.",
                _ => "Thiết bị chưa được kiểm tra."
            };
        }
    }

    private bool IsAgentVersionAtLeast(int requiredMajor, int requiredMinor)
    {
        if (string.IsNullOrWhiteSpace(XXTouchVersion) ||
            !XXTouchVersion.Contains("LuaAgent", StringComparison.OrdinalIgnoreCase))
            return false;
        var firstDigit = XXTouchVersion.IndexOfAny("0123456789".ToCharArray());
        if (firstDigit < 0) return false;
        var versionText = new string(XXTouchVersion[firstDigit..]
            .TakeWhile(character => char.IsDigit(character) || character == '.')
            .ToArray());
        if (!Version.TryParse(versionText, out var parsed)) return false;
        return parsed.Major > requiredMajor ||
               parsed.Major == requiredMajor && parsed.Minor >= requiredMinor;
    }

    public void SetRepeatProgress(int current, int total)
    {
        total = Math.Max(0, total);
        current = total == 0 ? 0 : Math.Clamp(current, 1, total);
        if (_repeatCurrent == current && _repeatTotal == total) return;
        _repeatCurrent = current;
        _repeatTotal = total;
        Notify(
            nameof(RepeatCurrent),
            nameof(RepeatTotal),
            nameof(ScriptDisplayText));
    }

    public void Apply(DeviceInfo source)
    {
        var sameRun = !string.IsNullOrWhiteSpace(RunId) &&
                      string.Equals(RunId, source.RunId, StringComparison.Ordinal);
        var keepLocalClock = sameRun && ScriptStartedAt is not null;

        if (!string.IsNullOrWhiteSpace(source.Name)) Name = source.Name;
        if (!string.IsNullOrWhiteSpace(source.Ip)) Ip = source.Ip;
        if (source.Port > 0) Port = source.Port;
        if (!string.IsNullOrWhiteSpace(source.DeviceId)) DeviceId = source.DeviceId;
        if (!string.IsNullOrWhiteSpace(source.Model)) Model = source.Model;
        if (!string.IsNullOrWhiteSpace(source.IosVersion)) IosVersion = source.IosVersion;
        if (!string.IsNullOrWhiteSpace(source.XXTouchVersion)) XXTouchVersion = source.XXTouchVersion;
        LastScriptError = source.LastScriptError;
        RunId = source.RunId;
        StoppedByUser = source.StoppedByUser;
        ScreenOn = source.ScreenOn;
        Locked = source.Locked;
        FrontmostApp = source.FrontmostApp;
        HomeReady = source.HomeReady;
        if (keepLocalClock)
        {
            if (source.ScriptState == ScriptState.Running)
                ScriptFinishedAt = null;
            else
                ScriptFinishedAt ??= DateTime.Now;
        }
        else
        {
            ScriptStartedAt = source.ScriptStartedAt;
            ScriptFinishedAt = source.ScriptFinishedAt;
        }
        ConnectionState = source.ConnectionState;
        ScriptState = source.ScriptState;
        LastUpdated = source.LastUpdated;
    }

    public void ClearHomeReadiness()
    {
        ScreenOn = null;
        Locked = null;
        FrontmostApp = null;
        HomeReady = null;
    }

    public void RefreshScriptClock()
    {
        if (ScriptState == ScriptState.Running)
            Notify(nameof(ScriptText), nameof(ScriptDisplayText));
    }

    private TimeSpan GetElapsed()
    {
        if (ScriptStartedAt is null) return TimeSpan.Zero;
        var end = ScriptState == ScriptState.Running
            ? DateTime.Now
            : ScriptFinishedAt ?? DateTime.Now;
        return end > ScriptStartedAt ? end - ScriptStartedAt.Value : TimeSpan.Zero;
    }

    private static string FormatElapsed(TimeSpan elapsed)
    {
        var totalHours = (int)Math.Floor(elapsed.TotalHours);
        return $"{totalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}";
    }

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
