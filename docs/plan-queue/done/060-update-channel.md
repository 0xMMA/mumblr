---
id: 060-update-channel
track: main
---

# Give the installed app a release feed it can actually read

## Intent
Self-update is dead and cannot be revived from inside the app. `GithubSource` is constructed
without a token, and the repository is private, so GitHub answers 404 to an anonymous client.
Velopack cannot see a single release. The same wall blocks the README badges: nothing outside
this account can read the repo's build status or its latest tag.

Right now the only way to move an installed copy to a new build is to download the asset by
hand from a browser that is logged in - which also means the "update to ..." button in the
status bar can never appear, however many releases are cut.

The check itself no longer lies about this (0.1.2 separates "up to date" from "could not ask",
and the environment variable `MUMBLR_GITHUB_TOKEN` is read if it is set), but an honest error
message is not a working update channel.

Three ways out. The choice is the owner's, because it is about what the world may see:

1. **Make `0xMMA/mumblr` public.** Everything works immediately with no code: updates, the CI
   badge, the release badge. The whole repo becomes readable, including the dictated-file
   examples and this queue.
2. **A public releases-only repo.** Code stays private, releases and the Velopack feed go to a
   public sibling. Costs a second repo and a token in the release workflow.
3. **Keep it private and set `MUMBLR_GITHUB_TOKEN`** on each machine that should self-update.
   Works today with no infrastructure, but every machine needs a PAT with repo read, and a
   token on disk is a credential to look after.

## Acceptance
- [ ] One of the three is picked. Until then this task is blocked, not open.
- [ ] An installed build finds a newer release and offers the update button.
- [ ] `UpdateService` points at whichever feed the decision implies, with the repository URL a
      constant in one place rather than spread over workflow and code.
- [ ] If the decision makes the repo or a mirror public: the README's commented-out CI and
      release badges replace the static ones, and the comment explaining why they were static
      is deleted.
- [ ] The release workflow publishes to whatever feed was chosen, and a real end-to-end update
      is observed once - installed old build, tag, button appears, restart lands on the new
      version.

## Decisions
- The token, if it is ever used, comes from the environment. Never from the config file, never
  committed, the same rule the ElevenLabs key follows. `UpdateService.TokenVariable` already
  reads `MUMBLR_GITHUB_TOKEN` this way.
- Never embed a token in the shipped binary, whatever the convenience. A PAT in a client is a
  PAT in everyone's hands.
- Option 2 costs nothing in privacy, which is a reason to prefer it - but not a reason to decide
  it here.

## Out of scope
- Delta updates, update channels (beta/stable), silent background installs.
- Anything about the update UI: the button and its states already exist.

## Blocked
Which release feed should the installed app read?

1. Make `0xMMA/mumblr` public - zero code, everything works, the whole repo becomes readable.
2. A public `mumblr-releases` sibling - code stays private, costs a second repo and a token in
   the workflow.
3. Stay private and set `MUMBLR_GITHUB_TOKEN` per machine - works today, but a PAT on every
   machine that should update.

This is a question about what the world may see, so it is not a decision to make unasked.

## Log
- Decided: make the repository public. The other two options traded privacy for nothing that
  matters here - there is no proprietary code in it, and a releases-only sibling would have cost a
  second repository and a token in the workflow to protect something nobody was hiding.
- A public-readiness audit ran first, by an agent with no context from the work. It found no
  secret in any blob of the history, and one genuine blocker: no LICENSE, which legally means a
  reader may look but not build, use or borrow. MIT with the Commons Clause now covers it, matching
  the sibling project.
- The one irreversible finding was six commits carrying a private email address. History was
  rewritten before the switch, all seven tags moved with it, and the releases and their assets came
  through untouched. Afterwards is too late for that one - it was the only reason to rewrite.
- `MUMBLR_GITHUB_TOKEN` stays in `UpdateService`. It costs nothing while unset and it is the
  escape hatch if the feed ever moves somewhere private again.
