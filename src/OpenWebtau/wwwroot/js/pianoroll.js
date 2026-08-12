// Piano roll view. Owns rendering, panning, zooming and hit testing; every edit
// is reported back to .NET, which applies it through OpenUtau's command stack so
// undo/redo keeps working.

const KEY_WIDTH = 64;
const RULER_HEIGHT = 24;
const MIN_TONE = 24;
const MAX_TONE = 107;
const BLACK_KEYS = new Set([1, 3, 6, 8, 10]);
const NOTE_NAMES = ['C', 'C#', 'D', 'D#', 'E', 'F', 'F#', 'G', 'G#', 'A', 'A#', 'B'];

// Drag must exceed this before it counts as a move rather than a click.
const DRAG_SLOP = 3;
// Grab zone on a note's right edge for resizing.
const RESIZE_HANDLE = 6;

let view = null;

export class PianoRoll {
    constructor(canvas, dotnet) {
        this.canvas = canvas;
        this.dotnet = dotnet;
        this.ctx = canvas.getContext('2d');

        this.notes = [];
        this.resolution = 480;
        this.beatsPerBar = 4;
        this.beatUnit = 4;
        this.playheadTick = -1;
        this.selected = new Set();

        this.tickWidth = 0.25;   // px per tick
        this.rowHeight = 16;     // px per semitone
        this.scrollTick = 0;
        this.scrollTone = 72;    // topmost visible tone
        this.snap = 480 / 4;     // 16th notes

        this.drag = null;
        this.hover = null;

        this._bind();
        this.resize();
    }

    // --- geometry ---------------------------------------------------------

    tickToX(tick) { return KEY_WIDTH + (tick - this.scrollTick) * this.tickWidth; }
    xToTick(x) { return (x - KEY_WIDTH) / this.tickWidth + this.scrollTick; }
    toneToY(tone) { return RULER_HEIGHT + (this.scrollTone - tone) * this.rowHeight; }
    // Inverse of toneToY: a row spans [toneToY(tone), toneToY(tone) + rowHeight).
    yToTone(y) { return Math.ceil(this.scrollTone - (y - RULER_HEIGHT) / this.rowHeight); }

    snapTick(tick) { return Math.round(tick / this.snap) * this.snap; }
    floorTick(tick) { return Math.floor(tick / this.snap) * this.snap; }

    get visibleTicks() { return (this.canvas.clientWidth - KEY_WIDTH) / this.tickWidth; }

    // --- state from .NET --------------------------------------------------

    setState(json) {
        const s = JSON.parse(json);
        this.notes = s.notes;
        this.resolution = s.resolution;
        this.beatsPerBar = s.beatsPerBar;
        this.beatUnit = s.beatUnit;
        this.playheadTick = s.playheadTick;
        this.render();
    }

    setPlayhead(tick) {
        this.playheadTick = tick;
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

        this._drawRows(w, h);
        this._drawGrid(w, h);
        this._drawNotes();
        this._drawKeyboard(h);
        this._drawRuler(w);
        this._drawPlayhead(h);
    }

    _drawRows(w, h) {
        const ctx = this.ctx;
        const top = this.scrollTone;
        const rows = Math.ceil((h - RULER_HEIGHT) / this.rowHeight) + 1;
        for (let i = 0; i < rows; i++) {
            const tone = top - i;
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

    _drawGrid(w, h) {
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
                ctx.fillRect(Math.round(this.tickToX(t)), RULER_HEIGHT, 1, h);
            }
        }
        if (drawBeats) {
            ctx.fillStyle = '#3a3a45';
            for (let t = start; t < end; t += ticksPerBeat) {
                if (t % ticksPerBar === 0) continue;
                ctx.fillRect(Math.round(this.tickToX(t)), RULER_HEIGHT, 1, h);
            }
        }
        ctx.fillStyle = '#54545f';
        for (let t = start; t < end; t += ticksPerBar) {
            ctx.fillRect(Math.round(this.tickToX(t)), RULER_HEIGHT, 1, h);
        }
    }

    _drawNotes() {
        const ctx = this.ctx;
        ctx.font = '11px system-ui, sans-serif';
        ctx.textBaseline = 'middle';
        for (const n of this.notes) {
            const x = this.tickToX(n.pos);
            const y = this.toneToY(n.tone);
            const wpx = Math.max(2, n.dur * this.tickWidth);
            if (x + wpx < KEY_WIDTH || x > this.canvas.clientWidth) continue;

            const isSel = this.selected.has(n.id);
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
        }
    }

    _drawKeyboard(h) {
        const ctx = this.ctx;
        ctx.fillStyle = '#141418';
        ctx.fillRect(0, RULER_HEIGHT, KEY_WIDTH, h);
        ctx.font = '10px system-ui, sans-serif';
        ctx.textBaseline = 'middle';

        const rows = Math.ceil((h - RULER_HEIGHT) / this.rowHeight) + 1;
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

    // --- input ------------------------------------------------------------

    _bind() {
        const c = this.canvas;
        c.addEventListener('mousedown', e => this._onDown(e));
        window.addEventListener('mousemove', e => this._onMove(e));
        window.addEventListener('mouseup', e => this._onUp(e));
        c.addEventListener('wheel', e => this._onWheel(e), { passive: false });
        c.addEventListener('contextmenu', e => e.preventDefault());
        c.addEventListener('dblclick', e => this._onDblClick(e));
    }

    _pos(e) {
        const r = this.canvas.getBoundingClientRect();
        return { x: e.clientX - r.left, y: e.clientY - r.top };
    }

    _onDown(e) {
        const { x, y } = this._pos(e);

        if (y < RULER_HEIGHT) {
            this.dotnet.invokeMethodAsync('OnSeek', Math.max(0, Math.round(this.xToTick(x))));
            this.drag = { kind: 'seek' };
            return;
        }
        if (x < KEY_WIDTH) return;

        const note = this.noteAt(x, y);

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
            this.selected.clear();
            this.render();
            return;
        }

        if (!e.shiftKey && !this.selected.has(note.id)) {
            this.selected.clear();
        }
        this.selected.add(note.id);

        const rightEdge = this.tickToX(note.pos + note.dur);
        const kind = (rightEdge - x) <= RESIZE_HANDLE ? 'resize' : 'move';
        this.drag = {
            kind, note, startX: x, startY: y, moved: false,
            origin: this.notes.filter(n => this.selected.has(n.id))
                .map(n => ({ id: n.id, pos: n.pos, dur: n.dur, tone: n.tone })),
        };
        this.render();
    }

    _onMove(e) {
        const { x, y } = this._pos(e);

        if (this.drag == null) {
            const over = y >= RULER_HEIGHT && x >= KEY_WIDTH ? this.noteAt(x, y) : null;
            let cursor = 'default';
            if (over) {
                cursor = (this.tickToX(over.pos + over.dur) - x) <= RESIZE_HANDLE ? 'ew-resize' : 'move';
            }
            this.canvas.style.cursor = cursor;
            return;
        }

        const d = this.drag;
        if (d.kind === 'seek') {
            this.dotnet.invokeMethodAsync('OnSeek', Math.max(0, Math.round(this.xToTick(x))));
            return;
        }
        if (Math.abs(x - d.startX) > DRAG_SLOP || Math.abs(y - (d.startY ?? y)) > DRAG_SLOP) {
            d.moved = true;
        }

        if (d.kind === 'create') {
            d.dur = Math.max(this.snap, this.snapTick(this.xToTick(x) - d.pos));
            this._previewCreate(d);
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

    _previewCreate(d) {
        this.render();
        const ctx = this.ctx;
        ctx.fillStyle = 'rgba(122, 200, 255, 0.6)';
        ctx.fillRect(this.tickToX(d.pos), this.toneToY(d.tone) + 1,
            Math.max(2, d.dur * this.tickWidth), this.rowHeight - 2);
    }

    _onUp(e) {
        const d = this.drag;
        this.drag = null;
        if (d == null) return;

        if (d.kind === 'create') {
            this.dotnet.invokeMethodAsync('OnAddNote', d.pos, d.dur, d.tone);
        } else if (d.kind === 'resize' && d.moved && d.delta) {
            this.dotnet.invokeMethodAsync('OnResizeNotes', d.origin.map(o => o.id), d.delta);
        } else if (d.kind === 'move' && d.moved && (d.deltaTick || d.deltaTone)) {
            this.dotnet.invokeMethodAsync('OnMoveNotes', d.origin.map(o => o.id),
                d.deltaTick ?? 0, d.deltaTone ?? 0);
        } else if (d.kind === 'move' && !d.moved) {
            this.render();
        }
    }

    _onDblClick(e) {
        const { x, y } = this._pos(e);
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
    new ResizeObserver(() => view.resize()).observe(canvas);
    return true;
}

export function setState(json) { view?.setState(json); }
export function setPlayhead(tick) { view?.setPlayhead(tick); }
export function setSnap(ticks) { if (view) { view.snap = ticks; view.render(); } }
export function dispose() { view = null; }
