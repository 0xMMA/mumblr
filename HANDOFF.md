# Handoff - 2026-09-04

Untracked scratch file. Delete it once the open points below are closed.

Previous session: https://claude.ai/code/session_01Fq5HB2F4rRGrsav3VrUNV5 (cannot be resumed)

## Where things stand

`main` at `5456bbc`, clean, every CI run on `windows-latest` green. 132 tests
(93 Core + 39 App), all passing locally on Linux.

The five items from the first day of real use were queued as nocturne task files and are all
done. Read `docs/plan-queue/done/*.md` - each one carries a `## Log` section with what was
actually decided while implementing it, including the parts that turned out different from the
plan.

```
03fe6c0  fix: send keyterms as repeated parameters (#1)
966a029  fix: a blank model or effort must not downgrade a command
37906e0  feat: prebuilt command buttons
0e8ed21  feat: a status bar that answers "is this thing working"
5456bbc  docs: give the README a front page
```

## Open, and each one needs Michael

1. ~~The release is stale.~~ Closed: `v0.1.1` was tagged and released, and the version is no
   longer written down anywhere - MinVer derives it from the tag, so the binary, the status bar
   and the release page cannot disagree. Releasing is `git tag vX.Y.Z && git push origin vX.Y.Z`
   and nothing else. See `AGENTS.md`.

2. **The repository is private**, so no service can read its build status: shields.io answers
   `ci: repo or workflow not found` and GitHub's own `actions/workflows/ci.yml/badge.svg`
   answers 404. Both checked live. The README head therefore carries static platform/stack
   badges, with the working CI and release markup sitting in an HTML comment directly above
   them - making the repo public turns that comment into the real badges and nothing else
   changes.

3. **The screenshot is missing on purpose.** The README references
   `docs/assets/screenshot.png`, which has to be taken on Windows with the app running. Until
   it exists the front page shows a broken image. `docs/assets/` exists with a `.gitkeep`.

4. **nocturne has no `--effort`.** `.nocturne/config` here pins `"model": "opus"` for queue
   agents, but `nocturne.cs` only ever passes `--model` (line ~1354). High effort for spawned
   queue agents is a change in the nocturne repo, not in mumblr. Not started.

## Things a fresh session will not guess

- **The API key** lives in `~/.config/envset/vars.env`, mode 600, managed by the `envset` tool
  at `~/.local/bin/envset` (`envset set NAME`, `envset list`, `envset rm NAME`). Claude Code's
  Bash calls are non-interactive and do **not** read `.bashrc`, so every command that needs the
  key must start with `. ~/.config/envset/vars.env`.
- **Never echo the key.** `${VAR:-fallback}` expands to the *value* when the variable is set -
  that leaked a key into a transcript in the previous session and it had to be rotated. Use
  `${#VAR}` and `-n` only.
- **Live API tests** are opt-in twice over: the key alone does nothing, `MUMBLR_LIVE_TESTS=1`
  arms them. `MUMBLR_LIVE_TESTS=1 dotnet test --filter FullyQualifiedName~LiveElevenLabs`.
  They exist because the rest of the suite asserts what mumblr *sends*, never what ElevenLabs
  *accepts* - which is how the keyterm bug shipped past a green build.
- **ElevenLabs keyterms**, verified against the live API on 2026-09-04: repeated parameters
  only, never a JSON array. Batch 1000 terms x 50 chars, realtime 50 x 20, at most 5 words per
  term, and `< > { } [ ] \` are forbidden inside a term. One bad term kills the whole request.
- **The status-message bug class.** Any code that writes `StatusMessage` can erase a warning
  that was just set. It has happened twice already (`Initialize`, `StopRecordingAsync`). Check
  `IsWarning` before informing.
- No Windows machine here. `windows-latest` CI is the only real Windows verification, and it
  has caught things Linux could not (an open WAV handle blocking a directory delete).

## Deferred by the spec itself, not by anyone here

Micro-Lock (decide after step 1), history view, MCP server, local STT backend, cleanup presets.
