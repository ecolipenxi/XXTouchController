using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using XXTouchController.Models;

namespace XXTouchController.Services;

public sealed class XXTouchClient : IXXTouchClient, IDisposable
{
    private readonly HttpClient _http;

    public XXTouchClient()
    {
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(4),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
            MaxConnectionsPerServer = 16
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(12) };
        _http.DefaultRequestHeaders.CacheControl = new CacheControlHeaderValue { NoCache = true };
    }

    public event Action<HttpDiagnostic>? Diagnostic;

    public async Task<DeviceInfo?> GetDeviceInfoAsync(
        string ip, int port, CancellationToken cancellationToken)
    {
        Exception? lastError = null;
        var delays = new[] { 0, 500, 2000 };
        for (var attempt = 0; attempt < delays.Length; attempt++)
        {
            if (delays[attempt] > 0)
                await Task.Delay(delays[attempt], cancellationToken);
            try
            {
                return await GetDeviceInfoOnceAsync(ip, port, cancellationToken);
            }
            catch (Exception ex) when (
                ex is not OperationCanceledException && attempt < delays.Length - 1)
            {
                lastError = ex;
            }
        }
        throw lastError ?? new InvalidOperationException("Không thể đọc thông tin thiết bị.");
    }

    private async Task<DeviceInfo?> GetDeviceInfoOnceAsync(
        string ip, int port, CancellationToken cancellationToken)
    {
        var url = BuildUrl(ip, port, "/deviceinfo");
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        using var response = await SendAsync(request, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        var (code, message, root) = ParseApiResponse(text);

        if (!response.IsSuccessStatusCode || code != 0 ||
            !root.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException(
                $"XXTouch từ chối /deviceinfo: HTTP {(int)response.StatusCode}, " +
                $"code={code?.ToString() ?? "null"}, message={message ?? "null"}");
        }

        var running = TryGetBoolean(data, "is_running");
        var lastScriptError = GetString(data, "last_error");
        var runId = GetString(data, "run_id");
        var startedAt = GetDouble(data, "started_at");
        var finishedAt = GetDouble(data, "finished_at");
        var stoppedByUser = TryGetBoolean(data, "stopped_by_user") == true;
        return new DeviceInfo
        {
            Name = GetString(data, "devname") ?? GetString(data, "bonjour_name") ?? "iPhone",
            Ip = GetString(data, "ip") ?? GetString(data, "wifi_ip") ?? ip,
            Port = GetInt(data, "port") ?? port,
            DeviceId = GetString(data, "deviceid"),
            Model = GetString(data, "marketing_name") ?? GetString(data, "devtype"),
            IosVersion = GetString(data, "sysversion"),
            XXTouchVersion = GetString(data, "tsversion") ?? GetString(data, "zeversion"),
            ScreenOn = TryGetBoolean(data, "screen_on"),
            Locked = TryGetBoolean(data, "locked"),
            FrontmostApp = GetString(data, "frontmost_app"),
            HomeReady = TryGetBoolean(data, "home_ready"),
            LastScriptError = lastScriptError,
            RunId = runId,
            StoppedByUser = stoppedByUser,
            ScriptStartedAt = FromUnixSeconds(startedAt),
            ScriptFinishedAt = FromUnixSeconds(finishedAt),
            ConnectionState = ConnectionState.Online,
            ScriptState = !string.IsNullOrWhiteSpace(lastScriptError)
                ? ScriptState.Error
                : running switch
            {
                true => ScriptState.Running,
                false when stoppedByUser => ScriptState.Stopped,
                false when finishedAt > 0 && !string.IsNullOrWhiteSpace(runId) =>
                    ScriptState.Stopped,
                false => ScriptState.Stopped,
                null => ScriptState.Unknown
            },
            LastUpdated = DateTime.Now
        };
    }

    public async Task<HealthInfo> GetHealthAsync(
        string ip, int port, CancellationToken cancellationToken)
    {
        var url = BuildUrl(ip, port, "/health");
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await SendAsync(request, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        var (code, _, root) = ParseApiResponse(text);
        if (!response.IsSuccessStatusCode || code != 0 ||
            !root.TryGetProperty("data", out var data))
            throw new InvalidOperationException("Health endpoint không khả dụng.");
        return new HealthInfo(
            true, TryGetBoolean(data, "running"), GetString(data, "version"));
    }

    public async Task<ApiResult> StartScriptAsync(
        string ip, int port, string luaContent, CancellationToken cancellationToken)
    {
        var requestId = Guid.NewGuid().ToString("N");
        var url = BuildUrl(ip, port, $"/spawn?request_id={requestId}");
        ApiResult? result = null;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var content = new StringContent(luaContent, Encoding.UTF8, "text/plain");
            result = await SendApiCommandAsync(
                HttpMethod.Post, url, content, cancellationToken);
            if (result.Success || result.Error is not ("Timeout" or "HttpRequestException"))
                return result;
            await Task.Delay(500, cancellationToken);
        }
        return result!;
    }

    public Task<ApiResult> UploadAssetAsync(
        string ip, int port, string fileName, byte[] bytes,
        CancellationToken cancellationToken)
    {
        if (!RegexSafeAssetName(fileName))
            throw new ArgumentException("Tên asset không hợp lệ.", nameof(fileName));
        var encodedName = Uri.EscapeDataString(fileName);
        var content = new ByteArrayContent(bytes);
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        content.Headers.ContentType = new MediaTypeHeaderValue(extension switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            _ => "image/png"
        });
        return SendApiCommandAsync(
            HttpMethod.Post, BuildUrl(ip, port, $"/upload_asset?name={encodedName}"),
            content, cancellationToken);
    }

    private static bool RegexSafeAssetName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 96 ||
            name.Contains('/') || name.Contains('\\')) return false;
        var extension = Path.GetExtension(name).ToLowerInvariant();
        if (extension is not (".png" or ".jpg" or ".jpeg")) return false;
        return name.All(c => char.IsLetterOrDigit(c) || c is '_' or '-' or '.');
    }

    public async Task<ApiResult> StopScriptAsync(
        string ip, int port, CancellationToken cancellationToken)
    {
        // /recycle is idempotent.  A short retry covers the brief socket
        // hand-off that occurs when an Agent is busy joining its Lua thread.
        ApiResult? result = null;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            result = await SendApiCommandAsync(
                HttpMethod.Post, BuildUrl(ip, port, "/recycle"), null,
                cancellationToken);
            if (result.Success || result.Error is not ("Timeout" or "HttpRequestException"))
                return result;
            if (attempt == 0)
                await Task.Delay(350, cancellationToken);
        }
        return result!;
    }

    public Task<ApiResult> InstallAgentUpdateAsync(
        string ip, int port, Uri downloadUri, CancellationToken cancellationToken) =>
        SendApiCommandAsync(
            HttpMethod.Post,
            BuildUrl(ip, port, "/install_update"),
            new StringContent(downloadUri.AbsoluteUri, Encoding.UTF8, "text/plain"),
            cancellationToken);

    public Task<ApiResult> RestartAgentAsync(
        string ip, int port, CancellationToken cancellationToken) =>
        SendApiCommandAsync(
            HttpMethod.Post, BuildUrl(ip, port, "/restart_agent"), null, cancellationToken);

    public async Task<bool?> GetRunningStatusAsync(
        string ip, int port, CancellationToken cancellationToken)
    {
        var url = BuildUrl(ip, port, "/is_running");
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await SendAsync(request, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        var (_, _, root) = ParseApiResponse(text);
        if (!response.IsSuccessStatusCode) return null;

        if (root.TryGetProperty("data", out var data))
        {
            if (data.ValueKind is JsonValueKind.True or JsonValueKind.False)
                return data.GetBoolean();
            if (data.ValueKind == JsonValueKind.Object)
                return TryGetBoolean(data, "is_running");
        }

        // TS 5.4.5 trả code=0 cả khi không chạy. Không suy diễn trạng thái từ code.
        return null;
    }

    public async Task<AgentLogInfo> GetAgentLogsAsync(
        string ip, int port, CancellationToken cancellationToken)
    {
        var url = BuildUrl(ip, port, "/logs");
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await SendAsync(request, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        var (code, message, root) = ParseApiResponse(text);
        if (!response.IsSuccessStatusCode || code != 0 ||
            !root.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException(
                $"LuaAgent từ chối /logs: HTTP {(int)response.StatusCode}, " +
                $"code={code?.ToString() ?? "null"}, message={message ?? "null"}");
        }

        var logs = new List<string>();
        if (data.TryGetProperty("logs", out var logArray) &&
            logArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in logArray.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                    logs.Add(item.GetString() ?? string.Empty);
            }
        }

        return new AgentLogInfo(
            TryGetBoolean(data, "running") == true,
            GetString(data, "run_id"),
            TryGetBoolean(data, "stopped_by_user") == true,
            GetString(data, "last_error"),
            logs);
    }

    public async Task<byte[]> GetSnapshotAsync(
        string ip, int port, CancellationToken cancellationToken)
    {
        var url = BuildUrl(ip, port, "/snapshot?ext=jpeg&compress=0.8&orient=0");
        Exception? lastError = null;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                using var response = await SendAsync(request, cancellationToken);
                response.EnsureSuccessStatusCode();
                var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                if (bytes.Length < 4) throw new InvalidDataException("Snapshot rỗng.");
                return bytes;
            }
            catch (Exception ex) when (attempt == 0 && ex is not OperationCanceledException)
            {
                lastError = ex;
                await Task.Delay(250, cancellationToken);
            }
        }
        throw lastError ?? new InvalidOperationException("Không tải được snapshot.");
    }

    private async Task<ApiResult> SendApiCommandAsync(
        HttpMethod method, string url, HttpContent? content, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, url) { Content = content };
        try
        {
            using var response = await SendAsync(request, cancellationToken);
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            var (code, message, root) = ParseApiResponse(text);
            var success = response.IsSuccessStatusCode && code == 0;
            string? runId = null;
            var duplicate = false;
            if (root.TryGetProperty("data", out var data) &&
                data.ValueKind == JsonValueKind.Object)
            {
                runId = GetString(data, "run_id");
                duplicate = TryGetBoolean(data, "duplicate") == true;
            }
            Diagnostic?.Invoke(new HttpDiagnostic(
                method.Method, url, (int)response.StatusCode,
                response.Content.Headers.ContentType?.ToString(), 0, code, message, null));
            return new ApiResult(
                success, code,
                message ?? (success ? "Operation succeed" : "Phản hồi JSON không hợp lệ"),
                (int)response.StatusCode,
                success ? null : text,
                runId,
                duplicate);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ApiResult(false, null,
                "Request hết thời gian chờ; không tự gửi lại để tránh chạy script hai lần.",
                Error: "Timeout");
        }
        catch (Exception ex)
        {
            return new ApiResult(false, null, ex.Message, Error: ex.GetType().Name);
        }
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var response = await _http.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            sw.Stop();
            Diagnostic?.Invoke(new HttpDiagnostic(
                request.Method.Method, request.RequestUri!.ToString(),
                (int)response.StatusCode, response.Content.Headers.ContentType?.ToString(),
                sw.ElapsedMilliseconds, null, null, null));
            return response;
        }
        catch (Exception ex)
        {
            sw.Stop();
            Diagnostic?.Invoke(new HttpDiagnostic(
                request.Method.Method, request.RequestUri!.ToString(),
                null, null, sw.ElapsedMilliseconds, null, null, ex.GetType().Name));
            throw;
        }
    }

    private static (int? Code, string? Message, JsonElement Root) ParseApiResponse(string text)
    {
        using var document = JsonDocument.Parse(text);
        var root = document.RootElement.Clone();
        int? code = null;
        if (root.TryGetProperty("code", out var codeElement) &&
            codeElement.TryGetInt32(out var parsed))
            code = parsed;
        return (code, GetString(root, "message"), root);
    }

    private static string BuildUrl(string ip, int port, string path)
    {
        var host = ip.Contains(':') && !ip.StartsWith('[') ? $"[{ip}]" : ip;
        return $"http://{host}:{port}{path}";
    }

    private static string? GetString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null
        };
    }

    private static int? GetInt(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return null;
        if (value.TryGetInt32(out var number)) return number;
        return value.ValueKind == JsonValueKind.String &&
               int.TryParse(value.GetString(), out number) ? number : null;
    }

    private static double? GetDouble(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return null;
        if (value.TryGetDouble(out var number)) return number;
        return value.ValueKind == JsonValueKind.String &&
               double.TryParse(value.GetString(), out number) ? number : null;
    }

    private static DateTime? FromUnixSeconds(double? seconds)
    {
        if (seconds is null or <= 0) return null;
        try
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(
                checked((long)Math.Round(seconds.Value * 1000.0))).LocalDateTime;
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    private static bool? TryGetBoolean(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number when value.TryGetInt32(out var number) => number != 0,
            JsonValueKind.String when bool.TryParse(value.GetString(), out var boolean) => boolean,
            _ => null
        };
    }

    public void Dispose() => _http.Dispose();
}
