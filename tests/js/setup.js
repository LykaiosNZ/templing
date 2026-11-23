// What the browser has and jsdom doesn't.
if (!window.matchMedia) {
    window.matchMedia = query => ({
        matches: false, media: query,
        addEventListener() { }, removeEventListener() { }, addListener() { }, removeListener() { },
    });
}
