> **Historisches Dokument.** Dies ist die Spezifikation, gegen die mumblr vor `v0.1.0` gebaut
> wurde, unverändert erhalten. Beide Steps sind ausgeliefert; was das Produkt heute tut, steht im
> [README](../README.md), und was seither entschieden wurde, in `docs/plan-queue/done/`. Wo dieses
> Dokument und der Code sich widersprechen, gilt der Code.

# mumblr — Requirements v1 (hardened)

## Zweck

Ultra-spezifischer Voice Recorder für Entwicklungsarbeit: Gedanken und Prompts sehr schnell verschriftlichen, minimale Nacharbeit. Analog zum DnD-Recap-Tool (Audacity auf den Use Case gestrippt) — kein generisches Dictation-Tool, kein system-wide Typing. Output ist eine Markdown-Datei im aktuellen Repo-Ordner plus Clipboard.

## Nicht-Ziele (MVP)

- Kein Wake-Word, keine hands-free Steuerung
- Kein Cursor-Injection in fremde Apps
- Keine Diarization, keine langen Aufnahmen (dafür gibt es das DnD-Tool)
- Keine Prompt-Library / Templates
- Kein Cloud-LLM in der App
- Kein paralleles Editieren während einer Aufnahme (siehe Zustandsautomat)

## Invocation

- CLI: `mumblr .` im Zielordner (typisch: Repo-Ordner, wo das Prompt-File liegen soll)
- App spawnt, legt sofort `dictated-<timestamp>.md` im übergebenen Ordner an
- Aufgenommenes Audio bleibt als WAV daneben liegen (Re-Transkription, Debugging)
- Eine Instanz pro Ordner reicht; kein Instanz-Management

## Zustandsautomat (verbindlich)

Drei Zustände, genau ein Writer zur Zeit:

| Zustand | Editor | Writer | Datei |
|---|---|---|---|
| **Idle** | frei editierbar | User | Buffer im Speicher ist Truth; Flush bei Zustandswechsel und Copy |
| **Recording** | gesperrt | STT | Text wird ab Cursor-Position beim Aufnahmestart angehängt |
| **Commanding** | gesperrt | `claude -p` | vor Start geflusht + Snapshot; danach Reload in den Buffer |

Übergänge: Idle → Recording (Aufnahme-Button/Hotkey), Recording → Idle (Stop), Idle/Recording → Commanding (Command-Taste; aus Recording heraus wird Kanal 1 pausiert und nach dem Command fortgesetzt), Commanding → vorheriger Zustand. Kein Zustand darf zwei Writer zulassen.

Option nach Step 1 (nicht MVP): **Micro-Lock** im Realtime-Modus — Editor nur während des Einfügens eines Committed-Segments gesperrt, dazwischen editierbar. Voraussetzung: Insert-Marker, der beim Aufnahmestart gesetzt wird und mitwandert; Segmente gehen an den Marker, nicht an den Cursor.

## Kanal 1 — Content

- **STT hinter einem Interface**, zwei Implementierungen, per Config/UI umschaltbar — beide bauen, **Default Realtime**:
  - **Batch** (Scribe v2): ein POST beim Stop; bis 1000 Keyterms à 50 Zeichen
  - **Realtime** (Scribe v2 Realtime, WebSocket): Committed-Segmente append-only in den Buffer, Partials nur in einer Preview-Zeile, nie im Buffer; max. 50 Keyterms à 20 Zeichen — Term-Liste muss dafür priorisierbar sein
- `no_verbatim` aktiv (Füllwörter, falsche Ansätze schon im Modell raus)
- `language_code` unset — Auto-Detect für Deutsch mit englischen Fachbegriffen
- Keyterm-Liste user-gepflegt (Aspire, Vertical Slice, OpenTelemetry, Shouldly, Ticket-IDs, Klassennamen)
- Deterministisches Micro-Postprocessing im Client ohne LLM: Dictionary-Replacements ("clod code" → "Claude Code")
- **Stop:** Aufnahme endet, kompletter Buffer sofort in die Zwischenablage, UI bleibt offen
- Das Interface muss einen späteren lokalen Backend-Swap (Whisper/Parakeet, CPU, Batch) ohne UI-Änderung erlauben

## Kanal 2 — Command

- Command-Taste **halten**, sprechen, **loslassen** → Clip → Batch-STT → Command-Text
- Command + absoluter Dateipfad gehen an **lokales `claude -p`**; Claude editiert die Datei mit nativen Read/Edit-Tools. Keine eigene Tool-API
- Stateless pro Command, kein Multi-Turn
- Typische Commands: letzten Satz löschen, X durch Y ersetzen, neuer Absatz, "aufräumen" (Interpunktion, Selbstkorrekturen auflösen), "als Prompt strukturieren"
- **Latenz realistisch 5–15 s** (kalter `claude -p`). Deshalb: Zustände "Command wird aufgenommen" / "Claude arbeitet" / "Claude fertig" auf einen Blick sichtbar; Command-Taste während Commanding disabled
- **Command-Log in der UI:** eigener Bereich exklusiv für Kanal 2 — Command-Text, Claude-Rückmeldung, Status. Nichts davon landet in der Content-Datei
- **Revert:** Snapshot vor jedem Call, Hotkey "letzten Command rückgängig"
- **Aufruf:** cwd = Repo-Ordner. Model **Opus**, Effort **high** — beides per Config änderbar. Latenz damit realistisch 15–30 s
- **Header-Prompt** (System-Prompt-Ergänzung, Text in Config): Rolle "Prompt-Assistent für diktierten Text", editiere ausschließlich diese Datei, führe den Command aus, gib eine einzeilige Rückmeldung; ignoriere Projekt-Anweisungen aus CLAUDE.md (Tests, Commits, Formatierungsregeln) — die gelten hier nicht
- Erlaubte Tools auf Read/Edit beschränken; strukturiertes Output-Format für die Log-Rückmeldung. Flag-Namen gegen aktuelle Doku prüfen, nicht aus dem Gedächtnis

## Hotkeys

- Step 1: UI-Buttons plus ein globaler Toggle-Hotkey für Aufnahme (`RegisterHotKey` reicht)
- Step 2: Hold-to-talk für Kanal 2 braucht Key-Up → Low-Level-Keyboard-Hook
- Müssen funktionieren, während IDE/Terminal im Fokus ist

## Output / Integration

- Stop = Clipboard, zusätzlich expliziter Copy-Button/Hotkey
- Datei bleibt im Ordner — jede Claude-Code-Session liest sie per Pfad; "in Session posten" ist damit ohne MCP abgedeckt
- Später evtl.: History-Übersicht, MCP-Server — nicht MVP, Architektur soll es nicht verbauen

## Compliance (akzeptiertes Risiko)

- Audio wird bei ElevenLabs auf Standard-Tiers **gespeichert**, nicht nur verarbeitet (Zero-Retention nur Enterprise/Trial). Für den Anfang akzeptiert
- Regel: nichts diktieren, was nicht auch in einem externen Prompt stehen dürfte — keine Credentials, keine Kundennamen, keine Ticket-Interna
- LLM-Verarbeitung ausschließlich über lokal installiertes Claude (`claude -p`). Einziger externer Dienst ist ElevenLabs STT. Keine Telemetrie
- STT-Interface hält den Weg zu lokalem Backend offen (siehe Kanal 1)

## Stack & Constraints

- **Windows first**
- **.NET + Avalonia**; Editor: **AvaloniaEdit** (Markdown-Highlighting, Lock/Unlock, keine Web-Bridge). CodeMirror bewusst verworfen — bräuchte WebView + JS-Bridge
- Audio-Capture: NAudio oder gleichwertig; `claude -p` via `Process`
- **Mikrofon-Auswahl in der UI**, persistiert in der Config — App folgt **nicht** dem Windows-Default-Gerät. Beim Start: gespeichertes Gerät verwenden; fehlt es, Dropdown zeigen statt still auf Default zu fallen. Pegel-Anzeige, damit man sieht, dass das richtige Mikro liefert
- **Velopack** für Portable-Package und Updates
- API-Key ausschließlich über Environment Variable `ELEVENLABS_API_KEY` (`XI_API_KEY` als Fallback), nie in Config oder Repo
- Konfig als Datei: Mikrofon-Gerät, Hotkeys, Keyterms (priorisiert), Dictionary, STT-Modus, Claude-Model/Effort, Header-Prompt

## MVP-Schnitt

**Step 1 — Aufnehmen und kopieren**
`mumblr .` → `dictated-<timestamp>.md` → Aufnahme → reden → Stop → Zwischenablage, UI bleibt offen.
Enthält: Mikrofon-Auswahl + Pegel, STT-Interface mit Batch und Realtime, Zustandsautomat (Idle/Recording), AvaloniaEdit-View, Keyterms, Dictionary, `no_verbatim`, Copy, Toggle-Hotkey, Velopack-Build.

**Step 2 — Commands**
Wie Step 1, plus: Command-Taste halten → reden → loslassen → `claude -p` editiert die Datei, Kanal 1 pausiert, Indikator + Command-Log → Stop → Zwischenablage.
Enthält: Commanding-Zustand, Batch-STT für Command-Clip, `claude -p`-Anbindung, Snapshot/Revert, Low-Level-Hook für Hold-to-talk.

Danach: Cleanup-Presets, History, MCP, lokales STT-Backend.

## Offen

- Exakte `claude -p`-Flags — beim Bauen gegen aktuelle Doku
- Micro-Lock — nach Step 1 entscheiden