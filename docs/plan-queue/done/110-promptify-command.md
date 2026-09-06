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

## Log
- First live run (`LiveClaudeTests.The_prompt_command_...`, 21 s, Opus) over ten sentences with
  fillers, one repetition and one contradiction. Input: "Also ähm ich glaube wir sollten den
  Order Service auf Vertical Slices umbauen. ... Das mit Aspire brauchen wir dafür eigentlich
  nicht ... Wichtig ist auch dass das Aspire Dashboard am Ende läuft ..." Output: German sections
  `## Auftrag`, `## Kontext`, `## Randbedingungen`, then `## Open questions` whose first entry is
  "Aspire soll erstmal weggelassen werden, aber das Aspire Dashboard soll am Ende laufen und
  Traces zeigen — was davon gilt?", followed by the gaps the text left (which slices, which of the
  old endpoints are the important ones, whether Shouldly is already in the project or would need
  the NuGet question). The repeated folder-structure remark was folded, the fillers dropped, no
  requirement added. Summary: "Restructured the dictation into a German agent prompt with
  Auftrag/Kontext/Randbedingungen sections plus an 'Open questions' list, stripping fillers and
  the repeated folder-structure remark."
- The header prompt's "leave every other line untouched" did not make the model timid; the
  command's "this is shaping, not rewriting" plus the explicit ordering was enough. Header
  unchanged.
- Section headings came out German except "Open questions", which the command names verbatim.
  Fine for a German author; the live test accepts either.
- The English-only Grammar list that 0.2.0-pre wrote is now a legacy fingerprint in
  `ConfigMigration`, so a config from between the two commits picks up the Prompt button too.
- Review: the header prompt's "decide rather than ask" and the Prompt command's "resolve none of
  them silently" pull in different directions on paper; the live run shows the command wins, as
  the header's "do what the command asks" intends. Left as is, with the live test as the tripwire.
