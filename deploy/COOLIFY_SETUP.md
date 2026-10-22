# Set up Coolify to host Retroscope Next

This guide takes you from an empty VPS to a running Retroscope Next behind HTTPS on Coolify. It covers every environment variable: what it does, and how to get or generate its value.

`deploy/RUNBOOK.md` stays the operations reference. This guide repeats only what you need for setup and links to the runbook for the rest. Items marked **UNVERIFIED** are steps or labels the research could not confirm in official docs or in the repo. Check them on your own screen as you go.

## 0. What you end up with

- A 2 vCPU / 2 GB x86_64 VPS running Coolify's control plane and Traefik.
- One Coolify **Docker Compose** resource built from `deploy/docker-compose.yml`, with three long-running services:
  - `app`: the Retroscope server, port 8080 inside the container, no host port.
  - `db`: Postgres 18, no host port.
  - `backup`: a sidecar that writes a daily `pg_dump` into the `pgbackups` volume.
- A fourth service, `importer`, is in profile `import`. It runs only during the one-time data import.
- HTTPS on `https://<your-domain>`, with a Let's Encrypt certificate issued by Coolify's Traefik.
- Images that GitHub Actions builds and pushes to GHCR. The VPS pulls them by digest and never builds anything.

Our stack is capped at 1 vCPU and about 1 GB by the `mem_limit` and `cpus` values in the compose file:

| Service | Memory | CPU |
|---|---|---|
| `app` | 200m | 0.6 |
| `db` | 288m | 0.35 |
| `backup` | 48m | 0.05 |
| `importer` (import only, runs in place of `app`) | 256m | 0.6 |

Coolify, Traefik, and the OS use the rest of the box.

### Prerequisites checklist

Collect these before you start:

- [ ] A VPS with **x86_64 (amd64)** CPU, 2 vCPU, 2 GB RAM, at least 10 GB disk, and a fresh Ubuntu LTS (20.04, 22.04, or 24.04). The images are built for `linux/amd64` only (`.github/workflows/images.yml`), so an ARM VPS cannot run them.
- [ ] Root or sudo SSH access to the VPS.
- [ ] A domain or subdomain you control, for example `retro.example.com`.
- [ ] Admin access to the GitHub repo `justinloveless/retroscope-next`.
- [ ] An account with a transactional email provider (section 8).
- [ ] Optional: a Google Cloud account for Google sign-in (section 6).
- [ ] Optional: a Microsoft Entra tenant for Microsoft sign-in (section 7).
- [ ] `openssl` on your own machine, to generate secrets.
- [ ] A password manager to store every secret you generate. Coolify and the providers show several of them only once.

## 1. Prepare the VPS and install Coolify

Skip the install steps if Coolify already runs on the box. Do the swap and firewall steps either way.

### 1.1 Add swap

`deploy/RUNBOOK.md` section 2 requires a 2 GB swapfile with `vm.swappiness=10` as a safety net against the OOM killer. Run as root:

```sh
fallocate -l 2G /swapfile && chmod 600 /swapfile && mkswap /swapfile && swapon /swapfile
echo '/swapfile none swap sw 0 0' >> /etc/fstab
echo 'vm.swappiness=10' > /etc/sysctl.d/99-swappiness.conf && sysctl --system
```

Check it: `swapon --show` lists `/swapfile`, and `sysctl vm.swappiness` prints `10`.

### 1.2 Install Coolify

Source: https://coolify.io/docs/get-started/installation

1. If Docker was installed through snap, remove it first.
2. The installer opens a registration page. The docs warn that "anyone who reaches the registration page first can become the instance admin and gain root access to your server." Avoid that by pre-seeding the admin account. Run as root, and type the password at the silent prompt so it stays out of shell history and the process list:

   ```sh
   read -rs ROOT_USER_PASSWORD   # type the admin password, press Enter
   export ROOT_USERNAME=<admin-name> ROOT_USER_EMAIL=<admin-email> ROOT_USER_PASSWORD
   curl -fsSL https://cdn.coollabs.io/coolify/install.sh | bash
   unset ROOT_USER_PASSWORD
   ```

   The docs' one-line form, `env ROOT_USER_PASSWORD='...' bash -c '...'`, puts the password in shell history. Do not use it. The installer writes `ROOT_USER_PASSWORD` in plain text into `/data/coolify/source/.env` (install.sh, the `update_env_var "ROOT_USER_PASSWORD"` call). Step 5 deals with that.

   If you do not pre-seed, run the plain `curl -fsSL https://cdn.coollabs.io/coolify/install.sh | bash` and register through the SSH tunnel in step 3 immediately after the install finishes.
3. Reach the dashboard only through an SSH tunnel until it has its own HTTPS domain. Plain `http://<vps-ip>:8000` sends the admin password, the GHCR token, and every app secret you enter later over the internet in clear text. From your own machine:

   ```sh
   ssh -L 8000:localhost:8000 root@<vps-ip>
   ```

   Then open `http://localhost:8000` and sign in, or register if you did not pre-seed the admin.
4. Optional: give the dashboard its own HTTPS domain under **Settings** > **Instance Domain** (for example `https://coolify.<your-domain>`, with its own DNS record). After that you can use the dashboard without the tunnel. Do not enter any secret over plain http.
5. After the first sign-in, change the admin password in the UI. Then delete the `ROOT_USER_PASSWORD` line from `/data/coolify/source/.env`, and copy the file to your password manager or another safe place. It holds Coolify's `APP_KEY` and other instance secrets.

### 1.3 Open the right ports

Source: https://coolify.io/docs/knowledge-base/server/firewall

- Keep 22 (SSH), 80 (HTTP, needed for certificate issuance), and 443 (HTTPS) open.
- Close ports 8000 (dashboard), 6001 (realtime), and 6002 (terminal) once you reach the dashboard through the SSH tunnel or its own HTTPS domain (section 1.2).
- Close them in the VPS provider's cloud firewall, or with `ufw-docker`. A plain `ufw deny 8000` does not work: Docker-published ports bypass ufw's INPUT rules, so the dashboard stays reachable. Coolify's firewall page points to the same two options.
- Check from your own machine that the ports are closed: `nc -vz <vps-ip> 8000` (and 6001, 6002) must fail.

### 1.4 Optional: cap Coolify's own memory

The box is exactly Coolify's minimum, and our stack shares it. You can cap Coolify's own containers in `/data/coolify/source/docker-compose.custom.yml`, which upgrades leave alone (https://coolify.io/docs/knowledge-base/custom-compose-overrides). The docs' example shape is:

```yaml
services:
  coolify:
    mem_limit: <limit>
```

The research found no recommended value. Measure first (section 15), and set a cap only if Coolify's memory grows into our slice.

## 2. Point DNS at the VPS

Sources: https://coolify.io/docs/knowledge-base/dns-configuration, https://coolify.io/docs/core/networking/dns, https://developers.cloudflare.com/dns/manage-dns-records/how-to/create-dns-records/

1. At your DNS provider, create an `A` record for the app's hostname (for example `retro`) with the VPS's public IPv4 address as the value.
   - In Cloudflare: **DNS** > **Records** > **Add record**, **Type** `A`, enter the **Name** and the IPv4 address as **Content**, set **Proxy status** and **TTL**, then **Save**.
2. Add an `AAAA` record only if IPv6 works end to end on the VPS. Coolify's docs warn that "a broken IPv6 path can cause domain or certificate requests to fail."
3. A wildcard `*` record does not cover the apex domain. If you use the root domain, add a separate record for it.
4. Check the record from your machine: `dig +short <your-domain> A` prints the VPS IP.
5. If you use Cloudflare, set the record to **DNS only** (grey cloud) until Coolify has issued the certificate. **UNVERIFIED:** Coolify's docs do not cover Let's Encrypt behind Cloudflare's proxy. DNS only is the safe choice. If you turn the proxy on later, set `CLIENT_IP_HEADER=CF-Connecting-IP` (section 5).

Your `PUBLIC_URL` is now `https://<your-domain>`. The Google and Entra redirect URIs must use exactly this host.

## 3. Prepare GitHub: images, digests, and registry access

### 3.1 Confirm the images build

`.github/workflows/images.yml` runs on every push to `main` and on every `v*` tag. It:

1. Runs the Native AOT smoke test (`./build.sh aot-smoke`).
2. Builds `ghcr.io/justinloveless/retroscope` from `deploy/Dockerfile` and `ghcr.io/justinloveless/retroscope-import` from `deploy/Dockerfile.importer`, for `linux/amd64` only.
3. Pushes both, tagged with the commit SHA only. There is no `latest` tag.
4. Prints both digests in the run summary.

To check it:

1. Open the repo on GitHub, then **Actions** > **images**.
2. Open the most recent green run on `main`. If none exists, push to `main` or run a tagged release, then wait for it.

### 3.2 Find the image digests

The run summary of the green run shows two lines:

```text
app: ghcr.io/justinloveless/retroscope@sha256:<hex>
importer: ghcr.io/justinloveless/retroscope-import@sha256:<hex>
```

Copy the hex after `sha256:` on the `app` line. That is `APP_DIGEST`. Copy the hex on the `importer` line. That is `IMPORTER_DIGEST`. Do not include the `sha256:` prefix, because the compose file adds it.

### 3.3 Create a token that can pull the private images

The repo is private. Packages that Actions pushes with `GITHUB_TOKEN` are linked to the repo and "automatically inherit the access permissions of the linked repository", and "when you first publish a package, the default visibility is private" (https://docs.github.com/en/packages/learn-github-packages/about-permissions-for-github-packages). The VPS therefore has to log in to GHCR.

GitHub Packages "only supports authentication using a personal access token (classic)" (https://docs.github.com/en/packages/working-with-a-github-packages-registry/working-with-the-container-registry). Fine-grained tokens are not listed as supported.

1. On GitHub, click your profile picture, then **Settings** > **Developer settings** > **Personal access tokens** > **Tokens (classic)** > **Generate new token** > **Generate new token (classic)** (https://docs.github.com/en/authentication/keeping-your-account-and-data-secure/managing-your-personal-access-tokens).
2. In **Note**, name it, for example `coolify-ghcr-pull`.
3. Set an **Expiration** and put a reminder in your calendar to rotate it before then.
4. Select **only** the `read:packages` scope.
5. Click **Generate token** and copy it into your password manager.

GitHub warns that a classic token with `read:packages` can read every package your account can reach, not only this repo's. Store it only in Coolify's Registries login or the server's Docker config. Never put it in the repo or in a URL.

Do not make the packages public. Anonymous pulls would work, but the research recommends against it for this app.

### 3.4 Add the GHCR login to the Coolify server

Source: https://coolify.io/docs/knowledge-base/docker/registry

1. In Coolify, open **Registries** in the main sidebar, or **Servers** > **localhost** > **Registries**.
2. Click **Log in** and choose the **GHCR** preset.
3. Enter your GitHub username and the token from 3.3.

"Coolify runs `docker login` for the configured server user." The credentials land in that user's `~/.docker/config.json`, which is "encoded, not encrypted". Coolify does not store the token in its database.

If your Coolify version has no **Registries** page, log in over SSH as the user Coolify connects with. On `localhost` that is normally root (**UNVERIFIED**):

```sh
read -rs REGISTRY_TOKEN   # paste the token, press Enter
printf '%s' "$REGISTRY_TOKEN" | docker login ghcr.io --username <your-github-username> --password-stdin
unset REGISTRY_TOKEN
```

Check it: `docker pull ghcr.io/justinloveless/retroscope@sha256:<APP_DIGEST>` succeeds on the VPS.

## 4. Create the Coolify project and the compose resource

Source: https://coolify.io/docs/get-started/concepts. Coolify organises things as Project, then Environment, then Resource.

### 4.1 Create the project

1. In Coolify, create a project, for example `retroscope`.
2. Open its environment. Use `production` if Coolify created one. **UNVERIFIED:** whether a new project gets a `production` environment automatically. If not, create one.

### 4.2 Give Coolify read access to the private repo

The recommended resource type is a Git-based compose application, so the compose file stays in sync with the repo. A private repo needs a deploy key or a GitHub App. The deploy key is the simpler, read-only option (https://coolify.io/docs/applications/sources/github/deploy-key).

1. In Coolify, go to **Keys & Tokens** > **Private Keys** > **New private key** and choose **Generate ED25519**. Save it.
2. Click the key's edit icon and copy the **public** key.
3. On GitHub, open `https://github.com/justinloveless/retroscope-next/settings/keys`, click **Add deploy key**, paste the public key, and leave **Allow write access** off.

Alternative: **+ New** > **Private Repository (with GitHub App)**. Coolify recommends the GitHub App "for the most complete integration" (https://coolify.io/docs/applications/sources/github/app). Either works for this stack.

### 4.3 Create the resource

Sources: https://coolify.io/docs/applications/build-packs/docker-compose, https://coolify.io/docs/knowledge-base/docker/compose

1. In the project's environment, click **+ New** > **Private Repository (with Deploy Key)**.
2. Choose the server **localhost** and the key from 4.2.
3. Enter the SSH URL `git@github.com:justinloveless/retroscope-next.git`. Do not use the HTTPS URL.
4. Choose the branch `main`.
5. Under **General** > **Build pipeline**, set:
   - **Build strategy**: `Compose`
   - **Base Directory**: `/`
   - **Docker Compose Location**: `/deploy/docker-compose.yml`

   Coolify joins the two paths, and the file extension must match exactly (`.yml`).
6. Do not switch on **Raw** compose deployment (**Advanced** > **Docker compose** > **Compose deployment**). Raw mode deploys the file as-is, and you would have to write the Traefik labels and networks yourself. Our file has none.
7. Do not deploy yet. The environment variables come first (section 5).

The compose file has only `image:` entries and no `build:` sections, so Coolify pulls and never builds. A test in `tests/Retroscope.Integration.Tests/Deploy` enforces that.

A fallback is **Docker Compose Empty**, where you paste the YAML into Coolify. It has "no Git source, branch, or pull-request events", so the pasted file drifts from the repo. Use it only if the Git option fails.

**Auto deploy on push:** a push to `main` also starts a new image build, but `APP_DIGEST` does not change until you set it. A Git-triggered redeploy would only restart the same image. In the resource, open **Advanced** > **Deployment** > **Auto deploy** and choose **Manual deployments only** (the other option is **Deploy on push (webhooks)**; source: Coolify `resources/views/livewire/project/application/advanced.blade.php`). Do not add a GitHub webhook for this resource. A deploy-key resource receives push events only through a webhook you add yourself. Deploy by hand or by API (section 11).

### 4.4 Attach the domain

Source: https://coolify.io/docs/core/networking/domains

1. In the resource, open **Domains** > **Add domain**.
2. Set **Service** to `app`, **Protocol** to `https`, the hostname to `<your-domain>`, and **Port** to `8080`.
   - In versions with a single text field instead of a form, enter `https://<your-domain>:8080` for the `app` service. **UNVERIFIED:** which Coolify version introduced the form.
3. Leave the domains of `db`, `backup`, and `importer` empty.

The port only tells Traefik which container port to route to. It does not publish a host port. Do not add a `ports:` entry for any service.

Coolify's Traefik issues the certificate with Let's Encrypt over the HTTP challenge on ports 80 and 443. The hostname must already resolve to the VPS (section 2).

### 4.5 Turn off gzip for this resource

`deploy/RUNBOOK.md` section 12 requires Traefik compression to stay off: the app compresses static assets itself and deliberately leaves dynamic responses uncompressed (BREACH risk). Coolify adds a `gzip` middleware by default.

1. In the resource, open **Advanced** and find the **Proxy** section.
2. Set **Gzip compression** to **Disabled** (API field `is_gzip_enabled`). It defaults to **Enabled**, and Coolify applies it to compose services too.

The control shows only while Coolify manages the container labels (the default). If the Proxy section says labels are managed manually, switch label management back to Coolify first. Source: Coolify v4.x source (`resources/views/livewire/project/application/advanced.blade.php`, `app/Livewire/Project/Application/Advanced.php`, `bootstrap/helpers/parsers.php`). The docs pages do not describe this control, so confirm the label on your screen.

You check the result after the first deploy (section 10).

### 4.6 WebSockets

The app uses WebSockets for live rooms. **UNVERIFIED** in Coolify's docs: no page covers WebSockets. Traefik normally passes Upgrade requests through, and the local rehearsal behind Traefik v3.7.16 confirmed it (`deploy/RUNBOOK.md` section 14). You prove it on the VPS with the drill in section 15. There is nothing to configure.

## 5. Set the environment variables

Source: https://coolify.io/docs/knowledge-base/environment-variables

Coolify reads every `${VAR}` in the compose file and lists it under the resource's **Environment Variables**:

- `${VAR:?}` marks a required variable. "Coolify blocks deployment while its value is empty."
- `${VAR:-x}` pre-fills the value `x`.

You can enter values in the **Normal view** (Add, then Name and Value) or paste `.env`-style text in the **Developer view**. Saving the Developer view "creates, updates, and removes entries to match its contents", so paste the complete list there, never part of it.

The VPS never builds, so for every secret set **Build time** to "Not available during build" and leave **Runtime** available.

Changes to runtime variables need a **Restart**. A digest change needs a **Redeploy** so the new image is pulled.

Values must not contain `;`, because `DB_APP_PASSWORD` and `DB_OWNER_PASSWORD` sit inside Npgsql connection strings in the compose file. Avoid `$` too, because of compose interpolation. The generator commands below produce safe values.

### 5.1 All variables at a glance

Required means the compose file marks it `${VAR:?}`. Defaults come from the compose file.

| Variable | Required | Example format | What it does |
|---|---|---|---|
| `GHCR_OWNER` | yes | `justinloveless` | Lower-case GitHub owner in `ghcr.io/<owner>/…`. |
| `APP_DIGEST` | yes | `<64 hex chars>` | App image digest, without `sha256:`. |
| `IMPORTER_DIGEST` | no, but set it | `<64 hex chars>` | Importer image digest, without `sha256:`. |
| `PUBLIC_URL` | yes | `https://retro.example.com` | Public origin. Drives CSP, CSRF and WebSocket Origin checks, mail links, OIDC redirect URIs, HSTS, and the SMTP EHLO name. |
| `DB_OWNER_PASSWORD` | yes | `<hex string>` | Password of the Postgres owner role `retroscope_owner`. |
| `DB_APP_PASSWORD` | yes | `<hex string>` | Password the migrator sets on the `retroscope_app` role the app uses. |
| `APP_KEYS` | yes | `k1:<base64 of 32 bytes>` | Keyring that seals Jira credentials, link-invite tokens, and the OIDC state cookie, and keys the audit-log email HMAC. |
| `ANON_TAG_KEY` | yes | `<base64 of 32 bytes>` | Tags anonymous cards and comments so their authors can still edit them. Never rotate it unless it leaks. |
| `SMTP_HOST` | yes | `smtp.resend.com` | Mail submission host. |
| `SMTP_PORT` | no (`587`) | `587` | Mail submission port, 1-65535. |
| `SMTP_SECURITY` | no (empty) | `starttls`, `tls`, or `none` | Empty means implicit TLS on 465 and STARTTLS on every other port. |
| `SMTP_USER` | no (empty) | provider-specific | SMTP login. Set both user and password, or neither. |
| `SMTP_PASSWORD` | no (empty) | provider-specific | SMTP password or API key. |
| `SMTP_FROM` | yes | `Retroscope <noreply@example.com>` | Sender address, used for both the envelope and the header. |
| `TRUSTED_PROXY_CIDRS` | yes | `10.0.5.0/24` | Subnet Traefik reaches the app from. Only peers inside it may set the client IP. |
| `CLIENT_IP_HEADER` | no (empty) | `CF-Connecting-IP` | Set only when Cloudflare proxies in front of Traefik. |
| `GOOGLE_CLIENT_ID` | no (empty) | `<id>.apps.googleusercontent.com` | Google sign-in. Set both Google values, or neither. |
| `GOOGLE_CLIENT_SECRET` | no (empty) | from Google | Google sign-in secret. |
| `ENTRA_CLIENT_ID` | no (empty) | `<GUID>` | Entra sign-in. Set both Entra values, or neither. |
| `ENTRA_CLIENT_SECRET` | no (empty) | from Entra | Entra sign-in secret. |
| `ENTRA_TENANT` | no (`organizations`) | `organizations` or `<tenant GUID>` | Authority tenant segment. ASCII letters, digits, `-`, and `.` only. |
| `ENTRA_ALLOWED_TENANTS` | no (empty) | `<tid1>,<tid2>` | Tenant IDs allowed to sign in. **Empty disables Entra.** |
| `ENTRA_TENANT_DOMAINS` | no (empty) | `<tid>=example.com\|example.org` | Email domains trusted per tenant when the token lacks `xms_edov`. |
| `JIRA_HOST_ALLOWLIST` | no (empty) | `jira.example.com` | Extra exact Jira host names beyond `*.atlassian.net`. |
| `BLOB_BYTES_MAX` | no (`2147483648`) | `2147483648` | Ceiling on all board images together, in bytes. |

Fixed values in the compose file that you do not set: `ASPNETCORE_URLS`, `DATABASE_URL`, `DATABASE_OWNER_URL`, `DOTNET_GCHeapHardLimit`, `DOTNET_gcServer`, `DOTNET_gcConcurrent`, and `DOTNET_GCConserveMemory`. Leave them alone.

The app also reads two variables that the compose file does not set. Leave them unset:

- `WEB_ROOT` (`src/Retroscope.Server/Hosting/HostingOptions.cs`): defaults to the `wwwroot` folder next to the binary.
- `HEALTHCHECK_PORT` (`src/Retroscope.Server/Hosting/Health.cs`): defaults to `8080`. It must match the port in `ASPNETCORE_URLS`, or the container healthcheck fails.

Optional variables that are **not** in the compose file. Add them to the `app` service's environment in the compose file only if you need them:

- `LEADER_ACQUIRE_SECONDS` (default 60): how long a starting app waits for the leader lease before it exits.
- `Logging__Console__FormatterName=simple`: plain-text logs instead of JSON.
- `Logging__LogLevel__Default=Warning`: quieter logs.

Never set `GOOGLE_AUTHORIZE_URL`, `GOOGLE_TOKEN_URL`, or `ENTRA_AUTHORITY_URL` in production. They exist for tests.

The `IMPORT_*` variables are only for the data import (section 14).

### 5.2 `GHCR_OWNER`

Set it to `justinloveless`. It must be lower case, because `images.yml` lowercases the owner when it names the images.

### 5.3 `APP_DIGEST` and `IMPORTER_DIGEST`

Copy them from the images run summary (section 3.2). Paste only the hex after `sha256:`.

Set `IMPORTER_DIGEST` too, even though the compose file does not require it. The `importer` service is in profile `import` and should not start, but the Coolify docs do not say whether Coolify honours `profiles` (**UNVERIFIED**). If Coolify parses or pulls the importer with an empty digest, the image reference `…/retroscope-import@sha256:` is invalid and the deploy fails. Setting it avoids that.

### 5.4 `PUBLIC_URL`

Set it to `https://<your-domain>`, with no trailing path, no query, and no `user@` part. The app refuses to start if the value is not an absolute http or https origin with path `/` only (`src/Retroscope.Server/Hosting/HostingOptions.cs`).

Use `https`. HSTS is sent only when `PUBLIC_URL` is https, and the session cookie `__Host-rs_sid` is `Secure`, so sign-in does not work over plain http.

### 5.5 `DB_OWNER_PASSWORD` and `DB_APP_PASSWORD`

Generate two different values on your own machine:

```sh
openssl rand -hex 32   # DB_OWNER_PASSWORD
openssl rand -hex 32   # DB_APP_PASSWORD
```

The repo gives no generator command. Hex is a recommendation: it avoids `;`, `=`, `$`, `+`, and `/`, which could break the connection strings or compose interpolation.

How each one behaves:

- `DB_OWNER_PASSWORD` becomes `POSTGRES_PASSWORD` when Postgres initialises the empty `pgdata` volume. After that first start, changing the variable does **not** change the role's password. The Postgres image only reads it at initialisation (standard image behaviour, not verified in the repo). The runbook has no rotation procedure for it, so choose it once and keep it.
- `DB_APP_PASSWORD` is set on the `retroscope_app` role by the migrator on every app start (`EnsureRolesAsync` in `src/Retroscope.Server/Data/Migrator.cs`). Changing it and redeploying works.

### 5.6 `APP_KEYS`

Generate a key:

```sh
openssl rand -base64 32
```

Set `APP_KEYS` to `k1:<that output>`. Rules from `src/Retroscope.Core/Security/Keyring.cs`:

- Entries are comma-separated, newest first.
- Each id is 1-32 characters of `A-Za-z0-9_-`, with no duplicates.
- Each key must decode to exactly 32 bytes. `openssl rand -base64 32` gives exactly that.

The keyring may be parsed lazily, so a malformed value might fail only on first use (a sign-in, a Jira call) rather than at boot. Copy it carefully. To rotate it later, see section 13.

### 5.7 `ANON_TAG_KEY`

Generate it the same way, as a separate value:

```sh
openssl rand -base64 32
```

Paste the output as-is, with no id prefix. **Never rotate this key** unless it leaks. A new key orphans every existing anonymous card from its author.

The app reads this key lazily (`Lazy<AnonTagger>` in `src/Retroscope.Server/Realtime/Rooms/RetroRoom.cs`). A malformed value passes boot and `/healthz`, then fails on the first anonymous card or authorship check on a retro board. The smoke test in section 10.4 covers it. Copy it carefully.

If you will import data from the old app (section 14), the importer must use these exact `APP_KEYS` and `ANON_TAG_KEY` values. Store them carefully.

### 5.8 SMTP variables

Set these from your mail provider (section 8):

- `SMTP_HOST`: the provider's SMTP host.
- `SMTP_PORT`: 587 unless your VPS provider blocks it (section 8.1).
- `SMTP_SECURITY`: leave empty for 587 (STARTTLS) or 465 (implicit TLS). Set `tls` for an implicit-TLS port other than 465, such as 2465. `none` is for local Mailpit only.
- `SMTP_USER` and `SMTP_PASSWORD`: the provider's SMTP credentials. Set both, or neither.
- `SMTP_FROM`: an address on your verified sending domain, either `noreply@<domain>` or `Retroscope <noreply@<domain>>`. The display name must not contain `"`, `<`, `>`, or `\`.

The app validates the server certificate against the system trust store, and STARTTLS fails if the server does not offer it.

### 5.9 `TRUSTED_PROXY_CIDRS` and `CLIENT_IP_HEADER`

`TRUSTED_PROXY_CIDRS` is the subnet Traefik reaches the app from. You can only find the exact subnet after the first deploy creates the network, but the compose file requires a value before that. Section 9 walks through it:

1. For the first deploy, use the Docker address pool that the Coolify installer configured as a temporary placeholder. On the VPS, run:

   ```sh
   jq '."default-address-pools"' /etc/docker/daemon.json
   ```

   Use the `base` it prints, normally `10.0.0.0/8`. The current installer (https://cdn.coollabs.io/coolify/install.sh) writes `"default-address-pools": [{"base": "10.0.0.0/8", "size": 24}]` unless `DOCKER_ADDRESS_POOL_BASE` was set or a pool already existed, so Coolify's networks are /24s inside `10.0.0.0/8`. Do not use `172.16.0.0/12,192.168.0.0/16`: on a Coolify box that misses Traefik, and every user then shares one rate limit.
2. After the first deploy, narrow it to the real /24 subnet (section 9).

Leave `CLIENT_IP_HEADER` empty unless Cloudflare proxies your domain (orange cloud). Then set `CF-Connecting-IP`.

### 5.10 Sign-in variables

`GOOGLE_CLIENT_ID` and `GOOGLE_CLIENT_SECRET` come from section 6. The `ENTRA_*` variables come from section 7. A provider with no credentials does not appear on the sign-in screen, so you can deploy first with email and password only and add providers later.

### 5.11 `JIRA_HOST_ALLOWLIST` and `BLOB_BYTES_MAX`

- `JIRA_HOST_ALLOWLIST`: leave it empty if your teams use Jira Cloud (`*.atlassian.net`). For self-hosted Jira, list the exact host names, comma-separated. A malformed entry stops the app.
- `BLOB_BYTES_MAX`: leave the default (2 GiB) unless disk space is short. It must be a positive whole number of bytes.

## 6. Set up Google sign-in (optional)

Sources: https://support.google.com/cloud/answer/15549257, https://support.google.com/cloud/answer/15544987, https://support.google.com/cloud/answer/15549945, https://support.google.com/cloud/answer/15549049

The app requests only `openid email profile`, uses the authorization code flow with PKCE, and trusts a Google email only when `email_verified` is true.

1. Open the Google Auth Platform at https://console.developers.google.com/auth/overview and click **GET STARTED**. If no project is selected, create one.
2. Under **App Information**, enter:
   - **App Name**, for example `Retroscope`. Do not make it look like Google's or another company's brand.
   - **User support email**.
   - **Contact Information**: where Google sends project notifications.
3. Under **Audience**, choose **External**. **Internal** works only for members of a Google Cloud Organization. Anyone else gets an `org_internal` error.
4. Under **Branding**:
   - Add your domain under **Authorized domains**.
   - Add a homepage and a privacy policy on that domain. Google requires both for an external app in production.
5. Under **Data Access**, the app needs only `openid`, `email`, and `profile`. Do not add other scopes.
6. Go to **Clients**, then **CREATE CLIENT**, and set **Application type** to **Web application**.
7. Under **Authorized redirect URIs**, add exactly `https://<your-domain>/auth/oidc/google/callback`. Leave **Authorized JavaScript origins** empty.
8. Click **CREATE**.
9. Copy the **Client secret** now. Google shows the full secret only once. Copy the **Client ID** too.
10. In Coolify, set `GOOGLE_CLIENT_ID` and `GOOGLE_CLIENT_SECRET`, then restart the resource.
11. Back in **Audience**, click **Publish app** to move from Testing to In production.

Notes:

- Redirect URI changes can take from 5 minutes to a few hours to apply. A mismatch shows as `redirect_uri_mismatch`.
- In Testing, Google limits most apps to 100 listed test users with consent expiring after 7 days. Google's page says that limit does not apply to apps that request only `openid`, `email`, and `profile`, which is this app's case.
- **UNVERIFIED:** whether publishing an app with only these non-sensitive scopes needs Google's verification. The pages say verification is needed to show the app's name and logo and for sensitive scopes.
- To replace the secret later, use **Add Secret** on the client page (two at most), switch the app over, then delete the old one.

## 7. Set up Microsoft Entra sign-in (optional)

Sources: https://learn.microsoft.com/en-us/entra/identity-platform/quickstart-register-app, https://learn.microsoft.com/en-us/entra/identity-platform/how-to-add-redirect-uri, https://learn.microsoft.com/en-us/entra/identity-platform/how-to-add-credentials, https://learn.microsoft.com/en-us/entra/identity-platform/optional-claims, https://learn.microsoft.com/en-us/entra/identity-platform/optional-claims-reference

How the app checks an Entra sign-in (`src/Retroscope.Server/Auth/Oidc.cs`):

- Entra is enabled only when `ENTRA_ALLOWED_TENANTS` is non-empty.
- The token's `tid` must be in `ENTRA_ALLOWED_TENANTS`. Otherwise sign-in fails with `oidc_tenant`.
- The issuer must be `https://login.microsoftonline.com/{tid}/v2.0`.
- The email is trusted only if `xms_edov` is true, or if its domain is listed for that tenant in `ENTRA_TENANT_DOMAINS`.

Steps:

1. Sign in to https://entra.microsoft.com as at least an Application Developer. If you have several tenants, switch with the **Settings** icon.
2. Go to **Entra ID** > **App registrations** > **New registration** and enter a **Name**.
3. Pick **Supported account types**:
   - For your organisation only: choose **Single tenant only**. You will set `ENTRA_TENANT=<tenant ID>` and `ENTRA_ALLOWED_TENANTS=<same tenant ID>`.
   - For several organisations: choose **Multiple Entra ID tenants**. Keep `ENTRA_TENANT=organizations` and list every permitted tenant ID in `ENTRA_ALLOWED_TENANTS`.
   - Do not choose any option that includes personal Microsoft accounts. Use `organizations` or a tenant ID for `ENTRA_TENANT`. Personal-account tenants are rejected unless listed in `ENTRA_ALLOWED_TENANTS`.
4. Click **Register**.
5. On the **Overview** page, copy the **Application (client) ID** into `ENTRA_CLIENT_ID`. Copy the tenant ID too. **UNVERIFIED:** its label, normally **Directory (tenant) ID**.
6. Add the redirect URI: **Manage** > **Authentication**, open the **Redirect URI configuration** tab, select **Add Redirect URI**, choose the **Web** tile, enter `https://<your-domain>/auth/oidc/entra/callback`, then click **Configure**.
7. Create the secret: **Certificates & secrets** > **Client secrets** > **New client secret**.
   - Enter a description and an expiry. The maximum is 24 months, and Microsoft recommends less than 12.
   - Click **Add**, then copy the **Value**, not the **Secret ID**, into `ENTRA_CLIENT_SECRET`. Microsoft never shows it again.
   - Put a rotation reminder in your calendar before the expiry. Microsoft prefers certificates over secrets in production, but the app reads only a secret.
8. Add optional claims: **Manage** > **Token configuration** > **Add optional claim**, choose token type **ID**, select `email` and `xms_edov`, then click **Add**.
   - `xms_edov` is issued only together with `email`.
   - **UNVERIFIED:** that `xms_edov` appears in the picker. If it does not, open **Manage** > **Manifest** and add `{"name":"xms_edov","essential":false}` (and `email` the same way, if missing) under `optionalClaims.idToken`.
   - **UNVERIFIED:** a prompt to turn on the Microsoft Graph email permission often appears when you add `email`. If it appears, accept it.
9. Optional: under **API permissions** > **Add a permission** > **Microsoft Graph** > **Delegated permissions**, tick `openid`, `email`, and `profile`. The app requests them dynamically anyway, and they need no admin consent. **UNVERIFIED:** exact labels.
10. In Coolify, set:
    - `ENTRA_CLIENT_ID` and `ENTRA_CLIENT_SECRET`.
    - `ENTRA_TENANT`: `organizations`, or your tenant ID for a single-tenant app.
    - `ENTRA_ALLOWED_TENANTS`: the tenant IDs, comma-separated, lower case.
    - Optional `ENTRA_TENANT_DOMAINS`: `<tid>=example.com|example.org`, and `,` between tenants. A malformed entry stops the app.
11. Restart the resource.

## 8. Set up email (SMTP) and its DNS records

The app sends sign-up verification, magic-link, and password-reset mail over SMTP. Without working SMTP, nobody can verify an email address.

### 8.1 Check which ports your VPS provider blocks

- Hetzner Cloud blocks 25 and 465 by default. Port 587 "is not blocked" (https://docs.hetzner.com/cloud/servers/faq/).
- DigitalOcean blocks 25, 465, and 587 on Droplets and recommends a third-party email service (https://docs.digitalocean.com/support/why-is-smtp-blocked/).
- Other providers: check their docs.

If 587 and 465 are blocked, use an alternate port that your mail provider offers (table in section 8.2): Resend and SES offer 2587 (STARTTLS) and 2465 (TLS); Postmark and Mailgun offer 2525 (STARTTLS). Leave `SMTP_SECURITY` empty for a STARTTLS port, and set `SMTP_SECURITY=tls` for 2465.

### 8.2 Pick a provider and get credentials

Any SMTP provider works. These are the ones researched. Use a subdomain such as `mail.<your-domain>` for sending if the provider recommends it.

| Provider | `SMTP_HOST` | Ports | `SMTP_USER` | `SMTP_PASSWORD` |
|---|---|---|---|---|
| Resend | `smtp.resend.com` | STARTTLS 25, 587, 2587. Implicit TLS 465, 2465. | the literal `resend` | a Resend API key |
| Postmark | `smtp.postmarkapp.com` | STARTTLS 25, 2525, 587 | Server API Token, or the SMTP token's Access Key | the same Server API Token, or the SMTP token's Secret Key |
| Amazon SES | `email-smtp.<region>.amazonaws.com` | STARTTLS 25, 587, 2587. TLS 465, 2465. | SMTP username from SES | SMTP password from SES |
| Mailgun | `smtp.mailgun.org` (US) or `smtp.eu.mailgun.org` (EU) | STARTTLS 25, 587, 2525. TLS 465. | per-domain SMTP login | per-domain SMTP password |

Provider steps:

- **Resend** (https://resend.com/docs/send-with-smtp, https://resend.com/docs/add-a-domain):
  1. Go to **Domains** > **Add Domain**. Resend recommends a subdomain, for example `notifications.<your-domain>`.
  2. Pick a region.
  3. Copy the TXT and MX or CNAME records from the **Records** tab to your DNS exactly. Turn off Cloudflare proxying on every CNAME.
  4. Wait for verification, usually about 15 minutes and up to 72 hours. Use **Restart verification** if it stalls.
  5. Create an API key and use it as `SMTP_PASSWORD`.
- **Postmark** (https://postmarkapp.com/developer/user-guide/send-email-with-smtp, https://postmarkapp.com/support/article/1046-how-do-i-verify-a-domain):
  1. Turn on SMTP in the server's **Settings**.
  2. Go to **Sender Signatures** > **DNS Settings**.
  3. Add the DKIM TXT record it shows.
  4. Add a Return-Path CNAME with host `pm_bounces` and value `pm.mtasv.net`.
  5. Click **Verify**. Allow up to 48 hours.
  6. Use the Server API Token as both user and password. Mail without an `X-PM-MESSAGE-STREAM` header goes to the default transactional stream. An SMTP Token's secret is shown only once.
- **Amazon SES** (https://docs.aws.amazon.com/ses/latest/dg/smtp-credentials.html, https://docs.aws.amazon.com/ses/latest/dg/creating-identities.html, https://docs.aws.amazon.com/ses/latest/dg/request-production-access.html):
  1. Go to **Configuration** > **Identities** > **Create identity** > **Domain**, and use Easy DKIM (2048-bit).
  2. Publish the 3 CNAME records it gives you.
  3. Go to **SMTP settings** > **Create SMTP Credentials**, which opens IAM. Click **Create user**, then **Show** or **Download .csv file**. You cannot view them again. The SMTP password is not your AWS secret access key, and it is specific to one region.
  4. New accounts are in the sandbox: they can send only to verified addresses, 200 messages per 24 hours. To leave it, go to **Account dashboard** > **View Get set up page** > **Request production access**, and choose **Transactional**. The first response comes within 96 hours.
- **Mailgun** (https://documentation.mailgun.com/docs/mailgun/user-manual/sending-messages/send-smtp): credentials are per domain under **Sending** > **Domain Settings** > **SMTP Credentials**. A domain's region (US or EU) is fixed when you create it. **UNVERIFIED:** the username format (often `postmaster@<domain>`) and whether the password is shown only once.
- **Brevo:** the official page could not be fetched. **UNVERIFIED** from third-party sources: host `smtp-relay.brevo.com`, ports 587 and 2525 (STARTTLS) or 465 (TLS), password is an SMTP key from **Settings** > **SMTP & API** > **SMTP Keys**, username is the SMTP login on that page.

### 8.3 Add DMARC

After the domain verifies, add a TXT record at `_dmarc.<sending-domain>`:

```text
v=DMARC1; p=none; rua=mailto:<reports-address>
```

Start with `p=none`. Move to `p=quarantine`, then `p=reject`, once reports look clean. Keep alignment relaxed (no `aspf=s`) if the MAIL FROM is a subdomain. The repo states no SPF, DKIM, or DMARC requirements. They come from the provider.

### 8.4 Set the variables

Set `SMTP_HOST`, `SMTP_PORT`, `SMTP_SECURITY`, `SMTP_USER`, `SMTP_PASSWORD`, and `SMTP_FROM` as in section 5.8. `SMTP_FROM` must use the verified domain.

## 9. Set the trusted proxy subnet

The app reads the client IP from `X-Forwarded-For` only when the request comes from inside `TRUSTED_PROXY_CIDRS`, and only the rightmost hop. Per-IP limits (10 logins a minute, 5 signups an hour) and audit-log address prefixes depend on it. A wrong value puts every user behind one shared limit.

Coolify gives each compose resource its own network, and the proxy joins "the `coolify` network and the resource-specific networks" (https://coolify.io/docs/core/networking-in-coolify). The right value is the subnet of the network that both `coolify-proxy` and the `app` container are on.

Until you do this, `TRUSTED_PROXY_CIDRS` holds the `10.0.0.0/8` placeholder from section 5.9. After the first deploy (section 10), on the VPS:

1. Find the app container's name. Coolify adds a suffix, so search for it:

   ```sh
   docker ps --format '{{.Names}}' | grep -i app
   ```

2. List the app's networks:

   ```sh
   docker inspect <app-container> -f '{{range $k, $v := .NetworkSettings.Networks}}{{$k}} {{end}}'
   ```

3. List the proxy's networks and pick the one that appears in both lists:

   ```sh
   docker inspect coolify-proxy -f '{{range $k, $v := .NetworkSettings.Networks}}{{$k}} {{end}}'
   ```

4. Get that network's subnet:

   ```sh
   docker network inspect <shared-network> -f '{{range .IPAM.Config}}{{.Subnet}}{{end}}'
   ```

5. In Coolify, set `TRUSTED_PROXY_CIDRS` to that subnet and restart the resource.
6. Check: sign in, then run

   ```sh
   docker exec <db-container> psql -U retroscope_owner -d retroscope \
     -c "select at, action, meta->>'ip' from audit_log where action in ('auth.login','auth.login.fail') order by id desc limit 5"
   ```

   The `ip` column shows your own address prefix, not Traefik's. `audit_log` has no address column; sign-ins store the prefix in `meta->>'ip'` (`db/migrations/0001_init.sql`, `src/Retroscope.Server/Endpoints/AuthEndpoints.cs`).

The proxy container name `coolify-proxy` comes from `deploy/RUNBOOK.md` and Coolify's networking docs. If it differs on your box, find the Traefik container with `docker ps`.

## 10. Deploy for the first time and smoke test

### 10.1 Deploy

1. Check that every required variable has a value. Coolify refuses to deploy otherwise.
2. Click **Deploy** and watch the log.
3. Check the log for the `importer` service. It should not start. If Coolify tries to pull or create it, `IMPORTER_DIGEST` must be set (section 5.3). **UNVERIFIED:** whether Coolify honours `profiles`.
4. The app starts after `db` reports healthy. It takes the leader lease, runs the migrations, creates the `retroscope_app` role with its grants, and then answers `/healthz`.
5. The resource shows healthy when the `app` healthcheck passes (every 10 s, after a 20 s start period). **UNVERIFIED:** whether the `backup` service, which has no healthcheck, affects the resource's status in Coolify.

### 10.2 Check the stack on the VPS

```sh
docker ps --format 'table {{.Names}}\t{{.Status}}'
```

`app` and `db` show `(healthy)`. `backup` shows `Up`.

Check that the limits were applied. The Coolify docs do not say whether compose `mem_limit` and `cpus` are honoured (**UNVERIFIED**):

```sh
docker inspect <app-container> -f '{{.HostConfig.Memory}} {{.HostConfig.NanoCpus}}'
docker stats --no-stream
```

For `app` you expect `209715200 600000000` (200 MiB, 0.6 CPU).

### 10.3 Check HTTPS, headers, and compression

From your own machine:

```sh
curl -sI https://<your-domain>/
curl -s -o /dev/null -D - -H 'Accept-Encoding: gzip' https://<your-domain>/
```

- The first shows `strict-transport-security: max-age=63072000; includeSubDomains`.
- The second is a real GET of the HTML shell, a dynamic response of 2.1-3.3 KB (`deploy/RUNBOOK.md` section 12). It must show **no** `content-encoding` header. If it shows `content-encoding: gzip`, go back to section 4.5.

Do not test with `curl -sI .../api/me`. `-I` sends HEAD, and `/api/me` accepts only GET, so the reply is a 405 with no body. Traefik's compress middleware skips small bodies (its documented default minimum is 1024 bytes, not checked in this repo), so that check prints no `content-encoding` even when gzip is on. `deploy/RUNBOOK.md` section 12 still shows that weak check.

### 10.4 Smoke test in the browser

1. Open `https://<your-domain>` and sign up with email and password.
2. Open the verification email and follow the link. If no mail arrives, read the app log for SMTP errors (section 16).
3. Create a team.
4. Start a poker session in the team. Open the same session in a second browser or a private window, join as another user or a guest, and vote in both. Votes appear live in both windows. That proves WebSockets work through Traefik.
5. Open a retro board, add an anonymous card, then edit it. That is the first use of `ANON_TAG_KEY` (section 5.7). If it fails, check the app log for a key error.
6. If you configured Google or Entra, sign out and sign in with each provider.

### 10.5 Make yourself an app admin

A fresh database has no app admin, and the repo has no bootstrap step for one outside the import (where `IMPORT_ADMIN_EMAILS` sets them). The `users` table has an `is_admin` column. To make your account an admin:

```sh
docker exec <db-container> psql -U retroscope_owner -d retroscope \
  -c "update users set is_admin = true where email = '<your-email-in-lower-case>'"
```

Emails are stored in lower case (a `CHECK` constraint in `db/migrations/0001_init.sql`). The change takes effect without signing out: every session lookup reads `users.is_admin` again (`src/Retroscope.Server/Data/Repos/SessionsRepo.cs`, `LoadAsync`).

## 11. Release a new version

Coolify redeploys a compose resource by stopping and recreating the containers. There is no rolling update. Clients see a short pause and reconnect.

### 11.1 By hand

1. Wait for the images run on `main` (or on a `v*` tag) to go green.
2. Copy the new `app` and `importer` digests from the run summary (section 3.2).
3. In Coolify, set `APP_DIGEST` and `IMPORTER_DIGEST`.
4. Click **Redeploy**. A restart is not enough, because the new image must be pulled.

Every migration must be quick and compatible with the previous release, because the old binary stops before the new one starts (`deploy/RUNBOOK.md` section 11).

### 11.2 By API (optional automation)

Sources: https://coolify.io/docs/knowledge-base/git/github/github-actions, https://next.coolify.io/docs/core/automation/deploy-webhooks, https://coolify.io/docs/api-reference/api/operations/deploy-by-tag-or-uuid, https://coolify.io/docs/api-reference/authorization

The repo has no release workflow that sets the digest. To automate it:

1. Enable API access on the instance: **Settings** > **Advanced** > **API and MCP** > **API access**: **Enabled**, then **Save**. An administrator must do this on a self-hosted install.
2. In the same section, fill in **Allowed IPs**. Empty (or `0.0.0.0`) allows every source, and Coolify shows a warning for that. List only the addresses that call the API. GitHub-hosted runners have no fixed small set of addresses, so for automation from GitHub prefer a self-hosted runner or a webhook relay with a fixed address over opening the API to everyone.
3. Create an API token. It is shown only once. The current source labels the page **Security** > **API Tokens**, and the docs say **Keys & Tokens** > **API Tokens**. Use whichever your version shows. The token needs both **Write** (for `PATCH /applications/{uuid}/envs`) and **Deploy** (for `POST /deploy`) (Coolify `routes/api.php`). Only a team admin or owner can create a token with `write`.
4. Set the token in your shell without echoing it:

   ```sh
   read -rs COOLIFY_TOKEN   # paste the token, press Enter
   export COOLIFY_TOKEN COOLIFY_URL=https://<coolify-domain>
   ```

5. Update the digest:

   ```sh
   curl -X PATCH "$COOLIFY_URL/api/v1/applications/<resource-uuid>/envs" \
     -H "Authorization: Bearer $COOLIFY_TOKEN" -H 'Content-Type: application/json' \
     -d '{"key":"APP_DIGEST","value":"<hex>"}'
   ```

   For a Docker Compose Empty resource the path is `/api/v1/services/<uuid>/envs`.
6. Deploy:

   ```sh
   curl -X POST "$COOLIFY_URL/api/v1/deploy" \
     -H "Authorization: Bearer $COOLIFY_TOKEN" -H 'Content-Type: application/json' \
     -d '{"uuid":"<resource-uuid>","force":false}'
   ```

   Or call the resource's **Webhooks** > **Deploy Webhook** URL (`…/api/v1/deploy?uuid=<uuid>&force=false`) with the same header.
7. Run `unset COOLIFY_TOKEN` when you are done.
8. To run this from GitHub Actions, store the URL and token as repo secrets (Coolify's docs name them `COOLIFY_WEBHOOK` and `COOLIFY_TOKEN`) under **Settings** > **Secrets and variables** > **Actions**. The Coolify API must be reachable from the runner and allowed by **Allowed IPs** (step 2).

## 12. Back up and restore

Full procedure: `deploy/RUNBOOK.md` section 8.

- The `backup` service runs `pg_dump -Fc` at start and then daily, writing `rs-<weekday 1-7>.dump` into the `pgbackups` volume. The volume keeps the last 7 days.
- The volume is on the same disk as the database. **Copy the dumps off the box daily.**

Coolify prefixes volume names. Find the real name first:

```sh
docker volume ls | grep pgbackups
```

Then copy today's dump to another machine. A dump holds user emails, password hashes, sealed Jira credentials, and audit data, so write it only into a private directory that is outside any git checkout (ideally on an encrypted disk):

```sh
umask 077
mkdir -p ~/retroscope-backups
ssh <vps> 'docker run --rm -v <volume-name>:/b alpine cat /b/rs-$(date +%u).dump' > ~/retroscope-backups/rs-$(date +%F).dump
```

To encrypt each copy, pipe it through `age` or `gpg` before it touches the disk, for example `... | age -r <your-age-public-key> > ~/retroscope-backups/rs-$(date +%F).dump.age`.

Run this from a cron job or scheduled task on the other machine, and prune old copies. Never save a dump, the Developer-view `.env` text, or the import seal key inside this repo: its `.gitignore` has no entries for `*.dump`, `.env*`, or `*.key`, so such a file could be committed.

Take a dump on demand:

```sh
docker exec <backup-container> sh -c 'pg_dump -Fc -h db -U retroscope_owner retroscope > /backups/manual.dump'
```

To restore, follow `deploy/RUNBOOK.md` section 8 steps 1-5: stop the app in Coolify, recreate the database empty, `pg_restore --no-owner --no-acl`, run `spot-load --sample 20`, then start the app. Rehearse a restore once before you depend on it.

Coolify's own scheduled database backups cover standalone databases and databases inside a Service (https://coolify.io/docs/databases/backups). **UNVERIFIED:** whether a Backups tab appears for the `db` of a Git-based compose application. Keep the `backup` sidecar either way.

## 13. Rotate keys

Full procedure: `deploy/RUNBOOK.md` section 6.

**`APP_KEYS`:**

1. Generate a new key with `openssl rand -base64 32` and pick a new id (`k2`, `k3`, …).
2. Set `APP_KEYS="k2:<new>,k1:<old>"` and redeploy. On start, the leader re-seals every stored Jira credential under `k2`. The log shows `Re-sealed N Jira credential boxes under the active key…`.
3. Keep `k1` listed for 30 days, because link invites live up to 30 days.
4. Remove `k1` and redeploy.

If a key leaked, drop the old key at once instead of waiting. Open link invites then stop working and must be re-created.

**`ANON_TAG_KEY`:** never rotate it unless it leaks.

**`DB_APP_PASSWORD`:** change it and redeploy. The migrator re-applies it on every start.

**`DB_OWNER_PASSWORD`:** changing the variable alone does not change the role's password. There is no documented procedure for it.

**External credentials:** the GHCR token (section 3.3), the Google and Entra secrets, and the SMTP credentials expire or can be rotated at the provider. Update the variable in Coolify and restart.

## 14. Import data from the old Supabase app

Full procedure: `deploy/IMPORT_RUNBOOK.md`. Almost all of it runs **off the VPS**, on your own machine. Only the restore happens on the VPS.

Summary of the order (`deploy/IMPORT_RUNBOOK.md` sections 5-9):

1. Generate the seal key pair once, on your machine (section 3 of that runbook). The private key never goes to the VPS, Coolify, or CI.

   ```sh
   umask 077
   openssl genpkey -algorithm RSA -pkeyopt rsa_keygen_bits:3072 -out ~/retroscope-import/seal.key
   openssl pkey -in ~/retroscope-import/seal.key -pubout -out ~/retroscope-import/seal.pub
   ```

2. Put the old app into maintenance and take a final Supabase backup.
3. Off-box, run `plan`, `fetch-storage`, `run`, and `verify` with the importer image into a local Postgres. Use the **production** `APP_KEYS` and `ANON_TAG_KEY`. Set `IMPORT_ADMIN_EMAILS` to at least your own email.
4. Review the report.
5. `pg_dump` the result and copy it to the VPS with `scp`. On the VPS, run `chmod 600 /root/rs-import.dump` at once.
6. On the VPS, with the app stopped in Coolify: recreate the database empty, `pg_restore --no-owner --no-acl`, and run `spot-load --sample 20`.
7. Start the app, check `/healthz`, and sign in as an admin.
8. Run `cutover` from the importer image on the db container's network.
9. Delete `/root/rs-import.dump` from the VPS (`deploy/IMPORT_RUNBOOK.md` section 8).
10. Switch DNS or the Coolify domain if needed. Old links keep working.
11. Rotate credentials and clean up (sections 11-12 of that runbook). Remove the `IMPORT_*` variables from Coolify afterwards.

**Keep the importer secrets out of history and `ps`.** `deploy/IMPORT_RUNBOOK.md` section 2 says to keep the importer variables in a mode-600 env file, "never in shell history and never in the repository". The spot-load and cutover commands in that runbook pass `DATABASE_OWNER_URL` (which holds the owner password), `APP_KEYS`, and `ANON_TAG_KEY` as `-e VAR=value`, which leaks them into the VPS shell history and process list. Use an env file instead:

```sh
umask 077
cat > /root/rs.env   # paste the VAR=value lines, then press Ctrl-D
docker run --rm --env-file /root/rs.env ...   # in place of every -e VAR=value
rm /root/rs.env      # when the import is done
```

Or export each value after `read -rs VAR` and pass `-e VAR` with no value, so Docker copies it from your environment.

Pulling the importer image on your own machine needs the same GHCR login as the VPS (section 3.3): `docker login ghcr.io --password-stdin` with a `read:packages` token. Rollback steps are in section 10 of `deploy/IMPORT_RUNBOOK.md`.

## 15. Measure on the VPS

`deploy/RUNBOOK.md` section 15 lists checks nobody has run on the production host yet. Run them after the first deploy and record the results, with the date, in that section:

- [ ] Coolify and Traefik memory after a day idle: `docker stats --no-stream` and `free -m`. App, db, and backup must stay within their 536 MiB of limits.
- [ ] App and db idle memory: adapt `deploy/drills/idle-memory.sh` to the Coolify container names. Targets: app under 120 MB, db under 180 MB.
- [ ] WebSocket idle timeout through Coolify's Traefik: `RS_E2E_BASE_URL=https://<your-domain> node deploy/drills/ws-idle.mjs --seconds 300` exits 0.
- [ ] bcrypt hashes per second on the VPS CPU type.
- [ ] Throughput and p99 latency with `deploy/drills/load.mjs` against a **staging copy** on the VPS, never production (it creates 160 accounts).
- [ ] Redeploy pause: clients are back within 15 s.
- [ ] Trusted proxy subnet: the `audit_log` check in section 9.
- [ ] No dynamic compression: the GET `curl -s -o /dev/null -D - -H 'Accept-Encoding: gzip' https://<your-domain>/` check in section 10.3 shows no `content-encoding`.
- [ ] HSTS on HTML: section 10.3.
- [ ] Swap: `swapon --show` and `sysctl vm.swappiness` (section 1.1).
- [ ] The off-box backup copy runs daily and a restore from it was rehearsed.

## 16. Troubleshooting

| Symptom | Likely cause | Fix |
|---|---|---|
| Coolify will not start the deploy | A required `${VAR:?}` variable is empty | Fill in every required variable (section 5.1). |
| Pull fails with `unauthorized` or `denied` | No GHCR login on the server, or the token lacks `read:packages` or expired | Redo section 3.3 and 3.4. Test with `docker pull` on the VPS. |
| Pull fails with `invalid reference format` | `APP_DIGEST` or `IMPORTER_DIGEST` is empty or includes `sha256:` | Paste only the hex (section 5.3). |
| Pull fails with `no matching manifest` | The VPS is ARM | The images are `linux/amd64` only. Use an x86_64 VPS. |
| App exits at start with a configuration error | Invalid `PUBLIC_URL`, `TRUSTED_PROXY_CIDRS`, `ENTRA_*`, `SMTP_*`, or `JIRA_HOST_ALLOWLIST` value, or only one half of a client ID and secret pair | Read the app log in Coolify, fix the named variable, and restart. |
| App log says `Cannot load library libgssapi_krb5.so.2` | A custom connection string without `GSS Encryption Mode=Disable` | Use the compose file's connection strings unchanged. |
| App never turns healthy | `/healthz` returns 200 only while the app holds the leader lease and the database answers | Check that `db` is healthy and read the app log. A second app instance waits `LEADER_ACQUIRE_SECONDS` and exits. |
| Database authentication fails after you changed `DB_OWNER_PASSWORD` | Postgres kept the password from its first initialisation | Set the variable back to the original value. |
| Certificate is not issued | DNS does not point at the VPS, port 80 is closed, a broken `AAAA` record, or Cloudflare proxy on | Check sections 1.3 and 2. |
| `redirect_uri_mismatch` from Google | The registered URI differs from `PUBLIC_URL` + `/auth/oidc/google/callback` | Fix the URI and wait up to a few hours. |
| Entra sign-in fails with `oidc_tenant` | The user's tenant is not in `ENTRA_ALLOWED_TENANTS` | Add the tenant ID. |
| Entra button missing | `ENTRA_ALLOWED_TENANTS` empty, or client ID or secret missing | Set all three (section 7). |
| Entra account not linked by email | No `xms_edov` claim and the domain not in `ENTRA_TENANT_DOMAINS` | Add the optional claims (section 7 step 8) or the domain. |
| No email arrives | Port blocked by the VPS provider, wrong `SMTP_SECURITY`, unverified sending domain, or SES sandbox | Check the app log, then section 8.1 and 8.2. |
| Every user hits the login rate limit together | `TRUSTED_PROXY_CIDRS` does not cover Traefik's address | Redo section 9. |
| Responses carry `content-encoding: gzip` | Coolify's gzip middleware is on | Section 4.5. |
| Anonymous card authors can no longer edit their cards | `ANON_TAG_KEY` changed | Restore the original value. |
| Jira credentials stop working after a key change | A key was removed from `APP_KEYS` too early | Put the old key back and follow section 13. |

Useful commands on the VPS:

- Database shell: `docker exec -it <db-container> psql -U retroscope_owner -d retroscope`.
- App logs: the resource's **Logs** in Coolify, or `docker logs <app-container>`. Resource metrics are not available for Docker Compose apps in Coolify.

## Unverified items to check on your screen

The research could not confirm these in official docs or the repo:

1. Whether Coolify honours compose `profiles`, so the `importer` service stays down.
2. The name of the network the proxy shares with `app` (section 9 finds it on the box).
3. Whether compose `mem_limit` and `cpus` are applied by Coolify.
4. WebSocket idle behaviour through Coolify's Traefik.
5. Whether the `backup` service, which has no healthcheck, affects the resource's status.
6. Which server user holds the GHCR login on `localhost` (normally root).
7. Whether a Backups tab exists for a database inside a Git-based compose application.
8. Which Coolify version added the Registries page and the domain form.
9. Whether a new Coolify project gets a `production` environment automatically.
10. Let's Encrypt behaviour behind Cloudflare's proxy.
11. Google verification for apps with only non-sensitive scopes.
12. Entra's tenant ID label, the `xms_edov` picker entry, the Graph email permission prompt, and the API permission labels.
13. Mailgun's username format and all Brevo details.
14. Traefik's compress middleware minimum body size (1024 bytes per Traefik's docs), which section 10.3 relies on.
15. On-screen labels taken from Coolify source rather than docs: **Gzip compression** (section 4.5), **Auto deploy** (section 4.3), and the API token page name (section 11.2).
