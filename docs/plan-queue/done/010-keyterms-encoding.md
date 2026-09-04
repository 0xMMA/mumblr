---
id: 010-keyterms-encoding
track: fix
---

# Send keyterms as repeated parameters, not as one JSON array

## Intent
v0.1.0 ships `keytermsEncoding: "json"`, which serialises the whole keyterm list into a
single value: `keyterms=["Aspire","Vertical Slice",...]`. ElevenLabs reads that as one
keyterm. Realtime caps a term at 20 characters, so it rejects the request and echoes the
whole array back in the error — which is exactly what GitHub issue #1 reports. Batch is no
better: the documented rules forbid `<`, `>`, `{`, `}`, `[`, `]` and `\` inside a keyterm,
and a JSON array is made of those characters.

The consequence is not a degraded transcript, it is no transcript at all: the very first
request of a fresh install fails. This is the one bug that makes the shipped binary useless
out of the box, so it runs before anything else in the queue.

Both endpoints want the list repeated: `keyterms=a&keyterms=b` on the realtime websocket
URL, one `keyterms` form field per term in the batch multipart POST.

## Acceptance
- [ ] `SttSessionOptions.KeytermsEncoding` defaults to `"repeated"`, and both engines send
      one parameter/field per term by default.
- [ ] A `config.json` already on disk carrying `"keytermsEncoding": "json"` is migrated to
      `"repeated"` when it is loaded, and the migrated value is written back. An install
      that already ran v0.1.0 must not stay broken after an update.
- [ ] `KeytermPlanner` rejects terms that contain any of `< > { } [ ] \`, and terms longer
      than 5 whitespace-separated words, in addition to the existing count/length limits.
      Rejected terms are dropped, not truncated — a mangled keyterm is worse than none.
- [ ] A failed STT request shows the API's own message in the status bar. Today a rejected
      websocket handshake is invisible, which is why this bug had to be found by reading
      ElevenLabs' error rather than mumblr's UI.
- [ ] `SttProtocolTests` asserts repeated encoding for both engines against the fake server,
      and asserts that a term with a forbidden character never reaches the wire.
- [ ] `dotnet build && dotnet test` green.
- [ ] GitHub issue #1 closed with a comment naming the cause (array in a single value) and
      the fix.

## Decisions
- Keep `"json"` as an accepted config value, but never as a default. It is an escape hatch
  if the API changes shape again; it is not a supported mode.
- Limits confirmed against the ElevenLabs docs on 2026-09-04: batch 1000 terms x 50 chars,
  realtime 50 terms x 20 chars, at most 5 words per term, forbidden characters as listed.
  `KeytermLimits` already carries the first two; add the rest there, not scattered.
- Do not add a keyterm editor to the UI in this task. The config file plus the existing
  reload button is enough to test the fix.
- More than 100 keyterms triggers a 20s minimum billable duration per request and keyterms
  cost a 20% surcharge. Worth a line in the README's config table, not a code change.

## Out of scope
- Keyterm editing UI, per-mode keyterm lists, keyterm import.
- Any other change to the STT wire protocol.

## Log
- Reproduced against the live API before touching code. Batch answered `HTTP 400
  {"status":"invalid_keyword","message":"Some keyword contains invalid characters"}`; realtime
  accepted the handshake and then sent `invalid_request` - "Each keyterm must be at most 20
  characters. '[\"Aspire\",\"Vertical Slice\",\"OpenTelemetry\"]' is 43 characters." Repeated
  encoding gives 200 and `session_started` with the keyterms echoed back.
- The acceptance item about migrating an existing config was moot: `keytermsEncoding` never lived
  in the config at all, only as a hardcoded default on `SttSessionOptions` that the factory never
  passed through. A `config.json` from v0.1.0 carries no such field and picks up the new default
  on load. `SttConfig.KeytermsEncoding` was added anyway, so the documented escape hatch is real.
- There was a second, independent cause for the silence: `StopRecordingAsync` overwrote the
  warning with "Stopped - buffer copied to the clipboard." A refused request was reported and then
  erased within the same keystroke. The stop message now appends when a warning is standing. This
  is the same bug class as the microphone warning fixed earlier in `Initialize` - worth watching
  for in 040-status-bar, since every status write is a candidate.
- Live tests are gated on `MUMBLR_LIVE_TESTS=1` rather than on the key alone, so `dotnet test` and
  the nocturne verify gate never spend money. Confirmed they fail with the old encoding and pass
  with the new one, so they are not vacuous.
