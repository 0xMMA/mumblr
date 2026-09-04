# Working in this repo

## Build and test

```
dotnet build
dotnet test
```

`dotnet test` never touches the network. The tests that talk to ElevenLabs are armed separately,
because they cost money per run:

```
MUMBLR_LIVE_TESTS=1 dotnet test --filter FullyQualifiedName~LiveElevenLabs
```

They exist because the rest of the suite can only assert what mumblr *sends*, never what
ElevenLabs *accepts* - which is how a broken keyterm encoding shipped past a green build
(issue #1). If you change anything about a request, arm them once before you call it done.

Everything targets plain `net10.0`, so it builds and tests on Linux. There is no Windows machine
here: CI on `windows-latest` is the only real Windows verification, and it has caught things Linux
cannot see - an open WAV handle stopping a directory delete, for one. Push before you believe a
Windows-specific claim.

## Versions and releases

**Never write a version number into a file.** MinVer derives it from the git tags:

| Situation | Version |
|---|---|
| Build on tag `v0.1.1` | `0.1.1` |
| Three commits later | `0.1.2-alpha.0.3` |

Releasing is one command, and it is the *only* way a release is made:

```
git tag v0.1.2 && git push origin v0.1.2
```

That triggers `.github/workflows/release.yml`, which tests, publishes, packs with Velopack and
uploads the portable zip and the installer. The tag is the single source of truth: the assembly
version, the status bar and the release page all read from it, so they cannot disagree.

Two consequences worth remembering. A shallow checkout has no tags, so every workflow needs
`fetch-depth: 0` or the build calls itself `0.0.0-alpha.0`. And a fix that is merged but not
tagged is a fix nobody has - check whether the newest tag is behind `main` before saying a bug is
released.

Update `RELEASE_NOTES.md` in the same commit as the work, not at tag time. Velopack ships that
file verbatim as the release body.

## The queue

Follow-up work lives in `docs/plan-queue/` as nocturne task files: `id`, a `# title`, `## Intent`,
`## Acceptance`, optional `## Decisions` and `## Out of scope`. Order comes from the filename
prefix, status from the directory (`done/`, `failed/`, `blocked/`). Run `nocturne lint` after
editing.

When you finish one, append a `## Log` section and move the file into `done/` in the same commit
as the code. Write down what turned out *different* from the plan - a wrong assumption caught
while implementing is the most valuable line in the file, and the next task in the track gets fed
those lines verbatim.

## Traps this repo has already fallen into

- **Never echo a secret.** `${VAR:-fallback}` expands to the *value* when the variable is set.
  That leaked an API key into a transcript once and the key had to be rotated. Use `${#VAR}` for
  a length and `-n` for a check, nothing else.
- **The key comes from the environment only** - `ELEVENLABS_API_KEY`, with `XI_API_KEY` as a
  fallback. Never from config, never from the repo. On this machine it lives in
  `~/.config/envset/vars.env`; agent shells are non-interactive and do not read `.bashrc`, so
  source it explicitly when a command needs the key.
- **Writing `StatusMessage` can erase a warning** that was set moments earlier. It has happened
  twice (`Initialize`, `StopRecordingAsync`). Check `IsWarning` before informing, or a real
  failure becomes a silent one.
- **ElevenLabs keyterms** are repeated parameters, never a JSON array: batch 1000 x 50 chars,
  realtime 50 x 20, at most 5 words, and `< > { } [ ] \` are forbidden inside a term. One bad
  term kills the whole request, so `KeytermPlanner` drops rather than repairs.
- **`vpk upload` reads the asset names from its own manifest, not from the directory.** Renaming
  a file between `pack` and `upload` makes it fail with "Could not find file" and leaves a draft
  release holding only the `.nupkg`. Rename on the release afterwards, through the GitHub API -
  `releases.win.json` references only the `.nupkg`, so the installer and the portable zip are
  free to be renamed.
- **`strings` reads ASCII by default and .NET literals are UTF-16.** Verifying a shipped binary
  with plain `strings` will tell you a string is missing when it is there. Use `strings -el`.
- **Check CLI flags against `claude --help`,** not against memory. The spec says so explicitly and
  it has been right to.

## Verification

Green tests are not the finish line. When the work is done - the code written, the suite passing,
the acceptance list ticked - hand it to a fresh reviewer before calling it done:

Dispatch a **fresh** agent - one with none of the working session's context - and have *it* run
the review:

```
Agent(subagent_type: "general-purpose", prompt: "run /review-pr on <target> ...")
```

Not a fork, and not the skill invoked directly from the working session. `review-pr` declares
`context: fork` in its own frontmatter, so calling it from the session that wrote the code hands
the reviewer every justification that was made along the way. That reviewer inherits the
assumptions instead of testing them: it once looked straight at a German button label in an
English window, commented on the spelling of the umlaut, and never questioned the language -
because the session it forked had already settled that.

Run it even when there is no pull request. The point is not the PR, it is a reader who did not
write the code, has not been talked into the design, and has no investment in the approach being
right. Everything expensive this repo has learned - a request
that never reached ElevenLabs, an update check that claimed to be current without asking, a
release that renamed itself into a broken upload - looked fine from the inside while it was being
written.

Act on what comes back before you report. A finding you disagree with gets an argument in the
reply, not silence.

## Style

**Every string the user sees in the window is English.** Buttons, labels, status messages, tooltips,
panel headings, units - all of it, without exception. mumblr's UI is English and a single German
word in it reads as a bug, which is exactly what shipping a button called "Grammatik" next to
"Record", "Copy" and "Revert last" looked like.

The exception is content, not chrome: a prebuilt command's *text* is a prompt about German
dictation and stays German, the keyterm list is whatever the user maintains, and a transcript is in
whatever language was spoken. If a string is displayed as an instruction to the user, it is
English; if it is data the app carries around, it is in its own language. When adding a config
default that surfaces in the window, the label and the payload are two different decisions.

Code, comments, commit messages, task files and the docs in this repo are English. Comments say
*why*, never *what* - if a line only restates the code, delete it. Match the density of the file
you are editing.
