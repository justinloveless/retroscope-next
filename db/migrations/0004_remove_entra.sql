-- UX19 (docs/decisions/UX19.md): Microsoft Entra ID sign-in is gone; Google, password and the emailed sign-in link stay.
-- One release with no expand/contract split: the previous binary still runs on this schema, but only with Entra left
-- unconfigured, since its Entra sign-ins would break the tightened checks (deploy/RUNBOOK.md §11).
-- Accounts stay. A user whose only sign-in was Entra keeps their account, teams and history, and signs in with an
-- emailed sign-in link or a password reset to the same email. The audit log keeps its Entra rows as history.

-- Entra sessions are signed out, and Entra identities unlinked.
delete from sessions where auth_method = 'entra';
delete from external_identities where provider = 'entra';

alter table external_identities
  drop constraint external_identities_provider_check,
  add constraint external_identities_provider_check check (provider in ('google'));
alter table sessions
  drop constraint sessions_auth_method_check,
  add constraint sessions_auth_method_check check (auth_method in ('password','google','magic_link'));
