using System.IO;
using System.Net.Sockets;
using System.Text;

namespace XXTouchController.Services;

public static class VncPointerClient
{
    public static async Task<(bool Success, string Message)> ClickInstallAsync(
        string ip, int port, CancellationToken cancellationToken)
    {
        try
        {
            using var client = new TcpClient { NoDelay = true };
            await client.ConnectAsync(ip, port, cancellationToken);
            await using var stream = client.GetStream();

            var version = new byte[12];
            await stream.ReadExactlyAsync(version, cancellationToken);
            if (!Encoding.ASCII.GetString(version).StartsWith("RFB ", StringComparison.Ordinal))
                return (false, "Máy chủ không trả về RFB.");
            await stream.WriteAsync(version, cancellationToken);

            var minorText = Encoding.ASCII.GetString(version, 8, 3);
            _ = int.TryParse(minorText, out var minor);
            if (minor >= 7)
            {
                var countBuffer = new byte[1];
                await stream.ReadExactlyAsync(countBuffer, cancellationToken);
                if (countBuffer[0] == 0) return (false, "VNC từ chối kết nối.");
                var securityTypes = new byte[countBuffer[0]];
                await stream.ReadExactlyAsync(securityTypes, cancellationToken);
                if (!securityTypes.Contains((byte)1))
                    return (false, "VNC yêu cầu mật khẩu; không có kiểu None.");
                await stream.WriteAsync(new byte[] { 1 }, cancellationToken);
                var securityResult = new byte[4];
                await stream.ReadExactlyAsync(securityResult, cancellationToken);
                if (ReadUInt32(securityResult) != 0)
                    return (false, "VNC không chấp nhận xác thực None.");
            }
            else
            {
                var securityType = new byte[4];
                await stream.ReadExactlyAsync(securityType, cancellationToken);
                if (ReadUInt32(securityType) != 1)
                    return (false, "VNC 3.3 yêu cầu xác thực.");
            }

            await stream.WriteAsync(new byte[] { 1 }, cancellationToken);
            var serverInit = new byte[24];
            await stream.ReadExactlyAsync(serverInit, cancellationToken);
            var width = ReadUInt16(serverInit.AsSpan(0, 2));
            var height = ReadUInt16(serverInit.AsSpan(2, 2));
            var nameLength = ReadUInt32(serverInit.AsSpan(20, 4));
            if (nameLength > 1024 * 1024) return (false, "Tên VNC quá dài.");
            if (nameLength > 0)
            {
                var name = new byte[nameLength];
                await stream.ReadExactlyAsync(name, cancellationToken);
            }

            // TrollStore's install confirmation uses a two-button alert.
            // The Install button center is stable at ~68% width, 78.5% height
            // across iPhone 6s/7 native framebuffer sizes.
            var x = (ushort)Math.Clamp(
                (int)Math.Round(width * 0.68), 0, Math.Max(0, width - 1));
            var y = (ushort)Math.Clamp(
                (int)Math.Round(height * 0.785), 0, Math.Max(0, height - 1));

            await stream.WriteAsync(PointerEvent(1, x, y), cancellationToken);
            await Task.Delay(140, cancellationToken);
            await stream.WriteAsync(PointerEvent(0, x, y), cancellationToken);
            await stream.FlushAsync(cancellationToken);
            return (true, $"Đã click VNC tại {x},{y} trên {width}x{height}.");
        }
        catch (Exception error) when (
            error is SocketException or IOException or OperationCanceledException)
        {
            return (false, error.Message);
        }
    }

    private static byte[] PointerEvent(byte buttonMask, ushort x, ushort y) =>
        new[]
        {
            (byte)5, buttonMask,
            (byte)(x >> 8), (byte)x,
            (byte)(y >> 8), (byte)y
        };

    private static ushort ReadUInt16(ReadOnlySpan<byte> bytes) =>
        (ushort)((bytes[0] << 8) | bytes[1]);

    private static uint ReadUInt32(ReadOnlySpan<byte> bytes) =>
        ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) |
        ((uint)bytes[2] << 8) | bytes[3];
}
