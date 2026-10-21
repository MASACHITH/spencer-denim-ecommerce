// Tests for tools/audit/audit-gate.mjs. Run:  node --test tools/audit/audit-gate.test.mjs
//
// The point of these is the failing direction: a gate that has only ever been seen green proves
// nothing. Every rule that makes it go red (an unlisted advisory, an expired entry, a stale entry, a
// malformed entry, an audit that could not run) is exercised here, both as functions and end to end
// through the command line, with a fixed --today so the suite does not rot as the calendar moves.
//
// The report shapes are the real ones: npm's from `npm audit --json` (auditReportVersion 2) and the
// NuGet one from `dotnet list package --vulnerable --include-transitive --format json` (version 1),
// both captured on 2026-10-17 and trimmed. Paths are made up.
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { delimiter, join } from 'node:path';
import { after, describe, test } from 'node:test';
import { fileURLToPath } from 'node:url';

import {
  DEFAULT_ALLOWLIST,
  GateError,
  evaluate,
  findingKey,
  isBlocking,
  isPassing,
  normaliseAdvisory,
  parseDotnetList,
  parseIsoDate,
  parseNpmAudit,
  readAllowlist,
  render,
} from './audit-gate.mjs';

const GATE = fileURLToPath(new URL('./audit-gate.mjs', import.meta.url));
const TODAY = '2026-10-17';

// ---- fixtures --------------------------------------------------------------------------------

const PISCINA_URL = 'https://github.com/advisories/GHSA-67c8-pqhq-4rmx';

function npmAdvisory(overrides = {}) {
  return {
    source: 1240852,
    name: 'piscina',
    dependency: 'piscina',
    title: 'piscina: Prototype-pollution gadget in ThreadPool.options allows RCE',
    url: PISCINA_URL,
    severity: 'critical',
    range: '5.0.0 - 5.3.1',
    ...overrides,
  };
}

/** A report in which `piscina` is vulnerable and `@angular/build` is merely downstream of it. */
function npmReport({ advisories = [npmAdvisory()], fixAvailable = { name: '@angular/build', version: '19.2.27', isSemVerMajor: true } } = {}) {
  const vulnerabilities = {};
  for (const advisory of advisories) {
    vulnerabilities[advisory.name] = { name: advisory.name, severity: advisory.severity, via: [advisory], fixAvailable: true };
  }
  if (advisories.length > 0) {
    vulnerabilities['@angular/build'] = { name: '@angular/build', severity: 'critical', via: [advisories[0].name], fixAvailable };
  }
  const counts = { info: 0, low: 0, moderate: 0, high: 0, critical: 0, total: Object.keys(vulnerabilities).length };
  return { auditReportVersion: 2, vulnerabilities, metadata: { vulnerabilities: counts } };
}

const cleanNpm = () => ({ auditReportVersion: 2, vulnerabilities: {}, metadata: { vulnerabilities: { total: 0 } } });

const NUGET_URL = 'https://github.com/advisories/GHSA-5crp-9r3c-p9vr';

function nugetReport(frameworks) {
  return {
    version: 1,
    parameters: '--vulnerable --include-transitive',
    sources: ['https://api.nuget.org/v3/index.json'],
    projects: [{ path: 'C:/repo/src/backend/Api/Api.csproj', frameworks }],
  };
}

const NUGET_FRAMEWORK = {
  framework: 'net10.0',
  topLevelPackages: [
    {
      id: 'Newtonsoft.Json',
      requestedVersion: '12.0.1',
      resolvedVersion: '12.0.1',
      vulnerabilities: [{ severity: 'High', advisoryurl: NUGET_URL }],
    },
  ],
  transitivePackages: [
    {
      id: 'Azure.Identity',
      resolvedVersion: '1.7.0',
      vulnerabilities: [
        { severity: 'Moderate', advisoryurl: 'https://github.com/advisories/GHSA-m5vv-6r4h-3vj9' },
        { severity: 'High', advisoryurl: 'https://github.com/advisories/GHSA-5mfx-4wcx-rv27' },
      ],
    },
  ],
};

function entry(overrides = {}) {
  return {
    ecosystem: 'npm',
    advisory: 'GHSA-67c8-pqhq-4rmx',
    package: 'piscina',
    severity: 'critical',
    reason: 'Build-time worker pool only; needs control of ThreadPool.options on the build machine.',
    acceptedBy: 'MASACHITH',
    acceptedOn: '2026-10-17',
    reviewBy: '2026-11-17',
    ...overrides,
  };
}

const allowlist = (...entries) => ({ entries });

// ---- identity and dates ----------------------------------------------------------------------

describe('identity, severity and dates', () => {
  test('GHSA ids compare case-insensitively after the prefix; other ids are kept as typed', () => {
    assert.equal(normaliseAdvisory(' GHSA-67C8-PQHQ-4RMX '), 'GHSA-67c8-pqhq-4rmx');
    assert.equal(normaliseAdvisory('ghsa-67c8-pqhq-4rmx'), 'GHSA-67c8-pqhq-4rmx');
    assert.equal(normaliseAdvisory('CVE-2026-0001'), 'CVE-2026-0001');
    assert.equal(findingKey('npm', 'GHSA-67C8-PQHQ-4RMX', 'Piscina'), findingKey('npm', 'GHSA-67c8-pqhq-4rmx', 'piscina'));
  });

  test('high and critical block; so does any label the gate does not know', () => {
    for (const severity of ['high', 'critical', 'unknown', '', 'urgent']) {
      assert.equal(isBlocking(severity), true, severity);
    }
    for (const severity of ['info', 'low', 'moderate']) {
      assert.equal(isBlocking(severity), false, severity);
    }
  });

  test('dates must be real calendar dates', () => {
    assert.ok(parseIsoDate('2026-10-17'));
    for (const bad of ['2026-02-31', '2026-13-01', '17/10/2026', '2026-1-1', '', null, 20261017]) {
      assert.equal(parseIsoDate(bad), null, String(bad));
    }
  });
});

// ---- the npm report --------------------------------------------------------------------------

describe('parseNpmAudit', () => {
  test('reads the advisory, not the packages that merely depend on the vulnerable one', () => {
    const findings = parseNpmAudit(JSON.stringify(npmReport()));
    assert.equal(findings.length, 1);
    assert.deepEqual(
      { ecosystem: findings[0].ecosystem, advisory: findings[0].advisory, package: findings[0].package, severity: findings[0].severity },
      { ecosystem: 'npm', advisory: 'GHSA-67c8-pqhq-4rmx', package: 'piscina', severity: 'critical' }
    );
  });

  test('says when npm\'s suggested fix is a semver-major change, which is how a downgrade presents', () => {
    const [finding] = parseNpmAudit(JSON.stringify(npmReport({ advisories: [npmAdvisory()] })));
    // The advisory sits on piscina, whose own fixAvailable is `true` in this fixture.
    assert.match(finding.fix, /fix inside the current version ranges/);

    const report = npmReport();
    report.vulnerabilities.piscina.fixAvailable = { name: '@angular/build', version: '19.2.27', isSemVerMajor: true };
    const [major] = parseNpmAudit(JSON.stringify(report));
    assert.match(major.fix, /SEMVER-MAJOR/);
  });

  test('an installed tree with no advisories is clean', () => {
    assert.deepEqual(parseNpmAudit(JSON.stringify(cleanNpm())), []);
  });

  test('an audit that could not run is an error, never an empty report', () => {
    const offline = { error: { code: 'ENOTFOUND', summary: 'request to https://registry.npmjs.org failed', detail: '' } };
    assert.throws(() => parseNpmAudit(JSON.stringify(offline)), (e) => e instanceof GateError && /ENOTFOUND/.test(e.message));
  });

  test('the offline report npm really prints names the reason, from the top-level message', () => {
    const offline = {
      message: 'request to https://registry.npmjs.org/-/npm/v1/security/advisories/bulk failed, reason: getaddrinfo ENOTFOUND registry.npmjs.org',
      error: { summary: '', detail: '' },
    };
    assert.throws(() => parseNpmAudit(JSON.stringify(offline)), (e) => e instanceof GateError && /ENOTFOUND registry.npmjs.org/.test(e.message));
  });

  test('output that is empty, not JSON, or from another report version is an error', () => {
    assert.throws(() => parseNpmAudit(''), GateError);
    assert.throws(() => parseNpmAudit('npm ERR! something'), GateError);
    assert.throws(() => parseNpmAudit(JSON.stringify({ auditReportVersion: 3, vulnerabilities: {} })), GateError);
    assert.throws(() => parseNpmAudit(JSON.stringify({ vulnerabilities: {} })), GateError);
  });

  test('a summary that counts vulnerable packages while no advisory can be read is an error', () => {
    const report = { auditReportVersion: 2, vulnerabilities: { x: { name: 'x', via: ['y'] } }, metadata: { vulnerabilities: { total: 1 } } };
    assert.throws(() => parseNpmAudit(JSON.stringify(report)), (e) => e instanceof GateError && /no advisory could be read/.test(e.message));
  });

  test('a report with no vulnerability summary is an error, not a clean bill of health', () => {
    // It used to pass: no `metadata` and no vulnerabilities read as zero advisories (#84's tester).
    const noMetadata = { auditReportVersion: 2, vulnerabilities: {} };
    assert.throws(() => parseNpmAudit(JSON.stringify(noMetadata)), (e) => e instanceof GateError && /summary/.test(e.message));
    assert.throws(() => parseNpmAudit(JSON.stringify({ ...noMetadata, metadata: { vulnerabilities: {} } })), GateError);
    assert.throws(() => parseNpmAudit(JSON.stringify({ ...noMetadata, metadata: { vulnerabilities: { total: '0' } } })), GateError);
  });

  test('a shape inside a report that the gate does not know is an error it names, never an uncaught exception', () => {
    const odd = npmReport();
    odd.vulnerabilities.piscina.via = { not: 'a list' };
    assert.throws(() => parseNpmAudit(JSON.stringify(odd)), (e) => e instanceof GateError && /via/.test(e.message));

    // `fixAvailable: null` used to throw a TypeError out of the describer.
    const noFix = npmReport();
    noFix.vulnerabilities.piscina.fixAvailable = null;
    const [finding] = parseNpmAudit(JSON.stringify(noFix));
    assert.match(finding.fix, /no fix is published/);
  });
});

// ---- the NuGet report ------------------------------------------------------------------------

describe('parseDotnetList', () => {
  test('reads top-level and transitive packages, one finding per advisory and package', () => {
    const findings = parseDotnetList(JSON.stringify(nugetReport([NUGET_FRAMEWORK, NUGET_FRAMEWORK])));
    assert.deepEqual(
      findings.map((f) => [f.advisory, f.package, f.severity]).sort(),
      [
        ['GHSA-5crp-9r3c-p9vr', 'Newtonsoft.Json', 'high'],
        ['GHSA-5mfx-4wcx-rv27', 'Azure.Identity', 'high'],
        ['GHSA-m5vv-6r4h-3vj9', 'Azure.Identity', 'moderate'],
      ]
    );
  });

  test('a project with no vulnerable packages is clean', () => {
    assert.deepEqual(parseDotnetList(JSON.stringify(nugetReport(undefined))), []);
  });

  test('any reported problem is an error: the report may be incomplete', () => {
    const report = nugetReport(undefined);
    report.problems = [{ project: 'C:/repo/Api.csproj', level: 'error', text: 'No assets file was found. Please run restore.' }];
    assert.throws(() => parseDotnetList(JSON.stringify(report)), (e) => e instanceof GateError && /run restore/.test(e.message));
  });

  test('an unfamiliar report version, or no JSON at all, is an error', () => {
    assert.throws(() => parseDotnetList(JSON.stringify({ ...nugetReport(undefined), version: 2 })), GateError);
    assert.throws(() => parseDotnetList('error: Unable to load the service index for source https://example.invalid'), GateError);
    assert.throws(() => parseDotnetList(''), GateError);
  });

  test('a report with no projects, or with packages under a key the gate does not read, is an error', () => {
    // Both used to read as "0 advisories" (#84's tester): nothing audited, or the format moved under us.
    assert.throws(
      () => parseDotnetList(JSON.stringify({ ...nugetReport(undefined), projects: [] })),
      (e) => e instanceof GateError && /no projects/.test(e.message)
    );
    const moved = nugetReport([{ framework: 'net10.0', packages: NUGET_FRAMEWORK.topLevelPackages }]);
    assert.throws(() => parseDotnetList(JSON.stringify(moved)), (e) => e instanceof GateError && /"packages"/.test(e.message));
  });
});

// ---- the allow-list --------------------------------------------------------------------------

describe('readAllowlist', () => {
  test('a complete entry is accepted and its advisory id normalised', () => {
    const { entries, invalid } = readAllowlist(allowlist(entry({ advisory: 'GHSA-67C8-PQHQ-4RMX' })), TODAY);
    assert.deepEqual(invalid, []);
    assert.equal(entries[0].advisory, 'GHSA-67c8-pqhq-4rmx');
  });

  test('an empty list is valid', () => {
    assert.deepEqual(readAllowlist(allowlist(), TODAY), { entries: [], invalid: [] });
  });

  const rejected = {
    'no ecosystem': { ecosystem: undefined },
    'unknown ecosystem': { ecosystem: 'pip' },
    'no advisory': { advisory: '' },
    'no package': { package: '  ' },
    'no accepted-by': { acceptedBy: undefined },
    'no reason': { reason: undefined },
    'a reason too thin to mean anything': { reason: 'build only' },
    'no acceptedOn': { acceptedOn: undefined },
    'an acceptedOn that is not a date': { acceptedOn: '2026-02-31' },
    'an acceptedOn in the future': { acceptedOn: '2026-10-18', reviewBy: '2026-11-18' },
    'no reviewBy': { reviewBy: undefined },
    'a reviewBy that is not a date': { reviewBy: 'soon' },
    'a reviewBy on or before acceptedOn': { reviewBy: '2026-10-17' },
    'a reviewBy further out than 90 days': { reviewBy: '2027-01-16' },
  };
  for (const [name, overrides] of Object.entries(rejected)) {
    test(`rejects an entry with ${name}`, () => {
      const { entries, invalid } = readAllowlist(allowlist(entry(overrides)), TODAY);
      assert.equal(entries.length, 0);
      assert.equal(invalid.length, 1);
    });
  }

  test('90 days is the longest a decision may run; the day after is rejected', () => {
    assert.equal(readAllowlist(allowlist(entry({ reviewBy: '2027-01-15' })), TODAY).invalid.length, 0);
    assert.equal(readAllowlist(allowlist(entry({ reviewBy: '2027-01-16' })), TODAY).invalid.length, 1);
  });

  test('a duplicate of an earlier entry is rejected, even in another letter case', () => {
    const { entries, invalid } = readAllowlist(allowlist(entry(), entry({ advisory: 'ghsa-67c8-pqhq-4rmx', package: 'Piscina' })), TODAY);
    assert.equal(entries.length, 1);
    assert.equal(invalid.length, 1);
  });

  test('a document that is not { entries: [] } is an error', () => {
    for (const bad of [null, [], {}, { entries: 'none' }]) {
      assert.throws(() => readAllowlist(bad, TODAY), GateError);
    }
  });
});

// ---- the decision ----------------------------------------------------------------------------

describe('evaluate', () => {
  const findings = (...severities) =>
    severities.map((severity, i) => ({
      ecosystem: 'npm',
      advisory: `GHSA-aaaa-bbbb-cc${i}${i}`,
      package: `pkg${i}`,
      severity,
      title: '',
      url: '',
      detail: '',
    }));
  const run = (found, entries, today = TODAY, ecosystem = 'npm') => evaluate({ ecosystem, findings: found, entries, today });

  test('an unlisted high or critical advisory blocks; lower severities do not', () => {
    const result = run(findings('critical', 'high', 'moderate', 'low'), []);
    assert.equal(result.blocking.length, 2);
    assert.equal(result.belowGate.length, 2);
    assert.equal(isPassing(result), false);
  });

  test('a tree with no advisories passes', () => {
    assert.equal(isPassing(run([], [])), true);
  });

  test('a listed advisory in date is accepted, including on the review date itself', () => {
    const found = parseNpmAudit(JSON.stringify(npmReport()));
    for (const today of ['2026-10-17', '2026-11-16', '2026-11-17']) {
      const result = run(found, [entry()], today);
      assert.equal(result.accepted.length, 1, today);
      assert.equal(isPassing(result), true, today);
    }
  });

  test('the day after reviewBy the acceptance has expired and the gate is red', () => {
    const found = parseNpmAudit(JSON.stringify(npmReport()));
    const result = run(found, [entry()], '2026-11-18');
    assert.equal(result.accepted.length, 0);
    assert.equal(result.expired.length, 1);
    assert.equal(isPassing(result), false);
    assert.match(render(result).lines.join('\n'), /EXPIRED.*GHSA-67c8-pqhq-4rmx/);
  });

  test('an entry whose advisory is no longer reported is stale and the gate is red', () => {
    const result = run([], [entry()]);
    assert.equal(result.stale.length, 1);
    assert.equal(isPassing(result), false);
    assert.match(render(result).lines.join('\n'), /STALE.*no longer reported/);
  });

  test('an entry whose advisory has been re-rated below the gate is stale too', () => {
    const found = parseNpmAudit(JSON.stringify(npmReport({ advisories: [npmAdvisory({ severity: 'moderate' })] })));
    const result = run(found, [entry()]);
    assert.equal(result.stale.length, 1);
    assert.equal(result.stale[0].nowRated, 'moderate');
  });

  test('an entry matches on advisory and package, in its own ecosystem only', () => {
    const found = parseNpmAudit(JSON.stringify(npmReport()));
    assert.equal(run(found, [entry({ package: 'another-package' })]).blocking.length, 1);
    assert.equal(run(found, [entry({ advisory: 'GHSA-zzzz-zzzz-zzzz' })]).blocking.length, 1);
    const asNuget = run(found, [entry({ ecosystem: 'nuget' })]);
    assert.equal(asNuget.blocking.length, 1, 'a NuGet entry must not accept an npm advisory');
    assert.equal(asNuget.stale.length, 0, 'and is not this ecosystem\'s to call stale');
  });

  test('an invalid allow-list entry fails the run even when nothing is wrong with the tree', () => {
    assert.equal(isPassing(run([], []), ['allow-list entry 1: "reason" is required.']), false);
  });
});

// ---- end to end through the command line -----------------------------------------------------

describe('command line', () => {
  const dir = mkdtempSync(join(tmpdir(), 'audit-gate-test-'));
  after(() => rmSync(dir, { recursive: true, force: true }));
  let counter = 0;

  function write(name, value) {
    const file = join(dir, `${counter++}-${name}`);
    writeFileSync(file, typeof value === 'string' ? value : JSON.stringify(value));
    return file;
  }

  function gate(target, report, list, extra = []) {
    const run = spawnSync(
      process.execPath,
      [GATE, target, '--input', write('report.json', report), '--allowlist', write('allowlist.json', list), ...extra],
      { encoding: 'utf8', env: { ...process.env, GITHUB_ACTIONS: '' } }
    );
    return { status: run.status, out: run.stdout + run.stderr };
  }

  test('GREEN: an audit with nothing in it', () => {
    const run = gate('npm', cleanNpm(), allowlist(), ['--today', TODAY]);
    assert.equal(run.status, 0, run.out);
    assert.match(run.out, /PASS/);
  });

  test('RED: a fabricated critical advisory that is not in the allow-list', () => {
    const fabricated = npmReport({ advisories: [npmAdvisory({ name: 'left-padz', url: 'https://github.com/advisories/GHSA-fake-fake-fake', severity: 'critical' })] });
    const run = gate('npm', fabricated, allowlist(), ['--today', TODAY]);
    assert.equal(run.status, 1, run.out);
    assert.match(run.out, /BLOCKED\s+GHSA-fake-fake-fake\s+left-padz/);
    assert.match(run.out, /FAIL/);
  });

  test('RED: a real-shaped NuGet high advisory that is not in the allow-list', () => {
    const run = gate('nuget', nugetReport([NUGET_FRAMEWORK]), allowlist(), ['--today', TODAY]);
    assert.equal(run.status, 1, run.out);
    assert.match(run.out, /BLOCKED\s+GHSA-5crp-9r3c-p9vr\s+Newtonsoft\.Json/);
  });

  test('GREEN, then RED: the same advisory allow-listed, then past its review date', () => {
    const report = npmReport();
    const list = allowlist(entry());
    const inDate = gate('npm', report, list, ['--today', '2026-10-20']);
    assert.equal(inDate.status, 0, inDate.out);
    assert.match(inDate.out, /accepted\s+GHSA-67c8-pqhq-4rmx/);

    const expired = gate('npm', report, list, ['--today', '2026-11-18']);
    assert.equal(expired.status, 1, expired.out);
    assert.match(expired.out, /EXPIRED/);
  });

  test('RED: an allow-list entry for an advisory that has disappeared', () => {
    const run = gate('npm', cleanNpm(), allowlist(entry()), ['--today', TODAY]);
    assert.equal(run.status, 1, run.out);
    assert.match(run.out, /STALE/);
  });

  test('RED: a malformed allow-list entry, even against a clean tree', () => {
    const run = gate('npm', cleanNpm(), allowlist(entry({ reason: 'ok' })), ['--today', TODAY]);
    assert.equal(run.status, 1, run.out);
    assert.match(run.out, /INVALID ENTRY/);
  });

  test('ERROR (2), not green: the audit could not run, or the input is not a report', () => {
    const offline = gate('npm', { error: { code: 'ENOTFOUND', summary: 'offline' } }, allowlist());
    assert.equal(offline.status, 2, offline.out);
    assert.match(offline.out, /NOT the same as clean/);
    assert.equal(gate('nuget', 'error: Unable to load the service index', allowlist()).status, 2);
  });

  test('ERROR (2): an allow-list that is not JSON, a bad flag, --input without saying which tool', () => {
    assert.equal(gate('npm', cleanNpm(), 'not json').status, 2);
    const noTarget = spawnSync(process.execPath, [GATE, '--input', write('r.json', cleanNpm())], { encoding: 'utf8' });
    assert.equal(noTarget.status, 2);
    const badFlag = spawnSync(process.execPath, [GATE, '--frobnicate'], { encoding: 'utf8' });
    assert.equal(badFlag.status, 2);
    const badDate = spawnSync(process.execPath, [GATE, 'npm', '--today', '2026-02-31'], { encoding: 'utf8' });
    assert.equal(badDate.status, 2);
  });

  test('CI annotations are printed only under GitHub Actions', () => {
    const args = [GATE, 'npm', '--input', write('r.json', npmReport()), '--allowlist', write('a.json', allowlist()), '--today', TODAY];
    const local = spawnSync(process.execPath, args, { encoding: 'utf8', env: { ...process.env, GITHUB_ACTIONS: '' } });
    const ci = spawnSync(process.execPath, args, { encoding: 'utf8', env: { ...process.env, GITHUB_ACTIONS: 'true' } });
    assert.doesNotMatch(local.stdout, /::error/);
    assert.match(ci.stdout, /::error title=Dependency audit \(npm\)::npm critical advisory GHSA-67c8-pqhq-4rmx in piscina is not allow-listed/);
  });

  test('an allow-list entry close to its review date is a warning, annotated under GitHub Actions only', () => {
    const args = [GATE, 'npm', '--input', write('r.json', npmReport()), '--allowlist', write('a.json', allowlist(entry({ reviewBy: '2026-10-22' }))), '--today', TODAY];
    const local = spawnSync(process.execPath, args, { encoding: 'utf8', env: { ...process.env, GITHUB_ACTIONS: '' } });
    const ci = spawnSync(process.execPath, args, { encoding: 'utf8', env: { ...process.env, GITHUB_ACTIONS: 'true' } });
    assert.equal(local.status, 0, local.stdout + local.stderr);
    assert.doesNotMatch(local.stdout, /::warning/);
    assert.equal(ci.status, 0, ci.stdout + ci.stderr);
    assert.match(ci.stdout, /::warning title=Dependency audit \(npm\)::Allow-list entry for GHSA-67c8-pqhq-4rmx \(piscina\) is due for review by 2026-10-22/);
  });

  test('ERROR (2): an input that cannot be read is reported, not thrown (no stack trace, never green)', () => {
    const run = spawnSync(process.execPath, [GATE, 'npm', '--input', dir, '--allowlist', write('a.json', allowlist())], { encoding: 'utf8' });
    assert.equal(run.status, 2, run.stdout + run.stderr);
    assert.match(run.stderr, /NOT the same as clean/);
    assert.match(run.stderr, /unexpected failure/);
  });

  // ---- the real spawn paths, against a fake `npm` and `dotnet` placed first on PATH ----------------------------
  //
  // Every test above feeds the gate a saved report. These run the command the gate actually spawns, which is where
  // #84's tester found it green on the vulnerable lockfile (NODE_ENV=production hid every dev dependency).

  const AMBIENT = ['NODE_ENV', 'npm_config_omit', 'npm_config_production', 'npm_config_include'];

  /** A folder holding fake tools. Each is a node script; a shim makes it run as `<name>` from a shell or a spawn. */
  function fakeTools(tools) {
    const bin = mkdtempSync(join(dir, 'bin-'));
    for (const [name, source] of Object.entries(tools)) {
      writeFileSync(join(bin, `${name}.mjs`), source);
      if (process.platform === 'win32') {
        writeFileSync(join(bin, `${name}.cmd`), `@echo off\r\n"${process.execPath}" "%~dp0${name}.mjs" %*\r\n`);
      } else {
        writeFileSync(join(bin, name), `#!/bin/sh\nexec "${process.execPath}" "$(dirname "$0")/${name}.mjs" "$@"\n`, { mode: 0o755 });
      }
    }
    return bin;
  }

  /** The environment with the fake tools first on PATH and none of npm's own switches set unless asked for. */
  function envWith(bin, extra = {}) {
    const env = { ...process.env, GITHUB_ACTIONS: '' };
    for (const name of AMBIENT) {
      delete env[name];
    }
    const pathKey = Object.keys(env).find((key) => key.toUpperCase() === 'PATH') ?? 'PATH';
    env[pathKey] = `${bin}${delimiter}${env[pathKey] ?? ''}`;
    return { ...env, ...extra };
  }

  /** What `npm audit` does: leaves the dev dependencies out when told to by the environment, unless `--include=dev`. */
  const FAKE_NPM = `
const args = process.argv.slice(2);
const e = process.env;
const omitsDev = !args.includes('--include=dev') &&
  (e.NODE_ENV === 'production' || e.npm_config_omit === 'dev' || e.npm_config_production === 'true');
process.stdout.write(JSON.stringify(omitsDev ? ${JSON.stringify(cleanNpm())} : ${JSON.stringify(npmReport())}));
process.exit(omitsDev ? 0 : 1);
`;

  for (const [label, ambient] of [
    ['nothing set', {}],
    ['NODE_ENV=production', { NODE_ENV: 'production' }],
    ['npm_config_omit=dev', { npm_config_omit: 'dev' }],
    ['npm_config_production=true', { npm_config_production: 'true' }],
  ]) {
    test(`RED whatever npm is told to omit: ${label}`, () => {
      const bin = fakeTools({ npm: FAKE_NPM });
      const frontend = mkdtempSync(join(dir, 'frontend-'));
      const run = spawnSync(process.execPath, [GATE, 'npm', '--frontend', frontend, '--allowlist', write('a.json', allowlist()), '--today', TODAY], {
        encoding: 'utf8',
        env: envWith(bin, ambient),
      });
      assert.equal(run.status, 1, run.stdout + run.stderr);
      assert.match(run.stdout, /BLOCKED\s+GHSA-67c8-pqhq-4rmx\s+piscina/);
    });
  }

  test('ERROR (2): a NuGet restore that fails stops the audit instead of being ignored', {
    skip: process.platform === 'win32' && 'a fake dotnet is a POSIX shim; Node will not spawn a .cmd without a shell',
  }, () => {
    const bin = fakeTools({
      dotnet: `
const args = process.argv.slice(2);
if (args[0] === 'restore') { console.error('error NU1301: Unable to load the service index'); process.exit(1); }
process.stdout.write(JSON.stringify(${JSON.stringify(nugetReport(undefined))}));
`,
    });
    const run = spawnSync(process.execPath, [GATE, 'nuget', '--solution', join(dir, 'x.sln'), '--allowlist', write('a.json', allowlist()), '--today', TODAY], {
      encoding: 'utf8',
      env: envWith(bin),
    });
    assert.equal(run.status, 2, run.stdout + run.stderr);
    assert.match(run.stderr, /dotnet restore failed/);
  });
});

// ---- the checked-in allow-list ---------------------------------------------------------------

describe('tools/audit/audit-allowlist.json', () => {
  test('is valid JSON in the documented shape, with every entry complete', () => {
    const document = JSON.parse(readFileSync(DEFAULT_ALLOWLIST, 'utf8'));
    const today = new Date().toISOString().slice(0, 10);
    const { invalid } = readAllowlist(document, today);
    assert.deepEqual(invalid, []);
  });

  test('can run from a scratch folder without the repository around it', () => {
    // The gate resolves its own default paths, so a fresh working directory must not matter.
    // The report holds exactly the npm advisories the checked-in allow-list accepts: a clean report
    // would make every real entry STALE, so this test would go red the day the first entry lands.
    const accepted = JSON.parse(readFileSync(DEFAULT_ALLOWLIST, 'utf8')).entries
      .filter((e) => e.ecosystem === 'npm')
      .map((e) => npmAdvisory({ name: e.package, dependency: e.package, title: e.advisory, severity: e.severity,
        url: `https://github.com/advisories/${e.advisory}` }));
    const cwd = mkdtempSync(join(tmpdir(), 'audit-gate-cwd-'));
    try {
      mkdirSync(join(cwd, 'x'));
      const input = join(cwd, 'x', 'r.json');
      writeFileSync(input, JSON.stringify(accepted.length > 0 ? npmReport({ advisories: accepted }) : cleanNpm()));
      const run = spawnSync(process.execPath, [GATE, 'npm', '--input', input], { cwd, encoding: 'utf8' });
      assert.equal(run.status, 0, run.stdout + run.stderr);
    } finally {
      rmSync(cwd, { recursive: true, force: true });
    }
  });
});
