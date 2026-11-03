namespace Glint.App.Services;

public sealed class CatalogMonitor : IDisposable
{
    private readonly List<FileSystemWatcher> watchers = [];
    private readonly object gate = new();
    private readonly Timer debounce;
    private readonly Timer fallback;
    private bool disposed;
    public event Action? Changed;

    public CatalogMonitor(IEnumerable<string>? directories = null)
    {
        debounce = new Timer(_ => RaiseChanged(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        // The periodic refresh also covers Store apps and missed filesystem notifications.
        fallback = new Timer(_ => RaiseChanged(), null, TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(10));
        foreach (var directory in (directories ?? new[]
                 { Environment.GetFolderPath(Environment.SpecialFolder.Programs), Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms) })
                 .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(directory)) continue;
            FileSystemWatcher? watcher = null;
            try
            {
                watcher = new FileSystemWatcher(directory)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite
                };
                watcher.Created += OnFileChanged;
                watcher.Changed += OnFileChanged;
                watcher.Deleted += OnFileChanged;
                watcher.Renamed += OnFileChanged;
                watcher.Error += (_, _) => ScheduleRefresh();
                watcher.EnableRaisingEvents = true;
                watchers.Add(watcher);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
            { watcher?.Dispose(); }
        }
    }

    private void OnFileChanged(object sender, FileSystemEventArgs args) => ScheduleRefresh();
    private void ScheduleRefresh()
    {
        lock (gate)
            if (!disposed) debounce.Change(TimeSpan.FromSeconds(1.5), Timeout.InfiniteTimeSpan);
    }

    private void RaiseChanged()
    {
        lock (gate)
        {
            if (disposed) return;
        }
        Changed?.Invoke();
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            debounce.Dispose();
            fallback.Dispose();
        }
        foreach (var watcher in watchers) watcher.Dispose();
    }
}
