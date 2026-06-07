# com.pixygon.buildtools

<!-- pixygon:workflow-start v1 -->
## How to finish a prompted session (MANDATORY)

When you finish a piece of prompted work in this repo that produces
shippable code, run **`pearl ship`** from the repo root. That is THE
final step of the session — don't commit by hand, don't curl the
release endpoints yourself, don't end the session without running it.
One command, every time.

```
pearl ship
```

If `pearl` isn't on PATH:

```
node /home/pixygon/repos/Dyson/scripts/pearl.mjs ship
```

### What `pearl ship` does

1. Reads `.pixygon.json` (here or any parent dir) for the project ID.
2. Runs `scripts/visual-check.mjs` if present (frontend repos).
3. AI-drafts changelog entries from your git diff.
4. POSTs to `/v1/ship/<project-id>` — appends entries, logs the
   timesheet entry (auto-skip if off), bumps version, releases the
   changelog, fans out the orchestrator side-effects.
5. Writes the returned `CHANGELOG.md` to the repo root.
6. Runs `scripts/post-ship.mjs` if present (e.g. translation sync).
7. `git add -A && git commit && git push` — AI headline as message.

If the drafter classifies your diff as 100% plumbing (refactor, dep
bump, no behaviour change), pearl skips the release and just commits +
pushes. Still one command.

### Auth — zero setup required

Pearl auto-reads `~/.config/dyson-swarm/config.toml` for the API key.
You don't need to set `PIXYGON_API_KEY` manually. Override the search
order via env (`PIXYGON_API_KEY`) or a Pearl-specific
`~/.config/pearl/auth.json`.

### Flags

- `--review` — print drafted entries, prompt before `/ship`
- `--no-test` — skip `scripts/visual-check.mjs`
- `--no-commit` — ship + write `CHANGELOG.md` but don't `git commit`
- `--magnitude=patch|minor|major` — override AI's auto bump
- `--minutes=N` — log N minutes to timesheet (silent skip if off)
- `--message="..."` — override the AI commit message
- `--dry` — print every step, send nothing

### Don't use partial-flow primitives directly

`POST /v1/changelogs/project/:id/append`, `/timesheet/...`,
`/changelogs/project/:id/bump` etc. exist for backfill + admin repair.
Reach for them only when `pearl ship` genuinely doesn't fit.
Otherwise: `pearl ship`.

## Universe Codex — for AI sessions in this repo

The Pixygon universe shares **one canonical lore database**. Every project
inherits the universe canon through its parent super-project. Before
writing anything fiction-flavored — names, places, characters, mechanics-
as-fiction — **read the Codex first**.

Quickest priming move:

```
pearl codex corpus           # dump this project's effective lore
                             # as Claude-ready markdown (own +
                             # inherited from super-project)
```

Other essentials:

- `pearl codex search "<q>"` — full-text find
- `pearl codex get <slug>` — one entry as JSON
- `pearl codex list --kind=character --tag=foo` — browse
- `pearl codex backlinks <slug>` — every entry that mentions this one
- `pearl codex push <file.md>` — bulk import a YAML-frontmatter bundle
- `pearl codex draft "<prompt>" --count=N` — AI-draft new entries
  (review the markdown, then `pearl codex push`)
- `pearl codex refine <slug>` — AI-expand one entry's body
- `pearl codex image upload <slug> <file> [--cover]` — gallery upload

Entry format (YAML frontmatter + body, multiple per file separated by `---`):

```yaml
---
name: The Sigil of the Bound
kind: item              # character | place | item | faction | organization
                        # concept | event | era | species | language
                        # religion | technology | document | game
subkind: relic
slug: the-sigil-of-the-bound
aliases: ["Sigil"]
sourceProjects: ["6a1d9df5817d5a6f7f785b38"]   # universe id; add this project too
status: canonical       # draft | canonical | disputed | archived
accessTier: sealed      # public | initiate | archivist | sealed  (DEFAULT sealed)
tags: ["wardenship"]
chronology: { startYear: -2400, era: "Wardenship" }
---
# Body in markdown. Cross-link with [[slug]] or [[slug|display text]].
```

**Access tiers** are diegetic — under-tier reads return an in-fiction
refusal voiced by the universe, not a 403. Default `sealed`; promote
intentionally as canon publishes.

**Rules of thumb:** specific over generic; cross-link liberally; at least
two of the Five Lenses should resonate (alchemy · Jung · numerology ·
religion · conspiracy); don't invent names that exist (always
`pearl codex search` first); pearls' own entries go to that pearl's
project, universe-wide ones to the super-project, many entries to both.

Full guide: [`Dyson/docs/CODEX.md`](../Dyson/docs/CODEX.md).

**Project ID**: `<unknown — set via PixygonServer admin or in this repo's .pixygon.json>`

Full spec: [`Dyson/docs/WORKFLOW.md`](../Dyson/docs/WORKFLOW.md). System map:
[`Dyson/docs/workflows/registry.yml`](../Dyson/docs/workflows/registry.yml) — also
rendered as the Atlas tab in Dyson.

_This section is managed by `Dyson/scripts/standardize-repos.mjs`. Don't edit
by hand — change the script or the canonical doc instead._
<!-- pixygon:workflow-end -->

