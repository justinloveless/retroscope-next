import assert from 'node:assert/strict';
import { test } from 'node:test';
import { TRACK_URL, createTrack, trackCorrection, trackDrift } from '../../../web/src/retro/track.js';

const close = (actual, expected, epsilon = 1e-9) => assert.ok(Math.abs(actual - expected) < epsilon, `${actual} ≉ ${expected}`);

/** An <audio> stand-in: plays when told, and fires its events when the test says the network delivered. */
class FakeAudio {
  constructor() {
    this.paused = true;
    this.seeking = false;
    this.readyState = 0;
    this.duration = NaN;
    this.currentTime = 0;
    this.playbackRate = 1;
    this.listeners = {};
    this.seeks = [];
    this.loads = 0;
    this.playResult = Promise.resolve();
  }

  addEventListener(type, listener) {
    (this.listeners[type] ??= []).push(listener);
  }

  fire(type) {
    for (const listener of this.listeners[type] ?? []) {
      listener();
    }
  }

  set src(value) {
    this.source = value;
  }

  get src() {
    return this.source;
  }

  load() {
    this.loads++;
  }

  play() {
    this.paused = false;
    return this.playResult;
  }

  pause() {
    this.paused = true;
  }

  /** Metadata and data arrive: the element knows its length and can play. */
  deliver(duration, readyState = 4) {
    this.duration = duration;
    this.readyState = 1;
    this.fire('loadedmetadata');
    this.readyState = readyState;
    if (readyState >= 3) {
      this.fire('canplay');
    }
  }
}

function setup({ audio = new FakeAudio(), volume = 0.5 } = {}) {
  let now = 1000;
  const graph = [];
  const node = (name) => ({ name, gain: { value: 1 }, connect(target) { graph.push([name, target.name]); return target; } });
  const context = {
    destination: { name: 'destination' },
    createGain: () => node('gain'),
    createMediaElementSource: (element) => {
      assert.equal(element, audio);
      return node('source');
    },
  };
  const sound = { context, volume };
  const made = [];
  const track = createTrack({
    sound,
    clock: () => now,
    createAudio: () => {
      made.push(audio);
      return audio;
    },
  });
  // Seeks are recorded as they happen; a real element would report `seeking` until the data arrives.
  let currentTime = 0;
  Object.defineProperty(audio, 'currentTime', {
    get: () => currentTime,
    set: (value) => {
      audio.seeks.push(value);
      currentTime = value;
    },
  });
  return { audio, track, graph, sound, made, advance: (ms) => { now += ms; }, setTime: (t) => { currentTime = t; } };
}

test('drift is measured the short way round the loop', () => {
  close(trackDrift(10, 10_000, 100), 0);
  close(trackDrift(10.2, 10_000, 100), 0.2);
  close(trackDrift(9.5, 10_000, 100), -0.5);
  close(trackDrift(0.1, 266_000 * 2 - 100, 266), 0.2);
  close(trackDrift(265.9, 266_000 + 100, 266), -0.2);
  close(trackDrift(61, 327_000, 266), 0);
});

test('a drift beyond 250 ms seeks, a smaller one nudges the rate, a tiny one plays at 1', () => {
  assert.deepEqual(trackCorrection(0.3), { seek: true, rate: 1 });
  assert.deepEqual(trackCorrection(-0.26), { seek: true, rate: 1 });
  assert.deepEqual(trackCorrection(0.2), { seek: false, rate: 0.97 });
  assert.deepEqual(trackCorrection(-0.1), { seek: false, rate: 1.03 });
  assert.deepEqual(trackCorrection(0.02), { seek: false, rate: 1 });
});

test('the element is made on first need with preload none, then loads only when it plays', () => {
  const { audio, track, made } = setup();
  assert.equal(made.length, 0);
  track.prime();
  assert.equal(made.length, 1);
  assert.equal(audio.src, TRACK_URL);
  assert.equal(audio.preload, 'none');
  assert.equal(audio.loop, true);
  assert.equal(audio.loads, 1);
  assert.equal(audio.paused, true);

  assert.equal(track.play(5000), true);
  assert.equal(audio.preload, 'auto');
  assert.equal(audio.paused, false);
});

test('it plays through a gain into the page context at the chosen volume', () => {
  const { track, graph, sound } = setup({ volume: 0.75 });
  track.play(0);
  assert.deepEqual(graph, [['source', 'gain'], ['gain', 'destination']]);
  sound.volume = 0.25;
  track.play(250);
  assert.deepEqual(graph.length, 2, 'the element is connected once');
});

test('on metadata it seeks to the shared position modulo the length, counting the time since play', () => {
  const { audio, track, advance } = setup();
  track.play(300_000);
  advance(400);
  audio.deliver(266);
  close(audio.seeks[0], (300_400 / 1000) % 266);
});

/** Starts playing at `positionMs` with the data in, and lets the post-seek settling pass. */
async function playing(positionMs) {
  const t = setup();
  t.track.play(positionMs);
  t.audio.deliver(266);
  await Promise.resolve();
  t.advance(1000);
  return t;
}

/** One tick of the timer `ms` after the last, with the element reporting `currentTime`. */
function tick(t, ms, positionMs, currentTime) {
  t.advance(ms);
  t.setTime(currentTime);
  t.track.play(positionMs);
}

test('a small drift that persists nudges the rate, and the rate returns to 1 once it is gone', async () => {
  const t = await playing(10_000);
  assert.equal(t.audio.seeks.length, 1);
  tick(t, 250, 11_000, 11.1);
  assert.equal(t.audio.playbackRate, 1, 'one sample decides nothing');
  tick(t, 250, 11_250, 11.35);
  assert.equal(t.audio.playbackRate, 0.97);
  tick(t, 250, 11_500, 11.5);
  tick(t, 250, 11_750, 11.75);
  tick(t, 250, 12_000, 12);
  tick(t, 250, 12_250, 12.25);
  tick(t, 250, 12_500, 12.5);
  tick(t, 250, 12_750, 12.75);
  assert.equal(t.audio.playbackRate, 1);
  assert.equal(t.audio.seeks.length, 1);
});

test('a lagging currentTime is read as the best of the last samples, so it does not cause a seek', async () => {
  const t = await playing(10_000);
  // Stepped reports: the element plays in time, but currentTime only moves every few ticks.
  tick(t, 250, 11_000, 11);
  tick(t, 250, 11_250, 11);
  tick(t, 250, 11_500, 11);
  tick(t, 250, 11_750, 11.75);
  tick(t, 250, 12_000, 11.75);
  assert.equal(t.audio.seeks.length, 1);
  assert.equal(t.audio.playbackRate, 1);
});

test('a jump while playing seeks to the shared position, then waits before judging again', async () => {
  const t = await playing(10_000);
  tick(t, 4000, 70_000, 14);
  tick(t, 250, 70_250, 14.25);
  assert.equal(t.audio.seeks.length, 2);
  close(t.audio.seeks[1], 70.25);
  tick(t, 250, 70_500, 70.1);
  tick(t, 250, 70_750, 70.1);
  assert.equal(t.audio.seeks.length, 2, 'settling after the seek');
  assert.equal(t.audio.playbackRate, 1);
});

test('a playing element seeks at most once every few seconds and is nudged in between', async () => {
  const t = await playing(10_000);
  tick(t, 2000, 12_000, 11.5);
  tick(t, 250, 12_250, 11.75);
  assert.equal(t.audio.seeks.length, 1, 'within the interval of the first seek');
  assert.equal(t.audio.playbackRate, 1.03);
  tick(t, 1000, 13_250, 12.75);
  assert.equal(t.audio.seeks.length, 2);
});

test('a paused element is aligned at once when it resumes', async () => {
  const t = await playing(10_000);
  t.track.stop();
  t.setTime(10);
  t.advance(250);
  t.track.play(50_000);
  assert.equal(t.audio.seeks.length, 2);
  close(t.audio.seeks[1], 50);
});

test('no correction while the element is seeking or still waiting for data', async () => {
  const t = setup();
  t.track.play(10_000);
  t.audio.deliver(266, 1);
  await Promise.resolve();
  assert.equal(t.audio.seeks.length, 1);
  t.advance(4000);
  t.setTime(0);
  t.track.play(20_000);
  t.track.play(20_000);
  assert.equal(t.audio.seeks.length, 1, 'waiting for data');
  t.audio.readyState = 4;
  t.audio.seeking = true;
  t.track.play(20_000);
  t.track.play(20_000);
  assert.equal(t.audio.seeks.length, 1, 'seeking');
  t.audio.seeking = false;
  t.track.play(20_000);
  t.advance(250);
  t.track.play(20_250);
  assert.equal(t.audio.seeks.length, 2);
});

test('stop pauses and resets the rate; a play that resolves after stop is paused again', async () => {
  let resolvePlay;
  const audio = new FakeAudio();
  audio.playResult = new Promise((done) => {
    resolvePlay = done;
  });
  const { track } = setup({ audio });
  track.play(0);
  audio.playbackRate = 1.03;
  track.stop();
  assert.equal(audio.paused, true);
  assert.equal(audio.playbackRate, 1);
  audio.paused = false;
  resolvePlay();
  await audio.playResult;
  await Promise.resolve();
  assert.equal(audio.paused, true);
});

test('a load error turns the track off for good, so the caller falls back to generated music', () => {
  const { audio, track } = setup();
  assert.equal(track.play(0), true);
  audio.fire('error');
  assert.equal(track.failed, true);
  assert.equal(track.play(250), false);
});

test('a refused format turns it off, a play blocked before a gesture does not', async () => {
  const blocked = new FakeAudio();
  blocked.playResult = Promise.reject(Object.assign(new Error('gesture'), { name: 'NotAllowedError' }));
  const first = setup({ audio: blocked });
  first.track.play(0);
  await blocked.playResult.catch(() => {});
  await Promise.resolve();
  assert.equal(first.track.failed, false);

  const unsupported = new FakeAudio();
  unsupported.playResult = Promise.reject(Object.assign(new Error('format'), { name: 'NotSupportedError' }));
  const second = setup({ audio: unsupported });
  second.track.play(0);
  await unsupported.playResult.catch(() => {});
  await Promise.resolve();
  assert.equal(second.track.failed, true);
});

test('a context that cannot take the element turns the track off', () => {
  const audio = new FakeAudio();
  const track = createTrack({
    sound: { volume: 1, context: { createMediaElementSource: () => { throw new Error('InvalidStateError'); } } },
    createAudio: () => audio,
  });
  assert.equal(track.play(0), false);
  assert.equal(track.failed, true);
});
