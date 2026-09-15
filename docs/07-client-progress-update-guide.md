# 07 — Client Progress Page: Update Guide

Last updated: 2026-09-15 · Status: **Internal team guide.** Not for upload.

The client progress page lives in `../client-progress/` (outside this codebase). It shows the client what we are building and how far along each feature is. See [05](05-delivery-plan.md) §2 for the tracker spec.

Open `client-progress/index.html` by double-clicking it (no server or build step needed).

**Upload:** the `client-progress/` folder is what gets uploaded, and it must contain only `index.html`, `styles.css`, `app.js` and `progress-data.js`. Keep internal notes (this guide, test notes, TODOs) out of that folder and out of code comments.

**All content lives in `progress-data.js`.** Daily updates, new features and renames never need changes to `index.html`, `app.js` or `styles.css`.

The client must NOT see test details, bugs, internal tasks, hours, prices, process notes or third-party brand names. Keep wording plain.

## Writing values correctly

`progress-data.js` is JavaScript, so small typos can stop the page loading.

| Value | Correct | Wrong |
|---|---|---|
| Text (name, status, date, log text) | `status: "planned"` | `status: planned` (no quotes) |
| Progress | `progress: 40` (plain number) | `progress: "40"` (quoted) |
| Dates | `"2026-09-16"` | `"16/09/2026"`, `"2026-9-16"` |
| Items in a list | `{ ... },` followed by the next `{ ... }` | a missing comma between items |

- `status` must be exactly `"planned"`, `"in-progress"` or `"done"`.
- Use straight double quotes `"`, not curly quotes copied from Word or email.
- If a name contains a double quote, write it as `\"`.

## Daily update (about 2 minutes)

Edit `client-progress/progress-data.js`:

1. **Change a status.** Find the feature by name and set `status` to one of:
   - `"planned"` (not started, counts as 0%)
   - `"in-progress"` (counts as its `progress` value)
   - `"done"` (counts as 100%). Mark Done only after the feature has passed testing.

   ```js
   { name: "Hero video banner", status: "in-progress", progress: 40 },
   ```

2. **Set progress %.** For `"in-progress"` features, set `progress` to a number from 0 to 100, **without quotes** (`40`, not `"40"`).
   Always include `progress` on in-progress features. The value is ignored for `"planned"` (0) and `"done"` (100).

3. **Add a log entry** at the top of `updates` (newest first). Plain, client-friendly sentences:

   ```js
   updates: [
     { date: "2026-09-16", text: "Design style guide started. Hosting and domain are being set up." },
     { date: "2026-09-15", text: "Project kick-off. ..." }
   ]
   ```

4. **Bump `lastUpdated`** to today's date, format `"YYYY-MM-DD"`.

5. **Check the page** (see the next section) before uploading.

## Checking for mistakes

Open `index.html`, then open the browser console: press **F12** (or Ctrl+Shift+J in Chrome/Edge) and choose the **Console** tab. Reload the page.

**If the page says "Progress information is not available right now"**, the data could not be read at all. The console shows a red error with the file and line number on the right, for example:

```
Uncaught ReferenceError: planned is not defined        progress-data.js:42
Uncaught SyntaxError: Unexpected token '{'              progress-data.js:57
```

- Click `progress-data.js:42` to jump to that line.
- **"… is not defined"** almost always means a text value is missing its quotes. `planned is not defined` means `status: planned` should be `status: "planned"`.
- **"Unexpected token" / "Unexpected identifier"** usually means a missing comma or bracket. The problem is often at the **end of the line before** the one reported.
- If there is no error but the message still shows, check that `progress-data.js` is in the same folder as `index.html` and that the file starts with `const progressData = {`.

**If the page loads but something looks wrong**, look for yellow `progressData:` warnings. Each one names the entry and the problem: an unknown status, a missing or non-number progress, progress outside 0–100, a missing name or text, or a date that is not a real `YYYY-MM-DD` date. Invalid entries are skipped or shown safely, but fix every warning before uploading. A correct file shows no console messages.

## Adding and renaming features and groups

The overall %, the Done / In progress / Planned counts, each group's % and "x of y done" all **update automatically**. You never edit numbers other than `progress`.

**Add a feature.** Copy an existing line inside the right group's `features` list and change it. Keep the comma between items:

```js
{
  name: "Homepage",
  features: [
    { name: "Hero video banner", status: "planned", progress: 0 },
    { name: "Gift cards", status: "planned", progress: 0 },   // new feature
    { name: "Shop by department (Women, Men, Kids)", status: "planned", progress: 0 },
```

Features are shown in the order they appear in the file.

**Rename a feature.** Change only the text inside the quotes of `name`; keep `status` and `progress` as they are:

```js
{ name: "Order history & live tracking", status: "planned", progress: 0 },
```

**Rename a group.** Change the group's `name`:

```js
{
  name: "Your Account",
  features: [
```

**Add a group.** Copy this block into the `groups` list, after the closing `},` of the group it should follow. **If you are adding it after the last group** (which ends with `}` and no comma), first type a comma after that `}` so it becomes `},`. A group needs a name and at least one feature to appear:

```js
    {
      name: "Loyalty & Rewards",
      features: [
        { name: "Points on every order", status: "planned", progress: 0 },
        { name: "Rewards page", status: "planned", progress: 0 }
      ]
    },
```

**Remove a feature.** Delete its whole line, from `{` to `},`. If it was the last line in its list, also delete the comma at the end of the line above it (optional, but it keeps the file tidy).

Only add, rename or remove features to match the agreed scope ([02](02-scope-baseline.md)). Discuss any change with the project lead before the client sees it.

## How the numbers are calculated

- **Overall %** = average of every feature (Done = 100, In progress = its `progress`, Planned = 0), rounded.
  It only shows 100% when every feature is Done.
- **Group %** uses the same rule for the features in that group.
- **Timeline:** the milestone whose date range contains today's date is highlighted as Current;
  earlier ones show Completed, later ones Upcoming. A milestone with invalid dates shows as Upcoming with no dates.
  To preview another date, add `?today=YYYY-MM-DD` to the address, for example `index.html?today=2026-09-30`.
- **Daily updates** are listed newest first. Entries with an invalid date are listed after the dated ones.
- **Header:** "Project timeline" and "Last updated" only show when their dates are valid.

## Files in `client-progress/`

| File | Purpose |
|---|---|
| `progress-data.js` | All content: project details, milestones, feature groups, daily updates |
| `index.html` | Page structure |
| `app.js` | Validates the data and builds the page |
| `styles.css` | Styling (see [04](04-design-system.md)) |
