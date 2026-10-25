// `./build.sh preview`: serves web/dist from the fake server with the demo scenario and prints where to go.
// Options: --port <n> (default 5173), --host <ip> (default 127.0.0.1), --quiet-peers (no simulated
// teammates; the screenshot run uses it), --dist <dir>, --music-file <path> (default $MUSIC_FILE: a local
// track for the retro timer, served as the real server serves MUSIC_FILE).
import { parseArgs } from 'node:util';
import { startFakeServer } from '../server.js';
import { CODES, DEMO_TEAM, installDemo } from './index.js';

const { values } = parseArgs({
  options: {
    port: { type: 'string', default: '5173' },
    host: { type: 'string', default: '127.0.0.1' },
    dist: { type: 'string' },
    'quiet-peers': { type: 'boolean', default: false },
    'music-file': { type: 'string' },
  },
});

const musicFile = values['music-file'] || process.env.MUSIC_FILE || null;
const fake = await startFakeServer({ port: Number(values.port), host: values.host, dist: values.dist, aliases: ['localhost'], musicFile });
fake.on('error', (error) => console.error(`demo: ${error.stack ?? error}`));
fake.on('violation', ({ errors }) => console.error(`demo: the client sent a frame the protocol refuses: ${errors.join('; ')}`));
const demo = installDemo(fake, { peers: !values['quiet-peers'] });

const port = new URL(fake.url).port;
const a = `http://localhost:${port}`;
const b = `http://127.0.0.1:${port}`;
const lines = [
  '',
  `Retroscope demo is running on ${a}`,
  '',
  'Open these (you are "Demo User"):',
  `  Home (Demo Team)  ${a}/`,
  `  Retros            ${a}/teams/${DEMO_TEAM}?tab=retros`,
  `  Team poker        ${a}/teams/${DEMO_TEAM}/poker/${CODES.teamPoker}`,
  `  Crowded poker     ${a}/poker/${CODES.crowdPoker}`,
  `  Retro, thinking   ${a}/retro/${CODES.thinking}`,
  `  Retro, voting     ${a}/retro/${CODES.voting}`,
  `  Retro, discussing ${a}/retro/${CODES.discussing}`,
  `  Retro, closed     ${a}/retro/${CODES.closed}`,
  `  Jira settings     ${a}/teams/${DEMO_TEAM}/settings#jira`,
  `  Create or join    ${a}/teams`,
  `  Admin             ${a}/admin/users`,
  `  Welcome page      ${a}/?as=signed-out (signed out; sign in again from the page)`,
  '',
  'Second person, live in the same browser (127.0.0.1 keeps its own cookies):',
  `  ${b}/teams/${DEMO_TEAM}/poker/${CODES.teamPoker}?as=marcus`,
  '  Demo Team: demo, priya, marcus, sofia, jonas, aisha. Platform Guild: lena, priya, demo, tom (unverified).',
  '  Product All-Hands: demo and eleven more (noor, mateo, …). ?as=signed-out signs out.',
  '',
  musicFile ? 'The retro timer plays the track from MUSIC_FILE when its music is on.' : 'The retro timer plays its generated music (set MUSIC_FILE to try a track).',
  values['quiet-peers'] ? 'Simulated teammates are paused.' : 'Simulated teammates vote, add cards and comment while a window is open.',
  'Mails (sign-in links, resets, verification) are printed here instead of sent.',
  'Ctrl-C stops the server.',
  '',
];
console.log(lines.join('\n'));

const stop = async () => {
  demo.stop();
  await fake.close();
  process.exit(0);
};
process.on('SIGINT', stop);
process.on('SIGTERM', stop);
