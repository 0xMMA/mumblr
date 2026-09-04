---
id: 050-readme-front-page
track: docs
---

# Make the README land in the first screen

## Intent
The README opens with a paragraph of prose. Someone who lands on the repo cannot see, within
one screen, what the app looks like, whether it builds, what the current version is, or
where the download is. Everything needed to answer those questions already exists — the icon
is in the repo, CI runs on every push, releases are published by tag — none of it is
surfaced.

## Acceptance
- [ ] The icon and the project name form the header, above everything else.
- [ ] Badges directly under it: CI status (workflow `ci.yml`), latest release version,
      platform (Windows x64), and license if one is present.
- [ ] A screenshot follows the badges, referenced as `docs/assets/screenshot.png`.
- [ ] A short download line — portable zip from the releases page — appears above the
      explanatory prose, not below it.
- [ ] Everything currently in the README survives, moved below the new head. No section is
      deleted.
- [ ] Badge URLs resolve against the real repo (`0xMMA/mumblr`) and the real workflow
      filename; a badge pointing at a workflow that does not exist is worse than no badge.

## Decisions
- Do not fabricate the screenshot. It has to be taken on Windows with the app running, which
  no agent here can do. Reference the path, create `docs/assets/` with the directory in
  place, and note in `## Log` that the image is missing — Michael drops the file in.
- Badges come from shields.io against public endpoints. No new dependency, no image checked
  into the repo except the screenshot.

## Out of scope
- A docs site, screenshots of individual features, an animated demo.
- Rewriting the existing prose sections for tone or length.
