---
status: superseded
---

# BMad Dev Auto Result

**Superseded 2026-07-10 by the Epic 4 renumber.** This file previously recorded a
blocked `bmad-dev-auto` attempt on the research-authoring story (then keyed `4-9`)
that could not proceed because the research sim it renders over did not yet exist.
The original blocked-run detail remains in git history and the run journal.

Since then:

- The research story cluster was renumbered off letter suffixes — the bmad-loop
  sprint-status parser (`STORY_RE`) silently drops any story key with a letter
  suffix: `4.8a → 4.8`, `4.8b → 4.9`, `4.8c → 4.10`, and this authoring story
  `4.9 → 4.11`.
- Story **4.8** (ResearchDefinition content model + validation) is **done**.
- Stories **4.9** (ResearchSystem order path) and **4.10** (ResearchStore
  SimChecksum fold) are **not started** — the sim this authoring story renders over.

**Next:** land 4.9 → 4.10, then create and dispatch **4.11** (research authoring,
command-card research buttons, upgrade display).
