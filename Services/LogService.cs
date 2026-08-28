using System.Collections.ObjectModel;
using System.Windows.Threading;
using XXTouchController.Models;

namespace XXTouchController.Services;

public sealed class LogService
{
    private readonly Dispatcher _dispatcher;
    public LogService(Dispatcher dispatcher) => _dispatcher = dispatcher;
    public ObservableCollection<LogEntry> Entries { get; } = [];

    public void Add(string deviceName, string ip, string action, string result,
        LogLevel level = LogLevel.Info, string? error = null)
    {
        void Append()
        {
            Entries.Add(new LogEntry(DateTime.Now, deviceName, ip, action, result, error, level));
            while (Entries.Count > 1000) Entries.RemoveAt(0);
        }
        if (_dispatcher.CheckAccess()) Append();
        else _dispatcher.Invoke(Append);
    }

    public void Clear() => Entries.Clear();
}
