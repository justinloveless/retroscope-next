// The deploy-time background track for the timer (UX13). The server serves it at TRACK_URL when the
// deployer set MUSIC_FILE, and says so in the retro boot JSON. It plays through an <audio> element, so the
// browser streams it with range requests instead of decoding minutes of PCM into memory, and the element
// is routed into the page's AudioContext so volume and mute keep working. The position is the timer's
// shared one modulo the track's length: a large drift is corrected with a seek, a small one by nudging the
// playback rate, which is inaudible where a seek every tick would stutter.

export const TRACK_URL = '/media/timer-music';
export const TRACK_LEVEL = 0.3;

const SEEK_DRIFT_S = 0.25;
const NUDGE_DRIFT_S = 0.04;
const NUDGE = 0.03;
const HAVE_METADATA = 1;
const HAVE_FUTURE_DATA = 3;

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

/** How to correct a drift: seek beyond 250 ms, else nudge the rate towards the shared position. */
export function trackCorrection(drift) {
  if (Math.abs(drift) > SEEK_DRIFT_S) {
    return { seek: true, rate: 1 };
  }
  if (Math.abs(drift) > NUDGE_DRIFT_S) {
    return { seek: false, rate: drift > 0 ? 1 - NUDGE : 1 + NUDGE };
  }
  return { seek: false, rate: 1 };
}

/**
 * `sound` gives the page's AudioContext and volume. `failed` turns true for good when the file cannot be
 * fetched or decoded; the timer then plays its generated music.
 */
export function createTrack({ url = TRACK_URL, sound, createAudio = () => new Audio(), clock = () => performance.now() }) {
  let audio = null;
  let gain = null;
  let failed = false;
  let playing = false;
  let pending = false;
  let wanted = null;

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

  // While data is still on its way the element stands still, so only the first alignment (on metadata)
  // seeks then; correcting again before playback resumes would chase the clock with seek after seek.
  function align(first) {
    if (!playing || wanted === null || audio.seeking || !(audio.duration > 0) || !Number.isFinite(audio.duration)) {
      return;
    }
    if (audio.readyState < (first ? HAVE_METADATA : HAVE_FUTURE_DATA)) {
      return;
    }
    const positionMs = wanted.positionMs + (clock() - wanted.at);
    const { seek, rate } = trackCorrection(trackDrift(audio.currentTime, positionMs, audio.duration));
    if (seek) {
      audio.currentTime = (positionMs / 1000) % audio.duration;
    }
    if (audio.playbackRate !== rate) {
      audio.playbackRate = rate;
    }
  }

  function stop() {
    playing = false;
    wanted = null;
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

    /** Inside the unlocking gesture: WebKit lets an element play later only if it was touched in one. */
    prime() {
      if (!failed) {
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
