// The demo build's stores (-p:Demo=true): IndexedDB's object stores, in memory. db.js loads this module only when
// DataStore calls useMemory(), and the normal build doesn't publish it (Templing.csproj). The demo never reads or
// overwrites the IndexedDB data of someone who uses the app on the same site (Pages serves the demo beside the app,
// on the same origin). Everything is gone when the page goes.
const stores = new Map();   // store name -> Map(Id -> row)

// An object store's requests, answered at once: rows are copied in and out, as IndexedDB's structured clone does
export function store(name) {
    if (!stores.has(name)) stores.set(name, new Map());
    const rows = stores.get(name);
    const done = result => ({ result });
    return {
        getAll: () => done([...rows.values()].map(r => structuredClone(r))),
        get: id => done(rows.has(id) ? structuredClone(rows.get(id)) : undefined),
        put: row => { rows.set(row.Id, structuredClone(row)); return done(row.Id); },
        delete: id => { rows.delete(id); return done(undefined); },
        clear: () => { rows.clear(); return done(undefined); },
    };
}
