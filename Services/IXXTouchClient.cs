using XXTouchController.Models;

namespace XXTouchController.Services;

public interface IXXTouchClient
{
    event Action<HttpDiagnostic>? Diagnostic;

    Task<DeviceInfo?> GetDeviceInfoAsync(
        string ip, int port, CancellationToken cancellationToken);

    Task<HealthInfo> GetHealthAsync(
        string ip, int port, CancellationToken cancellationToken);

    Task<ApiResult> StartScriptAsync(
        string ip, int port, string luaContent, CancellationToken cancellationToken);

    Task<ApiResult> UploadAssetAsync(
        string ip, int port, string fileName, byte[] bytes, CancellationToken cancellationToken);

    Task<ApiResult> StopScriptAsync(
        string ip, int port, CancellationToken cancellationToken);

    Task<ApiResult> InstallAgentUpdateAsync(
        string ip, int port, Uri downloadUri, CancellationToken cancellationToken);

    Task<ApiResult> RestartAgentAsync(
        string ip, int port, CancellationToken cancellationToken);

    Task<bool?> GetRunningStatusAsync(
        string ip, int port, CancellationToken cancellationToken);

    Task<AgentLogInfo> GetAgentLogsAsync(
        string ip, int port, CancellationToken cancellationToken);

    Task<byte[]> GetSnapshotAsync(
        string ip, int port, CancellationToken cancellationToken);
}
