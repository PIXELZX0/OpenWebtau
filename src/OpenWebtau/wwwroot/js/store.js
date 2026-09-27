// Project and voicebank storage. The wasm filesystem is in-memory and dies with
// the tab, so data lives either on the server or in IndexedDB.
//
// Server: the Docker image serves /data/ from a volume through nginx WebDAV
// (PUT, DELETE, JSON directory listing). Each item is a payload file plus a
// .json sidecar with its metadata, so listing never downloads the payloads.
// IndexedDB: used when /data/ is not there, e.g. under `dotnet run`.

let serverPromise = null;
function server() {
    serverPromise ??= fetch('data/projects/', { cache: 'no-store' })
        .then(r => r.ok && (r.headers.get('content-type') || '').includes('json'))
        .catch(() => false);
    return serverPromise;
}

const url = (dir, name) => `data/${dir}/${encodeURIComponent(name)}`;

async function http(method, path, body) {
    const r = await fetch(path, { method, body, cache: 'no-store' });
    if (!r.ok && !(method === 'DELETE' && r.status === 404)) {
        throw new Error(`${method} ${path}: ${r.status}`);
    }
    return r;
}

/// Metadata of every <name>.json sidecar in a /data/ directory.
async function listMeta(dir) {
    const entries = await (await http('GET', `data/${dir}/`)).json();
    const metas = await Promise.all(entries
        .filter(e => e.type === 'file' && e.name.endsWith('.json'))
        .map(e => http('GET', url(dir, e.name)).then(r => r.json()).catch(() => null)));
    return metas.filter(m => m != null);
}

const putJson = (dir, name, obj) => http('PUT', url(dir, name + '.json'), JSON.stringify(obj));
const fromBase64 = b64 => Uint8Array.from(atob(b64), c => c.charCodeAt(0));
async function toBase64(bytes) {
    // FileReader handles large buffers without blowing the argument limit of btoa(String.fromCharCode(...)).
    const dataUrl = await new Promise(res => {
        const fr = new FileReader();
        fr.onload = () => res(fr.result);
        fr.readAsDataURL(new Blob([bytes]));
    });
    return dataUrl.slice(dataUrl.indexOf(',') + 1);
}

const DB_NAME = 'openwebtau';
const STORE = 'projects';
const SINGERS = 'singers';
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
            if (!db.objectStoreNames.contains(SINGERS)) {
                db.createObjectStore(SINGERS, { keyPath: 'name' });
            }
        };
        req.onsuccess = () => resolve(req.result);
        req.onerror = () => reject(req.error);
    });
    return dbPromise;
}

async function tx(mode, fn, store = STORE) {
    const db = await open();
    return new Promise((resolve, reject) => {
        const t = db.transaction(store, mode);
        const req = fn(t.objectStore(store));
        t.onerror = () => reject(t.error);
        req.onsuccess = () => resolve(req.result);
        req.onerror = () => reject(req.error);
    });
}

/// Metadata only — the .ustx bytes stay out of the listing so it loads fast.
async function idb_list() {
    const all = await tx('readonly', s => s.getAll());
    return all
        .map(p => ({ id: p.id, name: p.name, updated: p.updated, tracks: p.tracks, notes: p.notes }))
        .sort((a, b) => b.updated - a.updated);
}

async function idb_save(id, name, base64, tracks, notes) {
    await tx('readwrite', s => s.put({
        id, name, ustx: base64, tracks, notes, updated: Date.now(),
    }));
    return id;
}

async function idb_load(id) {
    const rec = await tx('readonly', s => s.get(id));
    return rec ? rec.ustx : null;
}

async function idb_remove(id) {
    await tx('readwrite', s => s.delete(id));
}

async function idb_rename(id, name) {
    const rec = await tx('readonly', s => s.get(id));
    if (!rec) return;
    rec.name = name;
    rec.updated = Date.now();
    await tx('readwrite', s => s.put(rec));
}

// --- voicebank archives -------------------------------------------------
// Singers install into the wasm in-memory VFS, which dies with the tab. The
// uploaded archives are kept here so startup can reinstall them.

// Bytes cross interop as Uint8Array, not base64: banks run to hundreds of MB.
async function idb_saveSinger(name, bytes, encoding) {
    await tx('readwrite', s => s.put({ name, data: bytes, encoding }), SINGERS);
}

async function idb_listSingerNames() {
    return await tx('readonly', s => s.getAllKeys(), SINGERS);
}

async function idb_getSinger(name) {
    const rec = await tx('readonly', s => s.get(name), SINGERS);
    return rec ? { data: rec.data, encoding: rec.encoding ?? 'shift_jis' } : null;
}

// --- server backend -----------------------------------------------------

const srv = {
    async list() {
        const all = await listMeta('projects');
        return all.sort((a, b) => b.updated - a.updated);
    },
    async save(id, name, base64, tracks, notes) {
        await http('PUT', url('projects', id + '.ustx'), fromBase64(base64));
        await putJson('projects', id, { id, name, tracks, notes, updated: Date.now() });
        return id;
    },
    async load(id) {
        const r = await fetch(url('projects', id + '.ustx'), { cache: 'no-store' });
        return r.ok ? await toBase64(new Uint8Array(await r.arrayBuffer())) : null;
    },
    async remove(id) {
        await http('DELETE', url('projects', id + '.json'));
        await http('DELETE', url('projects', id + '.ustx'));
    },
    async rename(id, name) {
        const r = await fetch(url('projects', id + '.json'), { cache: 'no-store' });
        if (!r.ok) return;
        const meta = await r.json();
        await putJson('projects', id, { ...meta, name, updated: Date.now() });
    },
    async saveSinger(name, bytes, encoding) {
        await http('PUT', url('singers', name), bytes);
        await putJson('singers', name, { name, encoding });
    },
    async listSingerNames() {
        return (await listMeta('singers')).map(m => m.name);
    },
    async getSinger(name) {
        const meta = await (await http('GET', url('singers', name + '.json'))).json();
        const r = await http('GET', url('singers', name));
        return { data: new Uint8Array(await r.arrayBuffer()), encoding: meta.encoding ?? 'shift_jis' };
    },
};

const idb = {
    list: idb_list, save: idb_save, load: idb_load, remove: idb_remove, rename: idb_rename,
    saveSinger: idb_saveSinger, listSingerNames: idb_listSingerNames, getSinger: idb_getSinger,
};
const pick = async () => (await server()) ? srv : idb;

export const storageKind = async () => (await server()) ? 'server' : 'browser';
export const list = async (...a) => (await pick()).list(...a);
export const save = async (...a) => (await pick()).save(...a);
export const load = async (...a) => (await pick()).load(...a);
export const remove = async (...a) => (await pick()).remove(...a);
export const rename = async (...a) => (await pick()).rename(...a);
export const saveSinger = async (...a) => (await pick()).saveSinger(...a);
export const listSingerNames = async (...a) => (await pick()).listSingerNames(...a);
export const getSinger = async (...a) => (await pick()).getSinger(...a);
