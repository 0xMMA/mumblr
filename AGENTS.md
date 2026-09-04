# Working in this repo

## Before you call it done

Run `/review-pr`, with or without a pull request. It declares `context: fork`, so it already runs
in a subagent that cannot see the conversation.

What it does inherit is the brief. A review pointed only at the parts you were already thinking
about inherits your blind spots with them — the state machine and the release pipeline get read
closely while a German button label sits untouched in an English window. Name what you did *not*
look at: every string the user sees, the layout at the shipped window size, and whether the README
still describes what the code does.

Act on what comes back before you report, and judge it rather than applying it. A finding can be
correct and still point at the wrong fix; you have the whole picture, the reviewer has one pass.
A finding you disagree with gets an argument in the reply, not silence.

## Build and test

```
dotnet build
dotnet test      # never touches the network
```

The tests that talk to ElevenLabs cost money per run and are armed only by the gate:

```
MUMBLR_LIVE_TESTS=1 dotnet test --filter FullyQualifiedName~LiveElevenLabs
```

They exist because the rest of the suite can assert only what mumblr *sends*, never what
ElevenLabs *accepts* — which is how a broken keyterm encoding shipped past a green build
(issue #1). Arm them once after changing anything about a request.

Everything targets plain `net10.0` and builds and tests on Linux. CI on `windows-latest` is the
only real Windows verification — push before you believe a Windows-specific claim.

## Language

**Every string shown in the application window is English** — buttons, labels, status messages,
tooltips, panel headings, units.

Content the app carries keeps its own language: a prebuilt command's prompt text (German
dictation), the keyterm list, a transcript. Displayed as an instruction to the user → English;
data the app carries around → its own language. A config default that surfaces in the window is
two separate decisions, label and payload.

Code, comments, commit messages, task files and repo docs are English. Comments say *why*; delete
one that only restates the code. Match the density of the file you are editing.

## Versions and releases

**Never write a version number into a file.** MinVer derives it from the git tags: on tag `v0.1.1`
→ `0.1.1`, three commits later → `0.1.2-alpha.0.3`.

A release is made one way and no other:

```
git tag v0.1.2 && git push origin v0.1.2
```

That triggers `.github/workflows/release.yml` — test, publish, Velopack pack, upload of the
portable zip and the installer. The tag is the single source of truth for the assembly version,
the status bar and the release page, so they cannot disagree.

- Every workflow needs `fetch-depth: 0`. A shallow checkout has no tags and the build calls itself
  `0.0.0-alpha.0`.
- Merged is not released. Check whether the newest tag is behind `main` before saying a bug is
  fixed for users.
- Update `RELEASE_NOTES.md` in the same commit as the work, not at tag time — Velopack ships that
  file verbatim as the release body.

## The queue

Follow-up work lives in `docs/plan-queue/` as nocturne task files: `id`, a `# title`,
`## Intent`, `## Acceptance`, optional `## Decisions` and `## Out of scope`. Order comes from the
filename prefix, status from the directory (`done/`, `failed/`, `blocked/`). Run `nocturne lint`
after editing.

Finishing a task: append a `## Log` section and move the file into `done/`, in the same commit as
the code. Log what turned out *different* from the plan — the next task in the track is fed those
lines verbatim.

## Traps this repo has already fallen into

- **Never echo a secret.** `${VAR:-fallback}` expands to the *value* when the variable is set; that
  leaked an API key into a transcript and forced a rotation. Use `${#VAR}` for a length and `-n`
  for a check, nothing else.
- **The API key comes from the environment only** — `ELEVENLABS_API_KEY`, with `XI_API_KEY` as a
  fallback. Never from config, never from the repo. Agent shells are non-interactive and do not
  read `.bashrc`, so source whatever holds it before a command that needs it.
- **Writing `StatusMessage` can erase a standing warning** (`Initialize` and `StopRecordingAsync`
  both did it). Check `IsWarning` before informing, or a real failure becomes a silent one.
- **ElevenLabs keyterms are repeated parameters, never a JSON array.** Batch 1000 x 50 chars,
  realtime 50 x 20, at most 5 words per term, and `< > { } [ ] \` are forbidden inside a term. One
  bad term kills the whole request, so `KeytermPlanner` drops rather than repairs.
- **`vpk upload` reads the asset names from its own manifest, not from the directory.** Renaming a
  file between `pack` and `upload` fails with "Could not find file" and leaves a draft release
  holding only the `.nupkg`. Rename on the release afterwards, through the GitHub API —
  `releases.win.json` references only the `.nupkg`, so the installer and the portable zip are free
  to be renamed.
- **`strings` reads ASCII by default and .NET literals are UTF-16.** Plain `strings` reports a
  shipped string as missing when it is there. Use `strings -el`.
- **Check CLI flags against `claude --help`,** never against memory.
