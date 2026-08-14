# com.pixygon.buildtools

<!-- pixygon:workflow-start v1 -->
## How to START a session (MANDATORY)

Before doing anything else, run **`pearl preflight`** from the repo root. It
prints this project's current state, the workflow, and the top open questions —
and hands you an acknowledgement **token**. **Begin your first reply with the
line it gives you** (`✦ Preflight <token> — …`), filling in a one-line summary
of what this project is and its #1 open item, drawn from the Codex. That line is
your proof of onboarding; skipping it means you haven't. Then read the Codex
(`pearl codex corpus`) before writing anything fiction- or architecture-flavored.

## Shared cross-session memory (MANDATORY — read before deep investigation)

Session memory is keyed by working directory, so an automated session launched
inside this repo does NOT automatically see what interactive sessions learned.
The SHARED memory store for the whole ecosystem lives at:

`/home/pixygon/.claude/projects/-home-pixygon-repos/memory/`

**Before any investigation, audit, or architecture claim**: read `MEMORY.md`
(the index) there and open any memory file whose description matches your task.
Verified system traces, disproved premises ("zero revenue", "missing
mechanism"), audit findings and pricing/id maps are recorded there — re-deriving
them wastes a session, and contradicting them without checking produces false
briefings.

**After verifying a durable finding** (a system trace, a disproved assumption,
an audit result, a gotcha that will bite again): write it back — one markdown
file per fact with `name`/`description`/`metadata.type` frontmatter
(reference | project | feedback), then add a one-line pointer in `MEMORY.md`.
Update or delete stale memories rather than adding duplicates. What only
matters to this session does not belong there.

## Design work? Use the Pixygon Design System (MANDATORY for UI/visual tasks)

The design canon lives at
`/home/pixygon/repos/pixygon-packages/@pixygon/design/tokens.mjs` — read it
BEFORE any redesign/touch-up/new-UI work. It has two layers: `foundation`
(the Pixygon master brand — void grounds, star-white, signal cyan, ember red,
the PIXYGONµ console language, the Dress) and `brands.<site>` (THIS site's own
palette + mood, which inherits the foundation). Work in the site's OWN brand
voice; don't leak another pearl's colors (that bug shipped once already).

The browsable showroom is the **"Pixygon Design System"** project on
claude.ai/design (readable/writable via the DesignSync tool). The proven
workflow for a serious redesign: read the canon → build a quick HTML MOCK and
screenshot it (Playwright is in most repos) → get the founder's yes → implement
→ screenshot-iterate against the real build → ship. Fold accepted new patterns
back into tokens.mjs (+ `node build.mjs`, DesignSync push) so the canon grows.
Keep internal vocabulary (pearls/dress/codex-lore) OUT of user-facing copy
unless the founder asks for it.

## SEO / AIEO? Use @pixygon/seo (MANDATORY for web/SPA projects)

If this is a web frontend (Vite SPA), it MUST serve real content to crawlers
AND AI answer engines (GPTBot, ClaudeBot, PerplexityBot, CCBot) — never an empty
`<div id="root">` shell. The estate-standard solution is the **@pixygon/seo**
package (`pixygon-packages/@pixygon/seo`) — do NOT hand-roll a prerender or an
SEO component; that's how six sites each shipped the same bugs (empty shells,
dead/duplicate og:image, missing sameAs, localhost canonicals).

It gives you: a **full-page prerender** (`@pixygon/seo/prerender.mjs`, run in a
jammy Playwright Docker stage — NOT Alpine, which silently degrades to head-only)
that also dedupes the react-helmet-over-index.html duplicate tags; the canonical
`PIXYGON_SAME_AS` + `pixygonSiteSchema()` identity graph; and `<PixygonSEO>`
(`@pixygon/seo/react`) whose canonicals come from the production domain, never
`window.location.origin`. New projects get it from the website template already.
To retrofit an existing one, follow the package README (install + jammy Docker
stage + nginx `try_files $uri $uri.html` + `pixygon-seo.config.json` routes),
then VERIFY live: `curl -A Googlebot <url>` must return real body content, not a
`<div id="root"></div>` shell.

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

