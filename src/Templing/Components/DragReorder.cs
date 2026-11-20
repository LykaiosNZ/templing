namespace Templing.Components;

/// <summary>
/// Drag-to-reorder for an editable list (plan steps, sections). The list reorders live as the dragged row passes
/// over others, so the list itself is the preview (js/drag.js hides the browser's ghost); Esc or a drop outside the
/// list puts it back. Rows wire <c>@ondragenter</c> → <see cref="Enter"/>, <c>@ondrop</c> → <see cref="Drop"/>,
/// and the handle's <c>@ondragstart</c> / <c>@ondragend</c> → <see cref="Start"/> / <see cref="End"/>.
/// Never put a handler on dragover: it fires continuously and each call would re-render.
/// </summary>
public sealed class DragReorder<T>(Func<List<T>> list, Func<T, string> id)
{
    string? _justSwapped;
    List<T>? _startOrder;
    bool _dropped;

    /// <summary>Id of the row being dragged (outline it), or null.</summary>
    public string? DraggingId { get; private set; }

    public void Start(T item)
    {
        DraggingId = id(item);
        _startOrder = list().ToList();
        _dropped = false;
        _justSwapped = null;
    }

    /// <summary>The pointer entered row <paramref name="index"/>. Returns false when nothing moved, so the caller can skip a render.</summary>
    public bool Enter(int index)
    {
        var items = list();
        if (DraggingId == null || index < 0 || index >= items.Count) return false;
        var targetId = id(items[index]);
        // dragenter fires for every child crossed; ignore the dragged row itself, and the row it just swapped
        // with (after a swap with a taller row the pointer can land on it, which would swap straight back)
        if (targetId == DraggingId) { _justSwapped = null; return false; }
        if (targetId == _justSwapped) return false;
        var from = items.FindIndex(x => id(x) == DraggingId);
        var dragged = items[from];
        items.RemoveAt(from);
        items.Insert(index, dragged);
        _justSwapped = targetId;
        return true;
    }

    public void Drop() => _dropped = true;

    /// <summary>The drag finished. Returns true if the order changed (dropped somewhere new).</summary>
    public bool End()
    {
        var items = list();
        var changed = false;
        if (_startOrder != null)
        {
            // cancelled (Esc, or dropped outside the list): put the rows back
            if (!_dropped) { items.Clear(); items.AddRange(_startOrder); }
            else changed = !items.Select(id).SequenceEqual(_startOrder.Select(id));
        }
        DraggingId = null; _startOrder = null; _justSwapped = null;
        return changed;
    }
}
