// Project storage. The wasm filesystem is in-memory and dies with the tab, so
// projects live in IndexedDB: one record per project holding the .ustx bytes.

const DB_NAME = 'openwebtau';
const STORE = 'projects';
let dbPromise = null;

function open() {
    if (dbPromise) return dbPromise;
    dbPromise = new Promise((resolve, reject) => {
        const req = indexedDB.open(DB_NAME, 1);
        req.onupgradeneeded = () => {
            const db = req.result;
            if (!db.objectStoreNames.contains(STORE)) {
                db.createObjectStore(STORE, { keyPath: 'id' });
            }
        };
        req.onsuccess = () => resolve(req.result);
        req.onerror = () => reject(req.error);
    });
    return dbPromise;
}

async function tx(mode, fn) {
    const db = await open();
    return new Promise((resolve, reject) => {
        const t = db.transaction(STORE, mode);
        const req = fn(t.objectStore(STORE));
        t.onerror = () => reject(t.error);
        req.onsuccess = () => resolve(req.result);
        req.onerror = () => reject(req.error);
    });
}

/// Metadata only — the .ustx bytes stay out of the listing so it loads fast.
export async function list() {
    const all = await tx('readonly', s => s.getAll());
    return all
        .map(p => ({ id: p.id, name: p.name, updated: p.updated, tracks: p.tracks, notes: p.notes }))
        .sort((a, b) => b.updated - a.updated);
}

export async function save(id, name, base64, tracks, notes) {
    await tx('readwrite', s => s.put({
        id, name, ustx: base64, tracks, notes, updated: Date.now(),
    }));
    return id;
}

export async function load(id) {
    const rec = await tx('readonly', s => s.get(id));
    return rec ? rec.ustx : null;
}

export async function remove(id) {
    await tx('readwrite', s => s.delete(id));
}

export async function rename(id, name) {
    const rec = await tx('readonly', s => s.get(id));
    if (!rec) return;
    rec.name = name;
    rec.updated = Date.now();
    await tx('readwrite', s => s.put(rec));
}
