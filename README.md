# Spencer Denim — Fashion E-Commerce Platform

Production build of the fashion e-commerce website for **Spencer Denim Industries Pvt Ltd** (client: Manusha Weerawardene), delivered by **TU2SL**.

> Status: **Planning / documentation phase.** No code is written until the open questions in [docs/01-project-understanding.md](docs/01-project-understanding.md) are answered and the scope baseline is signed off.

## Source of truth

| Priority | Source | Location |
|---|---|---|
| 1 | Signed budget proposal TU2SL-2026-0823 (23 Aug 2026) | `../douments/Spencer-Denim-Ecommerce-Website-Budget-Proposal.pdf` |
| 2 | Theme reference (replaces "Denim & Co. × Natural Linen") | `../douments/Refrence sites/refernce.txt` → https://bananarepublic.gap.com/ |
| 3 | Demo prototype shown to client (reference only — **not** reused as code) | `../tuasl-fashion/` |
| 4 | Content checklist sent to client | `../tuasl-fashion/Client/Spencer-Denim-Content-Checklist.pdf` |

Older drafts in `../tuasl-fashion/Client/` (`CLIENT_SCOPE_PROPOSAL.md`, `CLIENT_COMMERCIAL_PROPOSAL.html`, `TUASL_Fashion_Proposal - Copy.html`) have been **replaced** by the signed PDF. Where they disagree, the PDF wins.

## Documents

| # | Document | Purpose |
|---|---|---|
| 01 | [Project understanding & open questions](docs/01-project-understanding.md) | What we understood, gaps, questions to answer before build |
| 02 | [Scope baseline](docs/02-scope-baseline.md) | Agreed features with IDs, demo items to remove, items to add, out of scope |
| 03 | [Architecture & tech stack](docs/03-architecture-and-tech-stack.md) | Stack, Docker, VPS layout, SOLID structure, Azure migration path |
| 04 | [Design system](docs/04-design-system.md) | Theme direction (Banana Republic–inspired), responsive rules |
| 05 | [Delivery plan & client tracker](docs/05-delivery-plan.md) | 1-month internal plan, milestones, client progress page |
| 06 | [Engineering process](docs/06-engineering-process.md) | Developer → tester agent loop, Definition of Done, test strategy |
| 07 | [Client progress update guide](docs/07-client-progress-update-guide.md) | How the team updates the client progress page (team only) |
| 08 | [Database design](docs/08-database-design.md) | Baselined schema (65 tables), state machines, stock concurrency, pricing |
| 09 | [Git & repository rules](docs/09-git-and-repository-rules.md) | **Push only to MASACHITH/spencer-denim-ecommerce with the personal account**, never the office account |
| — | [TASKS.md](TASKS.md) | Live task board (internal) |
| — | [MEMORY.md](MEMORY.md) | Running project log: decisions, what's done, what's next |

## Rules for anyone working on this project

1. Read `MEMORY.md` and `TASKS.md` first, every session.
2. Nothing is built that is not in [02-scope-baseline.md](docs/02-scope-baseline.md). New requests → logged as change requests (chargeable, per proposal §5).
3. Every task goes developer → tester; a failed test goes back to the developer until it passes ([06](docs/06-engineering-process.md)).
4. Every screen must work at 320 / 375 / 390 / 768 / 1024 / 1280 / 1440 / 1920 px.
5. Update `MEMORY.md` and `TASKS.md` at the end of every working session. Update the docs whenever a decision changes them.
