# Security

mumblr handles an ElevenLabs API key. It reads it from the `ELEVENLABS_API_KEY` environment
variable (or `XI_API_KEY`) and never from the config file, never from this repository, and never
writes it anywhere. The status bar shows only whether a key is present.

Found something that undermines that, or any other security issue? Open an issue with the shape
of the problem - no keys, no tokens, no dictated content in the report - or reach the author
through the GitHub profile for anything that should not be public first.

One thing that is by design rather than a vulnerability: on standard ElevenLabs tiers your audio
is stored, not just processed. Do not dictate anything that could not go into an external prompt.
