import { h, replaceContent } from '../core/h.js';
import { reconcile } from '../core/list.js';
import { formatDate } from '../core/fmt.js';
import { createAvatar } from '../ui/avatar.js';
import { createButton } from '../ui/button.js';
import { createChoice } from '../ui/choice.js';
import { createMenu } from '../ui/menu.js';
import { confirmDialog } from '../ui/confirm.js';
import { icon } from '../ui/icons.js';
import { toast } from '../ui/toast.js';
import { avatarSrc, copyText, field, formDialog, input, toastError } from './util.js';

const ROLE = {
  owner: { label: 'Owner', icon: 'crown' },
  admin: { label: 'Admin', icon: 'shield' },
  member: { label: 'Member', icon: 'user' },
};

export function createMembersTab(ctx) {
  const title = h('h2', { class: 'team-section-title' });
  const list = h('ul', { class: 'team-list team-members', 'aria-label': 'Team members' });
  const inviteActions = h('div', { class: 'team-actions' });
  const invites = h('ul', { class: 'team-list team-invites', 'aria-label': 'Pending invites' });
  const invitesSection = h('section', { class: 'team-subsection', 'aria-label': 'Invites' },
    h('div', { class: 'team-toolbar' }, h('h3', { class: 'team-section-title', text: 'Invites' }), inviteActions),
    invites);
  const el = h('section', { class: 'team-section' },
    h('div', { class: 'team-toolbar' }, title), list, invitesSection);

  let pending = [];
  let invitesLoaded = false;

  function render() {
    const members = ctx.room.view?.members ?? [];
    title.textContent = `Team members (${members.length})`;
    reconcile(list, members, (m) => m.pid, (member) => {
      const row = h('li', { class: 'team-member', dataset: { pid: member.pid } });
      updateRow(row, member);
      return row;
    }, updateRow);

    const admin = ctx.isAdmin();
    invitesSection.hidden = !admin;
    if (admin) {
      if (!invitesLoaded) {
        invitesLoaded = true;
        loadInvites();
      }
      renderInviteActions();
    }
  }

  function updateRow(row, member) {
    const role = ROLE[member.role] ?? ROLE.member;
    const self = member.pid === ctx.me?.id;
    replaceContent(row,
      createAvatar({ name: member.name, src: avatarSrc(member.avatar), size: 'md' }).el,
      h('span', { class: 'team-member-name' }, member.name, self ? h('span', { class: 'team-muted', text: ' (you)' }) : null),
      h('span', { class: ['badge', `team-role-${member.role}`] }, icon(role.icon), role.label),
      memberActions(member, self));
  }

  function memberActions(member, self) {
    const owner = ctx.isOwner();
    const items = [];
    if (owner && !self) {
      if (member.role === 'member') {
        items.push({ action: 'admin', label: 'Make admin', icon: 'shield' });
      } else if (member.role === 'admin') {
        items.push({ action: 'member', label: 'Make member', icon: 'user' });
      }
      items.push({ action: 'transfer', label: 'Transfer ownership', icon: 'crown' });
    }
    const removable = !self && member.role !== 'owner' && (owner || (ctx.isAdmin() && member.role === 'member'));
    if (removable) {
      items.push('separator', { action: 'remove', label: 'Remove from team', icon: 'user-minus', destructive: true });
    }
    if (self && member.role !== 'owner') {
      items.push({ action: 'leave', label: 'Leave team', icon: 'log-out', destructive: true });
    }
    if (items[0] === 'separator') {
      items.shift();
    }
    if (items.length === 0) {
      return null;
    }
    const button = createButton({ label: `Actions for ${member.name}`, icon: 'ellipsis', iconOnly: true, variant: 'ghost', size: 'icon' });
    createMenu({ label: 'Member actions', items, onSelect: (action) => act(member, action) }).attach(button.el);
    return button.el;
  }

  async function act(member, action) {
    const path = `/members/${member.pid}`;
    try {
      if (action === 'admin' || action === 'member') {
        await ctx.api.patch(path, { role: action });
        toast(`${member.name} is now ${action === 'admin' ? 'an admin' : 'a member'}.`, { variant: 'success', duration: 3000 });
      } else if (action === 'transfer') {
        if (await confirmDialog({
          title: `Make ${member.name} the owner?`,
          message: 'You become an admin. Only the new owner can give ownership back.',
          confirmLabel: 'Transfer ownership',
          destructive: true,
        })) {
          await ctx.api.post('/transfer-ownership', { userId: member.pid });
          toast(`${member.name} now owns the team.`, { variant: 'success', duration: 3000 });
        }
      } else if (action === 'remove') {
        if (await confirmDialog({
          title: `Remove ${member.name}?`,
          message: 'They lose access to the team, its boards and its poker sessions at once.',
          confirmLabel: 'Remove',
          destructive: true,
        })) {
          await ctx.api.del(path);
          toast(`${member.name} was removed.`, { variant: 'success', duration: 3000 });
        }
      } else if (action === 'leave') {
        if (await confirmDialog({ title: 'Leave this team?', message: 'You need a new invite to come back.', confirmLabel: 'Leave team', destructive: true })) {
          await ctx.api.del(path);
          ctx.navigate('/teams');
        }
      }
    } catch (error) {
      toastError(error, 'The membership could not be changed.');
    }
  }

  function renderInviteActions() {
    if (inviteActions.childElementCount > 0) {
      return;
    }
    const verified = ctx.user?.emailVerified !== false;
    const byEmail = createButton({
      label: 'Invite by email',
      icon: 'user-plus',
      variant: 'primary',
      disabled: !verified,
      title: verified ? null : 'Verify your email address to send email invites.',
      onClick: inviteByEmail,
    });
    const byLink = createButton({ label: 'Invite link', icon: 'link', variant: 'secondary', onClick: inviteByLink });
    inviteActions.append(byLink.el, byEmail.el);
  }

  async function loadInvites() {
    try {
      pending = (await ctx.api.get('/invites')).items;
      renderInvites();
    } catch (error) {
      toastError(error, 'The invites could not be loaded.');
    }
  }

  function renderInvites() {
    if (pending.length === 0) {
      replaceContent(invites, h('li', { class: 'team-muted', text: 'No pending invites.' }));
      return;
    }
    invites.replaceChildren(...pending.map((invite) => h('li', { class: 'team-invite', dataset: { id: invite.id } },
      icon(invite.kind === 'link' ? 'link' : 'mail'),
      invite.kind === 'link'
        ? h('code', { class: 'team-invite-url', text: invite.url ?? '' })
        : h('span', { text: invite.email ?? '' }),
      h('span', { class: 'badge', text: (ROLE[invite.role] ?? ROLE.member).label }),
      invite.expiresAt ? h('span', { class: 'team-muted', text: `expires ${formatDate(invite.expiresAt)}` }) : null,
      invite.kind === 'link' && invite.url
        ? createButton({ label: 'Copy invite link', icon: 'copy', iconOnly: true, variant: 'ghost', size: 'icon', onClick: () => copyText(invite.url) }).el
        : null,
      createButton({ label: 'Revoke', variant: 'ghost', size: 'sm', onClick: () => revoke(invite) }).el)));
  }

  async function revoke(invite) {
    try {
      await ctx.api.del(`/invites/${invite.id}`);
      pending = pending.filter((i) => i.id !== invite.id);
      renderInvites();
    } catch (error) {
      toastError(error, 'The invite could not be revoked.');
    }
  }

  function inviteByEmail() {
    const email = input({ name: 'email', type: 'email', required: true, autocomplete: 'off', placeholder: 'name@example.com' });
    const role = createChoice({ label: 'Role', name: 'role', options: [{ value: 'member', label: 'Member' }, { value: 'admin', label: 'Admin' }] });
    formDialog({
      title: 'Invite by email',
      description: 'They get an email with a link to join. The invite works only for that address.',
      content: [field('Email', email), field('Role', role.el)],
      submitLabel: 'Send invite',
      async submit() {
        const invite = await ctx.api.post('/invites', { kind: 'email', email: email.value.trim(), role: role.value });
        pending = [invite, ...pending];
        renderInvites();
        toast('Invite sent.', { variant: 'success', duration: 3000 });
      },
    });
  }

  function inviteByLink() {
    const days = createChoice({
      label: 'Expires after',
      name: 'expiresInDays',
      options: [1, 7, 30].map((d) => ({ value: String(d), label: d === 1 ? '1 day' : `${d} days` })),
      value: '7',
    });
    formDialog({
      title: 'Create an invite link',
      description: 'Anyone with the link can join as a member until it expires or you revoke it.',
      content: field('Expires after', days.el),
      submitLabel: 'Create link',
      async submit() {
        const invite = await ctx.api.post('/invites', { kind: 'link', expiresInDays: Number(days.value) });
        pending = [invite, ...pending];
        renderInvites();
        if (invite.url) {
          copyText(invite.url);
        }
      },
    });
  }

  const stop = ctx.room.select((s) => s?.members, Object.is, render);
  render();

  return {
    el,
    destroy() {
      stop();
      el.remove();
    },
  };
}
