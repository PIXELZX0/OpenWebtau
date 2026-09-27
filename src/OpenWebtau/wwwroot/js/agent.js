// Bridge to mcp/server.mjs. The editor long-polls the MCP server over localhost
// HTTP, runs each command through .NET (Editor.AgentCall) and posts the result.
// Plain fetch keeps the server dependency-free: no WebSocket library needed.

const URL_BASE = 'http://127.0.0.1:5178';
const KEY = 'openwebtau.agent';
let run = null;

export function wasEnabled() {
    try { return localStorage.getItem(KEY) === '1'; } catch { return false; }
}

export function start(dotnet) {
    stop(false);
    try { localStorage.setItem(KEY, '1'); } catch { }
    const me = { dotnet, abort: new AbortController(), alive: true };
    run = me;
    loop(me);
}

/// forget = false keeps the switch on across reloads (page teardown).
export function stop(forget = true) {
    if (forget) { try { localStorage.removeItem(KEY); } catch { } }
    if (!run) return;
    run.alive = false;
    run.abort.abort();
    run = null;
}

async function loop(me) {
    let state = null;
    const report = s => {
        if (s === state || !me.alive) return;
        state = s;
        me.dotnet.invokeMethodAsync('OnAgentState', s).catch(() => { });
    };
    report('waiting');
    while (me.alive) {
        let cmd;
        try {
            const res = await fetch(URL_BASE + '/poll', { signal: me.abort.signal });
            if (!res.ok) throw new Error(res.status);
            report('on');
            const body = await res.text();
            if (!body) continue;                         // hold timed out, nothing queued
            cmd = JSON.parse(body);
        } catch {
            if (!me.alive) return;
            report('waiting');                           // server not running yet
            await new Promise(r => setTimeout(r, 2000));
            continue;
        }
        let reply;
        try {
            reply = JSON.parse(await me.dotnet.invokeMethodAsync('AgentCall', cmd.method, JSON.stringify(cmd.params ?? {})));
        } catch (e) {
            reply = { ok: false, error: String(e?.message ?? e) };
        }
        try {
            // text/plain keeps this a "simple" CORS request: no preflight.
            await fetch(URL_BASE + '/result', {
                method: 'POST',
                headers: { 'Content-Type': 'text/plain' },
                body: JSON.stringify({ id: cmd.id, ...reply }),
                signal: me.abort.signal,
            });
        } catch { }
    }
}
