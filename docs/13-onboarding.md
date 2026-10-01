# 13 — Onboarding a Developer

Last updated: 2026-09-22 · Target: a new developer productive in about 2 hours.

Work through this in order and tick as you go. Everything runs on Windows without administrator rights; Docker is **not** needed ([docs/12](12-team-collaboration.md) explains why).

## 1. Access (project owner does this)

- [ ] GitHub collaborator on `MASACHITH/spencer-denim-ecommerce` (already done for `tharanganuwan`)
- [ ] Added to the WhatsApp/chat group
- [ ] Told which issues to start with

The repository is **private** and contains client contracts and pricing. Don't fork it, copy it elsewhere, or share screenshots of the client documents.

## 2. Install

| Tool | Version | Get it / check |
|---|---|---|
| .NET SDK | **10.0** | `dotnet --version` |
| Node.js | **24** (npm 11) | `node -v` |
| SQL Server **LocalDB** | 2019+ | `sqllocaldb info MSSQLLocalDB` — comes with SQL Server Express or the SSMS installer |
| Git | 2.4x | `git --version` |
| GitHub CLI | 2.x | `winget install --id GitHub.cli -e`, open a new terminal, then `gh auth login` and `gh auth setup-git`. No CLI? Use a fine-grained token instead: [docs/09 §2a](09-git-and-repository-rules.md#2a-first-time-setup-on-a-new-machine) |
| An editor | VS Code or Visual Studio 2026 | |
| SSMS or VS Code "SQL Server (mssql)" | optional, for looking in the database | |

Docker is deferred (task W0-15) because the office firewall blocks it. Don't install it for this project.

## 3. Clone and configure

You push with **your own GitHub account**; the owner has already given you Write access ([docs/09 §1b](09-git-and-repository-rules.md)). If cloning asks for a username and password, set up credentials first: [docs/09 §2a](09-git-and-repository-rules.md#2a-first-time-setup-on-a-new-machine).

```powershell
git clone https://github.com/MASACHITH/spencer-denim-ecommerce.git
cd spencer-denim-ecommerce

# Your identity for this repository
git config user.name  "<your name>"
git config user.email "<your github email>"

# Enable the safety hook that blocks pushing to the wrong repository
git config core.hooksPath tools/git-hooks
```

## 4. First build (about 15 minutes)

```powershell
# 1) Create the development database once
sqlcmd -S "(localdb)\MSSQLLocalDB" -E -Q "IF DB_ID(N'SpencerDenim_Dev') IS NULL CREATE DATABASE [SpencerDenim_Dev] COLLATE Latin1_General_100_CI_AS_SC;"

# 2) Backend
dotnet build
$env:TEST_SQL_CONNECTION = "Server=(localdb)\MSSQLLocalDB;Trusted_Connection=True;TrustServerCertificate=True"
dotnet test                       # all tests must pass
dotnet watch --project src/backend/SpencerDenim.Api    # http://localhost:5080/api/ping

# 3) Frontend (new terminal)
cd src/frontend
npm ci
npx ng serve storefront           # http://localhost:4200
npx ng serve admin                # http://localhost:4300/admin/
```

Working means: `/api/ping` returns JSON, `/health/ready` is healthy, and both pages show "API connected". Full details are in the [README](../README.md#getting-started).

**Database rules:** only use LocalDB, and only databases named `SpencerDenim_*`. **Never** point anything at the default SQL Server instance (`.`, `localhost`, `MSSQLSERVER`); on company machines that instance may hold office data.

## 5. Read, in this order (about 1 hour)

| # | Document | Why |
|---|---|---|
| 1 | [`CLAUDE.md`](../CLAUDE.md) | The non-negotiable rules, including git |
| 2 | [`MEMORY.md`](../MEMORY.md) | Where the project is right now, and why decisions were made |
| 3 | [`TASKS.md`](../TASKS.md) | The plan by week |
| 4 | [docs/02 Scope baseline](02-scope-baseline.md) | **What we build and what we don't.** Nothing outside it gets built. |
| 5 | [docs/12 Team collaboration](12-team-collaboration.md) | How we work in parallel: issues, branches, reviews |
| 6 | [docs/09 Git rules](09-git-and-repository-rules.md) | Branching, commits, pull requests |
| 7 | [docs/06 Engineering process](06-engineering-process.md) | Definition of Done, tests, standards, lessons learned |
| 8 | [docs/03 Architecture](03-architecture-and-tech-stack.md) | Layering, SOLID, technology choices |
| 9 | [docs/04 Design system](04-design-system.md) + [`design/style-tile/index.html`](../design/style-tile/index.html) | Colours, type, components. Open the style tile in a browser. |
| 10 | [docs/11 Business processes](11-business-processes.md) | How the shop actually works: orders, stock, payments, refunds |
| 11 | [docs/08 Database design](08-database-design.md) | Reference while coding. Long; read the parts you need. |

**Project background in one paragraph:** Spencer Denim Industries Pvt Ltd is a Sri Lankan fashion retailer (Women, Men, Kids) whose shop trades online as **Colombo Wardrobe** — the company name is the client, the brand name is everything a shopper reads (`docs/02` §I2). TU2SL is building their online shop and admin panel for a fixed fee, to a fixed scope, due **15 Oct 2026**. Prices are in LKR. Payment is by bank transfer (staff approve it) and PayPal (charged in USD). The look follows a Banana Republic–style reference: white, black, lots of space, big photos. A demo was shown to the client and approved; the old demo lives in `reference/` as a **feature reference only**, and none of its code is reused.

## 6. Your first pull request

1. `gh issue list --search "no:assignee label:good-first-issue"`, pick one, `gh issue edit <n> --add-assignee "@me"`.
2. `git switch main; git pull --ff-only; git switch -c feature/<ID>-<short-name>`.
3. Build it, with tests. Follow the existing patterns; don't introduce new libraries without asking.
4. Run `dotnet test` and the frontend tests. Check your screens at 320px and 1280px.
5. Commit as `feat(scope): what it does (ID)`, push, `gh pr create --base main --fill`.
6. Ask for review in the chat group. Fix comments, keep the pipeline green, then squash merge.

## 7. Things that will trip you up

- **Don't edit `TASKS.md` or `MEMORY.md` during the day.** Write `docs/journal/YYYY-MM-DD-<yourname>.md` instead ([12](12-team-collaboration.md) §3).
- **One database migration at a time across the whole team.** Say so in your issue before creating one.
- **The office SQL Server is off limits.** LocalDB only.
- **The scope baseline wins.** If the client asks for something new, it becomes an issue labelled `needs-decision`, not code.
- **The pipeline must be green before merging.** A red pipeline blocks everyone.
- **Every task gets a tester pass** before the pull request is merged; the developer's own checks are not enough.
