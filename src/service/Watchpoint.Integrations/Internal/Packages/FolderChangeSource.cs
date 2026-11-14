namespace Watchpoint.Integrations.Internal.Packages;

/// <summary>Notifications about a watched folder. Events may be raised on any thread, several times per change.</summary>
internal interface IFolderChangeSource : IDisposable
{
    /// <summary>Something changed in the folder (created, written, renamed, deleted).</summary>
    event Action? Changed;

    /// <summary>Events were lost (e.g. the operating system's buffer overflowed): the folder content is unknown.</summary>
    event Action<Exception?>? Overflowed;

    /// <summary>Starts watching.</summary>
    void Start();
}

/// <summary>Creates the change source of a folder.</summary>
internal interface IFolderChangeSourceFactory
{
    IFolderChangeSource Create(string folder);
}

/// <summary><see cref="FileSystemWatcher"/> on the package folder and its sub-folders (unpacked packages).</summary>
internal sealed class FileSystemFolderChangeSource : IFolderChangeSource
{
    private readonly FileSystemWatcher _watcher;

    public FileSystemFolderChangeSource(string folder)
    {
        Directory.CreateDirectory(folder);
        _watcher = new FileSystemWatcher(folder)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
            InternalBufferSize = 64 * 1024,
        };
        _watcher.Created += OnEvent;
        _watcher.Changed += OnEvent;
        _watcher.Deleted += OnEvent;
        _watcher.Renamed += OnEvent;
        _watcher.Error += (_, e) => Overflowed?.Invoke(e.GetException());
    }

    public event Action? Changed;

    public event Action<Exception?>? Overflowed;

    public void Start() => _watcher.EnableRaisingEvents = true;

    public void Dispose() => _watcher.Dispose();

    private void OnEvent(object sender, FileSystemEventArgs e)
    {
        // The runtime's own folders inside the user folder (.cache, .staging) change while packages are verified.
        var name = e.Name ?? string.Empty;
        if (name.StartsWith(".cache", StringComparison.Ordinal))
        {
            return;
        }

        Changed?.Invoke();
    }

    internal sealed class Factory : IFolderChangeSourceFactory
    {
        public IFolderChangeSource Create(string folder) => new FileSystemFolderChangeSource(folder);
    }
}
