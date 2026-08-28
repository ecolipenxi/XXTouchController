using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using XXTouchController.Models;

namespace XXTouchController.Services;

public sealed record DeviceDiscoveryResult(
    IReadOnlyList<DeviceInfo> Devices,
    int CandidateCount,
    int RejectedCount,
    IReadOnlyList<string> RejectedEndpoints);

public sealed class DeviceDiscoveryService
{
    private const int DiscoveryPort = 46953;
    private const int AgentPort = 46952;
    private const int MaxRangeHosts = 4094;
    private const int RangeScanConcurrency = 256;
    private static readonly TimeSpan PortProbeTimeout = TimeSpan.FromMilliseconds(450);
    private readonly IXXTouchClient _client;

    public DeviceDiscoveryService(IXXTouchClient client) => _client = client;

    public async Task<DeviceDiscoveryResult> ScanDevicesAsync(
        CancellationToken cancellationToken)
    {
        var addresses = GetActiveIpv4Addresses().ToArray();
        if (addresses.Length == 0)
            return new DeviceDiscoveryResult(Array.Empty<DeviceInfo>(), 0, 0, Array.Empty<string>());

        var udpTasks = addresses.Select(address =>
            ScanInterfaceAsync(address, cancellationToken));
        var rangeTasks = addresses.Select(address =>
            ScanLocalRangeAsync(address, cancellationToken));
        var udpResults = await Task.WhenAll(udpTasks);
        var rangeResults = await Task.WhenAll(rangeTasks);
        var rawDevices = udpResults.SelectMany(x => x)
            .Concat(rangeResults.SelectMany(x => x))
            .ToList();
        var candidates = Deduplicate(rawDevices).ToArray();
        var verified = new ConcurrentBag<DeviceInfo>();
        var rejectedCount = 0;
        var rejectedEndpoints = new ConcurrentBag<string>();
        await Parallel.ForEachAsync(
            candidates,
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = 32
            },
            async (raw, token) =>
            {
                try
                {
                    var device = await _client.GetDeviceInfoAsync(raw.Ip, raw.Port, token);
                    // A TCP accept alone is not proof that this endpoint is an
                    // iPhone Agent. LAN proxy/virtual adapters can make many
                    // addresses look open, so only keep a signed deviceinfo
                    // response containing an Agent identity or version.
                    if (device is not null &&
                        (!string.IsNullOrWhiteSpace(device.DeviceId) ||
                         !string.IsNullOrWhiteSpace(device.XXTouchVersion)))
                    {
                        verified.Add(device);
                    }
                    else
                    {
                        Interlocked.Increment(ref rejectedCount);
                        rejectedEndpoints.Add($"{raw.Ip}:{raw.Port}");
                    }
                }
                catch
                {
                    // Fail closed: never turn an unverified port response into
                    // a persistent Offline "iPhone" row.
                    Interlocked.Increment(ref rejectedCount);
                    rejectedEndpoints.Add($"{raw.Ip}:{raw.Port}");
                }
            });
        var devices = Deduplicate(verified).ToArray();
        return new DeviceDiscoveryResult(
            devices,
            candidates.Length,
            rejectedCount,
            rejectedEndpoints.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private static async Task<IReadOnlyList<DeviceInfo>> ScanLocalRangeAsync(
        InterfaceAddress local, CancellationToken cancellationToken)
    {
        var hosts = EnumerateHosts(local).ToArray();
        if (hosts.Length == 0) return Array.Empty<DeviceInfo>();

        var open = new ConcurrentBag<DeviceInfo>();
        await Parallel.ForEachAsync(
            hosts,
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = RangeScanConcurrency
            },
            async (address, token) =>
            {
                if (address.Equals(local.Address)) return;
                if (await IsPortOpenAsync(address, AgentPort, token))
                {
                    open.Add(new DeviceInfo
                    {
                        Ip = address.ToString(),
                        Port = AgentPort,
                        Name = "iPhone",
                        ConnectionState = ConnectionState.Checking,
                        ScriptState = ScriptState.Unknown
                    });
                }
            });
        return open.ToArray();
    }

    private static async Task<bool> IsPortOpenAsync(
        IPAddress address, int port, CancellationToken cancellationToken)
    {
        using var socket = new Socket(
            AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeout.CancelAfter(PortProbeTimeout);
        try
        {
            await socket.ConnectAsync(new IPEndPoint(address, port), timeout.Token);
            return socket.Connected;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private static IEnumerable<IPAddress> EnumerateHosts(InterfaceAddress local)
    {
        var ip = ToUInt32(local.Address);
        var mask = ToUInt32(local.Mask);
        var network = ip & mask;
        var broadcast = network | ~mask;
        var hostCount = broadcast > network ? (long)broadcast - network - 1 : 0;
        if (hostCount <= 0 || hostCount > MaxRangeHosts) yield break;

        for (var value = network + 1; value < broadcast; value++)
            yield return FromUInt32(value);
    }

    private static uint ToUInt32(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return ((uint)bytes[0] << 24) |
               ((uint)bytes[1] << 16) |
               ((uint)bytes[2] << 8) |
               bytes[3];
    }

    private static IPAddress FromUInt32(uint value) =>
        new([
            (byte)(value >> 24),
            (byte)(value >> 16),
            (byte)(value >> 8),
            (byte)value
        ]);

    private static async Task<IReadOnlyList<DeviceInfo>> ScanInterfaceAsync(
        InterfaceAddress local, CancellationToken cancellationToken)
    {
        var devices = new List<DeviceInfo>();
        using var udp = new UdpClient(new IPEndPoint(local.Address, 0));
        udp.EnableBroadcast = true;
        var receivePort = ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
        var payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            ip = local.Address.ToString(),
            port = receivePort
        }));

        var targets = new HashSet<IPAddress>
        {
            IPAddress.Broadcast,
            CalculateBroadcast(local.Address, local.Mask)
        };
        foreach (var target in targets)
            await udp.SendAsync(payload, new IPEndPoint(target, DiscoveryPort), cancellationToken);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(4));
        while (!timeout.IsCancellationRequested)
        {
            try
            {
                var result = await udp.ReceiveAsync(timeout.Token);
                var device = ParseDiscoveryResponse(result.Buffer, result.RemoteEndPoint);
                if (device is not null) devices.Add(device);
            }
            catch (OperationCanceledException) { break; }
            catch (JsonException) { }
            catch (SocketException) when (!cancellationToken.IsCancellationRequested) { break; }
        }
        return devices;
    }

    private static DeviceInfo? ParseDiscoveryResponse(byte[] bytes, IPEndPoint sender)
    {
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        var ip = ReadString(root, "ip") ?? sender.Address.ToString();
        var port = int.TryParse(ReadString(root, "port"), out var parsed) ? parsed : 46952;
        if (string.IsNullOrWhiteSpace(ip)) return null;
        return new DeviceInfo
        {
            Ip = ip,
            Port = port,
            Name = ReadString(root, "devname") ?? "iPhone",
            DeviceId = ReadString(root, "deviceid"),
            Model = ReadString(root, "marketing_name") ?? ReadString(root, "devtype"),
            IosVersion = ReadString(root, "sysversion"),
            XXTouchVersion = ReadString(root, "tsversion") ?? ReadString(root, "zeversion"),
            ConnectionState = ConnectionState.Checking,
            ScriptState = ScriptState.Unknown
        };
    }

    private static IEnumerable<DeviceInfo> Deduplicate(IEnumerable<DeviceInfo> devices)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var endpoints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var device in devices)
        {
            var endpoint = $"{device.Ip}:{device.Port}";
            if (!string.IsNullOrWhiteSpace(device.DeviceId))
            {
                if (!ids.Add(device.DeviceId)) continue;
                endpoints.Add(endpoint);
            }
            else if (!endpoints.Add(endpoint)) continue;
            yield return device;
        }
    }

    private static IEnumerable<InterfaceAddress> GetActiveIpv4Addresses()
    {
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.OperationalStatus != OperationalStatus.Up ||
                adapter.NetworkInterfaceType is NetworkInterfaceType.Loopback or
                    NetworkInterfaceType.Tunnel)
                continue;
            var properties = adapter.GetIPProperties();
            var hasIpv4Gateway = properties.GatewayAddresses.Any(gateway =>
                gateway.Address.AddressFamily == AddressFamily.InterNetwork &&
                !gateway.Address.Equals(IPAddress.Any));
            if (!hasIpv4Gateway) continue;

            foreach (var unicast in properties.UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork ||
                    unicast.IPv4Mask is null ||
                    IPAddress.IsLoopback(unicast.Address) ||
                    unicast.Address.ToString().StartsWith("169.254.", StringComparison.Ordinal))
                    continue;
                yield return new InterfaceAddress(unicast.Address, unicast.IPv4Mask);
            }
        }
    }

    private static IPAddress CalculateBroadcast(IPAddress address, IPAddress mask)
    {
        var ip = address.GetAddressBytes();
        var subnetMask = mask.GetAddressBytes();
        var broadcast = new byte[4];
        for (var i = 0; i < 4; i++)
            broadcast[i] = (byte)(ip[i] | ~subnetMask[i]);
        return new IPAddress(broadcast);
    }

    private static string? ReadString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null
        };
    }

    private sealed record InterfaceAddress(IPAddress Address, IPAddress Mask);
}
