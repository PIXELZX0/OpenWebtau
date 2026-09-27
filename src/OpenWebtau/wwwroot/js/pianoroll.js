// Piano roll view. Owns rendering, panning, zooming and hit testing; every edit
// is reported back to .NET, which applies it through OpenUtau's command stack so
// undo/redo keeps working.
//
// Three editable layers, matching what UTAU tuning actually needs:
//   notes      - draw, move, resize, retime
//   pitch      - portamento control points and per-note vibrato
//   expression - a curve or per-note bar lane along the bottom

const KEY_WIDTH = 64;
const RULER_HEIGHT = 24;
const EXP_HEIGHT = 130;
const MIN_TONE = 24;
const MAX_TONE = 107;
const BLACK_KEYS = new Set([1, 3, 6, 8, 10]);
const NOTE_NAMES = ['C', 'C#', 'D', 'D#', 'E', 'F', 'F#', 'G', 'G#', 'A', 'A#', 'B'];

// Drag must exceed this before it counts as a move rather than a click.
const DRAG_SLOP = 3;
// Grab zone on a note's right edge for resizing.
const RESIZE_HANDLE = 6;
// Grab radius for pitch control points and vibrato handles.
const POINT_RADIUS = 5;
const GRAB_RADIUS = 8;
// Pitch curves are sampled this many pixels apart when drawing.
const CURVE_STEP_PX = 3;

let view = null;

// Mirrors OpenUtau's MusicMath so the drawn curve matches the rendered pitch.
function interpolate(x0, x1, y0, y1, x, shape) {
    if (x1 - x0 < 1e-6) return y1;
    const t = (x - x0) / (x1 - x0);
    switch (shape) {
        case 'i': return y0 + (y1 - y0) * (1 - Math.cos(t * Math.PI / 2));
        case 'o': return y0 + (y1 - y0) * Math.sin(t * Math.PI / 2);
        case 'l': return y0 + (y1 - y0) * t;
        default: return y0 + (y1 - y0) * (1 - Math.cos(t * Math.PI)) / 2;
    }
}

// Mirrors UVibrato.Evaluate. nPos is 0..1 across the note; returns semitones.
function vibratoOffset(vib, nPos, noteDurMs) {
    if (!vib || vib.len <= 0 || noteDurMs <= 0) return 0;
    const nStart = 1 - vib.len / 100;
    const nIn = (vib.len / 100) * (vib.fadeIn / 100);
    const nOut = (vib.len / 100) * (vib.fadeOut / 100);
    if (nPos < nStart) return 0;
    const nPeriod = vib.period / noteDurMs;
    const t = (nPos - nStart) / nPeriod + vib.shift / 100;
    let y = Math.sin(2 * Math.PI * t) * vib.depth + (vib.depth / 100) * vib.drift;
    if (nIn > 0 && nPos < nStart + nIn) {
        y *= (nPos - nStart) / nIn;
    } else if (nOut > 0 && nPos > 1 - nOut) {
        y *= (1 - nPos) / nOut;
    }
    return y / 100;
}

export class PianoRoll {
    constructor(canvas, dotnet) {
        this.canvas = canvas;
        this.dotnet = dotnet;
        this.ctx = canvas.getContext('2d');

        this.notes = [];
        this.ghosts = [];
        this.exp = null;
        this.resolution = 480;
        this.beatsPerBar = 4;
        this.beatUnit = 4;
        this.playheadTick = -1;
        this.rangeStart = 0;
        this.rangeEnd = 0;
        this.selected = new Set();
        this.mode = 'notes';

        this.tickWidth = 0.25;   // px per tick
        this.rowHeight = 16;     // px per semitone
        this.scrollTick = 0;
        this.scrollTone = 72;    // topmost visible tone
        this.snap = 480 / 4;     // 16th notes

        this.drag = null;

        // Keep a handle so dispose() can disconnect it; otherwise a resize of the
        // detached canvas fires into a dead view.
        this._ro = new ResizeObserver(() => {
            if (!this.disposed) this.resize();
        });
        this._ro.observe(canvas);

        this._bind();
        this.resize();
    }

    // --- geometry ---------------------------------------------------------

    tickToX(tick) { return KEY_WIDTH + (tick - this.scrollTick) * this.tickWidth; }
    xToTick(x) { return (x - KEY_WIDTH) / this.tickWidth + this.scrollTick; }
    toneToY(tone) { return RULER_HEIGHT + (this.scrollTone - tone) * this.rowHeight; }
    // Inverse of toneToY: a row spans [toneToY(tone), toneToY(tone) + rowHeight).
    yToTone(y) { return Math.ceil(this.scrollTone - (y - RULER_HEIGHT) / this.rowHeight); }
    // Fractional tone, for dragging pitch points smoothly. A tone's pitch line runs
    // through the vertical centre of its row, half a row below toneToY.
    yToToneF(y) { return this.scrollTone - (y - RULER_HEIGHT) / this.rowHeight + 0.5; }
    toneToYF(tone) { return RULER_HEIGHT + (this.scrollTone - tone + 0.5) * this.rowHeight; }

    snapTick(tick) { return Math.round(tick / this.snap) * this.snap; }
    floorTick(tick) { return Math.floor(tick / this.snap) * this.snap; }

    get visibleTicks() { return (this.canvas.clientWidth - KEY_WIDTH) / this.tickWidth; }
    get expTop() { return this.canvas.clientHeight - EXP_HEIGHT; }
    get notesBottom() { return this.expTop; }

    expValueToY(v) {
        const { min, max } = this.exp;
        const t = (v - min) / (max - min || 1);
        return this.canvas.clientHeight - 6 - t * (EXP_HEIGHT - 18);
    }

    yToExpValue(y) {
        const { min, max } = this.exp;
        const t = (this.canvas.clientHeight - 6 - y) / (EXP_HEIGHT - 18);
        return Math.max(min, Math.min(max, min + t * (max - min)));
    }

    // --- state from .NET --------------------------------------------------

    setState(json) {
        const s = JSON.parse(json);
        // Diagnostic breadcrumb for automated tests.
        window.__lastPush = { notes: s.notes.length, playheadTick: s.playheadTick };
        this.notes = s.notes;
        this.ghosts = s.ghosts ?? [];
        this.exp = s.exp;
        this.resolution = s.resolution;
        this.beatsPerBar = s.beatsPerBar;
        this.beatUnit = s.beatUnit;
        this.playheadTick = s.playheadTick;
        this.rangeStart = s.rangeStart ?? 0;
        this.rangeEnd = s.rangeEnd ?? 0;
        const live = new Set(this.notes.map(n => n.id));
        let pruned = false;
        for (const id of [...this.selected]) {
            if (!live.has(id)) { this.selected.delete(id); pruned = true; }
        }
        // Undo can delete selected notes from the .NET side; tell it so the
        // properties panel closes instead of showing stale ids.
        if (pruned) this.publishSelection();
        this.render();
    }

    setPlayhead(tick) {
        this.playheadTick = tick;
        this.render();
    }

    /// Tells .NET what is selected so the properties panel and clipboard follow along.
    publishSelection() {
        if (this.disposed) return;
        this.dotnet.invokeMethodAsync('OnSelectionChanged', [...this.selected]).catch(() => { });
    }

    setSelection(ids) {
        this.selected = new Set(ids);
        this.render();
    }

    setMode(mode) {
        this.mode = mode;
        this.drag = null;
        this.render();
    }

    resize() {
        const dpr = window.devicePixelRatio || 1;
        const w = this.canvas.clientWidth;
        const h = this.canvas.clientHeight;
        this.canvas.width = Math.round(w * dpr);
        this.canvas.height = Math.round(h * dpr);
        this.ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
        this.render();
    }

    // --- rendering --------------------------------------------------------

    render() {
        const ctx = this.ctx;
        const w = this.canvas.clientWidth;
        const h = this.canvas.clientHeight;

        ctx.fillStyle = '#1b1b1f';
        ctx.fillRect(0, 0, w, h);

        ctx.save();
        ctx.beginPath();
        ctx.rect(0, RULER_HEIGHT, w, this.notesBottom - RULER_HEIGHT);
        ctx.clip();
        this._drawRows(w);
        this._drawGrid(w, this.notesBottom);
        this._drawNotes();
        this._drawPitch();
        ctx.restore();

        this._drawExpLane(w);
        this._drawKeyboard();
        this._drawRuler(w);
        this._drawRange(h);
        this._drawPlayhead(h);
        this._drawMarquee();
    }

    _drawRows(w) {
        const ctx = this.ctx;
        const rows = Math.ceil((this.notesBottom - RULER_HEIGHT) / this.rowHeight) + 1;
        for (let i = 0; i < rows; i++) {
            const tone = this.scrollTone - i;
            if (tone < MIN_TONE || tone > MAX_TONE) continue;
            const y = this.toneToY(tone);
            ctx.fillStyle = BLACK_KEYS.has(tone % 12) ? '#212127' : '#26262d';
            ctx.fillRect(KEY_WIDTH, y, w - KEY_WIDTH, this.rowHeight);
            if (tone % 12 === 0) {
                ctx.fillStyle = '#33333d';
                ctx.fillRect(KEY_WIDTH, y + this.rowHeight - 1, w - KEY_WIDTH, 1);
            }
        }
    }

    _drawGrid(w, bottom) {
        const ctx = this.ctx;
        const ticksPerBeat = this.resolution * 4 / this.beatUnit;
        const ticksPerBar = ticksPerBeat * this.beatsPerBar;
        // Don't draw beat lines once they'd be denser than ~8px apart.
        const drawBeats = ticksPerBeat * this.tickWidth > 8;
        const drawSnap = this.snap * this.tickWidth > 8;

        const start = Math.floor(this.scrollTick / ticksPerBar) * ticksPerBar;
        const end = this.scrollTick + this.visibleTicks;

        if (drawSnap) {
            ctx.fillStyle = '#2e2e36';
            for (let t = Math.floor(start / this.snap) * this.snap; t < end; t += this.snap) {
                if (t % ticksPerBeat === 0) continue;
                ctx.fillRect(Math.round(this.tickToX(t)), RULER_HEIGHT, 1, bottom);
            }
        }
        if (drawBeats) {
            ctx.fillStyle = '#3a3a45';
            for (let t = start; t < end; t += ticksPerBeat) {
                if (t % ticksPerBar === 0) continue;
                ctx.fillRect(Math.round(this.tickToX(t)), RULER_HEIGHT, 1, bottom);
            }
        }
        ctx.fillStyle = '#54545f';
        for (let t = start; t < end; t += ticksPerBar) {
            ctx.fillRect(Math.round(this.tickToX(t)), RULER_HEIGHT, 1, bottom);
        }
    }

    _drawNotes() {
        const ctx = this.ctx;
        ctx.font = '11px system-ui, sans-serif';
        ctx.textBaseline = 'middle';

        // Other tracks, so the arrangement stays visible while editing one of them.
        ctx.fillStyle = '#3a3a45';
        for (const g of this.ghosts) {
            const x = this.tickToX(g.pos);
            const wpx = Math.max(2, g.dur * this.tickWidth);
            if (x + wpx < KEY_WIDTH || x > this.canvas.clientWidth) continue;
            ctx.fillRect(x, this.toneToY(g.tone) + 1, wpx, this.rowHeight - 2);
        }

        const dim = this.mode !== 'notes';
        for (const n of this.notes) {
            const x = this.tickToX(n.pos);
            const y = this.toneToY(n.tone);
            const wpx = Math.max(2, n.dur * this.tickWidth);
            if (x + wpx < KEY_WIDTH || x > this.canvas.clientWidth) continue;

            const isSel = this.selected.has(n.id);
            ctx.globalAlpha = dim ? 0.45 : 1;
            ctx.fillStyle = isSel ? '#7fd1ff' : '#4a9eff';
            ctx.fillRect(x, y + 1, wpx, this.rowHeight - 2);
            if (isSel) {
                ctx.strokeStyle = '#ffffff';
                ctx.lineWidth = 1;
                ctx.strokeRect(x + 0.5, y + 1.5, wpx - 1, this.rowHeight - 3);
            }
            if (wpx > 18 && this.rowHeight >= 12) {
                ctx.save();
                ctx.beginPath();
                ctx.rect(x, y, wpx - 3, this.rowHeight);
                ctx.clip();
                ctx.fillStyle = '#0b0b10';
                ctx.fillText(n.lyric, x + 3, y + this.rowHeight / 2);
                ctx.restore();
            }
            ctx.globalAlpha = 1;
        }
    }

    // --- pitch layer ------------------------------------------------------

    /// Samples one note's pitch curve, in (tick, tone) pairs.
    pitchCurve(n) {
        const pts = n.pitch;
        if (!pts || pts.length < 2) return [];
        const out = [];
        const stepTicks = Math.max(1, CURVE_STEP_PX / this.tickWidth);
        for (let i = 0; i < pts.length - 1; i++) {
            const a = pts[i];
            const b = pts[i + 1];
            out.push([a.x, n.tone + a.y]);
            for (let t = a.x + stepTicks; t < b.x; t += stepTicks) {
                out.push([t, n.tone + interpolate(a.x, b.x, a.y, b.y, t, a.shape)]);
            }
        }
        const last = pts[pts.length - 1];
        out.push([last.x, n.tone + last.y]);

        if (n.vib && n.vib.len > 0 && n.durMs > 0) {
            for (const p of out) {
                const nPos = (p[0] - n.pos) / n.dur;
                if (nPos >= 0 && nPos <= 1) {
                    p[1] += vibratoOffset(n.vib, nPos, n.durMs);
                }
            }
            // The curve between control points is too coarse to show the wave;
            // sample the vibrato span densely so the oscillation is visible.
            const startTick = n.pos + n.dur * (1 - n.vib.len / 100);
            for (let t = startTick; t <= n.pos + n.dur; t += stepTicks) {
                const nPos = (t - n.pos) / n.dur;
                out.push([t, n.tone + this._basePitchAt(n, t) + vibratoOffset(n.vib, nPos, n.durMs)]);
            }
            out.sort((p, q) => p[0] - q[0]);
        }
        return out;
    }

    _basePitchAt(n, tick) {
        const pts = n.pitch;
        if (!pts || pts.length === 0) return 0;
        if (tick <= pts[0].x) return pts[0].y;
        for (let i = 0; i < pts.length - 1; i++) {
            if (tick <= pts[i + 1].x) {
                return interpolate(pts[i].x, pts[i + 1].x, pts[i].y, pts[i + 1].y, tick, pts[i].shape);
            }
        }
        return pts[pts.length - 1].y;
    }

    _drawPitch() {
        const ctx = this.ctx;
        const editing = this.mode === 'pitch';
        for (const n of this.notes) {
            const curve = this.pitchCurve(n);
            if (curve.length < 2) continue;
            const right = this.tickToX(curve[curve.length - 1][0]);
            const left = this.tickToX(curve[0][0]);
            if (right < KEY_WIDTH || left > this.canvas.clientWidth) continue;

            ctx.beginPath();
            for (let i = 0; i < curve.length; i++) {
                const px = this.tickToX(curve[i][0]);
                const py = this.toneToYF(curve[i][1]);
                if (i === 0) ctx.moveTo(px, py); else ctx.lineTo(px, py);
            }
            ctx.strokeStyle = editing ? '#ffd166' : 'rgba(255, 209, 102, 0.55)';
            ctx.lineWidth = editing ? 2 : 1.5;
            ctx.stroke();

            if (!editing) continue;

            for (let i = 0; i < n.pitch.length; i++) {
                const p = n.pitch[i];
                const px = this.tickToX(p.x);
                const py = this.toneToYF(n.tone + p.y);
                ctx.beginPath();
                ctx.arc(px, py, POINT_RADIUS, 0, Math.PI * 2);
                ctx.fillStyle = '#1b1b1f';
                ctx.fill();
                ctx.strokeStyle = '#ffd166';
                ctx.lineWidth = 2;
                ctx.stroke();
            }
            this._drawVibratoHandles(n);
        }
    }

    vibratoHandles(n) {
        const len = n.vib ? n.vib.len : 0;
        const startTick = n.pos + n.dur * (1 - len / 100);
        const top = this.toneToY(n.tone);
        return {
            // Pull left to grow the vibrato span.
            length: { x: this.tickToX(startTick), y: top - 6 },
            // Up/down for depth, shift-drag sideways for period.
            depth: { x: this.tickToX(startTick + (n.pos + n.dur - startTick) / 2), y: top + this.rowHeight + 8 },
        };
    }

    _drawVibratoHandles(n) {
        const ctx = this.ctx;
        const h = this.vibratoHandles(n);
        const on = n.vib && n.vib.len > 0;
        for (const [kind, p] of Object.entries(h)) {
            if (kind === 'depth' && !on) continue;
            ctx.beginPath();
            ctx.rect(p.x - 4, p.y - 4, 8, 8);
            ctx.fillStyle = on ? '#ff8fab' : '#55555f';
            ctx.fill();
        }
    }

    // --- expression lane --------------------------------------------------

    _drawExpLane(w) {
        const ctx = this.ctx;
        const top = this.expTop;
        ctx.fillStyle = '#17171c';
        ctx.fillRect(0, top, w, EXP_HEIGHT);
        ctx.fillStyle = '#2c2c34';
        ctx.fillRect(0, top, w, 1);
        if (!this.exp) return;

        ctx.save();
        ctx.beginPath();
        ctx.rect(KEY_WIDTH, top, w - KEY_WIDTH, EXP_HEIGHT);
        ctx.clip();
        this._drawGrid(w, this.canvas.clientHeight);

        // Zero / default reference line.
        const refY = this.expValueToY(this.exp.defaultValue);
        ctx.fillStyle = '#3a3a45';
        ctx.fillRect(KEY_WIDTH, Math.round(refY), w - KEY_WIDTH, 1);

        if (this.exp.type === 'curve') {
            this._drawExpCurve(w);
        } else {
            this._drawExpBars();
        }
        ctx.restore();

        ctx.fillStyle = '#17171c';
        ctx.fillRect(0, top + 1, KEY_WIDTH, EXP_HEIGHT);
        ctx.fillStyle = '#8a8a96';
        ctx.font = '10px system-ui, sans-serif';
        ctx.textBaseline = 'top';
        ctx.fillText(this.exp.abbr, 6, top + 6);
    }

    _drawExpCurve(w) {
        const ctx = this.ctx;
        const { xs, ys } = this.exp;
        if (!xs || xs.length === 0) return;
        ctx.beginPath();
        let started = false;
        for (let i = 0; i < xs.length; i++) {
            const px = this.tickToX(xs[i]);
            if (px < KEY_WIDTH - 50) continue;
            if (px > w + 50) break;
            const py = this.expValueToY(ys[i]);
            if (!started) { ctx.moveTo(px, py); started = true; } else { ctx.lineTo(px, py); }
        }
        ctx.strokeStyle = '#7ee787';
        ctx.lineWidth = 1.5;
        ctx.stroke();
    }

    _drawExpBars() {
        const ctx = this.ctx;
        const base = this.expValueToY(this.exp.min);
        for (const n of this.notes) {
            const v = this.exp.values[n.id];
            if (v == null) continue;
            const x = this.tickToX(n.pos);
            const wpx = Math.max(2, n.dur * this.tickWidth);
            const y = this.expValueToY(v);
            ctx.fillStyle = this.selected.has(n.id) ? '#7ee787' : 'rgba(126, 231, 135, 0.6)';
            ctx.fillRect(x, y, wpx, base - y);
            ctx.fillStyle = '#b9f2bf';
            ctx.fillRect(x, y - 1, wpx, 2);
        }
    }

    // --- chrome -----------------------------------------------------------

    _drawKeyboard() {
        const ctx = this.ctx;
        const bottom = this.notesBottom;
        ctx.fillStyle = '#141418';
        ctx.fillRect(0, RULER_HEIGHT, KEY_WIDTH, bottom - RULER_HEIGHT);
        ctx.save();
        ctx.beginPath();
        ctx.rect(0, RULER_HEIGHT, KEY_WIDTH, bottom - RULER_HEIGHT);
        ctx.clip();
        ctx.font = '10px system-ui, sans-serif';
        ctx.textBaseline = 'middle';

        const rows = Math.ceil((bottom - RULER_HEIGHT) / this.rowHeight) + 1;
        for (let i = 0; i < rows; i++) {
            const tone = this.scrollTone - i;
            if (tone < MIN_TONE || tone > MAX_TONE) continue;
            const y = this.toneToY(tone);
            const black = BLACK_KEYS.has(tone % 12);
            ctx.fillStyle = black ? '#1d1d22' : '#e8e8ec';
            ctx.fillRect(0, y, KEY_WIDTH - 1, this.rowHeight - 1);
            if (tone % 12 === 0 && this.rowHeight >= 11) {
                ctx.fillStyle = '#55555f';
                ctx.fillText(`${NOTE_NAMES[0]}${Math.floor(tone / 12) - 1}`, 4, y + this.rowHeight / 2);
            }
        }
        ctx.restore();
    }

    _drawRuler(w) {
        const ctx = this.ctx;
        ctx.fillStyle = '#141418';
        ctx.fillRect(0, 0, w, RULER_HEIGHT);
        ctx.fillStyle = '#8a8a96';
        ctx.font = '10px system-ui, sans-serif';
        ctx.textBaseline = 'middle';

        const ticksPerBar = this.resolution * 4 / this.beatUnit * this.beatsPerBar;
        const start = Math.floor(this.scrollTick / ticksPerBar) * ticksPerBar;
        const end = this.scrollTick + this.visibleTicks;
        // Thin out bar numbers when bars get narrow.
        const step = ticksPerBar * this.tickWidth < 40
            ? ticksPerBar * Math.ceil(40 / (ticksPerBar * this.tickWidth))
            : ticksPerBar;
        for (let t = start; t < end; t += step) {
            const x = this.tickToX(t);
            if (x < KEY_WIDTH) continue;
            ctx.fillRect(Math.round(x), 0, 1, RULER_HEIGHT);
            ctx.fillText(String(Math.floor(t / ticksPerBar) + 1), x + 4, RULER_HEIGHT / 2);
        }
    }

    _drawRange(h) {
        if (this.rangeEnd <= this.rangeStart) return;
        const x0 = Math.max(KEY_WIDTH, this.tickToX(this.rangeStart));
        const x1 = Math.max(KEY_WIDTH, this.tickToX(this.rangeEnd));
        if (x1 <= x0) return;
        const ctx = this.ctx;
        ctx.fillStyle = 'rgba(122, 200, 255, 0.08)';
        ctx.fillRect(x0, RULER_HEIGHT, x1 - x0, h - RULER_HEIGHT);
        ctx.fillStyle = '#4a9eff';
        ctx.fillRect(x0, 0, x1 - x0, 3);
    }

    _drawMarquee() {
        const d = this.drag;
        if (!d || d.kind !== 'marquee') return;
        const ctx = this.ctx;
        const x = Math.min(d.x0, d.x1), y = Math.min(d.y0, d.y1);
        const w = Math.abs(d.x1 - d.x0), h = Math.abs(d.y1 - d.y0);
        ctx.fillStyle = 'rgba(122, 200, 255, 0.12)';
        ctx.fillRect(x, y, w, h);
        ctx.strokeStyle = '#7ac8ff';
        ctx.lineWidth = 1;
        ctx.strokeRect(x + 0.5, y + 0.5, w, h);
    }

    _drawPlayhead(h) {
        if (this.playheadTick < 0) return;
        const x = this.tickToX(this.playheadTick);
        if (x < KEY_WIDTH) return;
        this.ctx.fillStyle = '#ff5c5c';
        this.ctx.fillRect(Math.round(x), 0, 1.5, h);
    }

    // --- hit testing ------------------------------------------------------

    noteAt(x, y) {
        const tick = this.xToTick(x);
        const tone = this.yToTone(y);
        // Reverse so the topmost drawn note wins on overlap.
        for (let i = this.notes.length - 1; i >= 0; i--) {
            const n = this.notes[i];
            if (n.tone === tone && tick >= n.pos && tick < n.pos + n.dur) return n;
        }
        return null;
    }

    pitchPointAt(x, y) {
        for (const n of this.notes) {
            if (!n.pitch) continue;
            for (let i = 0; i < n.pitch.length; i++) {
                const p = n.pitch[i];
                const dx = this.tickToX(p.x) - x;
                const dy = this.toneToYF(n.tone + p.y) - y;
                if (dx * dx + dy * dy <= GRAB_RADIUS * GRAB_RADIUS) {
                    return { note: n, index: i, point: p };
                }
            }
        }
        return null;
    }

    vibratoHandleAt(x, y) {
        for (const n of this.notes) {
            const h = this.vibratoHandles(n);
            const on = n.vib && n.vib.len > 0;
            for (const [kind, p] of Object.entries(h)) {
                if (kind === 'depth' && !on) continue;
                if (Math.abs(p.x - x) <= GRAB_RADIUS && Math.abs(p.y - y) <= GRAB_RADIUS) {
                    return { note: n, kind };
                }
            }
        }
        return null;
    }

    /// Nearest point on any note's pitch curve, for inserting a new control point.
    pitchCurveAt(x, y) {
        const tick = this.xToTick(x);
        for (const n of this.notes) {
            if (!n.pitch || n.pitch.length < 2) continue;
            if (tick < n.pitch[0].x || tick > n.pitch[n.pitch.length - 1].x) continue;
            const tone = n.tone + this._basePitchAt(n, tick);
            if (Math.abs(this.toneToYF(tone) - y) <= GRAB_RADIUS) {
                let index = n.pitch.findIndex(p => p.x > tick);
                if (index < 0) index = n.pitch.length - 1;
                return { note: n, index, tick, tone };
            }
        }
        return null;
    }

    // --- input ------------------------------------------------------------

    _bind() {
        const c = this.canvas;
        // Keep bound handlers so dispose() can remove the window-level ones;
        // a stale keydown would call into a disposed DotNetObjectReference.
        this._handlers = {
            down: e => this._onDown(e),
            move: e => this._onMove(e),
            up: e => this._onUp(e),
            wheel: e => this._onWheel(e),
            ctx: e => e.preventDefault(),
            dbl: e => this._onDblClick(e),
            key: e => this._onKey(e),
        };
        c.addEventListener('mousedown', this._handlers.down);
        window.addEventListener('mousemove', this._handlers.move);
        window.addEventListener('mouseup', this._handlers.up);
        c.addEventListener('wheel', this._handlers.wheel, { passive: false });
        c.addEventListener('contextmenu', this._handlers.ctx);
        c.addEventListener('dblclick', this._handlers.dbl);
        window.addEventListener('keydown', this._handlers.key);
    }

    dispose() {
        this.disposed = true;
        this._ro?.disconnect();
        const c = this.canvas;
        const h = this._handlers;
        if (!h) return;
        c.removeEventListener('mousedown', h.down);
        window.removeEventListener('mousemove', h.move);
        window.removeEventListener('mouseup', h.up);
        c.removeEventListener('wheel', h.wheel);
        c.removeEventListener('contextmenu', h.ctx);
        c.removeEventListener('dblclick', h.dbl);
        window.removeEventListener('keydown', h.key);
    }

    /// UTAU's editing is keyboard-heavy; these are the bindings people expect.
    _onKey(e) {
        if (this.disposed) return;
        // Never steal keys from a text field or the toolbar's inputs.
        const tag = (e.target?.tagName || '').toLowerCase();
        if (tag === 'input' || tag === 'textarea' || tag === 'select' || e.target?.isContentEditable) {
            return;
        }
        const mod = e.ctrlKey || e.metaKey;
        const send = (name, ...args) => {
            e.preventDefault();
            this.dotnet.invokeMethodAsync(name, ...args);
        };

        if (mod) {
            switch (e.key.toLowerCase()) {
                case 'a':
                    e.preventDefault();
                    this.selected = new Set(this.notes.map(n => n.id));
                    this.publishSelection();
                    this.render();
                    return;
                case 'c': return send('OnCopy');
                case 'x': return send('OnCut');
                case 'v': return send('OnPaste');
                case 'z': return send(e.shiftKey ? 'OnRedo' : 'OnUndo');
                case 'y': return send('OnRedo');
            }
            return;
        }

        switch (e.key) {
            case 'Escape':
                e.preventDefault();
                this.selected.clear();
                this.publishSelection();
                this.render();
                return;
            case 'Delete':
            case 'Backspace':
                return send('OnDeleteSelection');
            case ' ':
                return send('OnPlayPause');
            case 'ArrowLeft':
                return e.altKey ? send('OnResizeSelection', -this.snap) : send('OnNudge', -this.snap, 0);
            case 'ArrowRight':
                return e.altKey ? send('OnResizeSelection', this.snap) : send('OnNudge', this.snap, 0);
            case 'ArrowUp':
                return send('OnNudge', 0, e.shiftKey ? 12 : 1);
            case 'ArrowDown':
                return send('OnNudge', 0, e.shiftKey ? -12 : -1);
        }
    }

    _pos(e) {
        const r = this.canvas.getBoundingClientRect();
        return { x: e.clientX - r.left, y: e.clientY - r.top };
    }

    _onDown(e) {
        const { x, y } = this._pos(e);

        if (y < RULER_HEIGHT) {
            const tick = Math.max(0, Math.round(this.xToTick(x)));
            if (e.shiftKey) {
                this.drag = { kind: 'range', anchor: tick };
                this.rangeStart = this.rangeEnd = tick;
                this.render();
            } else {
                this.dotnet.invokeMethodAsync('OnSeek', tick);
                this.drag = { kind: 'seek' };
            }
            return;
        }
        if (x < KEY_WIDTH) return;

        if (y >= this.expTop) {
            this._startExpDrag(x, y, e);
            return;
        }
        if (this.mode === 'pitch') {
            this._startPitchDrag(x, y, e);
            return;
        }
        this._startNoteDrag(x, y, e);
    }

    _startNoteDrag(x, y, e) {
        const note = this.noteAt(x, y);

        if (e.button === 0 && (e.ctrlKey || e.metaKey)) {
            // Rubber band. Drawing owns a plain drag, so selection needs a modifier.
            this.drag = { kind: 'marquee', x0: x, y0: y, x1: x, y1: y, add: e.shiftKey };
            if (!e.shiftKey) this.selected.clear();
            this.render();
            return;
        }

        if (e.button === 2) {
            if (note) this.dotnet.invokeMethodAsync('OnRemoveNote', note.id);
            return;
        }
        if (e.button !== 0) return;

        if (note == null) {
            // Empty space: drag out a new note.
            const pos = this.floorTick(this.xToTick(x));
            const tone = this.yToTone(y);
            this.drag = { kind: 'create', startX: x, pos, tone, dur: this.snap, moved: false };
            if (this.selected.size > 0) {
                this.selected.clear();
                this.publishSelection();
            }
            this.render();
            return;
        }

        if (e.shiftKey && this.selected.has(note.id)) {
            this.selected.delete(note.id);
            this.publishSelection();
            this.render();
            return;
        }
        if (!e.shiftKey && !this.selected.has(note.id)) {
            this.selected.clear();
        }
        this.selected.add(note.id);
        this.publishSelection();

        const rightEdge = this.tickToX(note.pos + note.dur);
        const kind = (rightEdge - x) <= RESIZE_HANDLE ? 'resize' : 'move';
        this.drag = {
            kind, note, startX: x, startY: y, moved: false,
            origin: this.notes.filter(n => this.selected.has(n.id))
                .map(n => ({ id: n.id, pos: n.pos, dur: n.dur, tone: n.tone })),
        };
        this.render();
    }

    _startPitchDrag(x, y, e) {
        const handle = this.vibratoHandleAt(x, y);
        if (handle) {
            const v = handle.note.vib || { len: 0, period: 175, depth: 25, fadeIn: 10, fadeOut: 10, shift: 0, drift: 0 };
            this.drag = {
                kind: 'vibrato', sub: handle.kind, note: handle.note,
                startX: x, startY: y, origin: { ...v }, moved: false,
            };
            return;
        }

        const hit = this.pitchPointAt(x, y);
        if (hit) {
            if (e.button === 2) {
                this.dotnet.invokeMethodAsync('OnDeletePitchPoint', hit.note.id, hit.index);
                return;
            }
            if (e.altKey) {
                this.dotnet.invokeMethodAsync('OnCyclePitchShape', hit.note.id, hit.index);
                return;
            }
            this.drag = {
                kind: 'pitch', note: hit.note, index: hit.index,
                startX: x, startY: y, originX: hit.point.x, originY: hit.point.y, moved: false,
            };
            return;
        }

        if (e.button === 0) {
            const onCurve = this.pitchCurveAt(x, y);
            if (onCurve) {
                this.dotnet.invokeMethodAsync('OnAddPitchPoint',
                    onCurve.note.id, Math.round(onCurve.tick), onCurve.tone - onCurve.note.tone);
            }
        }
    }

    _startExpDrag(x, y, e) {
        if (!this.exp) return;
        if (this.exp.type === 'curve') {
            const tick = Math.round(this.xToTick(x));
            const value = Math.round(this.yToExpValue(y));
            this.drag = { kind: 'expCurve', lastTick: tick, lastValue: value };
            this.dotnet.invokeMethodAsync('OnSetCurve', tick, value, tick, value);
        } else {
            this.drag = { kind: 'expNote' };
            this._paintExpNote(x, y);
        }
    }

    _paintExpNote(x, y) {
        const tick = this.xToTick(x);
        const hit = this.notes.find(n => tick >= n.pos && tick < n.pos + n.dur);
        if (!hit) return;
        const value = Math.round(this.yToExpValue(y));
        const ids = this.selected.has(hit.id) ? [...this.selected] : [hit.id];
        this.dotnet.invokeMethodAsync('OnSetNoteExpression', ids, value);
    }

    _onMove(e) {
        if (this.disposed) return;
        const { x, y } = this._pos(e);

        if (this.drag == null) {
            this._updateCursor(x, y);
            return;
        }

        const d = this.drag;
        if (d.kind === 'seek') {
            this.dotnet.invokeMethodAsync('OnSeek', Math.max(0, Math.round(this.xToTick(x))));
            return;
        }
        if (Math.abs(x - (d.startX ?? x)) > DRAG_SLOP || Math.abs(y - (d.startY ?? y)) > DRAG_SLOP) {
            d.moved = true;
        }

        switch (d.kind) {
            case 'range': {
                const tick = Math.max(0, Math.round(this.xToTick(x)));
                this.rangeStart = Math.min(d.anchor, tick);
                this.rangeEnd = Math.max(d.anchor, tick);
                this.render();
                return;
            }
            case 'marquee':
                d.x1 = x;
                d.y1 = y;
                this._applyMarquee(d);
                this.render();
                return;
            case 'create':
                d.dur = Math.max(this.snap, this.snapTick(this.xToTick(x) - d.pos));
                this._previewCreate(d);
                return;
            case 'pitch':
                this._dragPitch(d, x, y);
                return;
            case 'vibrato':
                this._dragVibrato(d, x, y, e);
                return;
            case 'expCurve': {
                const tick = Math.round(this.xToTick(x));
                const value = Math.round(this.yToExpValue(y));
                this.dotnet.invokeMethodAsync('OnSetCurve', tick, value, d.lastTick, d.lastValue);
                d.lastTick = tick;
                d.lastValue = value;
                return;
            }
            case 'expNote':
                this._paintExpNote(x, y);
                return;
        }

        const deltaTick = this.snapTick(this.xToTick(x) - this.xToTick(d.startX));
        if (d.kind === 'resize') {
            d.delta = deltaTick;
            for (const o of d.origin) {
                const n = this.notes.find(n => n.id === o.id);
                if (n) n.dur = Math.max(this.snap, o.dur + deltaTick);
            }
        } else {
            const deltaTone = this.yToTone(y) - this.yToTone(d.startY);
            d.deltaTick = deltaTick;
            d.deltaTone = deltaTone;
            for (const o of d.origin) {
                const n = this.notes.find(n => n.id === o.id);
                if (n) {
                    n.pos = Math.max(0, o.pos + deltaTick);
                    n.tone = Math.min(MAX_TONE, Math.max(MIN_TONE, o.tone + deltaTone));
                }
            }
        }
        this.render();
    }

    _dragPitch(d, x, y) {
        const tick = this.xToTick(x);
        const tone = this.yToToneF(y) - d.note.tone;
        const p = d.note.pitch[d.index];
        // Points keep their order; the outer two set where the portamento starts
        // and ends, which is exactly what dragging them sideways should change.
        const lo = d.index > 0 ? d.note.pitch[d.index - 1].x : -Infinity;
        const hi = d.index < d.note.pitch.length - 1 ? d.note.pitch[d.index + 1].x : Infinity;
        p.x = Math.max(lo, Math.min(hi, tick));
        p.y = tone;
        d.newX = p.x;
        d.newY = p.y;
        this.render();
    }

    _dragVibrato(d, x, y, e) {
        const n = d.note;
        const v = { ...d.origin };
        if (d.sub === 'length') {
            const tick = Math.max(n.pos, Math.min(n.pos + n.dur, this.xToTick(x)));
            v.len = Math.max(0, Math.min(100, (n.pos + n.dur - tick) / n.dur * 100));
        } else if (e.shiftKey) {
            v.period = Math.max(5, Math.min(500, d.origin.period + (x - d.startX)));
        } else {
            v.depth = Math.max(5, Math.min(200, d.origin.depth + (d.startY - y) * 2));
        }
        n.vib = v;
        d.value = v;
        this.render();
    }

    _updateCursor(x, y) {
        let cursor = 'default';
        if (y >= RULER_HEIGHT && y < this.expTop && x >= KEY_WIDTH) {
            if (this.mode === 'pitch') {
                if (this.vibratoHandleAt(x, y)) cursor = 'grab';
                else if (this.pitchPointAt(x, y)) cursor = 'grab';
                else if (this.pitchCurveAt(x, y)) cursor = 'copy';
            } else {
                const over = this.noteAt(x, y);
                if (over) {
                    cursor = (this.tickToX(over.pos + over.dur) - x) <= RESIZE_HANDLE ? 'ew-resize' : 'move';
                }
            }
        } else if (y >= this.expTop && x >= KEY_WIDTH) {
            cursor = 'crosshair';
        }
        this.canvas.style.cursor = cursor;
    }

    _applyMarquee(d) {
        const t0 = this.xToTick(Math.min(d.x0, d.x1));
        const t1 = this.xToTick(Math.max(d.x0, d.x1));
        const hi = this.yToTone(Math.min(d.y0, d.y1));
        const lo = this.yToTone(Math.max(d.y0, d.y1));
        if (!d.add) this.selected.clear();
        for (const n of this.notes) {
            // Any overlap counts, the way a marquee in a DAW behaves.
            if (n.pos < t1 && n.pos + n.dur > t0 && n.tone >= lo && n.tone <= hi) {
                this.selected.add(n.id);
            }
        }
    }

    _previewCreate(d) {
        this.render();
        const ctx = this.ctx;
        ctx.fillStyle = 'rgba(122, 200, 255, 0.6)';
        ctx.fillRect(this.tickToX(d.pos), this.toneToY(d.tone) + 1,
            Math.max(2, d.dur * this.tickWidth), this.rowHeight - 2);
    }

    _onUp(e) {
        if (this.disposed) return;
        const d = this.drag;
        this.drag = null;
        if (d == null) return;

        switch (d.kind) {
            case 'range':
                this.dotnet.invokeMethodAsync('OnSetRange', this.rangeStart, this.rangeEnd);
                return;
            case 'marquee':
                this.publishSelection();
                this.render();
                return;
            case 'create':
                this.dotnet.invokeMethodAsync('OnAddNote', d.pos, d.dur, d.tone);
                return;
            case 'pitch':
                if (d.moved) {
                    // Absolute target, not a delta: .NET converts ticks back to the
                    // milliseconds the model stores.
                    this.dotnet.invokeMethodAsync('OnMovePitchPoint',
                        d.note.id, d.index, d.newX, d.newY);
                }
                return;
            case 'vibrato':
                if (d.moved && d.value) {
                    this.dotnet.invokeMethodAsync('OnSetVibrato',
                        d.note.id, d.value.len, d.value.depth, d.value.period);
                }
                return;
            case 'resize':
                if (d.moved && d.delta) {
                    this.dotnet.invokeMethodAsync('OnResizeNotes', d.origin.map(o => o.id), d.delta);
                }
                return;
            case 'move':
                if (d.moved && (d.deltaTick || d.deltaTone)) {
                    this.dotnet.invokeMethodAsync('OnMoveNotes', d.origin.map(o => o.id),
                        d.deltaTick ?? 0, d.deltaTone ?? 0);
                } else {
                    this.render();
                }
                return;
        }
    }

    _onDblClick(e) {
        if (this.mode !== 'notes') return;
        const { x, y } = this._pos(e);
        if (y >= this.expTop) return;
        const note = this.noteAt(x, y);
        if (note) this.dotnet.invokeMethodAsync('OnEditLyric', note.id);
    }

    _onWheel(e) {
        e.preventDefault();
        if (e.ctrlKey || e.metaKey) {
            // Zoom horizontally around the cursor so the tick under it stays put.
            const { x } = this._pos(e);
            const anchor = this.xToTick(x);
            const factor = Math.exp(-e.deltaY * 0.002);
            this.tickWidth = Math.min(4, Math.max(0.01, this.tickWidth * factor));
            this.scrollTick = Math.max(0, anchor - (x - KEY_WIDTH) / this.tickWidth);
        } else if (e.shiftKey) {
            this.scrollTick = Math.max(0, this.scrollTick + e.deltaY / this.tickWidth);
        } else {
            this.scrollTone = Math.min(MAX_TONE,
                Math.max(MIN_TONE, this.scrollTone - Math.sign(e.deltaY) * 2));
            this.scrollTick = Math.max(0, this.scrollTick + e.deltaX / this.tickWidth);
        }
        this.render();
    }
}

export function init(canvas, dotnet) {
    view = new PianoRoll(canvas, dotnet);
    return true;
}

export function setState(json) { view?.setState(json); }
export function setPlayhead(tick) { view?.setPlayhead(tick); }
export function setSnap(ticks) { if (view) { view.snap = ticks; view.render(); } }
export function setMode(mode) { view?.setMode(mode); }
export function setSelection(ids) { view?.setSelection(ids); }

/// Pixel geometry of every visible note in the CURRENT transform, plus the
/// scroll state. Lets automated tests aim clicks without guessing the layout.
export function debugState() {
    if (!view) return null;
    return {
        scrollTick: view.scrollTick,
        scrollTone: view.scrollTone,
        tickWidth: view.tickWidth,
        keyWidth: KEY_WIDTH,
        rulerHeight: RULER_HEIGHT,
        expTop: view.expTop,
        rowHeight: view.rowHeight,
        notes: view.notes.map(n => ({
            id: n.id, pos: n.pos, dur: n.dur, tone: n.tone, lyric: n.lyric,
            x: view.tickToX(n.pos),
            w: Math.max(2, n.dur * view.tickWidth),
            y: view.toneToY(n.tone),
            h: view.rowHeight,
            vibLen: n.vib ? n.vib.len : 0,
        })),
        playheadTick: view.playheadTick,
    };
}
export function dispose() {
    // The Blazor page is going away: drop the window listeners so a stale
    // keydown cannot call into the disposed DotNetObjectReference.
    view?.dispose();
    view = null;
}
