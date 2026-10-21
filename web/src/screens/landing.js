import { h } from '../core/h.js';
import { http } from '../core/http.js';
import { createDialog } from '../ui/dialog.js';
import { toast } from '../ui/toast.js';
import { currentMe, navigate, setMe } from './app.js';
import { card, createFrame, errorText, field, form, link, muted, row, stack } from './frame.js';

const CODE = /^[A-Za-z0-9-]{3,64}$/;

// Quick sessions need a principal: signed-out visitors pick a guest name first (§0.1 Guests).
function askGuestName() {
  return new Promise((resolve) => {
    let done = false;
    const name = field({ label: 'Your name', name: 'displayName', autocomplete: 'nickname', required: true, maxlength: 40 });
    const guestForm = form({
      fields: { displayName: name },
      submitLabel: 'Continue',
      fieldMessages: { invalid: 'Use 1–40 characters and no email address.' },
      async submit({ displayName }) {
        const guest = await http.post('/api/guest', { displayName: displayName.trim() });
        setMe({ principal: guest.principal, teams: [], unreadMentions: 0 });
        done = true;
        dialog.close();
      },
    });
    const dialog = createDialog({
      title: 'Join as a guest',
      description: 'Pick the name others will see. No account needed.',
      content: [guestForm.el, h('p', { style: { 'font-size': '0.875rem' } }, 'Have an account? ', link(`/login?return=${encodeURIComponent('/')}`, 'Sign in'))],
      onClose() {
        dialog.destroy();
        resolve(done);
      },
    });
    dialog.open();
    name.input.focus();
  });
}

async function startQuick(kind, button) {
  if (!currentMe() && !(await askGuestName())) {
    return;
  }
  button.disabled = true;
  try {
    const created = await http.post(kind === 'retro' ? '/api/retro' : '/api/poker', {});
    await navigate(`/${kind}/${encodeURIComponent(created.code)}`);
  } catch (error) {
    toast(errorText(error, { rate_limited: 'You have created many sessions recently. Try again later.' }), { variant: 'error', duration: 6000 });
  } finally {
    button.disabled = false;
  }
}

function section(kind, { title, blurb, startLabel, variant }) {
  const start = h('button', { type: 'button', class: `btn btn-${variant} btn-lg` }, startLabel);
  start.addEventListener('click', () => startQuick(kind, start));
  const code = field({
    label: `Join a ${kind === 'retro' ? 'retro' : 'poker session'} by code`,
    name: `${kind}-code`,
    autocomplete: 'off',
    spellcheck: 'false',
    required: true,
  });
  const join = form({
    fields: { code },
    submitLabel: 'Join',
    messages: { not_found: `No ${kind === 'retro' ? 'retro board' : 'poker session'} has that code.` },
    async submit(values) {
      const value = values.code.trim();
      if (!CODE.test(value)) {
        code.setError('Codes use letters, digits and dashes.');
        return;
      }
      await http.get(`/api/${kind}/by-code/${encodeURIComponent(value)}`);
      await navigate(`/${kind}/${encodeURIComponent(value)}`);
    },
  });
  return card(title, muted(blurb), h('div', { style: row() }, start), join.el);
}

export function mount(root) {
  const frame = createFrame(root, { title: 'Retros and planning poker for your team' });
  frame.main.append(
    h('p', { style: { 'font-size': '1.125rem' } }, 'Run a retrospective or point your backlog in seconds. No account required for quick sessions.'),
    h('div', { style: { ...stack('1.5rem'), 'grid-template-columns': 'repeat(auto-fit, minmax(18rem, 1fr))' } },
      section('retro', { title: 'Retro', blurb: 'Collect cards, vote, discuss and track action items.', startLabel: 'Start quick retro', variant: 'indigo' }),
      section('poker', { title: 'Planning poker', blurb: 'Vote on stories with the Neotro card table.', startLabel: 'Start quick poker', variant: 'green' })),
    h('p', { 'data-signed-in': '', style: row('0.5rem') }));
  const signedIn = frame.main.querySelector('[data-signed-in]');

  return {
    update() {
      const user = currentMe()?.user;
      signedIn.replaceChildren(...(user
        ? [muted(`Signed in as ${currentMe().principal.name}.`), link('/dashboard', 'Go to your dashboard', { class: 'btn btn-outline btn-sm' })]
        : []));
    },
    unmount() {
      frame.destroy();
    },
  };
}
