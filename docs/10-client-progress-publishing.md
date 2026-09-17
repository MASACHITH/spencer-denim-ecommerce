# 10 — Publishing the Client Progress Page (Task Sheet)

Last updated: 2026-09-17 · Applies to: **the client progress page only** (`D:\Projects\e com\client-progress\`)

> ⛔ This hosting is **only** for the client progress page. **Never upload the main project** (`spencer-denim-ecommerce`) or any other files here.
> The e-commerce platform itself deploys to the Hostinger **VPS** with Docker ([03](03-architecture-and-tech-stack.md) §4).

## 1. Where it is published

| Item | Value |
|---|---|
| Live URL | **https://colombowardrobecompany-build-tracker.kt-wedding.online** |
| Hosting | Hostinger shared hosting (subdomain of `kt-wedding.online`) |
| FTP host | `147.93.101.68` (the name `ftp.kt-wedding.online` also points to this IP) |
| FTP port | `21` |
| Protocol | **FTPS** (explicit TLS). Plain FTP is not used because it would send the password unencrypted. |
| FTP username | `u765465138.colombowardrobecompany-build-tracker.kt-wedding.online` |
| Remote folder | `public_html`. The FTP account opens **directly inside** it, so upload to `/`. |
| Password | **Not stored in any document or in git.** Kept encrypted on the developer PC (see §3). |
| Files uploaded | **Only** `index.html`, `styles.css`, `app.js`, `progress-data.js`, plus `.htaccess` (cache rules) |
| Other remote files | `default.php` (Hostinger placeholder) is left alone; the publish script never deletes anything. |

## 2. How to publish (after every daily update)

1. Update `client-progress/progress-data.js` as described in [07](07-client-progress-update-guide.md): statuses, progress, a new log line, and `lastUpdated`.
2. Run from the repo folder:
   ```powershell
   cd "D:\Projects\e com\spencer-denim-ecommerce"
   pwsh tools/publish-client-progress.ps1 -DryRun   # optional: validate only
   pwsh tools/publish-client-progress.ps1           # validate + upload + verify
   ```
3. The script:
   - checks the 4 files exist and ignores anything else in the folder
   - checks JavaScript syntax and the data (prints overall %, counts and `lastUpdated`)
   - uploads the 4 files over **encrypted FTPS**, with the Hostinger certificate key pinned
   - verifies that the remote file sizes match the local ones
   - verifies that the live site serves the new `lastUpdated`
4. Open the live URL on a phone and a desktop to check it looks right.

**A feature is marked Done only after it has passed testing** ([06](06-engineering-process.md)).

## 3. Password handling

- The password is stored **encrypted with Windows DPAPI** at `%USERPROFILE%\.spencer-denim\tracker-ftp-credential.xml`. Only the same Windows user on the same PC can decrypt it. It is outside the repo.
- To set or change it, run this. It prompts with hidden input, so nothing is typed into a document or chat:
  ```powershell
  pwsh tools/publish-client-progress.ps1 -SetPassword
  ```
- **Never** put the password in docs, git, chat messages, `MEMORY.md` or scripts.
- If the password is ever shared in a message or document: change it in **hPanel → Files → FTP Accounts**, then run `-SetPassword`.
- On a new PC, run `-SetPassword` once.

## 4. Security details

- **Encrypted connection:** FTPS (explicit TLS, `--ssl-reqd`). The login is passed to `curl.exe` through standard input, so it never appears on the command line.
- **Certificate:** Hostinger's FTP certificate is issued to `hostinger.com`, not to the IP address, so normal name checking fails. Instead of turning checks off, the script **pins the certificate's public key** (SHA-256). Any other certificate is rejected.
- **Several pins are allowed.** Hostinger serves more than one certificate (a Sectigo `hostinger.com` one and a Let's Encrypt `*.hstgr.io` one, which rotates every few months). `$PinnedKey` holds a semicolon-separated list, and the connection succeeds if the server key matches any of them.
- **When a pin error appears** (`curl (90) SSL: public key does not match pinned public key`), publishing stops safely and nothing is uploaded. To fix it:
  ```powershell
  pwsh tools/publish-client-progress.ps1 -ShowPin
  ```
  Check that the Subject is a Hostinger name (`CN=hostinger.com` or `CN=*.hstgr.io`), the issuer is a public CA (Sectigo, Let's Encrypt), and the only error is `RemoteCertificateNameMismatch`. Then **add** the new pin to `$PinnedKey` (keep at most 3, drop expired ones) and commit. If the subject or issuer is anything else, **stop and investigate**: that could be interception.
- The page has a `noindex` tag, so search engines don't list it.

## 5. Manual fallback (if the script can't be used)

Use **FileZilla** (or WinSCP):
- **Protocol:** FTP. **Encryption:** *Require explicit FTP over TLS*.
- **Host:** `147.93.101.68`, **Port:** `21`, **User:** as in §1. Enter the password yourself.
- **Certificate:** accept Hostinger's certificate (`hostinger.com`) when prompted.
- **Upload:** only the 4 files, into the folder the account opens in (`/public_html`).

## 5b. Known issue: office network blocks FTP data connections (2026-09-17)

From the office network (`scienter.lk`), the FTP **login works** (port 21, encrypted), but the **data connection** that carries files and listings is reset or times out. It fails the same way with encrypted and unencrypted data, so it is a network or firewall restriction, not a TLS or password problem. The script then stops with `curl (56) response reading failed` or `(28) Operation timed out`.

Ways to publish, in order of preference:
1. **Run the script from another network**, e.g. a home connection or a mobile hotspot. Nothing else changes.
2. **hPanel File Manager** (works through the browser over HTTPS on any network):
   hPanel → Websites → `colombowardrobecompany-build-tracker.kt-wedding.online` → **File Manager** → open `public_html` → **Upload** the 4 files (`index.html`, `styles.css`, `app.js`, `progress-data.js`), choosing *Replace* if asked. Leave `default.php` alone; `index.html` takes priority over it.
3. Ask IT to allow outbound FTP passive data connections to `147.93.101.68`.

Don't retry the script repeatedly on the blocked network. Many failed connections can make Hostinger temporarily block your IP.

## 5c. Caching: the client must never see an old page

Hostinger serves this site with `Cache-Control: public, max-age=604800` by default, so **a visitor could see a week-old page** after we publish. Fixed on 2026-09-18 by uploading `client-progress/.htaccess`, which:
- sends `no-cache, no-store, must-revalidate, max-age=0` for `.html`, `.js` and `.css`
- turns off LiteSpeed page cache for the site
- puts `index.html` ahead of Hostinger's `default.php`

After each publish the script checks the live `Cache-Control` header and warns if caching has returned (e.g. if `.htaccess` was deleted). If the warning appears: re-upload `.htaccess`, and purge the cache in hPanel → Websites → Performance.

Anyone still seeing an old page after this should reload with **Ctrl+F5** once; their browser cached the earlier, long-lived copy.

## 6. Rules

1. This site hosts **only** the client progress page, and only the 4 files.
2. Never upload the main project, docs, `reference/` or anything with internal notes.
3. Never delete remote files unless the project owner asks.
4. Publish after each daily update. Statuses must always reflect real, tested progress.
5. For AI assistants: use `tools/publish-client-progress.ps1` only. Never print, log or store the password. Report the live URL and `lastUpdated` after publishing.

## 7. History

| Date | Event |
|---|---|
| 2026-09-17 | Hosting set up. FTPS with key pinning verified; `public_html` contained only `default.php`. Publish script and this document added. |
| 2026-09-17 | First publish succeeded after earlier data-connection failures (intermittent). Live page verified at 390 px and 1280 px; `index.html` is served ahead of `default.php`. |
