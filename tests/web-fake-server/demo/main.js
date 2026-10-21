// `./build.sh preview`: serves web/dist from the fake server with the demo scenario and prints where to go.
// Options: --port <n> (default 5173), --host <ip> (default 127.0.0.1), --quiet-peers (no simulated
// teammates; the screenshot run uses it), --dist <dir>.
import { parseArgs } from 'node:util';
import { startFakeServer } from '../server.js';
import { CODES, DEMO_TEAM, installDemo } from './index.js';

const { values } = parseArgs({
  options: {
    port: { type: 'string', default: '5173' },
    host: { type: 'string', default: '127.0.0.1' },
    dist: { type: 'string' },
    'quiet-peers': { type: 'boolean', default: false },
  },
});

const fake = await startFakeServer({ port: Number(values.port), host: values.host, dist: values.dist, aliases: ['localhost'] });
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
  `  Dashboard         ${a}/dashboard`,
  `  Demo Team         ${a}/teams/${DEMO_TEAM}`,
  `  Team poker        ${a}/teams/${DEMO_TEAM}/poker/${CODES.teamPoker}`,
  `  Quick poker       ${a}/poker/${CODES.quickPoker}`,
  `  Retro, thinking   ${a}/retro/${CODES.thinking}`,
  `  Retro, voting     ${a}/retro/${CODES.voting}`,
  `  Retro, discussing ${a}/retro/${CODES.discussing}`,
  `  Retro, closed     ${a}/retro/${CODES.closed}`,
  `  Jira settings     ${a}/teams/${DEMO_TEAM}/settings#jira`,
  `  Admin             ${a}/admin/users`,
  '',
  'Second person, live in the same browser (127.0.0.1 keeps its own cookies):',
  `  ${b}/teams/${DEMO_TEAM}/poker/${CODES.teamPoker}?as=priya`,
  '  Members: demo, priya, marcus, sofia, jonas, aisha (and tom, lena in Platform Guild). ?as=signed-out signs out.',
  '',
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
