---
id: 100-raw-input-restore
track: main
---

# The raw dictation is always one click away

## Intent
Every command rewrites the file in place. Revert undoes the last one, and the one before,
twenty deep, one at a time - and a revert throws away whatever was dictated after the command
it undoes. Nothing answers "what did I actually say" once a few commands have run over the
text. Before commands get bolder (110) that is the guarantee the user needs: whatever a
prompt does to the text, the words as spoken are never lost.

The raw dictation is what STT produced and only that. It gets its own file next to the
markdown and the wav, and a button that puts it back into the buffer.

## Acceptance
- [ ] Every committed channel-1 segment is appended, after the dictionary pass, to
      `dictated-<timestamp>.raw.md` next to the content file. Claude never touches it: the user
      message names one file and the tool allow-list has no Write.
- [ ] A **Raw** button in the command log header next to Revert last. Click: the buffer is
      snapshotted first, so this too is revertible; the buffer becomes the raw text; the file
      on disk follows; the log gets an entry. Disabled in Recording and Commanding, and when
      the buffer already equals the raw text.
- [ ] The raw file grows across pauses and resumes: a command in the middle of a recording
      neither splits nor loses a take.
- [ ] Dictionary replacements are part of raw - deterministic and configured by the user.
      Nothing an LLM wrote ever is.
- [ ] Headless tests: raw grows per segment and survives a command; Raw restores after two
      commands; Raw is revertible; Raw disabled when nothing differs.
- [ ] README: the file list under `mumblr .` names the raw file; the revert paragraph mentions
      Raw.
- [ ] `dotnet build && dotnet test` green.

## Decisions
- Raw means "what you said", not "what you typed". Typed edits in Idle go into the buffer as
  today and are covered by revert, not by raw. One definition, testable, no merging.
- A file, not only memory. An agent reading the folder by path can read the raw dictation next
  to the shaped prompt, and a crash keeps it.
- Revert's data-loss window (dictate after a command, then revert) stays as it is. Raw is the
  net under it; teaching revert to re-append later dictation is its own task if it bites.

## Out of scope
- A diff view between raw and current.
- Restoring the raw of an earlier session.
