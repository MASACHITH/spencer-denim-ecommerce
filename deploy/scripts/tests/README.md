# Tests for the deployment scripts (#195)

One command: `bash deploy/scripts/tests/run-all.sh` (add `--fast` to skip the docker test). Each test also runs alone.
None of them touches a server, the repository's data, or anything outside a temp directory; the docker test starts
containers named `sd195-hostnginx-*` and removes them. They need `bash`, `openssl` and `curl`; `node` for the mock shop;
`docker` only for `test-host-nginx.sh` (it skips itself, with a message, when docker is missing or not running).

| Test | Proves |
|---|---|
| `test-verify-public.sh [name]` | `verify-public.sh` against `mock-shop.mjs`: a right environment passes every check; 35 variants, each wrong in exactly one way (wrong redirect, no HSTS, no SSR, robots wrong, noindex missing/present, an unknown host answered by the shop, 5xx pages, stack traces, version leaks, open `/internal`, insecure cookies, bad/expired/self-signed/wrong-name certificates …), each turn exactly that check red (or amber) and set the exit status |
| `test-preflight.sh [name]` | `preflight.sh` with fake `docker`/`ss`/`ip`/`nginx`/`dig`/`curl`/`id` and a `PATH` that has **no** `rm`, `mkdir`, `mv`, `cp`, `touch`, `tee`, `chmod`, `chown`, `install`: every PASS/WARN/FAIL branch fires; the script changes nothing (the directories it reads are compared byte for byte, and any command it runs that is not a read is logged and fails the test) |
| `test-check-env-seed.sh` | `check-env.sh` refuses a first-Super-Admin password the API would refuse: no digit, no lower-case letter, no upper-case letter, no symbol (the old B5 generator made letters and digits only: the API logged `SeedingFailed` and created no account), or too short; names what is missing, never prints the value; not checked when the seed is switched off |
| `test-install-owners.sh` | `install.sh` in a temp tree with an `install` that refuses a numeric `-o`/`-g` like uutils (Ubuntu 26.04) and a logging `chown`: it finishes, and every directory gets `install -d -m <mode>` followed by `chown <owner>`; no script gives `install` an owner or group option in any spelling or over a continuation (the pattern is shown to catch nine spellings first); `deploy.sh` keeps its `owned_dir` calls. Fifteen mutations of the three scripts all turn it red |
| `test-make-dp-cert.sh` | `make-dp-cert.sh` (password shown on the terminal only and never written to any file unless `--write-env`; refuses to replace a certificate or encrypt an existing key ring; password rules; a run that fails between installing the `.pfx` and showing the password removes the `.pfx` again and leaves `app.env` exactly as it was, never empty; `--days` without a value) and the certificate rules in `check-env.sh` (mode 640 or stricter: 660, 644, 604 refused); `compose.yml` passes the two settings only when set |
| `test-wipe-staging.sh` | `wipe-staging.sh` in a temp tree with a fake `docker` and a fake `install.sh`: it empties the three staging directories (dot-files and nested directories included) and nothing else (a production tree beside it is compared byte for byte; config/ and the `.pfx` stay); it refuses any argument, a wrong confirmation, a staging `db.env` that says production, a container still running, a missing directory, a missing `db.env`, a relative base, a staging base or `data` directory that is a junction/symlink to production (production's files are checked intact) and a non-root run, each before anything is stopped or deleted; a deletion that silently does nothing, or a directory it cannot read (made unreadable with `icacls` on Windows, `chmod 000` elsewhere), fails loudly and `install.sh` does not run. It also shows the old `rm -rf dir/*` form exiting 0 on that unreadable directory with every file left (M1 of the #219 review) |
| `test-host-nginx.sh` | the host-nginx templates in a real nginx 1.24 container: `nginx -t` for staging, production, the catch-all, the optional www block and the optional basic-auth lines; a second `default_server` is refused; `verify-public.sh` run from a Linux container against it (the catch-all missing is the failure it must report; the catch-all present fixes it; staging answers `Disallow: /` and `noindex`; production adds neither) |

`mock-shop.mjs` is the stand-in for a deployed environment (`--env`, `--faults a,b,c`, or `--upstream` to play the stack behind nginx).
Ports are picked by the operating system and written to a `--ready` file, so tests never collide with each other or with
anything else on the machine (docs/14 "Scratch scripts and ports are shared too").

Also: `DataProtectionKeyRingTests.A_certificate_made_by_make_dp_cert_sh_is_accepted_and_encrypts_the_key_ring`
(`tests/SpencerDenim.IntegrationTests`) runs the real `make-dp-cert.sh` and starts the API with the `.pfx` it wrote.

**Speed:** on Linux each suite takes seconds to a minute. In Git Bash on Windows every process start costs ~150 ms, so the
shell suites take several minutes each (`TEST_JOBS=8` runs more cases side by side).
