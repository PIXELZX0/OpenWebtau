#!/usr/bin/env node
// OpenWebtau MCP server. Speaks MCP (JSON-RPC) on stdio and as Streamable HTTP
// at POST /mcp, and relays each tool call to the open OpenWebtau editor tab,
// which long-polls http://127.0.0.1:5178 (or /mcp/poll through the Docker
// image's nginx). With no tab connected, calls go to the headless host
// (src/OpenWebtau.Headless), started on first use, which edits the same stored
// projects without a browser. Node stdlib only.
//
//   claude mcp add openwebtau -- node /path/to/OpenWebtau/mcp/server.mjs
//   node mcp/server.mjs --http      # HTTP only, no stdio (the Docker image)
//
// Open a project in OpenWebtau and switch "Agent" on in the toolbar to watch
// the agent edit live; without that it works headless.

import fs from 'node:fs';
import http from 'node:http';
import path from 'node:path';
import readline from 'node:readline';
import { spawn } from 'node:child_process';
import { pipeline } from 'node:stream/promises';
import { fileURLToPath } from 'node:url';

const PORT = Number(process.env.OPENWEBTAU_MCP_PORT ?? 5178);
const CALL_TIMEOUT_MS = 120_000;   // wav export renders the whole song
const POLL_HOLD_MS = 25_000;
// Newest first. Nothing here depends on the differences between them.
const PROTOCOL_VERSIONS = ['2025-11-25', '2025-06-18', '2025-03-26', '2024-11-05'];
const SEEN_WINDOW_MS = 35_000;

// --- tools ----------------------------------------------------------------

const track = { type: 'integer', description: 'Track index (see get_project). Editing a track also shows it in the editor. Defaults to the visible track.' };
const pick = {
    indices: { type: 'array', items: { type: 'integer' }, description: 'Note indices within the track, as listed by get_project' },
    from: { type: 'integer', description: 'Select notes starting at or after this tick' },
    to: { type: 'integer', description: 'Select notes starting before this tick' },
};

const TOOLS = [
    {
        name: 'list_projects',
        description: 'Stored projects (id, name, track and note counts, last update), newest first.',
        inputSchema: { type: 'object', properties: {} },
    },
    {
        name: 'open_project',
        description: 'Make a stored project the open one; the other tools act on it. In the browser the editor reloads on it.',
        inputSchema: { type: 'object', properties: { id: { type: 'string', description: 'Project id from list_projects' } }, required: ['id'] },
    },
    {
        name: 'create_project',
        description: 'Create an empty project and open it.',
        inputSchema: { type: 'object', properties: { name: { type: 'string' } } },
    },
    {
        name: 'get_project',
        description: 'Read the open project: tempo, resolution (ticks per quarter note, usually 480), time signature, and every track with its singer, phonemizer and notes (index, pos and dur in ticks, tone as MIDI number with C4=60, lyric).',
        inputSchema: { type: 'object', properties: { track: { type: 'integer', description: 'Only include notes for this track' } } },
    },
    {
        name: 'list_voices',
        description: 'Installed voicebanks (singers) and available phonemizers. Voicebanks are installed by the user in the editor and kept on the server.',
        inputSchema: { type: 'object', properties: {} },
    },
    {
        name: 'add_track',
        description: 'Append a track and make it the visible one. Optionally set its singer, phonemizer and name.',
        inputSchema: {
            type: 'object', properties: {
                name: { type: 'string' },
                singer: { type: 'string', description: 'Singer id or name from list_voices' },
                phonemizer: { type: 'string', description: 'Phonemizer id or tag from list_voices, e.g. "KO CVVC"' },
            },
        },
    },
    {
        name: 'set_track',
        description: 'Change a track: singer, phonemizer, name, volume (dB, -24..12), pan (-100..100), mute, solo.',
        inputSchema: {
            type: 'object', properties: {
                track, name: { type: 'string' },
                singer: { type: 'string' }, phonemizer: { type: 'string' },
                volume: { type: 'number' }, pan: { type: 'number' },
                mute: { type: 'boolean' }, solo: { type: 'boolean' },
            },
        },
    },
    {
        name: 'remove_track',
        description: 'Delete a track and its notes. A project keeps at least one track.',
        inputSchema: { type: 'object', properties: { track }, required: ['track'] },
    },
    {
        name: 'add_notes',
        description: 'Add notes. pos and dur are in ticks (480 = one quarter note at the default resolution). tone is a MIDI number (60 = C4) or a name like "C4", "F#3". lyric is the sung syllable (e.g. "a", "ka", "あ", "가"); the track phonemizer maps it to the voicebank.',
        inputSchema: {
            type: 'object', properties: {
                track,
                notes: {
                    type: 'array', items: {
                        type: 'object', properties: {
                            pos: { type: 'integer' }, dur: { type: 'integer' },
                            tone: { type: ['integer', 'string'] }, lyric: { type: 'string' },
                        }, required: ['pos', 'dur', 'tone'],
                    },
                },
            }, required: ['notes'],
        },
    },
    {
        name: 'remove_notes',
        description: 'Remove notes by index or tick range. With no selector, removes every note in the track.',
        inputSchema: { type: 'object', properties: { track, ...pick } },
    },
    {
        name: 'set_lyrics',
        description: 'Set lyrics on consecutive notes in time order, starting at note index "start" (default 0).',
        inputSchema: {
            type: 'object', properties: {
                track, lyrics: { type: 'array', items: { type: 'string' } }, start: { type: 'integer' },
            }, required: ['lyrics'],
        },
    },
    {
        name: 'set_expression',
        description: 'Set a per-note expression on notes (selected like remove_notes; default all). UTAU note properties: vel (consonant velocity, 0-200), vol (intensity, 0-200), mod (modulation), gen (g flag), bre (B flag), lpf (H flag), atk, dec, alt, shft.',
        inputSchema: {
            type: 'object', properties: {
                track, ...pick, abbr: { type: 'string' }, value: { type: 'number' },
            }, required: ['abbr', 'value'],
        },
    },
    {
        name: 'set_tempo',
        description: 'Set the project tempo in BPM.',
        inputSchema: { type: 'object', properties: { bpm: { type: 'number' } }, required: ['bpm'] },
    },
    {
        name: 'play',
        description: 'Render and play the project in the browser, optionally from a tick. Needs the editor open; otherwise use export wav.',
        inputSchema: { type: 'object', properties: { from: { type: 'integer' } } },
    },
    { name: 'stop', description: 'Stop playback.', inputSchema: { type: 'object', properties: {} } },
    { name: 'undo', description: 'Undo the last edit.', inputSchema: { type: 'object', properties: {} } },
    { name: 'redo', description: 'Redo the last undone edit.', inputSchema: { type: 'object', properties: {} } },
    {
        name: 'get_ustx',
        description: 'The whole project as OpenUtau .ustx (YAML) text.',
        inputSchema: { type: 'object', properties: {} },
    },
    {
        name: 'export',
        description: 'Export ustx (project), ust (one track, classic UTAU) or wav (rendered mixdown). With the editor open the browser downloads it; without, it is written to exports/ in the data directory and the path is returned.',
        inputSchema: {
            type: 'object', properties: { format: { enum: ['ustx', 'ust', 'wav'] }, track },
            required: ['format'],
        },
    },
];

// --- bridge to the editor tab -------------------------------------------

const queue = [];                 // commands waiting for the editor
const pending = new Map();        // id -> { resolve, timer }
let waiter = null;                // held /poll response
let lastSeen = 0;
let nextId = 1;

function allowed(req, path) {
    // Only pages served from this machine may drive the bridge; a random website
    // could otherwise poll it and read what the agent sends. Behind the Docker
    // image's nginx (which sets X-Forwarded-For) the page is same-origin instead.
    const origin = req.headers.origin;
    if (!origin) return path === '/mcp' || req.headers['x-forwarded-for'] !== undefined;
    try {
        const { hostname, host } = new URL(origin);
        if (req.headers['x-forwarded-for'] !== undefined) return host === req.headers.host;
        return hostname === 'localhost' || hostname === '127.0.0.1' || hostname === '[::1]';
    } catch { return false; }
}

function flush() {
    if (!waiter || queue.length === 0) return;
    const { res, timer } = waiter;
    waiter = null;
    clearTimeout(timer);
    if (res.destroyed) return;
    res.end(JSON.stringify(queue.shift()));
}

// --- project and voicebank storage -----------------------------------------
// Outside Docker the app runs under `dotnet run`, which has no /data/, so
// projects and voicebank archives would only live in the tab's IndexedDB,
// out of reach of the headless host. This serves the same interface as the
// image's nginx WebDAV (GET a directory as a JSON listing, GET/PUT/DELETE a
// file) at /data/projects/ and /data/singers/, on disk in DATA_DIR. The Docker
// image (--http) leaves /data/ to nginx.

const HTTP_ONLY = process.argv.includes('--http');
const DATA_DIR = path.resolve(process.env.OPENWEBTAU_DATA_DIR ?? fileURLToPath(new URL('../data', import.meta.url)));
const SERVE_DATA = !HTTP_ONLY;
const DATA_DIRS = ['projects', 'singers'];

/// { dir, name } for /data/<dir>/[<name>] (name '' lists dir), or null.
function dataPath(p) {
    const m = /^\/data\/([^/]+)\/([^/]*)$/.exec(p);
    if (!m || !DATA_DIRS.includes(m[1])) return null;
    let name;
    try { name = decodeURIComponent(m[2]); } catch { return null; }
    // One flat directory: no separators, no dot files (.tmp uploads).
    if (name.startsWith('.') || /[\/\\\0]/.test(name)) return null;
    return { dir: path.join(DATA_DIR, m[1]), name };
}

async function serveData(req, res, { dir, name }) {
    await fs.promises.mkdir(dir, { recursive: true });
    const file = path.join(dir, name);
    if (name === '') {
        if (req.method !== 'GET') { res.writeHead(405, { Allow: 'GET' }).end(); return; }
        const entries = await fs.promises.readdir(dir, { withFileTypes: true });
        const list = entries.filter(e => !e.name.startsWith('.'))
            .map(e => ({ name: e.name, type: e.isDirectory() ? 'directory' : 'file' }));
        res.writeHead(200, { 'Content-Type': 'application/json', 'Cache-Control': 'no-store' })
            .end(JSON.stringify(list));
        return;
    }
    if (req.method === 'GET') {
        let stat;
        try { stat = await fs.promises.stat(file); } catch { res.writeHead(404).end(); return; }
        if (!stat.isFile()) { res.writeHead(404).end(); return; }
        res.writeHead(200, {
            'Content-Type': 'application/octet-stream', 'Content-Length': stat.size, 'Cache-Control': 'no-store',
        });
        await pipeline(fs.createReadStream(file), res);
        return;
    }
    if (req.method === 'PUT') {
        // Banks run to hundreds of MB: stream to a temp file, then rename, so a
        // cut-off upload never replaces a good archive.
        const tmp = path.join(dir, `.tmp-${process.pid}-${nextTmp++}`);
        try {
            await pipeline(req, fs.createWriteStream(tmp));
            await fs.promises.rename(tmp, file);
        } catch (e) {
            await fs.promises.rm(tmp, { force: true });
            throw e;
        }
        res.writeHead(201).end();
        return;
    }
    if (req.method === 'DELETE') {
        try { await fs.promises.unlink(file); } catch { res.writeHead(404).end(); return; }
        res.writeHead(204).end();
        return;
    }
    res.writeHead(405, { Allow: 'GET, PUT, DELETE' }).end();
}
let nextTmp = 1;

const server = http.createServer((req, res) => {
    // The bridge answers at /poll and /result, and at /mcp/poll and /mcp/result
    // for the Docker image, which proxies all of /mcp here.
    const path = req.url.split('?')[0].replace(/^\/mcp(?=\/)/, '');
    const origin = req.headers.origin;
    if (!allowed(req, path)) { res.writeHead(403).end(); return; }
    if (origin) {
        res.setHeader('Access-Control-Allow-Origin', origin);
        res.setHeader('Vary', 'Origin');
    }

    const target = SERVE_DATA && dataPath(path);
    if (target) {
        if (req.method === 'OPTIONS') {
            // PUT and DELETE from the app's own port are preflighted.
            res.writeHead(204, {
                'Access-Control-Allow-Methods': 'GET, PUT, DELETE',
                'Access-Control-Max-Age': '600',
            }).end();
            return;
        }
        serveData(req, res, target).catch(e => {
            process.stderr.write(`openwebtau-mcp: ${req.method} ${path}: ${e.message}\n`);
            if (!res.headersSent) res.writeHead(500);
            res.end();
        });
        return;
    }

    if (path === '/mcp') {
        // Streamable HTTP without sessions or server-sent streams: every POST is
        // one JSON-RPC message answered with one JSON body.
        if (req.method !== 'POST') { res.writeHead(405, { Allow: 'POST' }).end(); return; }
        const version = req.headers['mcp-protocol-version'];
        if (version !== undefined && !PROTOCOL_VERSIONS.includes(version)) {
            return reply(res, 400, { jsonrpc: '2.0', id: null, error: { code: -32600, message: `Unsupported MCP-Protocol-Version: ${version}` } });
        }
        let body = '';
        req.on('data', c => { body += c; });
        req.on('end', async () => {
            let msg;
            try { msg = JSON.parse(body); } catch {
                return reply(res, 400, { jsonrpc: '2.0', id: null, error: { code: -32700, message: 'Parse error' } });
            }
            const out = await handle(msg).catch(e => ({ id: msg.id, error: { code: -32603, message: String(e?.message ?? e) } }));
            if (!out) { res.writeHead(202).end(); return; }
            reply(res, out.error?.code === -32600 ? 400 : 200, { jsonrpc: '2.0', ...out });
        });
        return;
    }
    if (req.method === 'GET' && path === '/poll') {
        lastSeen = Date.now();
        // Headers go out now so the tab knows it is connected; the body is a
        // command, or empty when the hold times out.
        res.writeHead(200, { 'Content-Type': 'application/json' });
        res.flushHeaders();
        if (waiter) { clearTimeout(waiter.timer); if (!waiter.res.destroyed) waiter.res.end(); }
        const timer = setTimeout(() => {
            if (waiter?.res === res) waiter = null;
            if (!res.destroyed) res.end();
        }, POLL_HOLD_MS);
        waiter = { res, timer };
        res.on('close', () => { if (waiter?.res === res) { clearTimeout(timer); waiter = null; } });
        flush();
        return;
    }
    if (req.method === 'POST' && path === '/result') {
        lastSeen = Date.now();
        let body = '';
        req.on('data', c => { body += c; });
        req.on('end', () => {
            try {
                const msg = JSON.parse(body);
                const p = pending.get(msg.id);
                if (p) { pending.delete(msg.id); clearTimeout(p.timer); p.resolve(msg); }
            } catch { }
            res.writeHead(204).end();
        });
        return;
    }
    res.writeHead(404).end();
});

function reply(res, status, msg) {
    res.writeHead(status, { 'Content-Type': 'application/json' }).end(JSON.stringify(msg));
}

server.on('error', e => {
    process.stderr.write(`openwebtau-mcp: cannot listen on 127.0.0.1:${PORT}: ${e.message}\n`);
});
server.listen(PORT, '127.0.0.1');

function callEditor(method, params) {
    if (Date.now() - lastSeen > SEEN_WINDOW_MS) return headless.call(method, params);
    const id = nextId++;
    return new Promise(resolve => {
        const timer = setTimeout(() => {
            pending.delete(id);
            const i = queue.findIndex(c => c.id === id);
            if (i >= 0) queue.splice(i, 1);
            resolve({ ok: false, error: 'The editor did not answer in time.' });
        }, CALL_TIMEOUT_MS);
        pending.set(id, { resolve, timer });
        queue.push({ id, method, params });
        flush();
    });
}

// --- headless host --------------------------------------------------------
// src/OpenWebtau.Headless speaks one JSON object per line on stdio. It starts
// on the first call made with no editor connected and stays up; the editor
// and it share projects and voicebanks through DATA_DIR.

const REPO = fileURLToPath(new URL('..', import.meta.url));
const START_TIMEOUT_MS = 300_000;   // `dotnet run` builds on first use

/// [command, args] for the headless host: OPENWEBTAU_HEADLESS (a command line,
/// split on spaces), the Docker image's copy, a Release build, or `dotnet run`.
function headlessCommand() {
    const data = ['--data', DATA_DIR];
    if (process.env.OPENWEBTAU_HEADLESS) {
        const [cmd, ...args] = process.env.OPENWEBTAU_HEADLESS.split(' ').filter(Boolean);
        return [cmd, [...args, ...data]];
    }
    const image = '/opt/openwebtau/headless/OpenWebtau.Headless';
    if (fs.existsSync(image)) return [image, data];
    const dll = path.join(REPO, 'src/OpenWebtau.Headless/bin/Release/net8.0/OpenWebtau.Headless.dll');
    if (fs.existsSync(dll)) return ['dotnet', [dll, ...data]];
    return ['dotnet', ['run', '-c', 'Release', '--project', path.join(REPO, 'src/OpenWebtau.Headless'), '--', ...data]];
}

const headless = {
    child: null,
    ready: null,        // promise, resolved on {"ready":true}
    waiting: new Map(), // id -> resolve
    nextId: 1,

    start() {
        const [cmd, args] = headlessCommand();
        const child = spawn(cmd, args, { stdio: ['pipe', 'pipe', 'pipe'] });
        this.child = child;
        let readyResolve, readyReject;
        this.ready = new Promise((res, rej) => { readyResolve = res; readyReject = rej; });
        const startTimer = setTimeout(() => readyReject(new Error('the headless host did not start in time')), START_TIMEOUT_MS);
        let stderrTail = '';
        child.stderr.on('data', c => {
            stderrTail = (stderrTail + c).slice(-2000);
            process.stderr.write(c);
        });
        readline.createInterface({ input: child.stdout }).on('line', line => {
            let msg;
            try { msg = JSON.parse(line); } catch { return; }
            if (msg.ready) { clearTimeout(startTimer); readyResolve(); return; }
            const resolve = this.waiting.get(msg.id);
            if (resolve) { this.waiting.delete(msg.id); resolve(msg); }
        });
        const fail = why => {
            clearTimeout(startTimer);
            readyReject(new Error(why));
            if (this.child === child) this.child = null;
            for (const resolve of this.waiting.values()) resolve({ ok: false, error: why });
            this.waiting.clear();
        };
        child.stdin.on('error', () => { });   // EPIPE after an exit; 'exit' reports it
        child.on('error', e => fail(`cannot start the headless host (${cmd}): ${e.message}`));
        child.on('exit', code => fail(`the headless host exited (${code}). ${stderrTail.trim().split('\n').slice(-3).join(' ')}`));
        this.ready.catch(() => { });
    },

    async call(method, params) {
        if (!this.child) this.start();
        try {
            await this.ready;
        } catch (e) {
            return {
                ok: false,
                error: `No editor tab is connected, and ${e.message}. Build it with \`dotnet build -c Release src/OpenWebtau.Headless\`, or open a project in OpenWebtau and switch "Agent" on.`,
            };
        }
        const id = this.nextId++;
        return new Promise(resolve => {
            const timer = setTimeout(() => {
                this.waiting.delete(id);
                resolve({ ok: false, error: 'The headless host did not answer in time.' });
            }, CALL_TIMEOUT_MS);
            this.waiting.set(id, msg => { clearTimeout(timer); resolve(msg); });
            this.child.stdin.write(JSON.stringify({ id, method, params }) + '\n');
        });
    },
};

// --- MCP messages ---------------------------------------------------------

/// The reply to one JSON-RPC message, or undefined for a notification or a
/// response (this server sends no requests, so there is nothing to match).
async function handle(msg) {
    if (msg === null || typeof msg !== 'object' || Array.isArray(msg) || msg.jsonrpc !== '2.0') {
        return { id: null, error: { code: -32600, message: 'Invalid Request: expected one JSON-RPC 2.0 message' } };
    }
    const { id, method, params } = msg;
    if (id === undefined || method === undefined) return;
    switch (method) {
        case 'initialize':
            return {
                id, result: {
                    protocolVersion: PROTOCOL_VERSIONS.includes(params?.protocolVersion) ? params.protocolVersion : PROTOCOL_VERSIONS[0],
                    capabilities: { tools: {} },
                    serverInfo: { name: 'openwebtau', version: '0.1.0' },
                    instructions: 'Edits OpenWebtau (UTAU/OpenUtau in the browser) projects. With the editor open and Agent on, edits show up live in that tab; with no tab, a headless host edits the stored projects directly (no playback; export wav writes a file). Call get_project first, or list_projects/open_project to pick one. Times are ticks (resolution per quarter note), tones are MIDI numbers with C4=60. Every edit is undoable.',
                },
            };
        case 'ping':
            return { id, result: {} };
        case 'tools/list':
            return { id, result: { tools: TOOLS } };
        case 'tools/call': {
            const name = params?.name;
            if (!TOOLS.some(t => t.name === name)) {
                return { id, error: { code: -32602, message: `Unknown tool: ${name}` } };
            }
            const r = await callEditor(name, params.arguments ?? {});
            const text = r.ok
                ? (typeof r.result === 'string' ? r.result : JSON.stringify(r.result ?? 'ok'))
                : r.error;
            return { id, result: { content: [{ type: 'text', text }], isError: !r.ok } };
        }
        default:
            return { id, error: { code: -32601, message: `Method not found: ${method}` } };
    }
}

// --- MCP over stdio -------------------------------------------------------

function send(msg) { process.stdout.write(JSON.stringify({ jsonrpc: '2.0', ...msg }) + '\n'); }

if (!process.argv.includes('--http')) readline.createInterface({ input: process.stdin }).on('line', line => {
    if (!line.trim()) return;
    let msg;
    try { msg = JSON.parse(line); } catch { return send({ id: null, error: { code: -32700, message: 'Parse error' } }); }
    handle(msg).then(r => r && send(r), e => send({ id: msg.id, error: { code: -32603, message: String(e?.message ?? e) } }));
}).on('close', () => process.exit(0));
