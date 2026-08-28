using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace XXTouchController.Services;

public sealed class TipaDistributionServer : IAsyncDisposable
{
    private readonly string _filePath;
    private readonly string _route = "/agent-" + Guid.NewGuid().ToString("N") + ".tipa";
    private readonly CancellationTokenSource _cts = new();
    private TcpListener? _listener;
    private int _downloadCount;

    public TipaDistributionServer(string filePath)
    {
        _filePath = Path.GetFullPath(filePath);
    }

    public int Port { get; private set; }
    public int DownloadCount => Volatile.Read(ref _downloadCount);
    public event Action<IPAddress>? DownloadCompleted;

    public void Start()
    {
        if (_listener is not null) return;
        try
        {
            _listener = new TcpListener(IPAddress.Any, 47891);
            _listener.Start(128);
        }
        catch (SocketException)
        {
            _listener = new TcpListener(IPAddress.Any, 0);
            _listener.Start(128);
        }
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = AcceptLoopAsync(_cts.Token);
    }

    public Uri GetDownloadUri(string deviceIp)
    {
        if (_listener is null) throw new InvalidOperationException("Server is not running.");
        var remote = IPAddress.Parse(deviceIp);
        using var routeProbe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        routeProbe.Connect(new IPEndPoint(remote, 46952));
        var local = ((IPEndPoint)routeProbe.LocalEndPoint!).Address;
        return new UriBuilder(Uri.UriSchemeHttp, local.ToString(), Port, _route).Uri;
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var client = await _listener!.AcceptTcpClientAsync(cancellationToken);
                _ = HandleClientAsync(client, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            client.NoDelay = true;
            await using var stream = client.GetStream();
            string? requestLine;
            try
            {
                requestLine = await ReadRequestLineAsync(stream, cancellationToken);
                for (var headerCount = 0; headerCount < 100; headerCount++)
                {
                    var requestHeader = await ReadRequestLineAsync(stream, cancellationToken);
                    if (string.IsNullOrEmpty(requestHeader)) break;
                }
            }
            catch
            {
                return;
            }

            var expected = $"GET {_route} ";
            if (requestLine is null || !requestLine.StartsWith(expected, StringComparison.Ordinal))
            {
                await WriteTextResponseAsync(stream, 404, "Not Found", cancellationToken);
                return;
            }

            var info = new FileInfo(_filePath);
            var header = Encoding.ASCII.GetBytes(
                "HTTP/1.1 200 OK\r\n" +
                "Content-Type: application/trollstore-ipa\r\n" +
                $"Content-Length: {info.Length}\r\n" +
                "Cache-Control: no-store\r\n" +
                "Connection: close\r\n\r\n");
            await stream.WriteAsync(header, cancellationToken);
            await using var file = new FileStream(
                _filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await file.CopyToAsync(stream, 128 * 1024, cancellationToken);
            await stream.FlushAsync(cancellationToken);
            Interlocked.Increment(ref _downloadCount);
            if (client.Client.RemoteEndPoint is IPEndPoint remote)
                DownloadCompleted?.Invoke(remote.Address);
        }
    }

    private static async Task<string?> ReadRequestLineAsync(
        NetworkStream stream, CancellationToken cancellationToken)
    {
        var bytes = new List<byte>(256);
        var buffer = new byte[1];
        while (bytes.Count < 4096)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0) return null;
            bytes.Add(buffer[0]);
            if (bytes.Count >= 2 && bytes[^2] == '\r' && bytes[^1] == '\n')
                return Encoding.ASCII.GetString(bytes.ToArray()).TrimEnd('\r', '\n');
        }
        throw new InvalidDataException("HTTP request line is too long.");
    }

    private static async Task WriteTextResponseAsync(
        NetworkStream stream, int status, string text, CancellationToken cancellationToken)
    {
        var body = Encoding.UTF8.GetBytes(text);
        var header = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status} {text}\r\nContent-Type: text/plain\r\n" +
            $"Content-Length: {body.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(body, cancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener?.Stop();
        _cts.Dispose();
        return ValueTask.CompletedTask;
    }
}
