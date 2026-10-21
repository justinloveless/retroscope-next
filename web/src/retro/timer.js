// The shared timer (DESIGN §g.5, R16). The server owns the state: a running timer is an `endsAt` on the
// server clock, so every tab shows `endsAt − serverNow()` whatever its own clock says. The display ticks at
// 4 Hz while visible and 1 Hz while hidden. The alarm (three 880 Hz square beeps) plays when local time
// crosses `endsAt`, or on the server's expiry if that is within 5 s of `endsAt`, and at most once per
// `endsAt`. Music is generated, and its position is `durationMs − remainingMs`, so every tab plays the
// same bar. Volume and mute are per browser.

import { h, replaceContent } from '../core/h.js';
import { announce } from '../core/a11y.js';
import { duration } from '../core/fmt.js';
import { createButton } from '../ui/button.js';
import { createDialog } from '../ui/dialog.js';
import { icon } from '../ui/icons.js';
import { createMenu } from '../ui/menu.js';
import { createSwitch } from '../ui/switch.js';
import { toast } from '../ui/toast.js';
import { isFacilitator } from './model.js';

const TICK_VISIBLE_MS = 250;
const TICK_HIDDEN_MS = 1000;
const ALARM_WINDOW_MS = 5000;
const CLOCK_STEP_MS = 1000;
const MIN_DURATION_MS = 1000;
const MAX_DURATION_MS = 7_200_000;
const VOLUME_KEY = 'rs.timer.volume';
const MUTED_KEY = 'rs.timer.muted';
const VOLUMES = [0.25, 0.5, 0.75, 1];

export function remainingMs(timer, serverNow) {
  if (timer.state === 'running') {
    return timer.endsAt - serverNow;
  }
  return timer.state === 'paused' ? timer.remainingMs : timer.durationMs;
}

/** Where the music is: the time the timer has run, derived and never stored (§c.4). */
export function musicPositionMs(timer, serverNow) {
  return Math.max(0, timer.durationMs - Math.max(0, remainingMs(timer, serverNow)));
}

/** "15" or "15:30" or ":45" → milliseconds; null when it is not a duration the server accepts. */
export function parseDuration(minutes, seconds) {
  const m = minutes.trim() === '' ? 0 : Number(minutes);
  const s = seconds.trim() === '' ? 0 : Number(seconds);
  if (!Number.isInteger(m) || !Number.isInteger(s) || m < 0 || s < 0 || s > 59) {
    return null;
  }
  const ms = (m * 60 + s) * 1000;
  return ms >= MIN_DURATION_MS && ms <= MAX_DURATION_MS ? ms : null;
}

function readPref(key, fallback) {
  try {
    return localStorage.getItem(key) ?? fallback;
  } catch {
    return fallback;
  }
}

function writePref(key, value) {
  try {
    localStorage.setItem(key, String(value));
  } catch {
    // The setting then lasts as long as the page.
  }
}

// One AudioContext per page, created and resumed inside the first pointer or key event, because browsers
// keep audio locked until the user interacts with the page.
const sound = {
  context: null,
  volume: Number(readPref(VOLUME_KEY, '0.5')) || 0.5,
  muted: readPref(MUTED_KEY, '0') === '1',
  listeners: new Set(),

  get ready() {
    return this.context?.state === 'running';
  },

  unlock() {
    const Context = globalThis.AudioContext ?? globalThis.webkitAudioContext;
    if (!Context) {
      return;
    }
    try {
      this.context ??= new Context();
      this.context.resume?.().then(() => this.changed(), () => {});
    } catch {
      return;
    }
    this.changed();
  },

  changed() {
    for (const listener of this.listeners) {
      listener();
    }
  },

  set(volume, muted) {
    this.volume = volume;
    this.muted = muted;
    writePref(VOLUME_KEY, volume);
    writePref(MUTED_KEY, muted ? '1' : '0');
    this.changed();
  },

  tone(destination, frequency, type, at, length, level) {
    const context = this.context;
    const oscillator = context.createOscillator();
    const gain = context.createGain();
    oscillator.type = type;
    oscillator.frequency.value = frequency;
    gain.gain.setValueAtTime(0.0001, at);
    gain.gain.linearRampToValueAtTime(level, at + 0.01);
    gain.gain.setValueAtTime(level, at + length - 0.03);
    gain.gain.linearRampToValueAtTime(0.0001, at + length);
    oscillator.connect(gain);
    gain.connect(destination);
    oscillator.start(at);
    oscillator.stop(at + length);
  },

  alarm() {
    if (!this.ready || this.muted) {
      return;
    }
    const start = this.context.currentTime + 0.05;
    for (let i = 0; i < 3; i++) {
      this.tone(this.context.destination, 880, 'square', start + i * 0.3, 0.18, 0.2 * this.volume);
    }
  },
};

function unlockSound() {
  sound.unlock();
  if (sound.ready) {
    document.removeEventListener('pointerdown', unlockSound, true);
    document.removeEventListener('keydown', unlockSound, true);
  }
}

// A calm A-minor arpeggio, 32 steps of 250 ms (8 s), played from the shared position.
const MUSIC_STEP_MS = 250;
const MUSIC_LOOKAHEAD_MS = 1500;
const MUSIC_NOTES = [220, 261.63, 329.63, 440, 329.63, 261.63, 220, 0,
  174.61, 220, 261.63, 349.23, 261.63, 220, 174.61, 0,
  130.81, 196, 261.63, 329.63, 261.63, 196, 130.81, 0,
  146.83, 196, 246.94, 392, 246.94, 196, 164.81, 0];

function createMusic() {
  let output = null;
  let until = 0;

  function stop() {
    output?.disconnect();
    output = null;
  }

  return {
    stop,
    /** Schedules the notes from `position` up to the lookahead; a jump (pause, resync) restarts the schedule. */
    play(position) {
      const context = sound.context;
      if (!output) {
        output = context.createGain();
        output.connect(context.destination);
        until = position;
      }
      output.gain.value = 0.08 * sound.volume;
      if (until < position || until > position + MUSIC_LOOKAHEAD_MS + MUSIC_STEP_MS) {
        stop();
        this.play(position);
        return;
      }
      const now = context.currentTime;
      let stepIndex = Math.ceil(until / MUSIC_STEP_MS);
      for (; stepIndex * MUSIC_STEP_MS < position + MUSIC_LOOKAHEAD_MS; stepIndex++) {
        const frequency = MUSIC_NOTES[stepIndex % MUSIC_NOTES.length];
        const at = now + (stepIndex * MUSIC_STEP_MS - position) / 1000;
        if (frequency > 0 && at >= now) {
          sound.tone(output, frequency, 'triangle', at, MUSIC_STEP_MS / 1000 * 0.9, 1);
        }
      }
      until = stepIndex * MUSIC_STEP_MS;
    },
  };
}

function timerDialog(ctx) {
  const view = ctx.view();
  const total = Math.round(view.timer.durationMs / 1000);
  const numeric = (value, label) => h('input', {
    class: 'input retro-timer-input', inputmode: 'numeric', pattern: '[0-9]*', maxlength: 3, value: String(value), 'aria-label': label,
  });
  const minutes = numeric(Math.floor(total / 60), 'Minutes');
  const seconds = numeric(String(total % 60).padStart(2, '0'), 'Seconds');
  const alarm = createSwitch({ label: 'Play an alarm when time is up', checked: view.timer.alarm });
  const music = createSwitch({ label: 'Play background music', checked: view.timer.music });
  const error = h('p', { class: 'retro-composer-error', role: 'alert', hidden: true });
  const start = () => {
    const durationMs = parseDuration(minutes.value, seconds.value);
    if (durationMs === null) {
      error.hidden = false;
      error.textContent = 'Choose between 1 second and 120 minutes.';
      minutes.focus();
      return;
    }
    send(ctx, 'retro.timer.start', { durationMs, alarm: alarm.checked, music: music.checked });
    dialog.close();
  };
  const form = h('form', {
    class: 'retro-timer-form',
    on: {
      submit(event) {
        event.preventDefault();
        start();
      },
    },
  },
  h('div', { class: 'retro-timer-duration' },
    h('label', { class: 'field' }, h('span', { class: 'field-label', text: 'Minutes' }), minutes),
    h('span', { class: 'retro-timer-colon', text: ':' }),
    h('label', { class: 'field' }, h('span', { class: 'field-label', text: 'Seconds' }), seconds)),
  error, alarm, music,
  h('button', { type: 'submit', hidden: true }));
  const dialog = createDialog({
    title: 'Set the timer',
    description: 'Everyone on the board sees the same countdown.',
    content: form,
    actions: [
      createButton({ label: 'Cancel', variant: 'secondary', onClick: () => dialog.close() }),
      createButton({ label: 'Start timer', icon: 'play', variant: 'primary', onClick: start }),
    ],
    onClose: () => dialog.destroy(),
  });
  dialog.open();
  minutes.focus();
  minutes.select();
}

function send(ctx, k, d) {
  const view = ctx.view();
  ctx.command(k, k === 'retro.timer.setFlags' ? d : { ...d, from: view.timer.state }, {
    onNack(nack) {
      if (nack.code !== 'stale') {
        return false;
      }
      toast('Someone else changed the timer first.');
      return true;
    },
  });
}

export const timerFeature = {
  board(ctx, { toolbar }) {
    const music = createMusic();
    const rung = new Set();
    let view = null;
    let seenExpired = null;
    let lastRemaining = null;
    let lastEndsAt = null;
    let wall = Date.now();
    let mono = performance.now();
    let tick = 0;

    const setup = createButton({ label: 'Timer', icon: 'timer', variant: 'outline', size: 'sm', onClick: () => timerDialog(ctx) });
    const signIn = h('span', { class: 'retro-timer-hint', text: 'Sign in to use the timer' });
    const display = h('span', { class: 'retro-timer-display', role: 'timer', 'aria-label': 'Time left' });
    const pause = createButton({ iconOnly: true, variant: 'ghost', size: 'icon', onClick: () => send(ctx, view.timer.state === 'running' ? 'retro.timer.pause' : 'retro.timer.resume', {}) });
    const reset = createButton({ label: 'Reset timer', icon: 'rotate-ccw', iconOnly: true, variant: 'ghost', size: 'icon', onClick: () => send(ctx, 'retro.timer.reset', {}) });
    const soundButton = h('button', { type: 'button', class: 'btn btn-ghost btn-icon btn-sm retro-timer-sound' });
    const soundMenu = createMenu({ items: [], label: 'Timer sound', onSelect: choose });
    soundMenu.attach(soundButton);
    const running = h('span', { class: 'retro-timer-running' }, icon('timer'), display, pause, reset, soundButton);
    const el = h('div', { class: 'retro-timer' }, setup, signIn, running);
    toolbar.append(el);

    function choose(action) {
      if (action === 'alarm' || action === 'music') {
        send(ctx, 'retro.timer.setFlags', { [action]: !view.timer[action] });
      } else if (action === 'mute') {
        sound.set(sound.volume, !sound.muted);
      } else if (action.startsWith('volume:')) {
        sound.set(Number(action.slice('volume:'.length)), false);
      }
    }

    function ring(endsAt) {
      if (rung.has(endsAt)) {
        return;
      }
      rung.add(endsAt);
      sound.alarm();
      announce('Time is up.', { assertive: true });
      el.dataset.alarms = String(rung.size);
    }

    function paintSound() {
      const facilitator = isFacilitator(view, ctx.me()) && !view.meta.archived;
      const wantsSound = view.timer.alarm || view.timer.music;
      const locked = wantsSound && !sound.ready;
      replaceContent(soundButton, icon(locked ? 'triangle-alert' : sound.muted ? 'volume-x' : view.timer.music ? 'music' : 'bell'));
      const label = locked ? 'Click anywhere on the page to turn on the timer sound' : 'Timer sound';
      soundButton.setAttribute('aria-label', label);
      soundButton.title = label;
      soundMenu.update({
        items: [
          ...(facilitator
            ? [
              { action: 'alarm', label: 'Alarm when time is up', icon: view.timer.alarm ? 'check' : 'circle' },
              { action: 'music', label: 'Background music', icon: view.timer.music ? 'check' : 'circle' },
              'separator',
            ]
            : []),
          { action: 'mute', label: sound.muted ? 'Unmute' : 'Mute', icon: sound.muted ? 'volume-x' : 'bell' },
          ...VOLUMES.map((v) => ({ action: `volume:${v}`, label: `Volume ${v * 100}%`, icon: !sound.muted && v === sound.volume ? 'check' : 'circle' })),
        ],
      });
    }

    // A wall-clock jump that monotonic time did not see (sleep, a manual clock change) makes the clock
    // offset untrustworthy; the socket drops its samples and sends a fresh ping (§g.5).
    function checkClock() {
      const nextWall = Date.now();
      const nextMono = performance.now();
      const stepped = Math.abs((nextWall - wall) - (nextMono - mono)) > CLOCK_STEP_MS;
      wall = nextWall;
      mono = nextMono;
      if (stepped) {
        ctx.socket.resync();
      }
    }

    function paint() {
      const timer = view.timer;
      const now = ctx.now();
      const remaining = remainingMs(timer, now);
      display.textContent = duration(remaining);
      if (timer.state === 'running') {
        display.dataset.deadline = String(Math.round(timer.endsAt - (now - Date.now())));
        if (lastEndsAt !== timer.endsAt) {
          lastEndsAt = timer.endsAt;
          lastRemaining = null;
        }
        if (remaining <= 0 && lastRemaining !== null && lastRemaining > 0 && timer.alarm) {
          ring(timer.endsAt);
        }
        lastRemaining = remaining;
      } else {
        delete display.dataset.deadline;
      }
      if (timer.state === 'running' && timer.music && !sound.muted && sound.ready && remaining > 0) {
        const position = musicPositionMs(timer, now);
        music.play(position);
        el.dataset.musicPosition = String(Math.round(position));
      } else {
        music.stop();
        delete el.dataset.musicPosition;
      }
    }

    function schedule() {
      clearTimeout(tick);
      if (view.timer.state !== 'running') {
        return;
      }
      tick = setTimeout(() => {
        checkClock();
        paint();
        schedule();
      }, document.visibilityState === 'hidden' ? TICK_HIDDEN_MS : TICK_VISIBLE_MS);
    }

    function update(next) {
      if (view === null) {
        seenExpired = next.expiredAt;
      }
      view = next;
      const timer = view.timer;
      const facilitator = isFacilitator(view, ctx.me()) && !view.meta.archived;
      const active = timer.state !== 'idle';
      el.dataset.state = timer.state;
      setup.el.hidden = active || !facilitator;
      signIn.hidden = active || facilitator || ctx.me()?.kind !== 'guest';
      running.hidden = !active;
      pause.el.hidden = !facilitator;
      reset.el.hidden = !facilitator;
      pause.update(timer.state === 'running'
        ? { label: 'Pause timer', icon: 'pause' }
        : { label: 'Resume timer', icon: 'play' });
      if (view.expiredAt !== seenExpired) {
        seenExpired = view.expiredAt;
        if (seenExpired !== null && timer.alarm && Math.abs(ctx.now() - seenExpired) <= ALARM_WINDOW_MS) {
          ring(seenExpired);
        }
      }
      paintSound();
      paint();
      schedule();
    }

    const onSound = () => {
      if (view !== null) {
        paintSound();
        paint();
      }
    };
    sound.listeners.add(onSound);
    document.addEventListener('pointerdown', unlockSound, true);
    document.addEventListener('keydown', unlockSound, true);
    const onVisibility = () => schedule();
    document.addEventListener('visibilitychange', onVisibility);

    return {
      update(next) {
        if (next !== view) {
          update(next);
        }
      },
      destroy() {
        clearTimeout(tick);
        music.stop();
        sound.listeners.delete(onSound);
        document.removeEventListener('visibilitychange', onVisibility);
        soundMenu.destroy();
        el.remove();
      },
    };
  },
};
