/**
 * The admin content stub's copy of the API's field rules (`ContentFieldRules.cs`, #192 tester D3).
 *
 * The stub must be no more permissive than the validators, or a screen passes here with a value the real
 * API refuses. JavaScript cannot run the C# rules, so the two are tied together by one table of cases,
 * `content-rule-cases.json`: `ContentFieldRulesParityTests` (C#) asserts the API agrees with every case, and
 * `admin-content-stub-parity.e2e.ts` asserts this module (through the stub) agrees with the same cases. A rule
 * changed on one side and not the other fails one of the two.
 */

const SAME_SITE_BASE = 'https://shop.invalid/';

/** A C0 or C1 control character, which .NET's `char.IsControl` refuses. */
function hasControl(value) {
  for (const character of value) {
    const code = character.codePointAt(0);
    if (code <= 0x1f || (code >= 0x7f && code <= 0x9f)) {
      return true;
    }
  }
  return false;
}

/** The reverse solidus, a control character or white space — refused in any link before it is parsed. */
function hasUnsafeCharacter(value) {
  return /[\\\s]/u.test(value) || hasControl(value);
}

/** `ContentFieldRules.IsSitePath`: one leading slash, nothing unsafe, and it stays on the site. */
export function isSitePath(value) {
  if (typeof value !== 'string' || !value.startsWith('/') || hasUnsafeCharacter(value)) {
    return false;
  }
  try {
    return new URL(value, SAME_SITE_BASE).origin === new URL(SAME_SITE_BASE).origin;
  } catch {
    return false;
  }
}

/** `ContentFieldRules.IsWebAddress`: absolute http(s), no credentials, a host. */
export function isWebAddress(value) {
  if (typeof value !== 'string' || hasUnsafeCharacter(value)) {
    return false;
  }
  let url;
  try {
    url = new URL(value);
  } catch {
    return false;
  }
  return (url.protocol === 'http:' || url.protocol === 'https:') && url.username === '' && url.password === '' && url.hostname.length > 0;
}

/**
 * `ContentFieldRules.OptionalText` / `RequiredText`: trimmed, blank is "nothing", bounded after trimming, and no
 * control characters. Returns the problem as a sentence, or null.
 */
export function textProblem(value, max, required = false) {
  if (value === null || value === undefined) {
    return required ? 'This is required.' : null;
  }
  if (typeof value !== 'string') {
    return 'Send this field as text.';
  }
  const cleaned = value.trim();
  if (cleaned.length === 0) {
    return required ? 'This is required.' : null;
  }
  if (cleaned.length > max) {
    return `Keep this to ${max} characters or fewer.`;
  }
  if (hasControl(cleaned)) {
    return 'This is a single line of plain text: remove line breaks and tabs.';
  }
  return null;
}

/** `ContentFieldRules.OptionalLink`. Returns the problem as a sentence, or null. */
export function linkProblem(value, mayLeaveSite) {
  if (value === null || value === undefined) {
    return null;
  }
  if (typeof value !== 'string') {
    return 'Send this field as text.';
  }
  const cleaned = value.trim();
  if (cleaned.length === 0) {
    return null;
  }
  if (cleaned.length > 500) {
    return 'A link cannot be longer than 500 characters.';
  }
  if (isSitePath(cleaned) || (mayLeaveSite && isWebAddress(cleaned))) {
    return null;
  }
  return mayLeaveSite
    ? "Use a path on this site starting with '/', or a full https:// address."
    : "Use a path on this site starting with '/', such as /women/dresses.";
}
