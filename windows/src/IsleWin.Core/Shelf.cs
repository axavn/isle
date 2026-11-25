using System.Text.Json;

namespace IsleWin.Core;

/// One thing on the shelf: a reference to a file or folder. Never a copy.
public sealed record ShelfItem(string Path, bool IsFolder)
{
    public string Name => System.IO.Path.GetFileName(Path.TrimEnd('\\', '/')) is { Length: > 0 } n ? n : Path;
}

/// Isle's Shelf (ShelfStore.swift): up to 10 references to files and folders, oldest first, kept across
/// launches in `~/.isle/shelf.json`. It holds the path itself (never a copy, never a move) and drops an item
/// when its file is moved, renamed or deleted (a rename is not followed, like Isle). Thread-safe;
/// `Changed` may be raised on a worker thread, so a view marshals to its dispatcher.
public sealed class ShelfStore : IDisposable
{
    public const int Capacity = 10;
    public static ShelfStore Current { get; } = new(System.IO.Path.Combine(IslePaths.Default.Root, "shelf.json"), loadAsync: true);

    readonly object _gate = new();
    readonly string _file;
    readonly List<ShelfItem> _items = new();
    readonly Dictionary<string, long> _added = new(StringComparer.OrdinalIgnoreCase);
    List<string> _pending = new();       // saved paths still being checked at load; they count against room and duplicates
    readonly Dictionary<string, FileSystemWatcher> _watchers = new(StringComparer.OrdinalIgnoreCase);
    readonly object _pruneLock = new();
    long _addCount; bool _disposed;

    public event Action? Changed;

    /// `loadAsync`: check the saved paths on a worker (a dead network share can hang), then put the survivors
    /// ahead of anything added meanwhile. Tests pass false to have everything in place when the constructor returns.
    public ShelfStore(string file, bool loadAsync = false)
    {
        _file = file;
        try
        {
            if (File.Exists(file) && JsonSerializer.Deserialize<List<string>>(AtomicFile.ReadAllText(file)) is { } saved)
                _pending = saved.Where(p => !string.IsNullOrWhiteSpace(p)).Select(Norm).Distinct(StringComparer.OrdinalIgnoreCase).Take(Capacity).ToList();
        }
        catch { }
        if (loadAsync) Task.Run(FinishLoad); else FinishLoad();
    }

    void FinishLoad()
    {
        List<string> toCheck;
        lock (_gate) toCheck = _pending.ToList();
        var alive = toCheck.Select(p => new ShelfItem(p, Directory.Exists(p))).Where(i => i.IsFolder || File.Exists(i.Path)).ToList();
        lock (_gate)
        {
            _pending = new();
            _items.InsertRange(0, alive.Where(a => !_items.Any(i => Same(i.Path, a.Path))));
            while (_items.Count > Capacity) _items.RemoveAt(_items.Count - 1);
            foreach (var i in _items) _added.TryAdd(i.Path, ++_addCount);
        }
        Save(); SyncWatchers(); Changed?.Invoke();
    }

    public IReadOnlyList<ShelfItem> Items { get { lock (_gate) return _items.ToArray(); } }
    public int Count { get { lock (_gate) return _items.Count; } }
    public bool IsEmpty => Count == 0;
    public bool IsFull => Count >= Capacity;
    /// How many more would fit (items still being restored count).
    public int Room { get { lock (_gate) return Math.Max(0, Capacity - _items.Count - _pending.Count); } }

    public static string Norm(string p)
    {
        var full = System.IO.Path.GetFullPath(p);
        var root = System.IO.Path.GetPathRoot(full);
        return full.Length > (root?.Length ?? 0) ? full.TrimEnd('\\', '/') : full;
    }
    static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// Normalised, de-duplicated (case-insensitive) paths, order kept.
    public static string[] Unique(IEnumerable<string> paths)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var r = new List<string>();
        foreach (var p in paths)
        {
            if (string.IsNullOrWhiteSpace(p)) continue;
            string n; try { n = Norm(p); } catch { continue; }
            if (seen.Add(n)) r.Add(n);
        }
        return r.ToArray();
    }

    /// How many of these would go on the shelf now (new paths, up to the room left).
    public int WouldKeep(IEnumerable<string> paths)
    {
        lock (_gate) return Math.Min(Unique(paths).Count(p => !Has(p)), Room);
    }

    bool Has(string p) => _items.Any(i => Same(i.Path, p)) || _pending.Any(x => Same(x, p));

    /// Puts new paths on the end; returns how many went on. Duplicates and anything past the 10th are skipped.
    public int Add(IEnumerable<string> paths)
    {
        var added = 0;
        lock (_gate)
        {
            foreach (var p in Unique(paths))
            {
                if (Has(p) || _items.Count + _pending.Count >= Capacity) continue;
                _items.Add(new ShelfItem(p, Directory.Exists(p))); _added[p] = ++_addCount; added++;
            }
        }
        if (added > 0) { Save(); SyncWatchers(); Changed?.Invoke(); }
        return added;
    }

    public bool Remove(string path)
    {
        bool gone;
        lock (_gate) { gone = _items.RemoveAll(i => Same(i.Path, path)) > 0; }
        if (gone) { Save(); SyncWatchers(); Changed?.Invoke(); }
        return gone;
    }

    public void Clear()
    {
        bool had;
        lock (_gate) { had = _items.Count > 0 || _pending.Count > 0; _items.Clear(); _pending.Clear(); }
        if (had) { Save(); SyncWatchers(); Changed?.Invoke(); }
    }

    /// Drops items whose file has gone (moved, renamed, deleted, drive unplugged), checking on a worker so a
    /// dead share never blocks the caller. One check at a time; an item added after a check began is safe from it.
    public void Prune() => Task.Run(PruneNow);

    /// The same, on the calling thread (tests, and the worker itself).
    public void PruneNow()
    {
        lock (_pruneLock) // one check at a time; a caller that arrives meanwhile runs right after
        {
            List<ShelfItem> snapshot; long asOf;
            lock (_gate) { snapshot = _items.ToList(); asOf = _addCount; }
            var gone = snapshot.Where(i => !(File.Exists(i.Path) || Directory.Exists(i.Path))).Select(i => i.Path).ToList();
            var changed = false;
            if (gone.Count > 0)
                lock (_gate)
                    foreach (var g in gone)
                    {
                        if (_added.TryGetValue(g, out var seq) && seq > asOf) continue; // added after this check began
                        changed |= _items.RemoveAll(i => Same(i.Path, g)) > 0;
                    }
            if (changed) { Save(); SyncWatchers(); Changed?.Invoke(); }
        }
    }

    void Save()
    {
        try
        {
            string[] all;
            lock (_gate) all = _pending.Concat(_items.Select(i => i.Path)).ToArray();
            AtomicFile.WriteAllText(_file, JsonSerializer.Serialize(all));
        }
        catch { }
    }

    // One watcher per parent folder: a file that goes (deleted, renamed, moved out, Recycle Bin) leaves the shelf.
    void SyncWatchers() => Task.Run(() =>
    {
        lock (_gate)
        {
            if (_disposed) return;
            var parents = _items.Select(i => System.IO.Path.GetDirectoryName(i.Path)).Where(d => !string.IsNullOrEmpty(d)).Select(d => d!).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var d in _watchers.Keys.Where(d => !parents.Contains(d)).ToList()) { _watchers[d].Dispose(); _watchers.Remove(d); }
            foreach (var d in parents.Where(d => !_watchers.ContainsKey(d)))
            {
                try
                {
                    var w = new FileSystemWatcher(d) { NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName, IncludeSubdirectories = false };
                    w.Deleted += (_, e) => OnGone(e.FullPath);
                    w.Renamed += (_, e) => OnGone(e.OldFullPath);
                    w.Error += (_, _) => Prune();
                    w.EnableRaisingEvents = true;
                    _watchers[d] = w;
                }
                catch { } // folder gone or unwatchable: the prune on panel open still catches it
            }
        }
    });

    void OnGone(string path)
    {
        lock (_gate) { if (!_items.Any(i => Same(i.Path, path))) return; }
        Prune();
    }

    public static string KindsLine(int folders, int files)
    {
        var parts = new List<string>();
        if (folders > 0) parts.Add(folders == 1 ? "1 folder" : $"{folders} folders");
        if (files > 0) parts.Add(files == 1 ? "1 file" : $"{files} files");
        return string.Join(", ", parts);
    }
    public string Tally { get { var l = Items; return KindsLine(l.Count(i => i.IsFolder), l.Count(i => !i.IsFolder)); } }

    public void Dispose()
    {
        lock (_gate) { _disposed = true; foreach (var w in _watchers.Values) w.Dispose(); _watchers.Clear(); }
    }
}
