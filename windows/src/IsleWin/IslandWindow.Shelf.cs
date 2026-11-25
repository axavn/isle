using System.Windows;
using System.Windows.Threading;
using IsleWin.Core;

namespace IsleWin;

/// The shelf and the drop face (Isle's ShelfStore / DropFaceView). The island reacts when a file drag reaches
/// it: it opens on the drop face, which stands in for whatever face was open, at the standard height, and goes
/// again with the drag. Keep on shelf puts references (never copies) on the shelf and shows its face; Start
/// Claude here opens a terminal in the one dropped folder. A drop off the targets hands the files back.
/// Not while a Claude alert is up (Isle's `acceptsDrops`), nor for a drag out of the shelf itself.
public partial class IslandWindow
{
    ShelfView _shelf = null!;
    DropFaceView _dropFace = null!;
    ShelfPreviewImage _shelfLead = null!;
    Action _shelfTile = null!;
    bool _dropping, _dragLeavePending;

    void InitShelf()
    {
        var store = ShelfStore.Current;
        _shelf = new ShelfView(store);
        AddFaceView(IslandFace.Shelf, _shelf);
        _shelf.ContentHeightChanged += () => { if (_place == IslandFace.Shelf) Retarget(); };

        _dropFace = new DropFaceView(store) { Visibility = Visibility.Hidden };
        Faces.Children.Add(_dropFace);

        _shelfLead = new ShelfPreviewImage(22, 5, true);
        _shelfTile = _home.AddTile(IslandFace.Shelf, _shelfLead, "Shelf", () =>
        {
            int n = store.Count;
            return n == 0 ? ("", "") : (n == 1 ? "1 item" : $"{n} items", store.Tally);
        }, null);
        store.Changed += () => Dispatcher.BeginInvoke(OnShelf);
        OnShelf();

        AllowDrop = true;
        DragEnter += OnDragEnter; DragOver += OnDragOver; DragLeave += OnDragLeave; Drop += OnDrop;
    }

    /// The shelf filled or emptied: its face (last in the row) comes or goes, and its tile shows the newest item.
    void OnShelf()
    {
        var items = ShelfStore.Current.Items;
        if (items.Count > 0) _shelfLead.SetItem(items[^1].Path, items[^1].IsFolder);
        _shelfTile();
        SetFaceAvailable(IslandFace.Shelf, items.Count > 0);
    }

    // ---------------- a file drag ----------------
    bool AcceptsDrop(DragEventArgs e) =>
        !ShelfView.DraggingOut && !Claude.NeedsAttention && e.Data.GetDataPresent(DataFormats.FileDrop);

    void OnDragEnter(object sender, DragEventArgs e)
    {
        _dragLeavePending = false;
        if (!AcceptsDrop(e)) { e.Effects = DragDropEffects.None; e.Handled = true; return; }
        if (!_dropping && e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } paths)
        {
            _dropFace.SetItems(paths);
            _dropping = true;
            CrumbSlot.Visibility = Visibility.Collapsed;
            Open(); Retarget(); LayoutPages();
        }
        OnDragOver(sender, e);
    }

    void OnDragOver(object sender, DragEventArgs e)
    {
        _dragLeavePending = false;
        e.Handled = true;
        if (!_dropping) { e.Effects = DragDropEffects.None; return; }
        var t = _dropFace.TargetAt(e.GetPosition(_dropFace));
        if (t is { } target && !_dropFace.CanPerform(target)) t = null;
        _dropFace.Highlight(t);
        e.Effects = t == null ? DragDropEffects.None // never Move: the file stays where it is
            : e.AllowedEffects.HasFlag(DragDropEffects.Copy) ? DragDropEffects.Copy
            : e.AllowedEffects.HasFlag(DragDropEffects.Link) ? DragDropEffects.Link : DragDropEffects.None;
    }

    /// WPF says "leave" each time the drag crosses from one element to another inside the window, with an
    /// "enter" right behind it; only a leave that no enter follows is the drag going (or being cancelled).
    void OnDragLeave(object sender, DragEventArgs e)
    {
        _dragLeavePending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (!_dragLeavePending || !_dropping) return;
            _dragLeavePending = false;
            EndDrop();
            Close_();
        });
    }

    void OnDrop(object sender, DragEventArgs e)
    {
        _dragLeavePending = false;
        e.Handled = true;
        if (!_dropping) { e.Effects = DragDropEffects.None; return; }
        var t = _dropFace.TargetAt(e.GetPosition(_dropFace));
        if (t is { } target && !_dropFace.CanPerform(target)) t = null;
        _dropFace.Highlight(null);
        e.Effects = t == null ? DragDropEffects.None : e.AllowedEffects.HasFlag(DragDropEffects.Copy) ? DragDropEffects.Copy : DragDropEffects.Link;
        // the panel stays open as a hover; the work runs once the drop's OLE call has returned
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            EndDrop();
            if (t is { } chosen && _dropFace.Perform(chosen)) { OnShelf(); ShowPlace(IslandFace.Shelf); }
        });
    }

    /// Back from the drop face to the face that was open.
    void EndDrop()
    {
        if (!_dropping) return;
        _dropping = false;
        _dropFace.Highlight(null);
        _dropFace.Visibility = Visibility.Hidden;
        CrumbSlot.Visibility = _place == null ? Visibility.Collapsed : Visibility.Visible;
        Retarget(); LayoutPages();
    }
}
