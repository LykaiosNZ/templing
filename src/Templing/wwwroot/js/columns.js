// The Library's virtualised card grid draws its cards row by row, so .NET needs to know how many columns fit.
// The sizes come from the grid's CSS (--card-min, --card-gap, --band-pad), the same ones its rows use.
// watch() again to measure afresh when they change (the Library does when its cards are grouped or not).
const watched = new Map(); // element -> ResizeObserver

export function watch(el, dotnet) {
    unwatch(el);
    let last = 0;
    const ro = new ResizeObserver(() => {
        const css = getComputedStyle(el);
        const min = parseFloat(css.getPropertyValue('--card-min')) || 260;
        const gap = parseFloat(css.getPropertyValue('--card-gap')) || 14;
        const pad = parseFloat(css.getPropertyValue('--band-pad')) || 0; // grouped: each group's box is padded on both sides
        const n = Math.max(1, Math.floor((el.clientWidth - 2 * pad + gap) / (min + gap)));
        if (n !== last) { last = n; dotnet.invokeMethodAsync('OnColumns', n).catch(() => { }); }
    });
    ro.observe(el); // reports the current width straight away
    watched.set(el, ro);
}

export function unwatch(el) {
    watched.get(el)?.disconnect();
    watched.delete(el);
}
