// Run: node src/OpenWebtau/wwwroot/js/pianoroll.test.mjs
//
// Covers the screen <-> musical coordinate mapping. An off-by-one row here does
// not throw; it silently makes every click land on the wrong note, so it is worth
// pinning down.
import assert from 'node:assert/strict';

const noop = new Proxy(() => {}, { get: () => noop, apply: () => noop });
globalThis.window = { devicePixelRatio: 1, addEventListener() {} };
globalThis.ResizeObserver = class { observe() {} };

const { PianoRoll } = await import('./pianoroll.js');

const canvas = {
    clientWidth: 1200,
    clientHeight: 700,
    style: {},
    getContext: () => noop,
    addEventListener() {},
    getBoundingClientRect: () => ({ left: 0, top: 0 }),
};

const roll = new PianoRoll(canvas, { invokeMethodAsync() {} });

// Every y inside a row must map back to that row, and only that row.
for (let tone = 30; tone <= 90; tone++) {
    const top = roll.toneToY(tone);
    for (const dy of [0, 1, roll.rowHeight / 2, roll.rowHeight - 0.01]) {
        assert.equal(roll.yToTone(top + dy), tone,
            `y=${top + dy} should be tone ${tone}`);
    }
    assert.equal(roll.yToTone(top + roll.rowHeight), tone - 1,
        `one row down from tone ${tone} should be ${tone - 1}`);
}

// The pitch line runs through the middle of a tone's row, and round-trips.
for (let tone = 40; tone <= 80; tone++) {
    const mid = roll.toneToY(tone) + roll.rowHeight / 2;
    assert.equal(roll.toneToYF(tone), mid, `pitch line for ${tone} should sit mid-row`);
    assert.ok(Math.abs(roll.yToToneF(roll.toneToYF(tone)) - tone) < 1e-9);
    // A pitch point drawn at its row centre must be inside that row.
    assert.equal(roll.yToTone(roll.toneToYF(tone)), tone);
}

// Ticks round-trip through pixels.
for (const tick of [0, 1, 480, 1920, 30000]) {
    assert.ok(Math.abs(roll.xToTick(roll.tickToX(tick)) - tick) < 1e-6);
}

// Hit testing picks the note under the cursor, and nothing outside it.
roll.notes = [{ id: 0, pos: 480, dur: 240, tone: 60, lyric: 'a' }];
const inside = { x: roll.tickToX(500), y: roll.toneToY(60) + 2 };
assert.equal(roll.noteAt(inside.x, inside.y)?.id, 0);
assert.equal(roll.noteAt(roll.tickToX(721), inside.y), null, 'past the note end');
assert.equal(roll.noteAt(roll.tickToX(479), inside.y), null, 'before the note start');
assert.equal(roll.noteAt(inside.x, roll.toneToY(61) + 2), null, 'wrong row');

// Snapping never produces a zero-length note.
roll.snap = 120;
assert.equal(roll.snapTick(59), 0);
assert.equal(roll.snapTick(61), 120);
assert.equal(roll.floorTick(119), 0);
assert.equal(roll.floorTick(120), 120);

console.log('pianoroll geometry ok');
