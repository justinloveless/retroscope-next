// Golden transcript replay (DESIGN §g.4). A transcript is `{frames, expectedViewerState?,
// expectedNav?}` as the C# domain units emit it into tests/golden/transcripts/; `frames` is what one
// viewer's socket carries for one room. Server frames are sent verbatim, in order, after checking each
// against protocol.gen.js; client frames in the list (what the viewer sent) are not replayed. The
// welcome is the fake's own (its `me` becomes the transcript's), and a bye closes with 1012 like the
// real server.
import { readFileSync } from 'node:fs';
import { CLIENT_FRAME_TYPES, checkServerFrame } from './protocol.js';

export function loadTranscript(path) {
  return JSON.parse(readFileSync(path, 'utf8'));
}

export function parseTranscript(transcript) {
  if (typeof transcript !== 'object' || transcript === null || !Array.isArray(transcript.frames)) {
    throw new Error('a transcript is an object with a "frames" array');
  }
  let roomKey = null;
  let me = null;
  const frames = [];
  transcript.frames.forEach((frame, i) => {
    if (CLIENT_FRAME_TYPES.includes(frame?.t)) {
      return;
    }
    const errors = checkServerFrame(frame);
    if (errors.length > 0) {
      throw new Error(`transcript frame ${i} is not a valid server frame: ${errors.join('; ')}`);
    }
    if (frame.t === 'welcome') {
      me = frame.me;
      return;
    }
    if (frame.t === 'pong') {
      return;
    }
    if (frame.r !== undefined) {
      roomKey ??= frame.r;
      if (frame.r !== roomKey) {
        throw new Error(`transcript frame ${i} is for ${frame.r}, but the transcript is about ${roomKey}`);
      }
    }
    frames.push(frame);
  });
  if (roomKey === null) {
    throw new Error('the transcript has no frame naming a room');
  }
  if (!['snap', 'replay'].includes(frames[0]?.t)) {
    throw new Error('a transcript must start with the snap (or replay) that opens the subscription');
  }
  return { roomKey, me, frames };
}

// Keeps the fake room's epoch, seq and replay log in step with what the transcript has released, so a
// client that resumes with {e, s} afterwards gets a correct replay from the fake.
function applyToRoom(room, frame) {
  if (frame.t === 'snap') {
    room.epoch = frame.e;
    room.seq = frame.s;
    room.firstSeq = frame.s + 1;
    room.log = [];
  } else if (frame.t === 'replay') {
    room.epoch = frame.e;
    for (const tx of frame.txs) {
      room.log.push({ s: tx.s, ev: tx.ev });
      room.seq = Math.max(room.seq, tx.s);
    }
  } else if (frame.t === 'tx') {
    room.log.push({ s: frame.s, ev: frame.ev });
    room.seq = Math.max(room.seq, frame.s);
  }
}

export function createReplay(server, transcript, { step = false } = {}) {
  const { roomKey, me, frames } = parseTranscript(transcript);
  const room = server.room(roomKey);
  if (me) {
    server.me = me;
  }
  const attached = new Set();
  let released = 0;

  function deliver(conn, frame) {
    if (!conn.open) {
      return;
    }
    if (frame.t === 'bye') {
      conn.bye(frame.retryMs);
    } else {
      conn.send(frame);
    }
    if (frame.t === 'kick') {
      server.unsubscribe(conn, room);
    }
  }

  const release = (count) => {
    const batch = frames.slice(released, released + count);
    released += batch.length;
    for (const frame of batch) {
      applyToRoom(room, frame);
      for (const conn of attached) {
        deliver(conn, frame);
      }
    }
    return batch;
  };

  if (!step) {
    for (const frame of frames) {
      applyToRoom(room, frame);
    }
    released = frames.length;
  }

  return {
    roomKey,
    frames,
    get released() {
      return released;
    },
    get done() {
      return released === frames.length;
    },
    // Sends every frame released so far to a newly subscribed connection.
    attach(conn) {
      attached.add(conn);
      for (const frame of frames.slice(0, released)) {
        deliver(conn, frame);
      }
    },
    next(count = 1) {
      return release(count);
    },
    rest() {
      return release(frames.length - released);
    },
    detach() {
      if (room.replay === this) {
        room.replay = null;
      }
    },
  };
}
