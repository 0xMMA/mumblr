# Roadmap: 0.2.1 to 0.3.0

Written 2026-09-17, after hardening. Four steps in order. Each one ships a release.

**Where it stands.** Step 1 is tagged. Steps 2 and 3 are on `main` and wait for one tag together:
`v0.2.2-beta.1` first, then `v0.2.2`. Step 4 is not started.

Step 2 closed differently than planned below: instead of a throwaway tag, the first run through the
channel routing is the real beta of 0.2.2. It proves the same plumbing, leaves nothing to delete,
and gives the preview a week of use before the stable tag.

## Settled

- **"Generalization" means prompts, nothing else.** recap is retired because its waveform, trim and
  cut are not needed, **not** because mumblr grows them. The non-goal "no long recordings" stands.
  Whether the recap repository is archived is decided at the end of step 4, once it is clear that
  nothing is missing - not before.
- **Prompt files come from the user directory only.** A prompt goes to `claude -p` with Read/Edit on
  the dictation file. A repository-local `.mumblr/prompts/` would let a cloned repo run its own
  prompt against your files, so there is no repository source at all - not even an opt-in one.
- **Channels stay minimal.** No in-app channel switch, no downgrade path. You get on preview by
  installing the preview build and back by installing stable.
- **The issue round is #5 and #7.** #3 (abort a command), #4 (summary language) and #2 (micro-lock)
  wait.

## Assumptions

- The Windows-only checks in #6 are covered in practice by heavy 0.2.0 use. The issue stays open as
  bookkeeping and blocks nothing.
- A prompt carries `label` and its text in v1. No per-prompt model, effort or tool scope.

---

## 1 - Tag v0.2.1

Two commits past v0.2.0, notes written, CI green on `c79d61e`:

- `5d2bc72` toolbar wraps instead of clipping, tooltips everywhere, larger default window
- `c79d61e` stopping no longer takes over the clipboard

Nothing open. Tag it.

## 2 - Release channels

The workflow routes on the tag suffix:

| Tag | Channel | GitHub release |
|---|---|---|
| `v0.3.0` | `win` | normal |
| `v0.3.0-beta.1` | `win-beta` | prerelease |

Work:

- `--channel` on `vpk pack`, `--pre` on `vpk upload`, both derived from the suffix the existing
  version regex already accepts.
- **The rename step will break.** It matches `^mumblr-win-(Setup\.exe|Portable\.zip)$` and throws
  unless it renames exactly two assets. Channel-named artifacts do not match, so the first preview
  tag goes red *after* the upload. Make the pattern tolerant of the channel, keep the count check.
- **The preview client may not see its own releases.** `UpdateService` builds
  `GithubSource(..., prerelease: false)`. Preview and stable are the same binary - the channel lives
  in Velopack metadata, not in code - so marking beta releases as GitHub prereleases makes that
  `false` filter them out again for the preview client too. The channel separates the feeds; the
  GitHub flag only controls how the repository page reads. Set `prerelease: true` and let the
  channel do the separating. **Verify on the first beta tag**: if `prerelease: true` turns out to
  exclude stable releases rather than include them, derive the flag from the channel name at
  runtime instead.

Velopack bakes the channel into the installed package: an installed build only ever searches
`releases.<its channel>.json`. That is why there is no in-app switch. `UpdateOptions.ExplicitChannel`
and `AllowVersionDowngrade` exist in 1.2.0 and are deliberately not used.

Close with `v0.2.2-beta.1`, the first tag through the routing. Planned as a throwaway; it became
the real beta of 0.2.2 once step 3 landed in the same window - a broken preview costs no more than
a broken throwaway, and this one is worth installing.

Two things were checked rather than assumed. `vpk pack --channel win-beta` was run locally against
a real win-x64 publish: it writes `mumblr-win-beta-Setup.exe`, `mumblr-win-beta-Portable.zip` and
`releases.win-beta.json`, which is what the rename step's prefix match expects. And Velopack's
`GithubSource` filters with `includePrereleases || !x.Prerelease`, so `prerelease: true` is
additive - the stable client keeps seeing stable releases, and a release with no index for the
asking channel is skipped rather than throwing. What is left to watch: GitHub is asked for the ten
most recent releases and no more, so preview tags have to stay rare.

Document the two directions - install preview to get on, install stable to get back - in one README
paragraph, not in code.

## 3 - Issues #5 and #7

**#5 - a hold key inside Stop's pause window keeps the recording alive.** (The issue was closed by
accident: the body of the commit that added this document contained the words "fix #5 and #7".) `StopRecordingAsync`
awaits `SafeStopEngineAsync()` for up to five seconds on a realtime backend; a `CommandKeyDown` in
that window moves the machine to Commanding, `TryStopRecording()` then fails silently, and
`TryFinishCommand` returns to `Recording`. This fires on exactly the common pattern: stop, then run
Grammar.

A `stopRequested` flag set before the await. The command still runs - it was pressed deliberately -
but `TryFinishCommand` lands in `Idle` rather than back in `Recording`. Both key presses are
honoured instead of one being swallowed. Test: stop, command inside the window, end state is Idle
and the taskbar is quiet.

**#7 - config.json is shared by every window.** `FileSystemWatcher` on the file, reload through the
path that already exists. **The reload must wait for `Idle`** - reloading mid-take would swap the
microphone or the STT mode underneath a running recording.

Ship as `v0.2.2`, the first real run through the new channel mechanics.

## 4 - Prompts as files

`%APPDATA%\mumblr\prompts\*.md`. Frontmatter carries `label` and `order`; the body is the prompt.

- Seed Grammar and Prompt on first run.
- **Migration must not eat an edited prompt.** `ConfigMigration` already knows whether a
  `PrebuiltCommands` entry still matches a shipped default. Unchanged entries become fresh files
  from the current default; edited ones are written out carrying the user's text. `PrebuiltCommands`
  leaves the config afterwards.
- Execution does not change. `RunPrebuiltAsync` already feeds `prebuilt.Text` into the same
  `RunCommandAsync` a spoken command uses. What is new is loading, ordering and reloading on change.
- The UI stays buttons while there are few. A picker gets built when there are really ten prompts,
  not before.

Ships as `v0.3.0`.

---

## Version plan

| Version | Contents |
|---|---|
| 0.2.1 | toolbar and clipboard fixes (ready) |
| 0.2.2 | #5, #7 - first release through the channel routing |
| 0.3.0 | prompts as files |
