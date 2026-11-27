// Brings a paged list's top back into view after the page changes, when it has scrolled above the screen
// (the list's CSS scroll-margin keeps it clear of the app bar).
export function showTop(el) {
    if (el && el.getBoundingClientRect().top < 0) el.scrollIntoView({ block: 'start' });
}
