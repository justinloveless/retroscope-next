import { h, replaceContent } from '../core/h.js';
import { http } from '../core/http.js';
import { currentTheme } from '../core/theme.js';
import { createAvatar } from '../ui/avatar.js';
import { confirmDialog } from '../ui/confirm.js';
import { toast } from '../ui/toast.js';
import { avatarUrl, currentMe, currentUser, navigate, refreshMe, session, setMe, signOut } from './app.js';
import { ago, card, createFrame, errorText, field, form, muted, row, setThemePreference, stack } from './frame.js';

const AVATAR_PX = 128;
const AVATAR_MAX_BYTES = 256 * 1024;

const METHODS = { password: 'Password', google: 'Google', entra: 'Microsoft', magic_link: 'Email link' };

// §d: the client crops and scales avatars to 128 px WebP (PNG where the browser cannot encode WebP).
async function shrinkImage(file) {
  const bitmap = await createImageBitmap(file);
  const side = Math.min(bitmap.width, bitmap.height);
  const canvas = document.createElement('canvas');
  canvas.width = AVATAR_PX;
  canvas.height = AVATAR_PX;
  canvas.getContext('2d').drawImage(bitmap, (bitmap.width - side) / 2, (bitmap.height - side) / 2, side, side, 0, 0, AVATAR_PX, AVATAR_PX);
  bitmap.close();
  return new Promise((resolve) => canvas.toBlob(resolve, 'image/webp', 0.9));
}

function profileCard(user, me) {
  const avatar = createAvatar({ name: me.principal.name, src: avatarUrl(me.principal), size: 'lg' });
  const picker = h('input', { type: 'file', accept: 'image/png,image/jpeg,image/gif,image/webp', class: 'sr-only', id: 'avatar-file' });
  const remove = h('button', { type: 'button', class: 'btn btn-ghost btn-sm', hidden: !me.principal.avatar }, 'Remove');
  picker.addEventListener('change', async () => {
    const file = picker.files?.[0];
    picker.value = '';
    if (!file) {
      return;
    }
    try {
      const image = await shrinkImage(file);
      if (!image || image.size > AVATAR_MAX_BYTES) {
        toast('That image could not be used. Try a smaller PNG or JPEG.', { variant: 'error', duration: 6000 });
        return;
      }
      await http.put('/api/me/avatar', image);
      await refreshMe();
      toast('Avatar updated.', { variant: 'success' });
    } catch (error) {
      toast(errorText(error, { unsupported_media_type: 'That file is not a supported image.' }), { variant: 'error', duration: 6000 });
    }
  });
  remove.addEventListener('click', async () => {
    try {
      await http.del('/api/me/avatar');
      await refreshMe();
    } catch (error) {
      toast(errorText(error), { variant: 'error', duration: 6000 });
    }
  });

  const fullName = field({ label: 'Full name', name: 'fullName', autocomplete: 'name', required: true, maxlength: 100, value: user.fullName });
  const nickname = field({
    label: 'Nickname',
    name: 'nickname',
    autocomplete: 'nickname',
    maxlength: 40,
    value: user.nickname ?? '',
    hint: 'Shown in rooms instead of your full name.',
  });
  const save = form({
    fields: { fullName, nickname },
    submitLabel: 'Save profile',
    async submit(values) {
      setMe(await http.patch('/api/me', { fullName: values.fullName.trim(), nickname: values.nickname.trim() }));
      toast('Profile saved.', { variant: 'success' });
    },
  });
  return card('Profile',
    h('div', { style: row('1rem') }, avatar.el,
      h('label', {
        for: 'avatar-file',
        class: 'btn btn-outline btn-sm',
        tabindex: '0',
        role: 'button',
        on: { keydown: (e) => (e.key === 'Enter' || e.key === ' ') && picker.click() },
      }, 'Upload picture'),
      picker, remove),
    h('p', null, h('span', { text: user.email }), ' ',
      h('span', { class: 'badge', text: user.emailVerified ? 'Verified' : 'Not verified' })),
    user.emailVerified ? null : muted('Open the link in the email we sent you to verify this address.'),
    save.el);
}

// Buttons rather than the kit's native <select>: WebKit reports its select as a style-src-attr CSP
// violation (docs/decisions/U29.md).
function appearanceCard() {
  const group = h('div', { role: 'group', 'aria-label': 'Theme', style: row('0.5rem') });
  const show = () => {
    for (const button of group.children) {
      const pressed = button.dataset.theme === currentTheme();
      button.setAttribute('aria-pressed', String(pressed));
      button.className = `btn btn-sm ${pressed ? 'btn-secondary' : 'btn-outline'}`;
    }
  };
  group.append(...[['system', 'System'], ['light', 'Light'], ['dark', 'Dark']].map(([theme, label]) =>
    h('button', { type: 'button', class: 'btn btn-outline btn-sm', dataset: { theme } }, label)));
  group.addEventListener('click', async (event) => {
    const theme = event.target.closest('[data-theme]')?.dataset.theme;
    if (theme) {
      await setThemePreference(theme);
      show();
    }
  });
  show();
  return card('Appearance', muted('Theme'), group);
}

function passwordCard(user) {
  const current = field({ label: 'Current password', name: 'current', type: 'password', autocomplete: 'current-password', required: true });
  const next = field({
    label: user.hasPassword ? 'New password' : 'Password',
    name: 'new',
    type: 'password',
    autocomplete: 'new-password',
    required: true,
    hint: 'At least 10 characters.',
  });
  const fields = user.hasPassword ? { current, new: next } : { new: next };
  const change = form({
    fields,
    submitLabel: user.hasPassword ? 'Change password' : 'Add password',
    messages: {
      wrong_password: 'Your current password is incorrect.',
      reauth_required: 'For your security, sign in again with Google, Microsoft or an email link, then add a password within 10 minutes.',
    },
    async submit(values) {
      await http.put('/api/me/password', user.hasPassword ? values : { new: values.new });
      toast('Password saved. Your other sessions were signed out.', { variant: 'success' });
      await refreshMe();
    },
  });
  return card('Password', user.hasPassword ? null : muted('You sign in without a password. You can add one.'), change.el);
}

function identitiesCard() {
  const buttons = [['google', 'Link Google'], ['entra', 'Link Microsoft']].map(([provider, label]) => {
    const button = h('button', { type: 'button', class: 'btn btn-outline btn-sm' }, label);
    button.addEventListener('click', async () => {
      button.disabled = true;
      try {
        const started = await http.post(`/api/me/identities/${provider}/link`, {});
        location.assign(started.url);
      } catch (error) {
        toast(errorText(error, { already_linked: 'That account is already linked to another user.' }), { variant: 'error', duration: 6000 });
        button.disabled = false;
      }
    });
    return button;
  });
  return card('Sign-in methods', muted('Link a Google or Microsoft account to sign in with it.'), h('div', { style: row() }, buttons));
}

function sessionsCard(signal) {
  const listEl = h('ul', { style: { ...stack('0.5rem'), margin: '0', padding: '0', 'list-style': 'none' } });
  const everywhere = h('button', { type: 'button', class: 'btn btn-destructive btn-sm' }, 'Sign out everywhere');

  async function load() {
    try {
      const sessions = await http.get('/api/me/sessions', { signal });
      replaceContent(listEl, sessions.map((s) => {
        const end = h('button', { type: 'button', class: 'btn btn-ghost btn-sm', dataset: { id: s.id } }, s.current ? 'Sign out' : 'Revoke');
        return h('li', { style: { ...row('0.75rem'), 'justify-content': 'space-between' } },
          h('div', { style: stack('0.125rem') },
            h('span', { text: `${METHODS[s.method] ?? s.method}${s.current ? ' · this browser' : ''}` }),
            muted(`${s.userAgent ?? 'Unknown browser'} · active ${ago(s.lastSeenAt)}`)),
          end);
      }));
    } catch (error) {
      if (error.name !== 'AbortError') {
        replaceContent(listEl, muted(errorText(error)));
      }
    }
  }

  listEl.addEventListener('click', async (event) => {
    const button = event.target.closest('button[data-id]');
    if (!button) {
      return;
    }
    button.disabled = true;
    try {
      await http.del(`/api/me/sessions/${button.dataset.id}`);
      if (button.textContent === 'Sign out') {
        setMe(null);
        await navigate('/login');
        return;
      }
      await load();
    } catch (error) {
      toast(errorText(error), { variant: 'error', duration: 5000 });
      button.disabled = false;
    }
  });
  everywhere.addEventListener('click', async () => {
    const confirmed = await confirmDialog({
      title: 'Sign out everywhere?',
      message: 'Every browser signed in to your account will be signed out, including this one.',
      confirmLabel: 'Sign out everywhere',
      destructive: true,
    });
    if (confirmed) {
      signOut({ everywhere: true }).catch((error) => toast(errorText(error), { variant: 'error', duration: 5000 }));
    }
  });
  load();
  return card('Sessions', listEl, h('div', { style: row() }, everywhere));
}

function endorsementsCard(signal) {
  const body = h('div', null, muted('Loading…'));
  http.get('/api/me/endorsements', { signal }).then((items) => {
    const list = Array.isArray(items) ? items : items?.items ?? [];
    replaceContent(body, list.length === 0
      ? muted('No kudos yet.')
      : h('ul', { style: { ...stack('0.5rem'), margin: '0', 'padding-left': '1.25rem' } }, list.map((e) => h('li', {
        text: `${e.emoji ?? ''} ${e.typeName ?? e.name ?? 'Kudos'} from ${e.fromName ?? 'a teammate'}${e.teamName ? ` (${e.teamName})` : ''}`,
      }))));
  }, (error) => {
    if (error.name !== 'AbortError') {
      replaceContent(body, muted(errorText(error)));
    }
  });
  return card('Kudos received', body);
}

export function mount(root) {
  const frame = createFrame(root, { title: 'Account', width: '44rem' });
  const controller = new AbortController();
  const profile = h('div');
  const password = h('div');
  let shown = false;

  function renderUser() {
    const me = currentMe();
    if (me?.user) {
      replaceContent(profile, profileCard(me.user, me));
      replaceContent(password, passwordCard(me.user));
    }
  }

  const stop = session.select((s) => s.me?.user && s.me, Object.is, renderUser);

  return {
    update() {
      if (!currentUser()) {
        navigate(`/login?return=${encodeURIComponent('/account')}`, { replace: true });
        return;
      }
      if (!shown) {
        shown = true;
        renderUser();
        frame.main.append(profile, appearanceCard(), password, identitiesCard(), sessionsCard(controller.signal), endorsementsCard(controller.signal));
      }
    },
    unmount() {
      stop();
      controller.abort();
      frame.destroy();
    },
  };
}
