# Handoff - 2026-09-05

Untracked scratch file. Delete it once the open points below are closed.

## Where things stand

`main` at `2087721`, one docs-only commit past `v0.1.6`. Every CI run on `windows-latest` green.
169 tests (101 Core + 68 App; 2 skipped - the live ElevenLabs tests, armed only by
`MUMBLR_LIVE_TESTS=1`).

Six releases in a day. `v0.1.1` fixed the keyterm encoding that made every fresh install fail,
`v0.1.3` stopped dictations being written into the folder the next update deletes, `v0.1.4` fixed
a command being ended by whoever pressed a key next, `v0.1.5` put `mumblr` on the PATH so the one
command the app exists for actually exists, `v0.1.6` turned off the customization layer in the
spawned `claude -p` - the user's own hooks were running over the dictation file.

The plan queue is empty except one parked task. Each finished file in `docs/plan-queue/done/`
carries a `## Log` with what turned out different from the plan.

## Open, each one a decision

1. **The release feed** - `docs/plan-queue/blocked/060-update-channel.md`. The repository is
   private, so GitHub answers 404 to the updater and to shields.io alike: no in-app updates, no CI
   or release badge. Three ways out are written up there. Nothing else is blocked by it.
2. **Micro-Lock** - the spec parked this explicitly ("nach Step 1 entscheiden", `mvp.md:35`) and
   step 1 has now shipped and been used. Today the editor is frozen for the whole recording.
   Micro-Lock would lock it only while a committed segment is being inserted. The insert marker it
   needs already exists (`insertOffset`, set at record start, moves with every insert).
3. **The command key during a running command** is ignored. A real abort needs a kill path for the
   `claude` process and a rollback to the snapshot - which is already taken.
4. **The summary language.** The one-line result in the log and status bar is pinned to English,
   following the rule that every string in the window is English. The dictation it describes is
   German.
5. **`--effort` in nocturne** - `nocturne.cs` only passes `--model`, so high effort for the agents
   working this queue is a change in that repo, not this one.

## Things a fresh session will not guess

Read `AGENTS.md` first - it carries the build and release mechanics, the language rule, and the
traps this repo has already paid for. Beyond it:

- The API key lives in `~/.config/envset/vars.env` (managed by `envset`, `~/.local/bin/envset`).
  Agent shells are non-interactive and do not read `.bashrc`: source it explicitly.
- A green local test run is not proof. Twice in one session it was green for the wrong reason -
  once on a directory an earlier run had left behind, once because the review had been aimed only
  at what was already suspected.
