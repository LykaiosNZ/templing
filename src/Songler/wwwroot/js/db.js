// Minimal IndexedDB wrapper. Values are passed as JSON strings so .NET controls serialization.
const DB_NAME = 'songler';
const DB_VERSION = 2;
// 'files' holds raw score files (Guitar Pro etc.) as {Id, Name, Data: Uint8Array}
const STORES = ['items', 'logs', 'presets', 'settings', 'files'];
let dbPromise = null;

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

export async function getAll(store) {
    const rows = await tx(store, 'readonly', os => os.getAll());
    return JSON.stringify(rows || []);
}

export async function get(store, id) {
    const row = await tx(store, 'readonly', os => os.get(id));
    return row ? JSON.stringify(row) : null;
}

export function put(store, json) {
    return tx(store, 'readwrite', os => os.put(JSON.parse(json)));
}

export function putMany(store, json) {
    const rows = JSON.parse(json);
    return tx(store, 'readwrite', os => { for (const r of rows) os.put(r); return null; });
}

export function remove(store, id) {
    return tx(store, 'readwrite', os => os.delete(id));
}

export function clear(store) {
    return tx(store, 'readwrite', os => os.clear());
}

export function downloadText(filename, text, mime) {
    const blob = new Blob([text], { type: mime || 'text/plain' });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url; a.download = filename;
    document.body.appendChild(a); a.click(); a.remove();
    setTimeout(() => URL.revokeObjectURL(url), 1000);
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
