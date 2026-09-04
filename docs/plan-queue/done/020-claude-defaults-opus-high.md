---
id: 020-claude-defaults-opus-high
track: fix
---

# Guarantee the spawned claude -p really runs opus at high effort

## Intent
`ClaudeConfig` declares `Model = "opus"` and `Effort = "high"`, and `ClaudeArgsBuilder` is
unit tested against those values — but nothing proves the process that actually starts is
the one the test describes, and nothing defends against a config that silently degrades it.
Issue #1 is the standing proof that a shipped default can be wrong in a way no test catches.

Two concrete holes: a `config.json` written by an older build (or hand-edited to an empty
string) deserialises `model`/`effort` as `""`, and an empty string is not the same as
"absent" — the builder would emit `--model ""`. And the command log never says which model
answered, so a downgrade is invisible in exactly the situation where it matters.

`--model` and `--effort` were both re-verified against `claude --help` on 2026-09-04, along
with `--json-schema`, `--permission-prompts`, `--no-session-persistence` and `--restricted`.
All are current. Re-check them here rather than trusting this note.

## Acceptance
- [ ] With a default config, the argument list contains `--model opus` and `--effort high`.
- [ ] A config whose `model` or `effort` is null, empty or whitespace falls back to
      `opus`/`high` instead of emitting an empty flag value.
- [ ] The command log entry for each run states the model and effort that were used, so a
      wrong one is visible without reading the config.
- [ ] `dotnet build && dotnet test` green.

## Decisions
- Opus at high effort is the default because a dictation fix-up is short, rare and
  latency-tolerant — the spec already budgets 15-30s per command. Cheaper models are a
  config choice, never the shipped default.
- Fall back rather than throw. A broken config must never stop a command, the same rule
  `ConfigStore` already follows for unparseable JSON.
- `.nocturne/config` in this repo pins `"model": "opus"` for the agents that work this
  queue. Nocturne has no `effort` field today, so high effort for queue agents needs a
  change in the nocturne repo, not here.

## Out of scope
- Model/effort pickers in the UI.
- Any change to the header prompt or the allowed tool set.

## Log
- The defaults were already `opus`/`high`; the hole was that nothing defended them. `ClaudeConfig`
  now resolves a null, empty or whitespace value back to the constant instead of handing
  `--model ""` to the CLI, and `Describe()` gives the log a single place to read it from.
- Flags re-verified against `claude --help` on 2026-09-04: `--model`, `--effort`, `--json-schema`,
  `--permission-prompts`, `--no-session-persistence`, `--restricted` are all current. `--effort`
  takes low, medium, high, xhigh, max.
- `.nocturne/config` pins `"model": "opus"` for the agents working this queue. Nocturne has no
  effort field at all - `nocturne.cs` only ever passes `--model` - so high effort for queue agents
  is a change in the nocturne repo, not something this task could deliver.
