# 2026-10-21 — ADM-22 Settings: verify bank, couriers and the rest end to end; the email account in admin

Branch `feature/22-settings-smtp-verify`, worktree `worktrees/sd-smtp`, cut from `main` at **`6cece38`**. Not pushed.
**No migration**: the keys are `ops.Settings` rows, and `ops.EmailOutbox.TransportOverrideProtected` has existed
since #133 with nothing writing it. Part 1 was done on a side branch (`feature/22-settings-verify-part1`, worktree
`worktrees/sd-smtp-verify`) and fast-forwarded in.

Owner's instruction (2026-10-21): admin must be able to add and change the bank account, the email account and the
couriers at any time; check that adding and showing each one works end to end, then mark Settings done. The owner
enters the real values later.

## 1. Part 1 — verification

| Setting | Admin screen | Where it shows | Tests | Result |
|---|---|---|---|---|
| Bank: save / change / clear | Settings → Bank transfer (`Settings.Payments`) | `ops.Settings` | int `AdminBankTransferEndpointTests` (`Bank_details_need_Settings_Payments…`, `A_save_that_omits_a_field_leaves_it_alone`); e2e `admin-bank-transfers` | existing, pass |
| Bank → checkout / order page | — | `/order/{id}` (checkout lands there) | int `BankDetailsEndToEndTests.Bank_details_saved_in_admin_reach_checkout_the_order_page_and_the_instructions_email`, `A_changed_account_number_reaches_the_next_order_and_the_open_order_page`; e2e `bank-transfer` | **new**, pass |
| Bank → instructions email | — | `BankTransferInstructions` outbox row | same new tests; unit `OrderEmailComposerTests.The_bank_instructions_carry_every_field…` | new + existing, pass |
| Bank → invoice | — | method label + customer reference only | int `AdminOrderEndpointTests` invoice tests | existing; by design (invoice exists only once paid, doc 11 P9 / G-34) |
| No bank details → email withheld, nothing invented | — | — | int `Cleared_bank_details_withhold_the_email_and_invent_no_account` (new), `With_the_bank_details_unset_the_instructions_email_is_not_queued`, `Half_filled_bank_details_show_no_instructions_at_all` | pass |
| Bank: audited + sensitive alert | — | `ops.AuditLogs`, `ops.AdminAlerts` | int `Every_bank_change_is_audited_per_key_and_raises_one_sensitive_alert_naming_keys_only`, `An_unchanged_or_refused_bank_save_raises_no_alert…` | **new; failed before the fix (defect 1)** |
| Couriers: add / edit / switch off | Orders → Couriers (`Settings.Edit`) | list, dispatch picker | int `A_courier_is_added_edited_and_deactivated_and_none_is_seeded`; e2e `adds a courier and then deactivates it`, `edits a courier and shows the saved name, telephone and tracking address` (new) | pass |
| Couriers: reorder | same ("Order in the list") | list order | int `Couriers_are_listed_active_first_then_by_their_order_in_the_list_and_an_edit_reorders_them`; e2e `reorders couriers…` | **new**, pass |
| Couriers: template needs the placeholder (and https) | same | — | unit `SaveCourierCommandTests`; int `A_tracking_template_with_no_placeholder_is_refused`; e2e no-placeholder (existing), not-https (new) | pass |
| Dispatched order: courier + working link | — | order page, shipped email | int `A_courier_added_in_admin_reaches_the_order_page_and_the_shipped_email_and_follows_its_template` (new), `Dispatching_an_order_queues_the_shipped_email_with_a_real_tracking_link` | pass |
| Dispatched order: account history | — | My account → orders | int `CustomerOrderHistoryTests.A_shipped_order_shows_its_courier_its_number_and_its_link`; e2e `account.e2e.ts` | existing, pass |
| Contact / social | Settings → Contact and social | footer, WhatsApp, contact page | int `AdminSettingsEndpointTests` save/storefront payload; e2e `admin-settings`, `site-branding` | existing, pass |
| Invoice details | Settings → Invoice and fulfilment | printed invoice | int `What_the_form_saves_is_what_the_invoice_reader_reads`, `An_invoice_prints_the_legal_details…`, `…prints_nothing_for_an_unset_legal_detail` | existing, pass |
| Store logo | Settings → Logo | storefront header | int logo usage/read-model/audit tests; e2e `site-branding` | existing, pass |
| **Delivery fees** | **none** | checkout quote | checkout pricing tests only | **gap — owner decision** |
| Email account | Settings → Email account (`Settings.Security`) | every email | see §2–§4 | **new**, pass |

**Defects found in Part 1**
1. **A bank-details change raised no sensitive-settings alert.** `UpdateBankDetailsCommandHandler` wrote the audit
   rows and never enlisted `SensitiveSettingsChangedAlert`; its docblock still said `ops.AdminAlerts` did not exist,
   and the 10-19 journal wrongly said it did. Fixed (`0e31221`).
2. **The courier stub was looser than the API (#126)** — insertion order instead of active-first/SortOrder/name, and no
   length or printable-ASCII limits. Fixed (`dd26135`).

**Gaps for the owner (safe choice: recorded, not built)**
- **Delivery fees have no admin screen.** docs/02 puts "delivery methods & fees" in ADM-22, and the client tracker said
  changing them later is a settings change. Today the nine zone rates are seeded draft figures in
  `SalesReferenceData.cs` that only SQL can change; pickup locations have no screen either. Building it is a new
  endpoint, handler, audit and screen. **Because of this, Settings is not marked Done on the client tracker** — see §6.
- The invoice does not print the shop's bank account. By design: it exists only for paid orders.

## 2. Part 2 — the email account

**Keys.** The seven doc 08 §3.8 keys (`Host, Port, Username, Password (secret), FromAddress, FromName, UseStartTls`)
are in `SettingCatalog` (`SmtpSettingDefinitions`), `Settings.Security` (GrantsAll only — `CK_RoleClaims_NoSecurity`),
their own save unit, and gone from `DeferredSettingKeys` (now 16 deferred; `SettingKeys` header counts updated). **No
reply-to** (doc 08 lists none). **Encryption is the STARTTLS switch, not none/STARTTLS/SSL**: `System.Net.Mail.SmtpClient`
cannot speak implicit TLS on 465, so an "SSL" option would be a promise the transport cannot keep; the panel says
"use port 587". **Seeded empty** — the first version seeded port 587 and STARTTLS on, and
`SettingKeySpecificationTests.A_seeded_value_exists_only_where_doc_08_states_the_default` refused it (doc 08 states no
SMTP default); an unsaved switch counts as on in the resolver, the handler and the panel.

**Precedence** (`SmtpSettingsResolver`, pure): a complete, valid saved account wins (server name, port 1–65535, bare
send-from address, plain-line name/user, readable password, never a user name with encryption off); otherwise
`Email__Smtp__*` exactly as before; otherwise none. Never a mixture. A saved account passed over logs why, never a value
(event 3413); the source logs on change (3412).

**Per send.** `IEmailTransport.SendAsync(email, account, ct)` — the transport no longer holds options; `IsConfigured` is
gone. `SendEmails` reads `ISmtpSettingsReader` each sweep (30 s), so a change needs no restart. The reader is the only
place the stored password is decrypted for sending; a password row not flagged secret is never read as one.

**N2 dual send** (`SensitiveSettingsChangeNotifier`): a change enlists, in the save's transaction, the banner and — per
active Super Admin — one `SensitiveSettingsChanged` email with no override (goes through the new account) and one
carrying the **previous effective account** (rows or configuration, read before any row changes) as Data Protection
ciphertext in `TransportOverrideProtected` (purpose `SpencerDenim.EmailOutbox.SmtpOverride.v1`, `SmtpOverrideProtector`).
The sweep sends a carried row through its own account; **with no current account it still claims and sends carried
rows only** (`ClaimAsync(overridesOnly: true)`), so a hijacker who *clears* the settings is still reported. A carried
account that cannot be decrypted fails that message — never a fallback to the current account. The column is still
NULLed on every terminal status (R5). No previous account → only the new copy (nobody it could reach). Emails name keys,
actor email, IP and time — never a value.

**Password**: write-only, blank keeps, not trimmed, compared on plaintext, audited `[secret changed]`, never in a read,
redacted from `SmtpSettings.ToString()`.

**Test email** (`POST …/email-smtp/test`): to the signed-in admin's address read from the user table, through the
details as typed (blank password → saved one), straight to the transport with a 20 s timeout, nothing written. A failure
is a fixed sentence by kind (535 → "refused the user name or password", 530, 550/553/554, socket, TLS, timeout); the
server's reply never reaches the page or the log (the endpoint logs type + numeric status, events 3420/3421).

**Panel**: `email-account-panel.ts`, drawn only when the server sends the group; save, send test, write-only password
(`new-password`), STARTTLS checkbox, field errors beside boxes, `aria-disabled` buttons.

**Also**: `EmailTemplateCodes.SensitiveSettingsChanged` (body kept 30 days). docs/15 §4 has the "Email account" paragraph
and `SD_SMTP_*` is described as the fallback.

## 3. Tests

| Suite | Result |
|---|---|
| Release build | **0 warnings, 0 errors** |
| Unit (whole assembly) | **3,991 / 3,991** (main 3,914 after Part 1; new `SmtpSettingsTests` 73 incl. theories, sweep N2 tests 4) |
| Integration, filtered, LocalDB | new `AdminSmtpSettingsEndpointTests` **12 / 12**; regression filter `~Settings\|~Email\|~Outbox\|~Alert\|~Seed\|~CustomerAccountTesterGap\|~PooledIsolation\|~BankDetails\|~AdminOrderEndpoint\|~Branding` **493 / 495** — the 2 failures were my own test assumptions (other active Super Admins in the shared DB also get the warning; `Every_seeded_key_arrives…` now excludes the Settings.Security keys no role can read); both fixed, re-run `~AdminSmtpSettingsEndpointTests\|~AdminSettingsEndpointTests` **86 / 86**. Part 1 (fork): 319 / 319 |
| Vitest admin | **870 / 870** (+9 `email-account-panel.spec.ts`) |
| `ng lint admin` | clean |
| Builds | admin initial 316.22 kB; storefront initial 449.83 kB (unchanged code) |
| Playwright `admin-settings.e2e.ts`, retries 0, admin stub only on port 6551 | **44 / 44** (9 new: shown/absent, save + reload + write-only password, field and cross-field refusals, test send ok/refused, 8 breakpoints, axe). Part 1: admin-orders 39/39, alerts+bank+settings 89/89 |

Every integration run waited for a quiet testhost; other agents' testhosts never went quiet for 60 s, so each run
started after the 20-minute cap **with overlap**, as briefed.

Prettier: `settings-page.ts`, `admin-settings.model.ts` and `admin-settings.e2e.ts` already failed `prettier --check` on
`main`; only the new files were formatted, to keep the diff to the change.

## 3a. Mutation testing

- **Unit — 25 run, 25 killed** (one, "banner not raised", first died at compile and was re-run as a behavioural
  mutant, killed). Two survived the first round and were killed after adding cases: the test-send failure message
  passing the server's text through on an unmapped status (added a 421 case), and the send-from address accepting a
  display name with no space (added `Shop<…>` cases). Covered: permission (save, test send), secret (plaintext store,
  blank clears, `ToString`, server reply in two branches), dual send (previous copy dropped / wrong account / read after
  the change / banner / sweep ignores carried account / unreadable falls back / no override claim when unconfigured),
  precedence (config beats rows, unreadable password used, user name without TLS, unsaved switch off), rules (host with
  port, display-name address, control characters), cross-field port, concurrency, test recipient.
- **Integration — 5 run, 5 killed:** route guarded by `Settings.Edit`; override-only predicate dropped from the claim;
  a password row not flagged secret read as one; the read model returns the secret; reader uses configuration only.
- **E2E stub — 2 run, 2 killed:** host check removed; cross-field rule removed.

## 4. Owner decisions (safe choice taken)
1. **Settings not marked Done on the client tracker**: delivery fees have no admin screen, and CLAUDE.md marks Done only
   after an independent tester pass. Decide whether delivery fees are in this item or a separate one.
2. **No SSL/465 option** (transport limit). MailKit would add it; a dependency decision.
3. **Bank and PayPal changes still send no email**, only the banner (doc 08 DB2 asks for the email too). The notifier
   now exists; switching those two handlers to it is a small follow-up, left out to keep this change to the brief.
4. **The test-email route has no rate limit** beyond Super Admin + 2FA.
5. A carried row stranded mid-send while no account is configured is released only once an account exists.
