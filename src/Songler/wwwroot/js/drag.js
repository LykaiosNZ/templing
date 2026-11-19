// Drag-to-reorder support for Blazor lists. Blazor handles the drag events itself; this only does the two
// things it can't: Firefox won't start a drag without dataTransfer data, and the drag image should be the
// whole row rather than just the handle. Mark the handle with data-drag-handle and the row with data-drag-row.
document.addEventListener('dragstart', e => {
    const handle = e.target instanceof Element ? e.target.closest('[data-drag-handle]') : null;
    if (!handle || !e.dataTransfer) return;
    e.dataTransfer.setData('text/plain', '');
    e.dataTransfer.effectAllowed = 'move';
    const row = handle.closest('[data-drag-row]');
    if (row) e.dataTransfer.setDragImage(row, 16, 16);
});
