#!/usr/bin/env node
// Password gate for the Docker image. nginx asks GET /auth/check (auth_request)
// before serving anything; this answers from a signed session cookie, or, for
// /<key>/mcp, from the MCP key. The first visitor sets the password. Node
// stdlib only; listens on 127.0.0.1:5179 behind nginx.
//
// Stored in OPENWEBTAU_AUTH_FILE (default /data/.auth/auth.json). Delete it and
// restart the container to reset the password and the MCP key.

import crypto from 'node:crypto';
import fs from 'node:fs';
import http from 'node:http';

const FILE = process.env.OPENWEBTAU_AUTH_FILE ?? '/data/.auth/auth.json';
const PORT = 5179;
const COOKIE = 'owt_session';
const SESSION_S = 30 * 24 * 3600;
const MIN_PASSWORD = 8;

let auth = null;   // { salt, hash, key, secret }
try { auth = JSON.parse(fs.readFileSync(FILE, 'utf8')); } catch { }

const b64 = buf => buf.toString('base64url');
const scrypt = (password, salt) => crypto.scryptSync(password, Buffer.from(salt, 'base64url'), 32);
const same = (a, b) => a.length === b.length && crypto.timingSafeEqual(a, b);
const sign = exp => b64(crypto.createHmac('sha256', auth.secret).update(String(exp)).digest());

function session() {
    const exp = Math.floor(Date.now() / 1000) + SESSION_S;
    return `${COOKIE}=${exp}.${sign(exp)}; Path=/; HttpOnly; SameSite=Lax; Max-Age=${SESSION_S}`;
}

function loggedIn(req) {
    if (!auth) return false;
    const m = (req.headers.cookie ?? '').match(new RegExp(`(?:^|; )${COOKIE}=(\\d+)\\.([\\w-]+)`));
    if (!m || Number(m[1]) < Date.now() / 1000) return false;
    return same(Buffer.from(m[2]), Buffer.from(sign(m[1])));
}

function keyOk(uri) {
    const m = uri.match(/^\/([\w-]+)\/mcp(?:\?|$)/);
    return !!(auth && m && same(Buffer.from(m[1]), Buffer.from(auth.key)));
}

function page(res, status, title, body, headers = {}) {
    res.writeHead(status, { 'Content-Type': 'text/html; charset=utf-8', 'Cache-Control': 'no-store', ...headers });
    res.end(`<!doctype html><html lang="ko"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>OpenWebtau · ${title}</title>
<style>body{font:15px system-ui,sans-serif;max-width:26rem;margin:15vh auto;padding:0 16px;color-scheme:light dark}
input,button{font:inherit;padding:.5rem;width:100%;box-sizing:border-box;margin:.25rem 0}
code{word-break:break-all;display:block;padding:.5rem;background:#8882}.err{color:#d33}</style>
<h1>OpenWebtau</h1>${body}</html>`);
}

const escape = s => s.replace(/[&<>"]/g, c => `&#${c.charCodeAt(0)};`);

function form(res, error = '', status = 200) {
    const err = error ? `<p class="err">${error}</p>` : '';
    if (!auth) {
        return page(res, status, '비밀번호 설정', `<p>처음 접속했습니다. 이 서버의 비밀번호를 정하세요.</p>${err}
<form method="post"><input type="password" name="password" placeholder="비밀번호 (${MIN_PASSWORD}자 이상)" autocomplete="new-password" required autofocus>
<input type="password" name="confirm" placeholder="비밀번호 확인" autocomplete="new-password" required><button>설정</button></form>`);
    }
    page(res, status, '로그인', `${err}<form method="post"><input type="password" name="password" placeholder="비밀번호" autocomplete="current-password" required autofocus><button>로그인</button></form>`);
}

function readForm(req) {
    return new Promise(resolve => {
        let body = '';
        req.on('data', c => { if (body.length < 4096) body += c; });
        req.on('end', () => resolve(new URLSearchParams(body)));
    });
}

const redirect = (res, to, headers = {}) => res.writeHead(303, { Location: to, ...headers }).end();

http.createServer((req, res) => {
    route(req, res).catch(e => {
        process.stderr.write(`openwebtau-auth: ${e.stack ?? e}\n`);
        if (!res.headersSent) res.writeHead(500);
        res.end();
    });
}).listen(PORT, '127.0.0.1');

async function route(req, res) {
    const path = req.url.split('?')[0];

    if (path === '/auth/check') {
        const uri = req.headers['x-original-uri'] ?? '';
        res.writeHead(loggedIn(req) || keyOk(uri) ? 204 : 401).end();
        return;
    }
    if (path === '/auth/login' && req.method === 'GET') return form(res);
    if (path === '/auth/login' && req.method === 'POST') {
        const f = await readForm(req);
        const password = f.get('password') ?? '';
        if (!auth) {
            if (password.length < MIN_PASSWORD) return form(res, `${MIN_PASSWORD}자 이상이어야 합니다.`, 400);
            if (password !== f.get('confirm')) return form(res, '두 비밀번호가 다릅니다.', 400);
            const salt = b64(crypto.randomBytes(16));
            const next = { salt, hash: b64(scrypt(password, salt)), key: b64(crypto.randomBytes(32)), secret: b64(crypto.randomBytes(32)) };
            // wx: if two first visitors race, only one sets the password.
            try { fs.writeFileSync(FILE, JSON.stringify(next), { mode: 0o600, flag: 'wx' }); } catch (e) {
                if (e.code !== 'EEXIST') throw e;
                auth = JSON.parse(fs.readFileSync(FILE, 'utf8'));
                return form(res, '이미 비밀번호가 설정되었습니다.', 409);
            }
            auth = next;
            return redirect(res, '/auth/', { 'Set-Cookie': session() });
        }
        if (!same(scrypt(password, auth.salt), Buffer.from(auth.hash, 'base64url'))) {
            return form(res, '비밀번호가 틀렸습니다.', 401);
        }
        return redirect(res, '/', { 'Set-Cookie': session() });
    }
    if (path === '/auth/logout' && req.method === 'POST') {
        return redirect(res, '/auth/login', { 'Set-Cookie': `${COOKIE}=; Path=/; HttpOnly; SameSite=Lax; Max-Age=0` });
    }
    if (path === '/auth/') {
        if (!loggedIn(req)) return redirect(res, '/auth/login');
        const url = `${req.headers['x-forwarded-proto'] ?? 'http'}://${req.headers.host}/${auth.key}/mcp`;
        return page(res, 200, 'MCP', `<p>에이전트는 이 주소로 연결합니다. 주소가 곧 비밀번호이니 공유하지 마세요.</p>
<code>${escape(url)}</code><p><code>claude mcp add --transport http openwebtau ${escape(url)}</code></p>
<p><a href="/">에디터로</a></p><form method="post" action="/auth/logout"><button>로그아웃</button></form>`);
    }
    res.writeHead(404).end();
}
