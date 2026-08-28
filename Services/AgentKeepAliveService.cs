using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using XXTouchController.Models;

namespace XXTouchController.Services;

public sealed class AgentKeepAliveService : IDisposable
{
    private readonly int _udpPort;
    private readonly int _tcpPort;
    private static readonly byte[] Ack = Encoding.ASCII.GetBytes("CONTROLLER_ACK_V1");
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<string, PresenceSession> _sessions = new();
    private UdpClient? _udp;
    private TcpListener? _tcp;

    public event Action<IPAddress>? HeartbeatReceived;

    public AgentKeepAliveService(int udpPort = 46954, int tcpPort = 46955)
    {
        _udpPort = udpPort;
        _tcpPort = tcpPort;
    }

    public void Start()
    {
        if (_udp is not null) return;
        _udp = new UdpClient(AddressFamily.InterNetwork);
        _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _udp.Client.Bind(new IPEndPoint(IPAddress.Any, _udpPort));
        _tcp = new TcpListener(IPAddress.Any, _tcpPort);
        _tcp.Start(256);
        _ = ReceiveUdpLoopAsync(_cts.Token);
        _ = AcceptTcpLoopAsync(_cts.Token);
    }

    public bool IsConnected(string ip) => _sessions.ContainsKey(ip);

    public async Task<ApiResult> RunLuaAsync(
        string ip, string luaContent, CancellationToken cancellationToken)
    {
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(luaContent));
        var response = await SendCommandAsync(
            ip, requestId => $"RUN {requestId} {encoded}", cancellationToken);
        if (response is null)
            return new ApiResult(
                false, null, "Kênh nền không phản hồi.",
                Error: "PresenceUnavailable");
        return ParseApiResult(response);
    }

    public async Task<HealthInfo?> GetHealthAsync(
        string ip, CancellationToken cancellationToken)
    {
        var response = await SendCommandAsync(
            ip, requestId => $"HEALTH {requestId}", cancellationToken);
        if (response is null) return null;
        using var document = JsonDocument.Parse(response);
        var root = document.RootElement;
        if (!root.TryGetProperty("code", out var codeNode) ||
            !codeNode.TryGetInt32(out var code) || code != 0 ||
            !root.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Object)
            return null;
        bool? running = null;
        if (data.TryGetProperty("running", out var runningNode) &&
            runningNode.ValueKind is JsonValueKind.True or JsonValueKind.False)
            running = runningNode.GetBoolean();
        var version = data.TryGetProperty("version", out var versionNode)
            ? versionNode.GetString()
            : null;
        return new HealthInfo(true, running, version);
    }

    private async Task<string?> SendCommandAsync(
        string ip, Func<string, string> createCommand,
        CancellationToken cancellationToken)
    {
        if (!_sessions.TryGetValue(ip, out var session))
            return null;
        await session.CommandGate.WaitAsync(cancellationToken);
        var requestId = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        session.Pending[requestId] = completion;
        try
        {
            await session.SendLineAsync(
                createCommand(requestId), cancellationToken);
            return await completion.Task.WaitAsync(
                TimeSpan.FromSeconds(10), cancellationToken);
        }
        catch (TimeoutException)
        {
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
        finally
        {
            session.Pending.TryRemove(requestId, out _);
            session.CommandGate.Release();
        }
    }

    private static ApiResult ParseApiResult(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        int? code = root.TryGetProperty("code", out var codeNode) &&
                    codeNode.TryGetInt32(out var codeValue)
            ? codeValue
            : null;
        var message = root.TryGetProperty("message", out var messageNode)
            ? messageNode.GetString() ?? ""
            : "";
        string? runId = null;
        var duplicate = false;
        if (root.TryGetProperty("data", out var data) &&
            data.ValueKind == JsonValueKind.Object)
        {
            if (data.TryGetProperty("run_id", out var runIdNode))
                runId = runIdNode.GetString();
            if (data.TryGetProperty("duplicate", out var duplicateNode) &&
                duplicateNode.ValueKind is JsonValueKind.True or JsonValueKind.False)
                duplicate = duplicateNode.GetBoolean();
        }
        return new ApiResult(
            code == 0, code, message, RunId: runId, Duplicate: duplicate);
    }

    private async Task ReceiveUdpLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var packet = await _udp!.ReceiveAsync(cancellationToken);
                if (!packet.Buffer.AsSpan().SequenceEqual("LUAAGENT_KEEPALIVE_V1"u8))
                    continue;
                HeartbeatReceived?.Invoke(packet.RemoteEndPoint.Address);
                await _udp.SendAsync(
                    Ack.AsMemory(), packet.RemoteEndPoint, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch
            {
                try
                {
                    await Task.Delay(250, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private async Task AcceptTcpLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var client = await _tcp!.AcceptTcpClientAsync(cancellationToken);
                client.NoDelay = true;
                client.Client.SetSocketOption(
                    SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                _ = HandleTcpClientAsync(client, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch
            {
                try
                {
                    await Task.Delay(250, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private async Task HandleTcpClientAsync(
        TcpClient client, CancellationToken cancellationToken)
    {
        var address = ((IPEndPoint?)client.Client.RemoteEndPoint)?.Address;
        if (address is null)
        {
            client.Dispose();
            return;
        }

        var ip = address.ToString();
        using var session = new PresenceSession(client);
        if (_sessions.TryGetValue(ip, out var oldSession))
            oldSession.Dispose();
        _sessions[ip] = session;
        HeartbeatReceived?.Invoke(address);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await session.Reader.ReadLineAsync(cancellationToken);
                if (line is null) break;
                if (string.Equals(
                        line, "LUAAGENT_PRESENCE_V1", StringComparison.Ordinal))
                {
                    HeartbeatReceived?.Invoke(address);
                    await session.SendLineAsync(
                        "CONTROLLER_ACK_V1", cancellationToken);
                    continue;
                }
                if (!line.StartsWith("RESULT ", StringComparison.Ordinal))
                    continue;
                var idEnd = line.IndexOf(' ', 7);
                if (idEnd < 0) continue;
                var requestId = line[7..idEnd];
                if (!session.Pending.TryRemove(requestId, out var completion))
                    continue;
                try
                {
                    var bytes = Convert.FromBase64String(line[(idEnd + 1)..]);
                    completion.TrySetResult(Encoding.UTF8.GetString(bytes));
                }
                catch (FormatException ex)
                {
                    completion.TrySetException(ex);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch { }
        finally
        {
            if (_sessions.TryGetValue(ip, out var current) &&
                ReferenceEquals(current, session))
                _sessions.TryRemove(ip, out _);
            session.FailPending();
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _tcp?.Stop();
        _udp?.Dispose();
        foreach (var session in _sessions.Values)
            session.Dispose();
        _sessions.Clear();
        _cts.Dispose();
    }

    private sealed class PresenceSession : IDisposable
    {
        private readonly TcpClient _client;
        private readonly StreamWriter _writer;
        private readonly SemaphoreSlim _writeGate = new(1, 1);
        private int _disposed;

        public PresenceSession(TcpClient client)
        {
            _client = client;
            var stream = client.GetStream();
            Reader = new StreamReader(
                stream, Encoding.ASCII, false, 4096, leaveOpen: true);
            _writer = new StreamWriter(
                stream, Encoding.ASCII, 4096, leaveOpen: true)
            {
                AutoFlush = true,
                NewLine = "\n"
            };
        }

        public StreamReader Reader { get; }
        public SemaphoreSlim CommandGate { get; } = new(1, 1);
        public ConcurrentDictionary<string, TaskCompletionSource<string>> Pending { get; } = new();

        public async Task SendLineAsync(
            string line, CancellationToken cancellationToken)
        {
            await _writeGate.WaitAsync(cancellationToken);
            try
            {
                await _writer.WriteLineAsync(
                    line.AsMemory(), cancellationToken);
            }
            finally
            {
                _writeGate.Release();
            }
        }

        public void FailPending()
        {
            foreach (var completion in Pending.Values)
                completion.TrySetException(
                    new IOException("Kênh nền tới thiết bị đã ngắt."));
            Pending.Clear();
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            FailPending();
            _client.Dispose();
        }
    }
}
