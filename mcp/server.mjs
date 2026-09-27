#!/usr/bin/env node
// OpenWebtau MCP server. Speaks MCP (JSON-RPC) on stdio and as Streamable HTTP
// at POST /mcp, and relays each tool call to the open OpenWebtau editor tab,
// which long-polls http://127.0.0.1:5178 (or /mcp/poll through the Docker
// image's nginx). Node stdlib only.
//
//   claude mcp add openwebtau -- node /path/to/OpenWebtau/mcp/server.mjs
//   node mcp/server.mjs --http      # HTTP only, no stdio (the Docker image)
//
// Then open a project in OpenWebtau and switch "Agent" on in the toolbar.

import http from 'node:http';
import readline from 'node:readline';

const PORT = Number(process.env.OPENWEBTAU_MCP_PORT ?? 5178);
const CALL_TIMEOUT_MS = 120_000;   // wav export renders the whole song
const POLL_HOLD_MS = 25_000;
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
        name: 'get_project',
        description: 'Read the open project: tempo, resolution (ticks per quarter note, usually 480), time signature, and every track with its singer, phonemizer and notes (index, pos and dur in ticks, tone as MIDI number with C4=60, lyric).',
        inputSchema: { type: 'object', properties: { track: { type: 'integer', description: 'Only include notes for this track' } } },
    },
    {
        name: 'list_voices',
        description: 'Installed voicebanks (singers) and available phonemizers. Voicebanks are installed by the user in the editor.',
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
        description: 'Render and play the project in the browser, optionally from a tick.',
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
        description: 'Download a file in the user\'s browser: ustx (project), ust (one track, classic UTAU) or wav (rendered mixdown).',
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

    if (path === '/mcp') {
        // Streamable HTTP without sessions or server-sent streams: every POST is
        // one JSON-RPC message answered with one JSON body.
        if (req.method !== 'POST') { res.writeHead(405, { Allow: 'POST' }).end(); return; }
        let body = '';
        req.on('data', c => { body += c; });
        req.on('end', async () => {
            let msg;
            try { msg = JSON.parse(body); } catch {
                return reply(res, 400, { jsonrpc: '2.0', id: null, error: { code: -32700, message: 'Parse error' } });
            }
            const out = await handle(msg).catch(e => ({ id: msg.id, error: { code: -32603, message: String(e?.message ?? e) } }));
            if (!out) { res.writeHead(202).end(); return; }
            reply(res, 200, { jsonrpc: '2.0', ...out });
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
    if (Date.now() - lastSeen > SEEN_WINDOW_MS) {
        return Promise.resolve({
            ok: false,
            error: 'No OpenWebtau editor is connected. Ask the user to open a project in OpenWebtau and switch "Agent" on in the toolbar.',
        });
    }
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

// --- MCP messages ---------------------------------------------------------

/// The reply to one JSON-RPC message, or undefined for a notification.
async function handle(msg) {
    const { id, method, params } = msg;
    if (id === undefined) return;   // notifications need no reply
    switch (method) {
        case 'initialize':
            return {
                id, result: {
                    protocolVersion: params?.protocolVersion ?? '2025-06-18',
                    capabilities: { tools: {} },
                    serverInfo: { name: 'openwebtau', version: '0.1.0' },
                    instructions: 'Drives the OpenWebtau (UTAU/OpenUtau in the browser) editor the user has open. Call get_project first. Times are ticks (resolution per quarter note), tones are MIDI numbers with C4=60. Every edit is undoable.',
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
