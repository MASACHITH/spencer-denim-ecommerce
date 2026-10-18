#!/usr/bin/env node
/**
 * Dependency-advisory gate (#84).
 *
 *   node tools/audit/audit-gate.mjs [npm|nuget|all] [options]        (from the repository root)
 *   npm run audit:gate                                               (npm half only, from src/frontend)
 *
 * WHAT IT DOES
 *   Reads the advisories that `npm audit --json` (src/frontend) and
 *   `dotnet list package --vulnerable --include-transitive --format json` (SpencerDenim.sln) report,
 *   and FAILS on any HIGH or CRITICAL advisory that is not in tools/audit/audit-allowlist.json.
 *
 * WHY NOT `npm audit --audit-level=high`
 *   Because the day an advisory has no forward fix (or only a downgrade), that command reds every PR,
 *   and the team learns to ignore a red check. The allow-list makes "we know, and here is why" an
 *   explicit, dated, reviewable decision instead.
 *
 * THE ALLOW-LIST CANNOT QUIETLY ROT. The gate also fails when
 *   - an entry is malformed (a field missing, a reason too thin to mean anything, reviewBy more than
 *     90 days after acceptedOn, a date that is not a real date);
 *   - an entry's reviewBy date has passed (accepting a risk is a decision with an end date);
 *   - an entry no longer matches a high/critical finding (the advisory was fixed, withdrawn or
 *     re-rated: delete the entry, a stale one would silently pre-approve the next advisory that
 *     happens to reuse that id).
 *
 * IT FAILS CLOSED. If a tool cannot run (no network, registry error, an unfamiliar report format, a
 * restore problem) the gate exits 2 and says so. "Could not look" is never reported as "clean".
 *
 * EXIT CODES   0 pass   1 policy failure (unlisted/expired/stale/invalid)   2 the audit itself failed
 *
 * OPTIONS
 *   --allowlist <file>   Use another allow-list (tests and demonstrations).
 *   --input <file>       Read a saved report instead of running the tool (needs npm or nuget, not all).
 *   --today YYYY-MM-DD   Pretend it is this UTC date (tests and demonstrations; CI never passes it).
 *   --no-restore         nuget only: do not run `dotnet restore` first (assets must already exist).
 *   --frontend <dir>     npm only: directory holding package-lock.json (default src/frontend).
 *   --solution <file>    nuget only: solution or project to audit (default SpencerDenim.sln).
 *
 * NUGET NOTES
 *   Restore is run here with -p:NuGetAudit=false: with TreatWarningsAsErrors, NuGet's own restore audit
 *   turns any advisory into a failed restore (that is how 2026-10-14 broke main), and this gate must be
 *   the one to look at the report and explain it. The ordinary build jobs keep the stricter behaviour.
 *   NuGetAuditSuppress does NOT hide an advisory from `dotnet list package --vulnerable`, so a
 *   suppression added to get a restore through still needs an allow-list entry here.
 *
 * Process, allow-list format and the review routine: docs/06-engineering-process.md section 4.
 */
import { spawnSync } from 'node:child_process';
import { readFileSync, realpathSync } from 'node:fs';
import { dirname, relative, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const HERE = dirname(fileURLToPath(import.meta.url));
export const REPO_ROOT = resolve(HERE, '..', '..');
export const DEFAULT_ALLOWLIST = resolve(HERE, 'audit-allowlist.json');

export const EXIT_PASS = 0;
export const EXIT_POLICY = 1;
export const EXIT_TOOL = 2;

export const ECOSYSTEMS = ['npm', 'nuget'];
export const MAX_REVIEW_WINDOW_DAYS = 90;
export const MIN_REASON_LENGTH = 30;
const REVIEW_SOON_DAYS = 14;

const BLOCKING_SEVERITIES = new Set(['high', 'critical']);
const KNOWN_SEVERITIES = new Set(['info', 'low', 'moderate', 'high', 'critical']);

/** The audit could not be carried out, or was asked for wrongly. Distinct from a policy failure. */
export class GateError extends Error {}

// ---------------------------------------------------------------------------------------------
// Identity and severity
// ---------------------------------------------------------------------------------------------

const GHSA = /GHSA(?:-[a-z0-9]{4}){3}/i;

/** GHSA ids are written lower-case after the prefix; anything else (a CVE, a URL) is kept as typed. */
export function normaliseAdvisory(value) {
  const text = String(value ?? '').trim();
  return /^GHSA(?:-[a-z0-9]{4}){3}$/i.test(text) ? `GHSA${text.slice(4).toLowerCase()}` : text;
}

export function advisoryFromUrl(url) {
  const match = GHSA.exec(String(url ?? ''));
  return match ? normaliseAdvisory(match[0]) : null;
}

/** An unrecognised severity blocks: a new label must never be a way past the gate. */
export function isBlocking(severity) {
  return BLOCKING_SEVERITIES.has(severity) || !KNOWN_SEVERITIES.has(severity);
}

export function findingKey(ecosystem, advisory, packageId) {
  return `${ecosystem}|${normaliseAdvisory(advisory)}|${String(packageId).toLowerCase()}`;
}

function parseJson(text, label) {
  const trimmed = String(text ?? '').trim();
  if (!trimmed) {
    throw new GateError(`${label} produced no output.`);
  }
  try {
    return JSON.parse(trimmed);
  } catch {
    throw new GateError(`${label} did not produce JSON. Its output began: ${trimmed.slice(0, 300)}`);
  }
}

// ---------------------------------------------------------------------------------------------
// Dates
// ---------------------------------------------------------------------------------------------

export function parseIsoDate(value) {
  if (typeof value !== 'string' || !/^\d{4}-\d{2}-\d{2}$/.test(value)) {
    return null;
  }
  const date = new Date(`${value}T00:00:00Z`);
  // The round trip rejects 2026-02-31, which Date would otherwise roll into March.
  return Number.isNaN(date.getTime()) || date.toISOString().slice(0, 10) !== value ? null : date;
}

const daysBetween = (from, to) => Math.round((to.getTime() - from.getTime()) / 86_400_000);

export function utcToday() {
  return new Date().toISOString().slice(0, 10);
}

// ---------------------------------------------------------------------------------------------
// Parsing the two reports. Each returns a list of findings:
//   { ecosystem, advisory, package, severity, title, url, detail, fix? }
// ---------------------------------------------------------------------------------------------

function describeNpmFix(fixAvailable) {
  if (fixAvailable === false || fixAvailable === undefined) {
    return 'npm reports no fix is published';
  }
  if (fixAvailable === true) {
    return 'npm reports a fix inside the current version ranges (npm audit fix, never --force)';
  }
  const target = `${fixAvailable.name}@${fixAvailable.version}`;
  return fixAvailable.isSemVerMajor
    ? `npm suggests ${target}, a SEMVER-MAJOR change: check it is not a downgrade before taking it`
    : `npm suggests ${target}`;
}

export function parseNpmAudit(text) {
  const report = parseJson(text, 'npm audit');

  if (report?.error) {
    // Offline, npm prints { message: "request to ... failed, reason: ...", error: { summary: "", detail: "" } }:
    // the useful text is the top-level message and the error's own fields are empty strings.
    const { code, summary, detail } = report.error;
    const why = report.message || summary || detail || 'no detail given';
    throw new GateError(`npm audit could not run${code ? ` (${code})` : ''}: ${why}`);
  }
  if (
    report === null ||
    typeof report !== 'object' ||
    report.auditReportVersion !== 2 ||
    typeof report.vulnerabilities !== 'object' ||
    report.vulnerabilities === null
  ) {
    throw new GateError(
      'npm audit returned a report this gate does not understand (expected auditReportVersion 2).'
    );
  }

  const findings = new Map();
  for (const [name, entry] of Object.entries(report.vulnerabilities)) {
    for (const via of entry?.via ?? []) {
      // A string names another vulnerable package: the advisory itself is listed under that package.
      if (via === null || typeof via !== 'object') {
        continue;
      }
      const packageId = via.name ?? name;
      const advisory = advisoryFromUrl(via.url) ?? (via.source === undefined ? null : `npm-${via.source}`);
      if (!advisory) {
        throw new GateError(`npm audit listed an advisory on ${packageId} with no id or URL.`);
      }
      const key = findingKey('npm', advisory, packageId);
      if (!findings.has(key)) {
        findings.set(key, {
          ecosystem: 'npm',
          advisory,
          package: packageId,
          severity: String(via.severity ?? '').toLowerCase(),
          title: via.title ?? '',
          url: via.url ?? '',
          detail: `affected range ${via.range ?? 'unknown'}`,
          fix: describeNpmFix(report.vulnerabilities[packageId]?.fixAvailable),
        });
      }
    }
  }

  // The summary counts vulnerable packages. If it says there are some and we read no advisory, the
  // format has moved under us: that is an error, not a clean bill of health.
  const reported = report.metadata?.vulnerabilities?.total ?? 0;
  if (reported > 0 && findings.size === 0) {
    throw new GateError(`npm audit reports ${reported} vulnerable packages but no advisory could be read from it.`);
  }
  return [...findings.values()];
}

export function parseDotnetList(text) {
  const report = parseJson(text, 'dotnet list package');

  if (report === null || typeof report !== 'object' || report.version !== 1 || !Array.isArray(report.projects)) {
    throw new GateError('dotnet list package returned a report this gate does not understand (expected version 1).');
  }
  if (Array.isArray(report.problems) && report.problems.length > 0) {
    const lines = report.problems.map((p) => `${p.level ?? 'problem'}: ${p.text ?? JSON.stringify(p)}`);
    throw new GateError(`dotnet list package reported problems, so its report may be incomplete:\n  ${lines.join('\n  ')}`);
  }

  const findings = new Map();
  for (const project of report.projects) {
    const projectName = String(project.path ?? '').split('/').pop();
    for (const framework of project.frameworks ?? []) {
      const packages = [...(framework.topLevelPackages ?? []), ...(framework.transitivePackages ?? [])];
      for (const pkg of packages) {
        for (const vulnerability of pkg.vulnerabilities ?? []) {
          const advisory = advisoryFromUrl(vulnerability.advisoryurl) ?? String(vulnerability.advisoryurl ?? '').trim();
          if (!advisory) {
            throw new GateError(`dotnet list package listed a vulnerability on ${pkg.id} with no advisory URL.`);
          }
          const key = findingKey('nuget', advisory, pkg.id);
          const where = `${projectName} (${framework.framework})`;
          const existing = findings.get(key);
          if (existing) {
            existing.where.add(where);
            existing.versions.add(pkg.resolvedVersion);
            continue;
          }
          findings.set(key, {
            ecosystem: 'nuget',
            advisory,
            package: pkg.id,
            severity: String(vulnerability.severity ?? '').toLowerCase(),
            title: '',
            url: vulnerability.advisoryurl ?? '',
            where: new Set([where]),
            versions: new Set([pkg.resolvedVersion]),
          });
        }
      }
    }
  }

  return [...findings.values()].map(({ where, versions, ...finding }) => ({
    ...finding,
    detail: `version ${[...versions].join(', ')} in ${[...where].join(', ')}`,
  }));
}

// ---------------------------------------------------------------------------------------------
// The allow-list
// ---------------------------------------------------------------------------------------------

const present = (value) => typeof value === 'string' && value.trim() !== '';

function entryIssues(raw, today) {
  if (raw === null || typeof raw !== 'object' || Array.isArray(raw)) {
    return ['is not an object'];
  }
  const issues = [];
  if (!ECOSYSTEMS.includes(raw.ecosystem)) {
    issues.push(`"ecosystem" must be one of: ${ECOSYSTEMS.join(', ')}`);
  }
  for (const field of ['advisory', 'package', 'acceptedBy']) {
    if (!present(raw[field])) {
      issues.push(`"${field}" is required`);
    }
  }
  if (!present(raw.reason) || raw.reason.trim().length < MIN_REASON_LENGTH) {
    issues.push(`"reason" must say why this is acceptable, in at least ${MIN_REASON_LENGTH} characters`);
  }

  const acceptedOn = parseIsoDate(raw.acceptedOn);
  const reviewBy = parseIsoDate(raw.reviewBy);
  if (!acceptedOn) {
    issues.push('"acceptedOn" must be a real date written YYYY-MM-DD');
  } else if (raw.acceptedOn > today) {
    issues.push('"acceptedOn" is in the future');
  }
  if (!reviewBy) {
    issues.push('"reviewBy" must be a real date written YYYY-MM-DD');
  }
  if (acceptedOn && reviewBy) {
    if (reviewBy <= acceptedOn) {
      issues.push('"reviewBy" must be after "acceptedOn"');
    } else if (daysBetween(acceptedOn, reviewBy) > MAX_REVIEW_WINDOW_DAYS) {
      issues.push(`"reviewBy" is more than ${MAX_REVIEW_WINDOW_DAYS} days after "acceptedOn"`);
    }
  }
  return issues;
}

/**
 * Validates every entry (whichever ecosystem it names) so a malformed one is caught on every run.
 * Returns the usable entries and a list of problems for the unusable ones.
 */
export function readAllowlist(document, today) {
  if (document === null || typeof document !== 'object' || !Array.isArray(document.entries)) {
    throw new GateError('The allow-list must be a JSON object with an "entries" array.');
  }
  const entries = [];
  const invalid = [];
  const seen = new Set();

  document.entries.forEach((raw, index) => {
    const label = `allow-list entry ${index + 1}${present(raw?.advisory) ? ` (${raw.advisory})` : ''}`;
    const issues = entryIssues(raw, today);
    if (issues.length === 0) {
      const key = findingKey(raw.ecosystem, raw.advisory, raw.package);
      if (seen.has(key)) {
        issues.push('duplicates an earlier entry');
      } else {
        seen.add(key);
      }
    }
    if (issues.length > 0) {
      invalid.push(`${label}: ${issues.join('; ')}.`);
    } else {
      entries.push({ ...raw, advisory: normaliseAdvisory(raw.advisory) });
    }
  });
  return { entries, invalid };
}

// ---------------------------------------------------------------------------------------------
// The decision
// ---------------------------------------------------------------------------------------------

export function evaluate({ ecosystem, findings, entries, today }) {
  const mine = entries.filter((entry) => entry.ecosystem === ecosystem);
  const byKey = new Map(mine.map((entry) => [findingKey(ecosystem, entry.advisory, entry.package), entry]));

  const blocking = [];
  const accepted = [];
  const expired = [];
  const belowGate = [];
  const matched = new Set();

  for (const finding of findings) {
    if (!isBlocking(finding.severity)) {
      belowGate.push(finding);
      continue;
    }
    const key = findingKey(ecosystem, finding.advisory, finding.package);
    const entry = byKey.get(key);
    if (!entry) {
      blocking.push(finding);
      continue;
    }
    matched.add(key);
    // reviewBy is the last day the acceptance stands.
    (today > entry.reviewBy ? expired : accepted).push({ finding, entry });
  }

  const stale = mine
    .filter((entry) => !matched.has(findingKey(ecosystem, entry.advisory, entry.package)))
    .map((entry) => {
      const still = findings.find((f) => findingKey(ecosystem, f.advisory, f.package) === findingKey(ecosystem, entry.advisory, entry.package));
      return { entry, nowRated: still?.severity ?? null };
    });

  return { ecosystem, today, blocking, accepted, expired, stale, belowGate };
}

export function isPassing(result, invalid = []) {
  return (
    invalid.length === 0 &&
    result.blocking.length === 0 &&
    result.expired.length === 0 &&
    result.stale.length === 0
  );
}

// ---------------------------------------------------------------------------------------------
// Reporting
// ---------------------------------------------------------------------------------------------

const LABELS = { npm: 'npm', nuget: 'NuGet' };

export function render(result, invalid = []) {
  const lines = [];
  const failures = []; // one line each, for CI annotations
  const warnings = [];
  const total = result.blocking.length + result.accepted.length + result.expired.length + result.belowGate.length;

  lines.push(`== ${LABELS[result.ecosystem]} advisory gate, ${result.today}, fails on high and critical ==`);
  lines.push(
    `Read ${total} advisories: ${result.blocking.length} high/critical not allow-listed, ` +
      `${result.accepted.length} accepted, ${result.expired.length} accepted but past their review date, ` +
      `${result.belowGate.length} below the gate.`
  );

  for (const message of invalid) {
    lines.push(`  INVALID ENTRY  ${message}`);
    failures.push(`Allow-list: ${message}`);
  }
  for (const finding of result.blocking) {
    lines.push(`  BLOCKED  ${finding.advisory}  ${finding.package}  [${finding.severity}]  ${finding.detail}`);
    if (finding.title) {
      lines.push(`           ${finding.title}`);
    }
    lines.push(`           ${finding.url}`);
    if (finding.fix) {
      lines.push(`           ${finding.fix}`);
    }
    failures.push(`${finding.ecosystem} ${finding.severity} advisory ${finding.advisory} in ${finding.package} is not allow-listed.`);
  }
  for (const { finding, entry } of result.expired) {
    lines.push(`  EXPIRED  ${finding.advisory}  ${finding.package}  [${finding.severity}]  review date ${entry.reviewBy} has passed`);
    lines.push(`           accepted by ${entry.acceptedBy} on ${entry.acceptedOn}: ${entry.reason}`);
    lines.push('           Review it: fix the dependency, or re-accept with a new reason, acceptedOn and reviewBy.');
    failures.push(`Allow-list entry for ${finding.advisory} (${finding.package}) passed its review date ${entry.reviewBy}.`);
  }
  for (const { entry, nowRated } of result.stale) {
    const why = nowRated
      ? `it is now rated ${nowRated}, below the gate`
      : 'the advisory is no longer reported';
    lines.push(`  STALE    ${entry.advisory}  ${entry.package}  ${why}: delete this allow-list entry`);
    failures.push(`Allow-list entry for ${entry.advisory} (${entry.package}) is stale (${why}); delete it.`);
  }
  for (const { finding, entry } of result.accepted) {
    const left = daysBetween(parseIsoDate(result.today), parseIsoDate(entry.reviewBy));
    lines.push(`  accepted ${finding.advisory}  ${finding.package}  [${finding.severity}]  until ${entry.reviewBy} (${left} days), by ${entry.acceptedBy}`);
    lines.push(`           ${entry.reason}`);
    if (left <= REVIEW_SOON_DAYS) {
      warnings.push(`Allow-list entry for ${finding.advisory} (${finding.package}) is due for review by ${entry.reviewBy}.`);
    }
  }
  for (const finding of result.belowGate) {
    lines.push(`  below the gate  ${finding.advisory}  ${finding.package}  [${finding.severity}]`);
  }

  const ok = isPassing(result, invalid);
  lines.push(
    ok
      ? `PASS: ${LABELS[result.ecosystem]} has no high or critical advisory outside the allow-list.`
      : `FAIL: ${failures.length} problem(s). Fix the dependency; only if no safe fix exists, add a dated entry to tools/audit/audit-allowlist.json (docs/06 section 4).`
  );
  return { lines, failures, warnings, ok };
}

// ---------------------------------------------------------------------------------------------
// Running the tools
// ---------------------------------------------------------------------------------------------

const MAX_BUFFER = 256 * 1024 * 1024;

function runNpmAudit(frontendDir) {
  // `npm` is npm.cmd on Windows, which only a shell resolves. The command is a constant string with
  // nothing interpolated, which also avoids Node's warning about passing an args array with a shell.
  const run = spawnSync('npm audit --json', {
    cwd: frontendDir,
    encoding: 'utf8',
    maxBuffer: MAX_BUFFER,
    shell: true,
  });
  if (run.error) {
    throw new GateError(`Could not start npm: ${run.error.message}`);
  }
  // Exit status 1 is the normal answer when advisories exist, so the status says nothing: the JSON does.
  if (!run.stdout?.trim()) {
    throw new GateError(`npm audit printed nothing (exit ${run.status}). ${String(run.stderr ?? '').slice(0, 300)}`);
  }
  return run.stdout;
}

function runDotnetList(solution, restore) {
  if (restore) {
    const restored = spawnSync('dotnet', ['restore', solution, '-p:NuGetAudit=false', '-nologo', '-v:q'], {
      encoding: 'utf8',
      maxBuffer: MAX_BUFFER,
    });
    if (restored.error || restored.status !== 0) {
      throw new GateError(
        `dotnet restore failed (exit ${restored.status}), so there is nothing to audit.\n${restored.error?.message ?? ''}${restored.stdout ?? ''}${restored.stderr ?? ''}`.trimEnd()
      );
    }
  }
  const run = spawnSync(
    'dotnet',
    ['list', solution, 'package', '--vulnerable', '--include-transitive', '--format', 'json', '--no-restore'],
    { encoding: 'utf8', maxBuffer: MAX_BUFFER }
  );
  if (run.error) {
    throw new GateError(`Could not start dotnet: ${run.error.message}`);
  }
  if (!run.stdout?.trim()) {
    throw new GateError(`dotnet list package printed nothing (exit ${run.status}). ${String(run.stderr ?? '').slice(0, 300)}`);
  }
  return run.stdout;
}

// ---------------------------------------------------------------------------------------------
// Command line
// ---------------------------------------------------------------------------------------------

export function parseArgs(argv) {
  const options = {
    target: 'all',
    allowlist: DEFAULT_ALLOWLIST,
    input: null,
    today: null,
    restore: true,
    frontend: resolve(REPO_ROOT, 'src', 'frontend'),
    solution: resolve(REPO_ROOT, 'SpencerDenim.sln'),
    help: false,
  };
  const args = [...argv];
  const value = (flag) => {
    if (args.length === 0) {
      throw new GateError(`${flag} needs a value.`);
    }
    return args.shift();
  };

  while (args.length > 0) {
    const arg = args.shift();
    switch (arg) {
      case 'npm':
      case 'nuget':
      case 'all':
        options.target = arg;
        break;
      case '--allowlist':
        options.allowlist = resolve(value(arg));
        break;
      case '--input':
        options.input = resolve(value(arg));
        break;
      case '--today':
        options.today = value(arg);
        if (!parseIsoDate(options.today)) {
          throw new GateError('--today must be a real date written YYYY-MM-DD.');
        }
        break;
      case '--frontend':
        options.frontend = resolve(value(arg));
        break;
      case '--solution':
        options.solution = resolve(value(arg));
        break;
      case '--no-restore':
        options.restore = false;
        break;
      case '-h':
      case '--help':
        options.help = true;
        break;
      default:
        throw new GateError(`Unknown argument "${arg}". Try --help.`);
    }
  }
  if (options.input && options.target === 'all') {
    throw new GateError('--input names one report, so say which: "npm" or "nuget".');
  }
  return options;
}

function gateOne(ecosystem, options, today, allowlist) {
  let findings;
  if (ecosystem === 'npm') {
    findings = parseNpmAudit(options.input ? readFileSync(options.input, 'utf8') : runNpmAudit(options.frontend));
  } else {
    findings = parseDotnetList(options.input ? readFileSync(options.input, 'utf8') : runDotnetList(options.solution, options.restore));
  }
  const result = evaluate({ ecosystem, findings, entries: allowlist.entries, today });
  return render(result, allowlist.invalid);
}

export function main(argv) {
  let options;
  try {
    options = parseArgs(argv);
  } catch (error) {
    console.error(`audit-gate: ${error.message}`);
    return EXIT_TOOL;
  }
  if (options.help) {
    const source = readFileSync(fileURLToPath(import.meta.url), 'utf8');
    console.log(source.slice(source.indexOf('/**'), source.indexOf('*/') + 2));
    return EXIT_PASS;
  }

  const today = options.today ?? utcToday();
  let allowlist;
  try {
    allowlist = readAllowlist(parseJson(readFileSync(options.allowlist, 'utf8'), `allow-list ${relative(process.cwd(), options.allowlist)}`), today);
  } catch (error) {
    console.error(`audit-gate: ${error instanceof GateError ? error.message : `cannot read the allow-list: ${error.message}`}`);
    return EXIT_TOOL;
  }

  const ecosystems = options.target === 'all' ? ECOSYSTEMS : [options.target];
  const inCi = process.env.GITHUB_ACTIONS === 'true';
  let exit = EXIT_PASS;

  for (const ecosystem of ecosystems) {
    try {
      const { lines, failures, warnings, ok } = gateOne(ecosystem, options, today, allowlist);
      console.log(lines.join('\n'));
      if (inCi) {
        failures.forEach((m) => console.log(`::error title=Dependency audit (${ecosystem})::${m}`));
        warnings.forEach((m) => console.log(`::warning title=Dependency audit (${ecosystem})::${m}`));
      }
      if (!ok) {
        exit = Math.max(exit, EXIT_POLICY);
      }
    } catch (error) {
      if (!(error instanceof GateError)) {
        throw error;
      }
      console.error(`ERROR: the ${ecosystem} audit could not be carried out, which is NOT the same as clean.\n${error.message}`);
      if (inCi) {
        console.log(`::error title=Dependency audit (${ecosystem})::${error.message.split('\n')[0]}`);
      }
      exit = Math.max(exit, EXIT_TOOL);
    }
    console.log('');
  }
  return exit;
}

// Run only when executed directly, not when the tests import it.
const invokedDirectly = (() => {
  try {
    return process.argv[1] !== undefined && realpathSync(process.argv[1]) === realpathSync(fileURLToPath(import.meta.url));
  } catch {
    return false;
  }
})();
if (invokedDirectly) {
  process.exitCode = main(process.argv.slice(2));
}
