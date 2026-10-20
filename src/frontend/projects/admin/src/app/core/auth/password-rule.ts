/**
 * The password rule, shown **before** a staff member types a password (issue #200).
 *
 * It is the same rule customers have had since W3-01 — staff were raised to it, so the admin panel no
 * longer has the weaker passwords. The server owns it: `PasswordPolicy.Requirement` (Domain) is both the
 * sentence below and the text of the refusal a too-short password gets, so the hint and the 400 cannot
 * word it differently.
 *
 * This file is the admin app's one copy of that sentence, and the copy is **pinned**:
 * `PasswordHintConventionTests` in the C# unit suite reads this file and fails the build if the string is not
 * byte-identical to the server's constant, or if a second admin file spells the rule out. That is the same
 * technique the storefront uses for the customer hint (`account.model.ts`), and for the same reason: a form
 * promising one rule while the server enforces another is defect #126's shape, and it is the only cure
 * that holds.
 *
 * Shown with `aria-describedby` on the field (WCAG 2.2 3.3.2 Labels or Instructions): a person typing a
 * password cannot see what they typed, so being told the rule only by a refusal is doubly opaque.
 */
export const STAFF_PASSWORD_RULE =
  'Use at least 10 characters, including an upper-case letter, a lower-case letter, a number and a symbol.';
