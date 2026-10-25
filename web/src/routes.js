// Route table (frozen after U0). `module` names the lazily imported screen; build.mjs checks it
// against the import() beside it and writes the route → chunk list into manifest.json.
export const routes = [
  { path: '/', module: 'screens/landing.js', load: () => import('./screens/landing.js') },
  { path: '/login', module: 'screens/auth.js', load: () => import('./screens/auth.js') },
  { path: '/auth/*', module: 'screens/auth.js', load: () => import('./screens/auth.js') },
  { path: '/account', module: 'screens/account.js', load: () => import('./screens/account.js') },
  { path: '/mentions', module: 'screens/mentions.js', load: () => import('./screens/mentions.js') },
  { path: '/teams', module: 'screens/teams.js', load: () => import('./screens/teams.js') },
  { path: '/teams/:id', module: 'team/index.js', load: () => import('./team/index.js') },
  { path: '/teams/:id/settings', module: 'team/index.js', load: () => import('./team/index.js') },
  { path: '/teams/:tid/poker/:code', module: 'poker/index.js', load: () => import('./poker/index.js') },
  { path: '/poker/:code', module: 'poker/index.js', load: () => import('./poker/index.js') },
  { path: '/retro/:code', module: 'retro/index.js', load: () => import('./retro/index.js') },
  { path: '/invite', module: 'screens/invite.js', load: () => import('./screens/invite.js') },
  { path: '/admin/*', module: 'screens/admin.js', load: () => import('./screens/admin.js') },
];
