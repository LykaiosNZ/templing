// Drag-to-reorder support for Blazor lists. Blazor handles the drag events itself; this only does the two
// things it can't: Firefox won't start a drag without dataTransfer data, and the browser's faint drag ghost
// is hidden, because the list reorders live and is itself the preview. Mark the handle with data-drag-handle.
const blank = new Image();
blank.src = 'data:image/gif;base64,R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7';

document.addEventListener('dragstart', e => {
    const handle = e.target instanceof Element ? e.target.closest('[data-drag-handle]') : null;
    if (!handle || !e.dataTransfer) return;
    e.dataTransfer.setData('text/plain', '');
    e.dataTransfer.effectAllowed = 'move';
    e.dataTransfer.setDragImage(blank, 0, 0);
});
