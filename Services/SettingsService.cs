using System.IO;
using System.Text.Json;
using XXTouchController.Models;

namespace XXTouchController.Services;

public sealed class SettingsService
{
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly string _configDirectory;
    private readonly string _settingsPath;
    private readonly string _devicesPath;
    private readonly JsonSerializerOptions _json = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public SettingsService(string? baseDirectory = null)
    {
        _configDirectory = Path.Combine(baseDirectory ?? AppContext.BaseDirectory, "config");
        _settingsPath = Path.Combine(_configDirectory, "settings.json");
        _devicesPath = Path.Combine(_configDirectory, "devices.json");
    }

    public async Task<(AppSettings Settings, List<DeviceInfo> Devices)> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_configDirectory);
        var settings = await ReadOrCreateAsync(_settingsPath, new AppSettings(), cancellationToken);
        var devices = await ReadOrCreateAsync(_devicesPath, new List<DeviceInfo>(), cancellationToken);
        foreach (var device in devices)
        {
            device.ConnectionState = ConnectionState.Offline;
            device.ScriptState = ScriptState.Unknown;
            device.LastUpdated = null;
        }
        return (settings, devices);
    }

    public async Task SaveAsync(
        AppSettings settings,
        IEnumerable<DeviceInfo> devices,
        CancellationToken cancellationToken = default)
    {
        var deviceSnapshot = devices.ToList();
        await _saveGate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(_configDirectory);
            await WriteAsync(_settingsPath, settings, cancellationToken);
            await WriteAsync(_devicesPath, deviceSnapshot, cancellationToken);
        }
        finally
        {
            _saveGate.Release();
        }
    }

    private async Task<T> ReadOrCreateAsync<T>(
        string path, T fallback, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            await WriteAsync(path, fallback, cancellationToken);
            return fallback;
        }
        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<T>(stream, _json, cancellationToken) ?? fallback;
        }
        catch (JsonException) { return fallback; }
    }

    private async Task WriteAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        var temporaryPath = path + ".tmp";
        try
        {
            await using (var stream = new FileStream(
                temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 16_384, true))
            {
                await JsonSerializer.SerializeAsync(stream, value, _json, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            File.Move(temporaryPath, path, true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }
}
