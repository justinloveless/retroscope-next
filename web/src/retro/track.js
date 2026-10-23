// The deploy-time background track for the timer (UX13). The server serves it at TRACK_URL when the
// deployer set MUSIC_FILE, and says so in the retro boot JSON. It plays through an <audio> element, so the
// browser streams it with range requests instead of decoding minutes of PCM into memory, and the element
// is routed into the page's AudioContext so volume and mute keep working. The position is the timer's
// shared one modulo the track's length: a large drift is corrected with a seek, a small one by nudging the
// playback rate, which is inaudible where a seek every tick would stutter.
//
// While it plays, an element's currentTime can lag the sound: WebKit updates it in steps of a few hundred
// milliseconds and stalls it for a moment after a seek. Lag only ever makes the element look behind, so
// the estimate is the largest drift seen over the last second and a half, and samples are ignored for a
// second after a seek; otherwise each seek's own stall would look like drift and trigger the next seek.
// An engine whose playback itself keeps stalling (headless WebKit does, every second or so) would still
// seek again and again, so a playing element seeks at most once every few seconds and is nudged between.

export const TRACK_URL = '/media/timer-music';
export const TRACK_LEVEL = 0.3;

const SEEK_DRIFT_S = 0.25;
const NUDGE_DRIFT_S = 0.04;
const NUDGE = 0.03;
const HAVE_METADATA = 1;
const HAVE_FUTURE_DATA = 3;
const WINDOW_MS = 1500;
const MIN_SAMPLES = 2;
const SETTLE_MS = 1000;
const SEEK_INTERVAL_MS = 4000;

/** Seconds the element is ahead (+) or behind (−) the shared position, the shorter way round the loop. */
export function trackDrift(currentTime, positionMs, duration) {
  let drift = currentTime - (positionMs / 1000) % duration;
  if (drift > duration / 2) {
    drift -= duration;
  } else if (drift < -duration / 2) {
    drift += duration;
  }
  return drift;
}

const nudged = (drift) => (drift > 0 ? 1 - NUDGE : 1 + NUDGE);

/** How to correct a drift: seek beyond 250 ms, else nudge the rate towards the shared position. */
export function trackCorrection(drift) {
  if (Math.abs(drift) > SEEK_DRIFT_S) {
    return { seek: true, rate: 1 };
  }
  return { seek: false, rate: Math.abs(drift) > NUDGE_DRIFT_S ? nudged(drift) : 1 };
}

/**
 * `sound` gives the page's AudioContext and volume. `failed` turns true for good when the file cannot be
 * fetched or decoded; the timer then plays its generated music.
 */
export function createTrack({ url = TRACK_URL, sound, createAudio = () => new Audio(), clock = () => performance.now() }) {
  let audio = null;
  let gain = null;
  let failed = false;
  let primed = false;
  let playing = false;
  let pending = false;
  let wanted = null;
  let samples = [];
  let settleUntil = -Infinity;
  let lastSeek = -Infinity;

  function element() {
    if (audio === null) {
      audio = createAudio();
      audio.preload = 'none';
      audio.loop = true;
      audio.addEventListener('error', () => {
        failed = true;
      });
      audio.addEventListener('loadedmetadata', () => align(true));
      audio.addEventListener('canplay', () => align(false));
      audio.src = url;
    }
    return audio;
  }

  function connect() {
    if (gain !== null) {
      return true;
    }
    try {
      const context = sound.context;
      const source = context.createMediaElementSource(audio);
      gain = context.createGain();
      source.connect(gain);
      gain.connect(context.destination);
      return true;
    } catch {
      failed = true;
      return false;
    }
  }

  function seekTo(positionMs, now) {
    audio.currentTime = (positionMs / 1000) % audio.duration;
    samples = [];
    settleUntil = now + SETTLE_MS;
    lastSeek = now;
  }

  function setRate(rate) {
    if (audio.playbackRate !== rate) {
      audio.playbackRate = rate;
    }
  }

  // `still`: the element is not playing yet, so its currentTime is exact and one look decides. While data
  // is on its way it stands still, so only that first alignment seeks then; correcting again before
  // playback starts would chase the clock with seek after seek.
  function align(still) {
    if (!playing || wanted === null || audio.seeking || !(audio.duration > 0) || !Number.isFinite(audio.duration)) {
      return;
    }
    if (audio.readyState < (still ? HAVE_METADATA : HAVE_FUTURE_DATA)) {
      return;
    }
    const now = clock();
    const positionMs = wanted.positionMs + (now - wanted.at);
    const drift = trackDrift(audio.currentTime, positionMs, audio.duration);
    if (still) {
      if (trackCorrection(drift).seek) {
        seekTo(positionMs, now);
      }
      return;
    }
    if (now < settleUntil) {
      setRate(1);
      return;
    }
    samples = samples.filter(([at]) => at > now - WINDOW_MS);
    samples.push([now, drift]);
    if (samples.length < MIN_SAMPLES) {
      return;
    }
    const estimate = Math.max(...samples.map(([, d]) => d));
    const { seek, rate } = trackCorrection(estimate);
    if (seek && now - lastSeek >= SEEK_INTERVAL_MS) {
      seekTo(positionMs, now);
      setRate(1);
    } else {
      setRate(seek ? nudged(estimate) : rate);
    }
  }

  function stop() {
    playing = false;
    wanted = null;
    samples = [];
    if (audio !== null) {
      if (!audio.paused) {
        audio.pause();
      }
      audio.playbackRate = 1;
    }
  }

  return {
    get failed() {
      return failed;
    },

    /**
     * Inside the unlocking gesture: WebKit lets an element play later only if it was touched in one. One touch
     * is enough, and load() would abort a playing element, so it runs once and never after the element played.
     */
    prime() {
      if (!failed && !primed) {
        primed = true;
        element().load();
      }
    },

    /** Plays from the shared position; false when the track cannot play and the caller should fall back. */
    play(positionMs) {
      if (failed) {
        return false;
      }
      element();
      if (!connect()) {
        return false;
      }
      gain.gain.value = TRACK_LEVEL * sound.volume;
      wanted = { positionMs, at: clock() };
      playing = true;
      audio.preload = 'auto';
      if (audio.paused && !pending) {
        pending = true;
        align(true);
        Promise.resolve(audio.play()).then(() => {
          pending = false;
          primed = true;
          if (!playing) {
            audio.pause();
          }
        }, (error) => {
          pending = false;
          if (error?.name === 'NotSupportedError') {
            failed = true;
          }
        });
      } else {
        align(false);
      }
      return true;
    },

    stop,
  };
}
