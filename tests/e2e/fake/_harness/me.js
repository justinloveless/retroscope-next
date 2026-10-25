// The boot JSON's `me` for specs: only signed-in users open pages and rooms (UX18), so a room spec's viewer is a
// user and a member of the room's team. The shape is GET /api/me's: {principal, user, teams, lastTeamId,
// unreadMentions}.

/**
 * The `me` of `who` ({pid, name}, a room's PrincipalInfo) as a member of `teams` ([{id, name, role?}]); the
 * first team is the one used last. `extra` overrides top-level members (unreadMentions, lastTeamId, …).
 */
export function memberMe(who, teams = [], extra = {}) {
  const slug = who.name.toLowerCase().replace(/[^a-z0-9]+/g, '.').replace(/^\.|\.$/g, '') || 'someone';
  return {
    principal: { id: who.pid, kind: 'user', name: who.name, ...(who.avatar ? { avatar: who.avatar } : {}) },
    user: {
      email: `${slug}@example.com`,
      emailVerified: true,
      fullName: who.name,
      theme: 'light',
      isAdmin: false,
      hasPassword: true,
    },
    teams: teams.map((t) => ({ role: 'member', openActions: 0, ...t })),
    lastTeamId: teams[0]?.id ?? null,
    unreadMentions: 0,
    ...extra,
  };
}
