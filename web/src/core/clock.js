// Server clock offset (DESIGN §g.5). welcome.n seeds it; each pong {c, n} is a sample with
// rtt = now − c and offset = n − (c + rtt/2); the offset in use is the one from the minimum-RTT
// sample among the last 8. A gap between wall-clock and monotonic time (sleep, manual clock change)
// discards the samples, and the caller should send a fresh ping.

const KEEP = 8;
const STEP_MS = 1000;

export function createClock({ now = () => Date.now(), monotonic = () => performance.now() } = {}) {
  let offset = 0;
  let samples = [];
  let lastWall = null;
  let lastMono = null;

  function best() {
    let min = samples[0];
    for (const sample of samples) {
      if (sample.rtt < min.rtt) {
        min = sample;
      }
    }
    offset = min.offset;
  }

  return {
    get offset() {
      return offset;
    },

    seed(serverMs) {
      samples = [];
      offset = serverMs - now();
    },

    sample(clientMs, serverMs, receivedAt = now()) {
      const rtt = receivedAt - clientMs;
      if (rtt < 0) {
        return;
      }
      samples.push({ rtt, offset: serverMs - (clientMs + rtt / 2) });
      if (samples.length > KEEP) {
        samples.shift();
      }
      best();
    },

    serverNow() {
      return now() + offset;
    },

    // Returns true when the clock stepped since the previous call; samples are then dropped.
    checkStep() {
      const wall = now();
      const mono = monotonic();
      const stepped = lastWall !== null && Math.abs((wall - lastWall) - (mono - lastMono)) > STEP_MS;
      lastWall = wall;
      lastMono = mono;
      if (stepped) {
        samples = [];
      }
      return stepped;
    },
  };
}
