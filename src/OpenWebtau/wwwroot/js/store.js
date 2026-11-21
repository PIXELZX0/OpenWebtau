// Project and voicebank storage. The wasm filesystem is in-memory and dies with
// the tab, so data lives either on a server or in IndexedDB.
//
// Server: the Docker image serves /data/ from a volume through nginx WebDAV
// (PUT, DELETE, JSON directory listing). Each item is a payload file plus a
// .json sidecar with its metadata, so listing never downloads the payloads.
// Outside Docker (`dotnet run`), mcp/server.mjs serves the same interface at
// http://127.0.0.1:5178/data/, on disk in data/, where its headless host
// (src/OpenWebtau.Headless) reads the same projects and voicebanks.
// IndexedDB: used when neither is there. What was kept there moves to a
// server once one answers.

const LOCAL = 'http://127.0.0.1:5178/data/';

/// Base URL of a /data/ server that has <dir>/, or null. Same origin first.
async function probe(dir, bases) {
    for (const base of bases) {
        try {
            const r = await fetch(new URL(dir + '/', base), { cache: 'no-store' });
            if (r.ok && (r.headers.get('content-type') || '').includes('json')) return base;
        } catch { }
    }
    return null;
}

const sameOrigin = () => new URL('data/', document.baseURI).href;
const bases = {};
/// Not cached when null: the MCP server may start after the page.
async function serverFor(dir) {
    const base = await (bases[dir] ??= probe(dir, [sameOrigin(), LOCAL]));
    if (base == null) delete bases[dir];
    return base;
}
const projectServer = () => serverFor('projects');
const singerServer = () => serverFor('singers');

const url = (base, dir, name) => new URL(`${dir}/${encodeURIComponent(name)}`, base).href;

async function http(method, path, body) {
    const r = await fetch(path, { method, body, cache: 'no-store' });
    if (!r.ok && !(method === 'DELETE' && r.status === 404)) {
        throw new Error(`${method} ${path}: ${r.status}`);
    }
    return r;
}

/// Metadata of every <name>.json sidecar in a /data/ directory.
async function listMeta(base, dir) {
    const entries = await (await http('GET', new URL(dir + '/', base).href)).json();
    const metas = await Promise.all(entries
        .filter(e => e.type === 'file' && e.name.endsWith('.json'))
        .map(e => http('GET', url(base, dir, e.name)).then(r => r.json()).catch(() => null)));
    return metas.filter(m => m != null);
}

const putJson = (base, dir, name, obj) => http('PUT', url(base, dir, name + '.json'), JSON.stringify(obj));
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

async function idb_removeSinger(name) {
    await tx('readwrite', s => s.delete(name), SINGERS);
}

// --- server backend -----------------------------------------------------

const P = 'projects', S = 'singers';
const srv = {
    async list(base) {
        const all = await listMeta(base, P);
        return all.sort((a, b) => b.updated - a.updated);
    },
    async save(base, id, name, base64, tracks, notes) {
        await http('PUT', url(base, P, id + '.ustx'), fromBase64(base64));
        await putJson(base, P, id, { id, name, tracks, notes, updated: Date.now() });
        return id;
    },
    async load(base, id) {
        const r = await fetch(url(base, P, id + '.ustx'), { cache: 'no-store' });
        return r.ok ? await toBase64(new Uint8Array(await r.arrayBuffer())) : null;
    },
    async remove(base, id) {
        await http('DELETE', url(base, P, id + '.json'));
        await http('DELETE', url(base, P, id + '.ustx'));
    },
    async rename(base, id, name) {
        const r = await fetch(url(base, P, id + '.json'), { cache: 'no-store' });
        if (!r.ok) return;
        const meta = await r.json();
        await putJson(base, P, id, { ...meta, name, updated: Date.now() });
    },
    async saveSinger(base, name, bytes, encoding) {
        // Payload first: a sidecar is what makes the bank show up in the list.
        await http('PUT', url(base, S, name), bytes);
        await putJson(base, S, name, { name, encoding });
    },
    async listSingerNames(base) {
        return (await listMeta(base, S)).map(m => m.name);
    },
    async getSinger(base, name) {
        const meta = await (await http('GET', url(base, S, name + '.json'))).json();
        const r = await http('GET', url(base, S, name));
        return { data: new Uint8Array(await r.arrayBuffer()), encoding: meta.encoding ?? 'shift_jis' };
    },
};

/// Projects saved while no server was reachable move to the server and leave
/// IndexedDB once the server has them. Keeps their names and dates.
async function migrateProjects(base) {
    let all;
    try { all = await tx('readonly', s => s.getAll()); } catch { return; }
    for (const p of all) {
        try {
            await http('PUT', url(base, P, p.id + '.ustx'), fromBase64(p.ustx));
            await putJson(base, P, p.id, { id: p.id, name: p.name, tracks: p.tracks, notes: p.notes, updated: p.updated });
            await idb_remove(p.id);
        } catch (e) {
            console.warn(`Moving project ${p.name} to the server failed`, e);
        }
    }
}

/// Banks installed while no server was reachable move to the server, one at a
/// time, and leave IndexedDB once the server has them.
async function migrateSingers(base) {
    let names;
    try { names = await idb_listSingerNames(); } catch { return; }
    for (const name of names) {
        try {
            const rec = await idb_getSinger(name);
            if (rec) await srv.saveSinger(base, name, rec.data, rec.encoding);
            await idb_removeSinger(name);
        } catch (e) {
            console.warn(`Moving voicebank ${name} to the server failed`, e);
        }
    }
}

const idb = {
    list: idb_list, save: idb_save, load: idb_load, remove: idb_remove, rename: idb_rename,
};

export const storageKind = async () => (await projectServer()) ? 'server' : 'browser';

const projects = method => async (...a) => {
    const base = await projectServer();
    return base ? srv[method](base, ...a) : idb[method](...a);
};
export async function list() {
    const base = await projectServer();
    if (!base) return await idb_list();
    await migrateProjects(base);
    return await srv.list(base);
}
export const save = projects('save');
export const load = projects('load');
export const remove = projects('remove');
export const rename = projects('rename');

export async function saveSinger(name, bytes, encoding) {
    const base = await singerServer();
    return base ? srv.saveSinger(base, name, bytes, encoding) : idb_saveSinger(name, bytes, encoding);
}
export async function listSingerNames() {
    const base = await singerServer();
    if (!base) return await idb_listSingerNames();
    await migrateSingers(base);
    return await srv.listSingerNames(base);
}
export async function getSinger(name) {
    const base = await singerServer();
    return base ? srv.getSinger(base, name) : idb_getSinger(name);
}

// --- voicebank install on the server ------------------------------------
// mcp/server.mjs takes the uploaded archive, its headless host extracts it,
// and the editor loads each extracted bank as a tar (see server.mjs,
// "voicebank install"). Docker reaches it through nginx at /mcp/singers/.

let installBasePromise = null;

/// Base URL whose singers/banks answers, or null (no MCP server or no
/// headless host: the editor then installs in the browser as before).
async function installBase() {
    const base = await (installBasePromise ??= (async () => {
        for (const b of [new URL('mcp/', document.baseURI).href, 'http://127.0.0.1:5178/']) {
            try {
                const r = await fetch(new URL('singers/banks', b), { cache: 'no-store' });
                if (r.ok && (r.headers.get('content-type') || '').includes('json')) return b;
            } catch { }
        }
        return null;
    })());
    if (base == null) installBasePromise = null;
    return base;
}

export const voicebankServer = async () => await installBase();

export async function moveLocalSingersToServer() {
    const data = await singerServer();
    if (data) await migrateSingers(data);
}

/// [{dir, archive, bytes}] of every bank extracted on the server.
export async function listBanks() {
    const base = await installBase();
    if (!base) return [];
    const r = await http('GET', new URL('singers/banks', base).href);
    return (await r.json()).banks;
}

/// POST target for server-side rendering (mcp/server.mjs, "rendering for the
/// editor"), or null. The same server that installs voicebanks, so it has them.
export async function renderUrl() {
    const base = await installBase();
    return base ? new URL('render', base).href : null;
}

export async function bankUrl(dir) {
    const base = await installBase();
    return new URL(`singers/bank/${encodeURIComponent(dir)}`, base).href;
}

/// Uploads an archive for the server to install. XHR, not fetch, for upload
/// progress, reported to progress.invokeMethodAsync('OnInstallProgress', %).
/// Resolves to {dirs, banks}; rejects with the server's reason.
export async function installSinger(name, bytes, encoding, progress) {
    const base = await installBase();
    if (!base) throw new Error('no voicebank server');
    // Earlier banks kept in this browser go to the server too, so it has them all.
    await moveLocalSingersToServer();
    const target = new URL(`singers/install?name=${encodeURIComponent(name)}&encoding=${encodeURIComponent(encoding)}`, base);
    return await new Promise((resolve, reject) => {
        const xhr = new XMLHttpRequest();
        xhr.open('POST', target.href);
        xhr.setRequestHeader('Content-Type', 'application/octet-stream');
        let last = -1;
        xhr.upload.onprogress = e => {
            if (!e.lengthComputable) return;
            const pct = Math.floor(e.loaded * 100 / e.total);
            if (pct !== last) { last = pct; progress?.invokeMethodAsync('OnInstallProgress', pct).catch(() => { }); }
        };
        xhr.onload = () => {
            let body = null;
            try { body = JSON.parse(xhr.responseText); } catch { }
            if (xhr.status === 200 && body) resolve(body);
            else reject(new Error(body?.error ?? `install failed (${xhr.status})`));
        };
        xhr.onerror = () => reject(new Error('upload failed'));
        xhr.send(bytes);
    });
}
