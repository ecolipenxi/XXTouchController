namespace XXTouchController.Models;

public sealed record ApiResult(
    bool Success,
    int? Code,
    string Message,
    int? HttpStatus = null,
    string? Error = null,
    string? RunId = null,
    bool Duplicate = false);

public sealed record HealthInfo(
    bool Online,
    bool? Running,
    string? Version);

public sealed record AgentLogInfo(
    bool Running,
    string? RunId,
    bool StoppedByUser,
    string? LastError,
    IReadOnlyList<string> Logs);

public sealed class AppSettings
{
    public string? LastLuaFile { get; set; }
    public int ScanIntervalSeconds { get; set; } = 10;
    public bool AutoScan { get; set; }
    public int SnapshotIntervalSeconds { get; set; } = 2;
    public bool AutoSnapshot { get; set; }
    public int ConcurrencyLimit { get; set; } = 5;
    public int BatchDelaySeconds { get; set; }
    public bool OnlyRunHomeReady { get; set; }
    public int DefaultPort { get; set; } = 46952;
    public bool Diagnostics { get; set; }
    public string AiModel { get; set; } = "gpt-5.6-terra";
    public bool AiIncludeSnapshot { get; set; }
    public bool AiRedactNetworkData { get; set; } = true;
}

public enum LogLevel { Info, Success, Warning, Error }

public sealed record LogEntry(
    DateTime Timestamp,
    string DeviceName,
    string Ip,
    string Action,
    string Result,
    string? Error,
    LogLevel Level)
{
    public string Display =>
        $"[{Timestamp:HH:mm:ss}] [{DeviceName}] {Action} {Result}" +
        (string.IsNullOrWhiteSpace(Error) ? "" : $" — {Error}");
}

public sealed record HttpDiagnostic(
    string Method,
    string Url,
    int? HttpStatus,
    string? ContentType,
    long ElapsedMilliseconds,
    int? JsonCode,
    string? JsonMessage,
    string? ExceptionType);
