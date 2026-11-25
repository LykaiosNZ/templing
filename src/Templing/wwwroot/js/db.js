// Minimal IndexedDB wrapper. Values are passed as JSON strings so .NET controls serialization.
const DB_NAME = 'templing';   // also opened by the boot script in index.html: keep the two in step
const DB_VERSION = 6; // 5: "items" store renamed "pieces"; 6: "devices"
// Adding a store? Bump DB_VERSION and follow the "Persisted data" checklist in CLAUDE.md
// (DataStore load/import/clear, Backup.cs export/restore).
// 'files' holds raw score files (Guitar Pro etc.) as {Id, Name, Data: Uint8Array}
// 'session' holds transient UI state (e.g. the running plan) that survives a refresh; never backed up
// 'devices' holds this device's identity and its paired devices, with their keys as non-extractable CryptoKeys
//   (link.js, which reads and writes it with getRows/putRow); never backed up, since a key belongs to one device
const STORES = ['pieces', 'logs', 'presets', 'settings', 'files', 'plans', 'session', 'devices'];
let dbPromise = null;

// The demo build (-p:Demo=true): DataStore calls useMemory() before anything else, and the stores then live in memory
// (memorydb.js, which only the demo build publishes and loads), so the demo never touches IndexedDB.
let memory = null;
export async function useMemory() {
    if (dbPromise) throw new Error('useMemory() after IndexedDB was opened');
    memory = await import('./memorydb.js');
}

function open() {
    if (dbPromise) return dbPromise;
    dbPromise = new Promise((resolve, reject) => {
        const req = indexedDB.open(DB_NAME, DB_VERSION);
        req.onupgradeneeded = () => {
            const db = req.result;
            for (const s of STORES) {
                if (!db.objectStoreNames.contains(s)) db.createObjectStore(s, { keyPath: 'Id' });
            }
        };
        req.onsuccess = () => { const db = req.result; db.onversionchange = () => db.close(); resolve(db); };
        req.onerror = () => reject(req.error);
    });
    return dbPromise;
}

function tx(store, mode, fn) {
    if (memory) return Promise.resolve().then(() => fn(memory.store(store))?.result);
    return open().then(db => new Promise((resolve, reject) => {
        const t = db.transaction(store, mode);
        const os = t.objectStore(store);
        let result;
        const r = fn(os);
        if (r) r.onsuccess = () => { result = r.result; };
        t.oncomplete = () => resolve(result);
        t.onerror = () => reject(t.error);
        t.onabort = () => reject(t.error);
    }));
}

// UTF-8 bytes rather than a string: Blazor passes a Uint8Array to .NET as it is, where a string goes through
// interop's own JSON encoding (a big log store took seconds), and System.Text.Json reads UTF-8 directly.
export async function getAll(store) {
    const rows = await tx(store, 'readonly', os => os.getAll());
    return new TextEncoder().encode(JSON.stringify(rows || []));
}

export async function get(store, id) {
    const row = await tx(store, 'readonly', os => os.get(id));
    return row ? JSON.stringify(row) : null;
}

export function put(store, json) {
    return tx(store, 'readwrite', os => os.put(JSON.parse(json)));
}

// json: a string, or UTF-8 bytes for big writes (a restore), which cross from .NET without interop re-encoding them
export function putMany(store, json) {
    const rows = JSON.parse(typeof json === 'string' ? json : new TextDecoder().decode(json));
    return tx(store, 'readwrite', os => { for (const r of rows) os.put(r); return null; });
}

export function remove(store, id) {
    return tx(store, 'readwrite', os => os.delete(id));
}

export function clear(store) {
    return tx(store, 'readwrite', os => os.clear());
}

// data: a string, or UTF-8 bytes (a backup), which Blazor passes as they are
export function download(filename, data, mime) {
    const blob = new Blob([data], { type: mime || 'text/plain' });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url; a.download = filename;
    document.body.appendChild(a); a.click(); a.remove();
    setTimeout(() => URL.revokeObjectURL(url), 1000);
}

// ---- rows kept as they are (structured clone, not JSON): link.js's 'devices', whose keys are CryptoKeys
export async function getRows(store) {
    return (await tx(store, 'readonly', os => os.getAll())) || [];
}

export function putRow(store, row) {
    return tx(store, 'readwrite', os => os.put(row));
}

export function removeRow(store, id) {
    return tx(store, 'readwrite', os => os.delete(id));
}

// ---- binary score files (kept in JS; never serialised through .NET)
export function putFile(id, name, bytes) {
    return tx('files', 'readwrite', os => os.put({ Id: id, Name: name, Data: bytes }));
}

export async function getFile(id) {
    const row = await tx('files', 'readonly', os => os.get(id));
    return row ? row.Data : null;
}

export function removeFile(id) {
    return tx('files', 'readwrite', os => os.delete(id));
}
