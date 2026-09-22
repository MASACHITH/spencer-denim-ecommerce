# Contributing

Short version. The detail is in [docs/12 — Team collaboration](docs/12-team-collaboration.md) and [docs/09 — Git rules](docs/09-git-and-repository-rules.md).

**New here?** Start with [docs/13 — Onboarding](docs/13-onboarding.md).

## The loop

```powershell
gh issue list --search "no:assignee"      # find work
gh issue edit <n> --add-assignee "@me"    # claim it

git switch main; git pull --ff-only
git switch -c feature/<ID>-<short-name>   # feature | fix | docs | chore | ci

# build it, with tests
dotnet test                                # backend (set TEST_SQL_CONNECTION for SQL tests)
cd src/frontend; npm run lint; npm run test:ci

git commit -m "feat(scope): what it does (ID)"
git fetch origin; git rebase origin/main
git push -u origin feature/<ID>-<short-name>
gh pr create --base main --fill            # link the issue with "Closes #n"
```

Merge with **squash** once the pipeline is green and the other developer has reviewed it, then delete the branch.

## Rules that are not negotiable

1. **Never commit to `main`**, and never force-push it.
2. **Only build what is in [docs/02 — Scope baseline](docs/02-scope-baseline.md).** Anything else becomes an issue labelled `needs-decision`.
3. **Every task gets an independent tester pass** before merge ([docs/06](docs/06-engineering-process.md) §3).
4. **No secrets in the repository.** Use user-secrets or environment variables.
5. **LocalDB only**, databases named `SpencerDenim_*`. Never the default SQL Server instance.
6. **One database migration in flight at a time** across the team. Announce it in your issue.
7. **Don't edit `TASKS.md` or `MEMORY.md` during the day.** Write `docs/journal/YYYY-MM-DD-<yourname>.md`.
8. **Responsive at 320 / 375 / 390 / 768 / 1024 / 1280 / 1440 / 1920 px**, no horizontal scroll.
9. Commit messages follow **Conventional Commits** and name the task or feature ID.
10. Push only to `MASACHITH/spencer-denim-ecommerce` with the personal GitHub account ([docs/09](docs/09-git-and-repository-rules.md)).
