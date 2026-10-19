# 2026-10-17 — Tharanga: #84 dependency audit gate

Branch `fix/84-npm-audit`, worktree `worktrees/sd-npmaudit`, cut from `origin/main` at `154b5e4`. Not pushed, no PR.
**There is no CI** (Actions is dead on billing), so every figure below is from a run on this machine.

## What I found

The issue's analysis (2026-10-1x) said the remaining two criticals had no forward fix. **That changed.** On re-running
`npm audit` there were three advisories (2 critical + 1 high at package level, 5 packages counting dependants), and all
three now reported `fixAvailable: true` (not a semver-major):

| Advisory | Package | Fixed by |
|---|---|---|
| GHSA-67c8-pqhq-4rmx critical | `piscina` 5.0.0 - 5.3.1 | `@angular/build` 21.2.27 pins `piscina` 5.3.2 |
| GHSA-6qxp-vccf-f47h high | `@modelcontextprotocol/sdk` 1.12.0 - 1.30.1 | `@angular/cli` 21.2.27 pins 1.31.0 |
| GHSA-ch52-4w7c-c8xp high | `http-cache-semantics` <= 4.2.0 | 4.3.0, inside the existing `^4.1.1` range (`npm update`) |

`npm audit` after: **0 vulnerabilities, full tree and `--omit=dev`**. The two criticals and the cli high were all
dev-only (build tooling); `--omit=dev` was already clean before. No `--force`, no `@angular/build` 19.x.
`npm audit fix` itself was not run: the same result was reached by raising floors in `package.json`, which keeps
the declared minimum honest (the PR #85 lesson), so the lockfile is not the only record.

## Dependency changes (`src/frontend/package.json` + lockfile, 118 lockfile lines)

- `@angular/build` ^21.2.24 -> ^21.2.27, `@angular/ssr` ^21.2.24 -> ^21.2.27 (its peer on `build` is `^21.2.27`; **`ssr` is a
  runtime dependency**, so the SSR server now runs 21.2.27), `@angular/cli` ^21.1.4 -> ^21.2.27.
- Followed: `@angular-devkit/{architect,core,schematics}` and `@schematics/angular` 21.2.27, `zod` 4.3.6 -> 4.6.6 (a cli pin),
  `@modelcontextprotocol/sdk` 1.31.0, `piscina` 5.3.2, `http-cache-semantics` 4.3.0, `hono` 4.13.13,
  `@hono/node-server` 2.1.5, `express-rate-limit` 8.7.1.
- The eight framework packages stay at 21.2.25: nothing newer is published (`@angular/core` ends at 21.2.25).
- `package.json` gains `"audit:gate"`.

## The gate (`tools/audit/`)

- `audit-gate.mjs`, `audit-allowlist.json`, `audit-gate.test.mjs` (node:test, no dependencies, 52 tests).
- Wired into `.github/workflows/ci.yml` as a **new job** `dependency-audit` (self-tests, npm gate, NuGet gate); the `backend` and
  `frontend` jobs are untouched. It does not need `npm ci`.
- Documented in `docs/06` section 4 (how to run, what fails, what to do, what it does not cover).
- **The allow-list is empty**, because after the bumps there is nothing to accept. The mechanism exists for the next one.

Things worth knowing, found by running it rather than reading docs:

1. **`dotnet list package --vulnerable` does not honour `NuGetAuditSuppress`.** A suppression added to get a restore through does
   not hide the advisory from the gate, so it cannot become a silent bypass. The flip side: with `TreatWarningsAsErrors`, NuGet's own
   restore audit already fails the **build jobs** on an advisory of any severity, so accepting a NuGet advisory needs both a
   `NuGetAuditSuppress` (a coordinated change to `Directory.Build.props`) and an allow-list entry. Written up in doc 06 and the allow-list.
2. The gate restores with `-p:NuGetAudit=false` so a finding is explained by the gate and not by a failed restore. Other jobs keep the strict behaviour.
3. Offline, `npm audit --json` puts the reason in a top-level `message` and leaves `error.summary` empty. The first version printed
   `(no code):` and nothing else; fixed and tested. Offline `dotnet list package --source <https unreachable>` prints plain text, not JSON:
   covered by the "not JSON is exit 2" rule.
4. Both tools exit non-zero when advisories exist, so the gate reads the JSON and ignores the exit status; it fails closed on anything
   it cannot read (unknown report version, `problems` in the NuGet report, a count of vulnerable packages with no advisory in it).

## Proof the gate can fail (all run here; commands in "Commands to re-run" below)

- Real pre-fix npm report (3 advisories): red, exit 1. With 2 of 3 allow-listed: red on the third. All 3 listed and in date: green.
  Same list, `--today` after `reviewBy`: red `EXPIRED`. Current clean tree with those 3 entries: red `STALE`.
- Live NuGet against a throwaway project outside the repo (Newtonsoft.Json 12.0.1, EF Core SqlServer 8.0.0): red, 4 high not allow-listed;
  4 entries -> green; `--today` past the review date -> red.
- `npm_config_registry=http://127.0.0.1:9`: exit 2 "NOT the same as clean".
- Mutation check (lesson L6): 19 deliberate breakages of `audit-gate.mjs` in scratch copies (expiry ignored, stale ignored, `high` not
  blocking, errors swallowed, exit codes zeroed, ...). Every one made the test file fail.
- Real tree: npm PASS, NuGet PASS (0 advisories).

## Not done, and why

- **Not scheduled.** The job runs on PRs and pushes to `main`; an advisory published during a quiet week appears on the next PR. A weekly
  `schedule:` trigger is the next step, but it changes `on:` for every job, so it belongs with the Actions-billing fix. 
- Docker base images and the SQL Server image are not audited.
- `.github/` and `tools/` are owner-reviewed (CODEOWNERS), and the new job's name may need adding to any required-checks list.

## Verification of the dependency change (this machine)

| Check | Result |
|---|---|
| `npm run lint` | pass |
| `npm run build:shared`, `build:storefront` (SSR), `build:admin` | all pass on 21.2.27 |
| SSR smoke (built storefront, private port 47183): `/healthz` 200, home HTML has "New Arrivals", "Sale" and the wordmark testid, `/robots.txt` 200, `/sitemap.xml` 503 with no catalogue | pass (the same assertions CI makes); server stopped afterwards |
| `ng test shared` | 24/24 |
| `ng test storefront` and `admin`, **as shipped** | **not green here**: 7 storefront and 1 admin failures plus one vitest worker crash, all "Test timed out in 5000/20000 ms" or a worker start-up timeout |
| same, timeouts relaxed to 120 s and 3 workers | **storefront 1925/1925, admin 644/644** |

Why I believe the as-shipped failures are the machine and not the upgrade: the CPU was at 100% and 3 GB free from other sessions
(Roslyn server, SQL Server, Docker). The **unmodified** `154b5e4` tree, installed fresh in a scratch folder, fails the same specs:
first full run 10 storefront + 1 admin timeouts; paired runs of just `listing-page.spec.ts` + `search-page.spec.ts`, baseline 4 failures
(the same four heavy "17 pages / ceiling / deep page" specs that fail in every run on both trees) against 5, 6, 9 on the changed tree, with
different light specs failing each time. Relaxing the budgets is what flips them, so they are slow here, not broken. I do not call that a
clean pass: the 120 s run needed a temporary edit (`20_000` -> `120_000` in those two spec files, reverted, not committed) plus a scratch
vitest config. **Re-run `npm run test:ci` on a quiet machine before merging.** Those specs' 5-20 s budgets are tight for a laptop under load;
that is a separate problem and I did not touch it.

## Commands to re-run

```
node --test tools/audit/audit-gate.test.mjs          # 52 tests, no network
node tools/audit/audit-gate.mjs                      # both ecosystems, real tree: PASS / PASS
cd src/frontend && npm run audit:gate                # npm half
npm audit ; npm audit --omit=dev                     # both: found 0 vulnerabilities
# red on the real pre-fix tree (the lockfile as it was on main at 154b5e4), no checkout needed:
mkdir /tmp/old-lock && git show 154b5e4:src/frontend/package.json > /tmp/old-lock/package.json   && git show 154b5e4:src/frontend/package-lock.json > /tmp/old-lock/package-lock.json
node tools/audit/audit-gate.mjs npm --frontend /tmp/old-lock     # RED, exit 1: piscina (critical), sdk, http-cache-semantics
# red on a fabricated advisory, an expired entry, a stale entry, a malformed entry, an audit that cannot run:
#   the "command line" tests in tools/audit/audit-gate.test.mjs run exactly those through the real CLI.
# offline: npm_config_registry=http://127.0.0.1:9 node tools/audit/audit-gate.mjs npm     # exit 2, not green
```

(`npm audit` and `dotnet` need network; the gate has `--input <saved report>` to run from a saved report.)

## Tester round 1 (2026-10-19): FAIL on the gate (one Major), dependency change clean

**The dependency change passed everything.** Lockfile: 749 entries before and after, none added or removed, 14 moved with no major bump,
every `resolved` on registry.npmjs.org, install scripts unchanged, 14 of 14 integrity hashes match the registry. `npm ci` in a scratch
copy exits 0 with no peer warnings, `npm audit` and `npm audit --omit=dev` both report 0 (main: 5). `dist/storefront`, `admin` and
`shared` of the branch are byte-identical to `main`'s build; both servers answer the same on 18 URLs. On a **quiet machine**, as shipped,
shared 24/24 and admin 644/644 pass; storefront passed 1925/1925 on 5 of 6 runs of the branch (the 6th timed out once on a 5 s budget in
`search-page.spec.ts`, 6.7 s against 1.5 s in isolation) and 6 of 6 on `main`. So the developer's timeouts were the loaded machine, apart
from that one flaky 5 s budget, which is not this branch's.

**The Major, fixed: the gate went green on the real vulnerable lockfile whenever npm was told to omit dev dependencies.** `npm audit`
honours `NODE_ENV=production`, `npm_config_omit=dev`, `npm_config_production=true` and an `omit=dev` line in any `.npmrc`; the gate ran a bare
`npm audit --json`; and all five advisories behind #84 were dev-only. The tester found it because the local storefront image sets
`NODE_ENV=production`. Today's CI job does not, so it was latent; any Docker stage or shell that does would have hidden the next one.
`runNpmAudit` now spawns `npm audit --include=dev --json` (a command-line flag outranks the environment and every config file).
**Proof on the real thing:** the gate, with real npm and the real registry, on the **old lockfile from `main`** with
`NODE_ENV=production` printed BLOCKED for `piscina` (critical) and the two highs and exited 1 (before the fix: PASS, exit 0);
the current tree passes, also under `NODE_ENV=production`.

**The tester's minors, addressed:**

| # | Finding | What was done |
|---|---|---|
| 1 | An npm report with no `metadata` and no vulnerabilities, a NuGet report with `projects: []`, and one with packages under an unknown key all exited 0 | All three are exit 2 now ("cannot tell that it read every advisory"; "listed no projects"; "listed packages under `packages`") |
| 2 | `via` as an object, or `fixAvailable: null`, threw an uncaught TypeError (exit 1, stack trace) | `via` not a list is a named error; `null` fix is "no fix is published"; and **any** exception the gate did not foresee is now exit 2 with the reason, never a stack trace and never green |
| 3 | 3 of 25 mutations survived: restore failure ignored, `shell: true` removed, the review-soon warning off | Real spawn-path tests added against a **fake `npm` and `dotnet` placed first on PATH** (see below). All three now die, plus the ambient-`npm` cases |
| 4 | Dates are UTC and the docs did not say so | Said in docs/06 §4 step 3 and in the allow-list's own format note |
| 5 | The job is named "Dependency audit (npm and NuGet advisories)" and docs/06 called it "the `Dependency audit` job" (matters for a required-checks list) | docs/06 now gives the real name and the job id `dependency-audit` |
| 6 | One flaky storefront spec timeout | Not this branch's (see above); left |
| 7 | No retry on a registry outage | By design (fail closed); docs/06 now says to re-run the job |

**New tests (52 -> 62):** the real `npm`-spawn path under `nothing set`, `NODE_ENV=production`, `npm_config_omit=dev` and
`npm_config_production=true` (a fake `npm` that omits dev dependencies exactly as npm does unless `--include=dev` is on the command line);
a failing `dotnet restore` stops the NuGet half (**Linux only**: Node will not spawn a `.cmd` shim without a shell, so on Windows that one
test is skipped); the review-soon warning and its annotation; an unreadable `--input` is exit 2; and the unit tests for each odd shape above.
**Mutation proof** (each verified to have changed the file, run, then restored): dropping `--include=dev` kills 3 tests; `shell: false`
kills 4; review-soon warning off kills 1; npm summary check off kills 1; unexpected errors re-thrown kills 1; empty `projects` allowed
kills 1; unknown package key allowed kills 1; NuGet restore failure ignored (on Linux, Alpine with Node 24) kills 1. **Numbers:** 62 tests,
61 pass and 1 skipped on Windows; 62 of 62 on Linux; 0 fail.

**Not run / still true:** GitHub Actions (down), the Docker image builds, `actionlint`. The gate is still not scheduled.
