---
id: 110-promptify-command
track: main
---

# The Prompt button: dictation to a prompt an agent can act on

## Intent
The README promises "think out loud, get a prompt" and the tool delivers a transcript. The
gap is one command wide: a shipped button that turns the dictation into something you can
hand to a coding agent. "Turn this into a prompt" already works as a spoken command; the
button fixes the flavour and makes the round trip free, like Grammar.

The hard part is restraint. The dictation is a few minutes of thinking out loud about a
codebase the model shaping it cannot see. A shaping that "improves" the text by filling gaps
invents requirements. What the command may do: drop speech artefacts, put the ask first,
group context and constraints, and turn what is unclear or contradictory into open questions
at the end - questions the receiving agent can ask (it has an ask tool) or the author can
answer before sending. What it may not do: answer those questions itself, add steps, add
facts, change the language.

## Acceptance
- [ ] A second shipped `PrebuiltCommand`, label **Prompt**, English text. It asks for: the
      author's words and language kept; speech artefacts and repetition removed; the order
      *what is wanted*, *context given*, *constraints*, then an **Open questions** section
      listing every gap or contradiction as a question - none answered, none silently
      resolved; nothing added the author did not say; markdown, no XML tags.
- [ ] The result replaces the file like every command: snapshot, revert, log entry. With 100
      in place the raw dictation survives it by construction.
- [ ] Run it once over a German dictation of about ten sentences with fillers, one repetition
      and one contradiction (needs `claude` on PATH). Check: German out, terms intact, the
      contradiction appears as an open question, no requirement that was not spoken. Input
      and output go into the Log - that is the fixture for the next round of wording.
- [ ] The header prompt's "leave every other line untouched" does not fight a command whose
      job is restructuring the whole file. If the run shows timidity, word the header so that
      "what the command asks" plainly includes restructuring, and run again.
- [ ] README: the Prompt button is described where the tagline's promise is made, and "What it
      deliberately is not" still holds - two shipped buttons are not a prompt library.
- [ ] A test pins the shipped command's must-haves (language kept, open questions, nothing
      added) the way 070 pins Grammar.
- [ ] `dotnet build && dotnet test` green.

## Decisions
- One flavour ships, the cautious one: shape and surface gaps, never interpret. The bolder
  end - read the content, infer the intent, write the prompt the author meant - becomes a
  second button once use shows the cautious one is not enough. A second button is a config
  entry and a wording, not code.
- Open questions go into the prompt, not into the command log. The prompt travels, the log
  does not, and the agent receiving the prompt is the one who can ask.
- The output prompt is in the dictation's language. A German author gets a German prompt with
  English terms; Claude Code reads that fine and the author can still edit it.
- Markdown sections, no XML. The author reads and edits this text before sending it; a human
  is its first reader.

## Out of scope
- A "tidy only" third button. Grammar already is that.
- Sending the prompt anywhere. Copy puts it on the clipboard; that stays the hand-off.
- Reading the raw file or the wav for context.
