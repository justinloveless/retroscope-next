// The demo's seed content: three teams, retro boards in every stage, poker sessions, action items, templates,
// invites and kudos. Every board and session belongs to a team (UX18). Times are relative to `now`, so the demo
// always looks recent.
import { AISHA, CROWD, DEMO, JONAS, LENA, MARCUS, PRIYA, SOFIA, TOM } from './people.js';
import { DAY, HOUR, MINUTE, demoId, demoIdAt } from './util.js';

export const DEMO_TEAM = demoId('0001', 1);
export const GUILD_TEAM = demoId('0001', 2);
export const ALL_HANDS_TEAM = demoId('0001', 3);
export const BURNDOWN_BLOB = demoId('00b0', 1);

export const CODES = {
  thinking: 'SPR42THNK1',
  voting: 'CHKVTNG042',
  discussing: 'SPR41DSCSS',
  closed: 'SPR40CL5D1',
  archived: 'Q2RETR0ARC',
  guild: 'GLDRETR0Q3',
  teamPoker: 'PKR41CHKT1',
  oldPoker: 'PKR40SRCH1',
  guildPoker: 'GLDPKR0001',
  crowdPoker: 'CRWDPKR012',
};

const TYPES = {
  helper: demoId('00e0', 1),
  solver: demoId('00e0', 2),
  shipper: demoId('00e0', 3),
  teacher: demoId('00e0', 4),
};

const mention = (who) => `[[mention:${who.pid}:${who.nickname}]]`;

const STANDARD_CONFIG = {
  allowAnonymous: true,
  votingEnabled: true,
  showAuthorNames: true,
  stagesEnabled: true,
  enforceReadiness: false,
  allowSelfVotes: false,
  voteEmoji: '👍',
  sortChronologically: false,
  maxVotes: 3,
};

function columns(board) {
  const id = (n) => demoId('00c0', board * 10 + n);
  return {
    good: id(1),
    bad: id(2),
    kudos: id(3),
    actions: id(4),
    list: [
      { id: id(1), title: 'Went well', color: 'green' },
      { id: id(2), title: 'To improve', color: 'red' },
      { id: id(3), title: 'Kudos', color: 'pink' },
      { id: id(4), title: 'Action items', color: 'yellow', actionItems: true },
    ],
  };
}

function cards(board, now, entries) {
  return entries.map(([column, author, body, extra = {}], i) => ({
    id: demoId('00d0', board * 100 + i + 1),
    columnId: column,
    author: author.pid,
    body,
    anonymous: false,
    createdAt: now - (entries.length - i) * 3 * MINUTE,
    ...extra,
  }));
}

function comments(board, now, entries) {
  return entries.map(([card, author, body, extra = {}], i) => ({
    id: demoId('00f0', board * 100 + i + 1),
    cardId: card.id,
    author: author.pid,
    body,
    anonymous: false,
    createdAt: now - (entries.length - i) * 2 * MINUTE,
    ...extra,
  }));
}

export function teams(now) {
  return [{
    id: DEMO_TEAM,
    name: 'Demo Team',
    description: 'Checkout & payments squad at Demo Retail',
    createdAt: now - 220 * DAY,
    members: [
      { person: DEMO, role: 'owner' },
      { person: PRIYA, role: 'admin' },
      { person: MARCUS, role: 'member' },
      { person: SOFIA, role: 'member' },
      { person: JONAS, role: 'member' },
      { person: AISHA, role: 'member' },
    ],
    pointDescriptions: {
      1: 'A quick tweak, under an hour',
      2: 'Half a day',
      3: 'About a day',
      5: 'Two or three days',
      8: 'Most of the sprint for one person',
      13: 'Too big: split it before we start',
      21: 'We do not understand it yet',
    },
    maxEndorsements: 3,
    jiraConfigured: true,
    endorsementTypes: [
      { id: TYPES.helper, name: 'Helping Hand', emoji: '🤝', archived: false, description: 'Dropped everything to unblock someone' },
      { id: TYPES.solver, name: 'Problem Solver', emoji: '🧠', archived: false, description: 'Cracked the hard one' },
      { id: TYPES.shipper, name: 'Shipped It', emoji: '🚀', archived: false, description: 'Got it over the line' },
      { id: TYPES.teacher, name: 'Great Teacher', emoji: '🎓', archived: false },
    ],
    pastEndorsements: [
      [PRIYA, MARCUS, TYPES.solver, 40], [SOFIA, PRIYA, TYPES.helper, 45], [JONAS, PRIYA, TYPES.teacher, 52],
      [AISHA, DEMO, TYPES.helper, 60], [MARCUS, AISHA, TYPES.shipper, 75], [DEMO, SOFIA, TYPES.teacher, 95],
      [PRIYA, JONAS, TYPES.shipper, 120], [SOFIA, MARCUS, TYPES.solver, 130], [JONAS, AISHA, TYPES.helper, 150],
    ].map(([from, to, typeId, days], i) => ({ id: demoId('00e1', i + 1), from: from.pid, to: to.pid, typeId, createdAt: now - days * DAY })),
    templates: [
      {
        id: demoId('0070', 1),
        name: 'Went well / To improve / Kudos',
        isDefault: true,
        columns: [
          { title: 'Went well', color: 'green', actionItems: false },
          { title: 'To improve', color: 'red', actionItems: false },
          { title: 'Kudos', color: 'pink', actionItems: false },
          { title: 'Action items', color: 'yellow', actionItems: true },
        ],
        config: { ...STANDARD_CONFIG },
      },
      {
        id: demoId('0070', 2),
        name: 'Start / Stop / Continue',
        isDefault: false,
        columns: [
          { title: 'Start', color: 'green', actionItems: false },
          { title: 'Stop', color: 'red', actionItems: false },
          { title: 'Continue', color: 'blue', actionItems: false },
          { title: 'Action items', color: 'yellow', actionItems: true },
        ],
        config: { ...STANDARD_CONFIG, stagesEnabled: false, maxVotes: 5 },
      },
      {
        id: demoId('0070', 3),
        name: 'Sailboat',
        isDefault: false,
        columns: [
          { title: 'Wind (what pushed us)', color: 'blue', actionItems: false },
          { title: 'Anchors (what held us back)', color: 'gray', actionItems: false },
          { title: 'Rocks (risks ahead)', color: 'orange', actionItems: false },
          { title: 'Island (our goal)', color: 'indigo', actionItems: false },
          { title: 'Next steps', color: 'yellow', actionItems: true },
        ],
        config: { ...STANDARD_CONFIG, voteEmoji: '⛵' },
      },
    ],
    invites: [
      { id: demoId('0080', 1), kind: 'email', email: 'new.hire@example.test', role: 'member', createdAt: now - 2 * DAY, expiresAt: now + 5 * DAY },
      { id: demoId('0080', 2), kind: 'link', role: 'member', token: 'demo-team-link', createdAt: now - 6 * DAY, expiresAt: now + 8 * DAY },
    ],
  }, {
    id: GUILD_TEAM,
    name: 'Platform Guild',
    description: 'Monthly sync of everyone who touches the shared platform',
    createdAt: now - 400 * DAY,
    members: [
      { person: LENA, role: 'owner' },
      { person: PRIYA, role: 'admin' },
      { person: DEMO, role: 'member' },
      { person: TOM, role: 'member' },
    ],
    pointDescriptions: {},
    maxEndorsements: 2,
    jiraConfigured: false,
    endorsementTypes: [
      { id: demoId('00e0', 11), name: 'Helping Hand', emoji: '🤝', archived: false },
      { id: demoId('00e0', 12), name: 'Problem Solver', emoji: '🧠', archived: false },
    ],
    pastEndorsements: [],
    templates: [],
    invites: [],
  }, {
    id: ALL_HANDS_TEAM,
    name: 'Product All-Hands',
    description: 'Everyone in product and engineering, for the quarterly estimates',
    createdAt: now - 90 * DAY,
    members: [
      { person: CROWD[0], role: 'owner' },
      { person: DEMO, role: 'member' },
      ...CROWD.slice(1).map((person) => ({ person, role: 'member' })),
    ],
    pointDescriptions: {},
    maxEndorsements: 3,
    jiraConfigured: false,
    endorsementTypes: [
      { id: demoId('00e0', 21), name: 'Helping Hand', emoji: '🤝', archived: false },
    ],
    pastEndorsements: [],
    templates: [],
    invites: [],
  }];
}

export function actionItems(now) {
  return [
    { id: demoId('0060', 1), text: 'Add a retry-budget dashboard for the payment provider', board: CODES.closed, card: 'closed:actions:0', assignedTo: MARCUS.pid, doneAt: now - 9 * DAY, doneBy: MARCUS.pid, createdAt: now - 15 * DAY },
    { id: demoId('0060', 2), text: 'Write the on-call runbook for checkout incidents', board: CODES.closed, card: 'closed:actions:1', assignedTo: AISHA.pid, createdAt: now - 15 * DAY },
    { id: demoId('0060', 3), text: 'Book the design hand-off **at sprint planning**, not mid-sprint', board: CODES.closed, card: 'closed:actions:2', createdAt: now - 15 * DAY },
    { id: demoId('0060', 4), text: 'Quarantine a flaky e2e test within one working day', board: CODES.discussing, card: 'discussing:actions:0', assignedTo: JONAS.pid, createdAt: now - 25 * MINUTE },
    { id: demoId('0060', 5), text: 'Split any story over 8 points before planning', board: CODES.discussing, card: 'discussing:actions:1', assignedTo: DEMO.pid, createdAt: now - 18 * MINUTE },
  ];
}

/** Boards by stage. Card references like 'closed:actions:0' let action items point at their cards. */
export function boards(now) {
  const thinking = columns(1);
  const thinkingCards = cards(1, now, [
    [thinking.good, MARCUS, 'The **Apple Pay** rollout went out behind a flag with zero incidents 🎉'],
    [thinking.good, SOFIA, 'Pairing on the postcode validation made code review a breeze'],
    [thinking.good, JONAS, 'Standups finally fit in 15 minutes', { anonymous: true }],
    [thinking.bad, JONAS, 'The flaky checkout e2e test blocked three merges on Tuesday'],
    [thinking.bad, AISHA, 'We committed to too much again: DEMO-125 slipped to next sprint', { anonymous: true }],
    [thinking.bad, PRIYA, `Our burndown looked like this again 📉\n\n![Sprint 42 burndown](blob:${BURNDOWN_BLOB})`],
    [thinking.kudos, AISHA, `Huge thanks to ${mention(PRIYA)} for untangling the payment provider timeouts 🙌`],
    [thinking.kudos, MARCUS, `${mention(DEMO)} ran a calm, blameless incident review on Thursday`],
  ]);
  const thinkingComments = comments(1, now, [
    [thinkingCards[3], PRIYA, 'Can we quarantine it until it is fixed?'],
    [thinkingCards[3], JONAS, 'Already on it, PR coming today.'],
    [thinkingCards[3], SOFIA, `${mention(DEMO)} do you remember which CI runner it failed on?`],
    [thinkingCards[0], AISHA, '+1, the feature flag made the rollback plan trivial'],
  ]);

  const voting = columns(2);
  const votingCards = cards(2, now, [
    [voting.good, PRIYA, 'Launch-day war room was calm and well organised'],
    [voting.good, MARCUS, 'Feature flags let us ship the new checkout to 5 % first'],
    [voting.good, AISHA, 'Support had the FAQ a week before launch'],
    [voting.bad, SOFIA, 'Nobody owned the analytics events, so the funnel was broken for two days'],
    [voting.bad, JONAS, 'Load test ran against staging data that was far too small', { anonymous: true }],
    [voting.bad, MARCUS, 'Too many Slack channels: decisions got lost'],
    [voting.bad, DEMO, 'We found the iOS keyboard bug from an App Store review'],
    [voting.kudos, PRIYA, `${mention(SOFIA)} rewrote the error messages in plain language. Conversions went up!`],
  ]);
  const votingVotes = [[PRIYA, MARCUS, SOFIA], [JONAS], [AISHA, DEMO], [MARCUS, PRIYA, JONAS, AISHA], [SOFIA, DEMO], [PRIYA], [], [MARCUS, JONAS]];
  votingCards.forEach((card, i) => {
    card.votes = votingVotes[i].map((p) => p.pid);
  });

  const discussing = columns(3);
  const discussingCards = cards(3, now, [
    [discussing.good, SOFIA, 'Saved cards shipped two days early 🚀'],
    [discussing.good, PRIYA, 'New joiners got a buddy from day one'],
    [discussing.bad, JONAS, 'The flaky e2e suite cost us roughly a day of merges'],
    [discussing.bad, MARCUS, '**DEMO-119** grew from 5 to 13 points mid-sprint'],
    [discussing.bad, AISHA, 'Too many meetings on Wednesday afternoons', { anonymous: true }],
    [discussing.kudos, MARCUS, `${mention(AISHA)} jumped on the discount-code bug on a Friday evening`],
    [discussing.actions, PRIYA, 'Quarantine a flaky e2e test within one working day'],
    [discussing.actions, DEMO, 'Split any story over 8 points before planning'],
  ]);
  const discussingVotes = [[PRIYA, MARCUS, DEMO], [AISHA], [PRIYA, SOFIA, MARCUS, AISHA, DEMO], [JONAS, SOFIA, DEMO], [JONAS, MARCUS], [SOFIA, PRIYA], [], []];
  discussingCards.forEach((card, i) => {
    card.votes = discussingVotes[i].map((p) => p.pid);
  });
  const discussingComments = comments(3, now, [
    [discussingCards[2], MARCUS, 'Most failures were the payment iframe timing out on CI'],
    [discussingCards[2], PRIYA, `${mention(JONAS)} could we run it against the provider's sandbox mock instead?`],
    [discussingCards[2], JONAS, 'Yes. I will try that this sprint.'],
    [discussingCards[3], SOFIA, `${mention(DEMO)} should we have split it at planning?`],
    [discussingCards[3], DEMO, 'Agreed, the wallet edge cases were hiding in there.'],
  ]);

  const closed = columns(4);
  const closedCards = cards(4, now, [
    [closed.good, AISHA, 'Zero pages during the sale weekend'],
    [closed.good, DEMO, 'Retro action items actually got done this time'],
    [closed.bad, MARCUS, 'We could not see payment-provider retries anywhere'],
    [closed.bad, SOFIA, 'Design hand-off arrived mid-sprint, again'],
    [closed.kudos, JONAS, `Thanks ${mention(MARCUS)} for pairing on the search flicker`],
    [closed.actions, MARCUS, 'Add a retry-budget dashboard for the payment provider'],
    [closed.actions, AISHA, 'Write the on-call runbook for checkout incidents'],
    [closed.actions, SOFIA, 'Book the design hand-off **at sprint planning**, not mid-sprint'],
  ].map(([c, a, b], i) => [c, a, b, { createdAt: now - 15 * DAY - (10 - i) * MINUTE }]));
  const closedVotes = [[PRIYA, DEMO], [MARCUS, AISHA, JONAS], [SOFIA, PRIYA, DEMO, JONAS], [AISHA, MARCUS], [PRIYA], [], [], []];
  closedCards.forEach((card, i) => {
    card.votes = closedVotes[i].map((p) => p.pid);
  });

  const archived = columns(5);
  const guild = columns(6);
  const peers = [PRIYA, MARCUS, SOFIA, JONAS, AISHA];

  return [{
    stage: 'thinking',
    id: demoId('0003', 1),
    code: CODES.thinking,
    team: DEMO_TEAM,
    title: 'Sprint 42 retro',
    createdBy: PRIYA.pid,
    createdAt: now - 40 * MINUTE,
    config: { ...STANDARD_CONFIG },
    timer: { state: 'running', durationMs: 600_000, alarm: true, music: false, endsAt: now + 510_000 },
    columns: thinking.list,
    cards: thinkingCards,
    comments: thinkingComments,
    ready: [MARCUS.pid],
    peers,
    facilitator: PRIYA,
    pool: [
      [thinking.good, 'Pull requests got reviewed within a day all sprint'],
      [thinking.bad, 'Release notes were written the night before the release'],
      [thinking.good, 'The new dashboards caught the 3DS failure before customers did', true],
      [thinking.kudos, `Shout-out to ${mention(SOFIA)} for the accessibility fixes on the payment form`],
      [thinking.bad, 'Two people fixed the same bug on Wednesday 🙈'],
      [thinking.good, 'Mob session on the refund flow was fun and fast'],
      [thinking.bad, 'Staging was down for most of Monday', true],
      [thinking.kudos, `${mention(JONAS)} kept the e2e suite alive almost single-handedly`],
      [thinking.bad, 'We still do not know who owns the receipts service'],
      [thinking.good, 'Customer interviews gave us three clear ideas for onboarding'],
      [thinking.bad, 'Too much context switching between checkout and search'],
      [thinking.kudos, `${mention(DEMO)} unblocked the Apple Pay review with a two-line fix`],
      [thinking.good, 'Pairing rotation worked: everyone touched the payments code'],
      [thinking.bad, 'The sprint goal changed on day three'],
    ].map(([columnId, body, anonymous]) => ({ columnId, body, anonymous })),
    commentPool: [
      'Same here.',
      'Could we try this for one sprint and see?',
      `${mention(DEMO)} what do you think?`,
      'I can pick this up.',
      'This one deserves an action item.',
      'Agreed 💯',
    ],
  }, {
    stage: 'voting',
    id: demoId('0003', 2),
    code: CODES.voting,
    team: DEMO_TEAM,
    title: 'Checkout launch retro',
    createdBy: DEMO.pid,
    createdAt: now - 3 * HOUR,
    config: { ...STANDARD_CONFIG, voteEmoji: '❤️' },
    timer: { state: 'idle', durationMs: 300_000, alarm: true, music: false },
    columns: voting.list,
    cards: votingCards,
    comments: comments(2, now, [[votingCards[3], PRIYA, 'We need an owner per event, written down in the ticket.']]),
    ready: [SOFIA.pid, JONAS.pid],
    peers,
    facilitator: DEMO,
  }, {
    stage: 'discussing',
    id: demoId('0003', 3),
    code: CODES.discussing,
    team: DEMO_TEAM,
    title: 'Sprint 41 retro',
    createdBy: PRIYA.pid,
    createdAt: now - 26 * HOUR,
    config: { ...STANDARD_CONFIG },
    timer: { state: 'idle', durationMs: 300_000, alarm: true, music: false },
    columns: discussing.list,
    cards: discussingCards,
    comments: discussingComments,
    focus: discussingCards[2].id,
    peers,
    facilitator: PRIYA,
    endorsements: [
      { id: demoId('00e2', 1), typeId: TYPES.shipper, from: PRIYA.pid, to: DEMO.pid, createdAt: now - 20 * MINUTE },
      { id: demoId('00e2', 2), typeId: TYPES.helper, from: MARCUS.pid, to: AISHA.pid, createdAt: now - 19 * MINUTE },
      { id: demoId('00e2', 3), typeId: TYPES.solver, from: SOFIA.pid, to: JONAS.pid, createdAt: now - 12 * MINUTE },
    ],
    pool: [
      { body: 'Run the e2e suite against the payment sandbox mock' },
      { body: 'Keep Wednesday afternoons meeting-free for a trial sprint' },
    ],
    commentPool: [
      'The sandbox mock would also speed up local runs.',
      'Who wants to own this?',
      `${mention(PRIYA)} can you add it to the next planning agenda?`,
      'Let us time-box it to one sprint.',
    ],
  }, {
    stage: 'closed',
    id: demoId('0003', 4),
    code: CODES.closed,
    team: DEMO_TEAM,
    title: 'Sprint 40 retro',
    createdBy: DEMO.pid,
    createdAt: now - 15 * DAY,
    config: { ...STANDARD_CONFIG },
    timer: { state: 'idle', durationMs: 300_000, alarm: true, music: false },
    columns: closed.list,
    cards: closedCards,
    comments: comments(4, now, [[closedCards[2], PRIYA, 'Action item below 👇']]).map((c) => ({ ...c, createdAt: now - 15 * DAY })),
    endorsements: [
      { id: demoId('00e3', 1), typeId: TYPES.teacher, from: JONAS.pid, to: MARCUS.pid, createdAt: now - 15 * DAY },
      { id: demoId('00e3', 2), typeId: TYPES.helper, from: DEMO.pid, to: AISHA.pid, createdAt: now - 15 * DAY },
    ],
    peers: [],
  }, {
    stage: 'closed',
    id: demoId('0003', 5),
    code: CODES.archived,
    team: DEMO_TEAM,
    title: 'Q2 planning retro',
    createdBy: PRIYA.pid,
    createdAt: now - 110 * DAY,
    archived: true,
    archivedAt: now - 80 * DAY,
    config: { ...STANDARD_CONFIG },
    timer: { state: 'idle', durationMs: 300_000, alarm: true, music: false },
    columns: archived.list,
    cards: cards(5, now - 110 * DAY, [
      [archived.good, PRIYA, 'Roadmap is finally on one page'],
      [archived.bad, MARCUS, 'Search work keeps getting bumped'],
      [archived.actions, DEMO, 'Reserve 20 % of each sprint for search relevance'],
    ]),
    peers: [],
  }, {
    stage: 'thinking',
    id: demoId('0003', 6),
    code: CODES.guild,
    team: GUILD_TEAM,
    title: 'Platform Guild · Q3 check-in',
    createdBy: LENA.pid,
    createdAt: now - 5 * DAY,
    config: { ...STANDARD_CONFIG, stagesEnabled: false },
    timer: { state: 'idle', durationMs: 300_000, alarm: true, music: false },
    columns: guild.list.slice(0, 2).concat(guild.list[3]),
    cards: cards(6, now - 5 * DAY, [
      [guild.good, LENA, 'Shared CI templates saved every team a day of setup'],
      [guild.bad, TOM, 'Nobody reads the platform changelog'],
    ]),
    peers: [],
  }];
}

export function pokerSessions(now) {
  const round = (n) => demoId('0005', n);
  const revealedVotes = (pairs) => Object.fromEntries(pairs.map(([who, points, high]) => [who.pid, points === null
    ? { locked: true, abstained: true }
    : { points, ...(high ? { high } : {}), locked: true, abstained: false }]));
  return [{
    id: demoIdAt(now - 50 * MINUTE, 4000 + 1),
    code: CODES.teamPoker,
    team: DEMO_TEAM,
    createdBy: PRIYA.pid,
    createdAt: now - 50 * MINUTE,
    facilitator: PRIYA,
    peers: [PRIYA, MARCUS, SOFIA, JONAS, { ...AISHA, abstains: true }],
    spotlight: { holder: PRIYA.pid, roundId: round(4) },
    queue: ['DEMO-125', 'DEMO-126', 'DEMO-131', 'DEMO-132', 'DEMO-141', 'DEMO-142', 'DEMO-143', 'DEMO-151'],
    currentRoundId: round(4),
    rounds: [
      {
        id: round(1), number: 1, version: 1, state: 'revealed', active: false, pending: false, autoReveal: true,
        ticketKey: 'DEMO-118', parentKey: 'DEMO-100', winner: 5, revealedAt: now - 44 * MINUTE, revealedBy: PRIYA.pid,
        jira: { points: 5, at: now - 43 * MINUTE, by: PRIYA.pid },
        votes: revealedVotes([[DEMO, 5], [PRIYA, 5], [MARCUS, 8], [SOFIA, 3, 5], [JONAS, 5], [AISHA, null]]),
      },
      {
        id: round(2), number: 2, version: 1, state: 'revealed', active: false, pending: false, autoReveal: true,
        ticketKey: 'DEMO-119', parentKey: 'DEMO-100', winner: 8, revealedAt: now - 33 * MINUTE,
        votes: revealedVotes([[DEMO, 8], [PRIYA, 8], [MARCUS, 13], [SOFIA, 8], [JONAS, 5, 8], [AISHA, 8]]),
      },
      {
        id: round(3), number: 3, version: 1, state: 'revealed', active: false, pending: false, autoReveal: true,
        ticketKey: 'DEMO-121', parentKey: 'DEMO-100', winner: 3, revealedAt: now - 21 * MINUTE, revealedBy: DEMO.pid,
        votes: revealedVotes([[DEMO, 3], [PRIYA, 2], [MARCUS, 3], [SOFIA, 3], [JONAS, 3], [AISHA, 5]]),
      },
      {
        id: round(4), number: 4, version: 1, state: 'voting', active: true, pending: false, autoReveal: true,
        ticketKey: 'DEMO-123', parentKey: 'DEMO-100',
        votes: { [MARCUS.pid]: { points: 8, locked: true, abstained: false }, [SOFIA.pid]: { points: 5, locked: false, abstained: false } },
      },
      {
        id: round(5), number: 5, version: 1, state: 'voting', active: true, pending: true, autoReveal: true,
        ticketKey: 'DEMO-124', parentKey: 'DEMO-100', pendingOwner: PRIYA.pid, votes: {},
      },
    ],
  }, {
    id: demoIdAt(now - 14 * DAY, 4000 + 2),
    code: CODES.oldPoker,
    team: DEMO_TEAM,
    createdBy: DEMO.pid,
    createdAt: now - 14 * DAY,
    peers: [],
    currentRoundId: round(13),
    rounds: [
      ['DEMO-141', 5, 11], ['DEMO-142', 3, 12], ['DEMO-150', 2, 13],
    ].map(([ticketKey, winner, n], i) => ({
      id: round(n), number: i + 1, version: 1, state: 'revealed', active: false, pending: false, autoReveal: true, ticketKey,
      parentKey: ticketKey === 'DEMO-150' ? undefined : 'DEMO-140', winner, revealedAt: now - 14 * DAY + i * 10 * MINUTE,
      votes: revealedVotes([[DEMO, winner], [PRIYA, winner], [MARCUS, winner], [JONAS, winner]]),
    })),
  }, {
    id: demoIdAt(now - 5 * DAY, 4000 + 3),
    code: CODES.guildPoker,
    team: GUILD_TEAM,
    createdBy: LENA.pid,
    createdAt: now - 5 * DAY,
    peers: [],
    currentRoundId: round(21),
    rounds: [{ id: round(21), number: 1, version: 1, state: 'revealed', active: false, pending: false, autoReveal: true, winner: 3, revealedAt: now - 5 * DAY, votes: revealedVotes([[LENA, 3], [TOM, 2], [DEMO, 3]]) }],
  }, {
    id: demoIdAt(now - 20 * MINUTE, 4000 + 5),
    code: CODES.crowdPoker,
    team: ALL_HANDS_TEAM,
    createdBy: CROWD[0].pid,
    createdAt: now - 20 * MINUTE,
    peers: CROWD,
    currentRoundId: round(42),
    rounds: [
      {
        id: round(41), number: 1, version: 1, state: 'revealed', active: false, pending: false, autoReveal: true, ticketKey: 'Onboarding checklist', winner: 5,
        revealedAt: now - 9 * MINUTE, votes: revealedVotes([[DEMO, 5], ...CROWD.map((who, i) => [who, [3, 5, 5, 8, 5, null, 5, 3, 5, 8, 5][i]])]),
      },
      {
        id: round(42), number: 2, version: 1, state: 'voting', active: true, pending: false, autoReveal: false, ticketKey: 'Export to CSV',
        votes: Object.fromEntries(CROWD.slice(0, 5).map((who, i) => [who.pid, { points: [3, 5, 8, 5, 2][i], locked: i % 2 === 0, abstained: false }])),
      },
    ],
  }];
}
