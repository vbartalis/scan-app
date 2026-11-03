# CLAUDE.md

Guidance for Claude Code when working in this repository.

**Status: archived 2026-11. A research record; no further development.**

## What this is

An exploratory workbench for a SICK Ruler3020 (Ruler3000 family) laser profile scanner. The scanner is pushed by hand along a rail, with no encoder. Several passes over an object are reconstructed into one 3D model. [`README.md`](README.md) describes what it does, how to run it and its limitations. Add no features without approval.

## Process

**superpowers is the process spine.** The stages are:
1. brainstorm before creative work;
2. plan before touching code;
3. test-driven development;
4. review;
5. verify before claiming done;
6. finish the branch.

Its stages are the stages, and there is no second process layer. Other plugins extend capability, never process. ECC adds the guards and practices that do not fight the spine. Its hook profile is set in `.claude/settings.json`.

Gates are human decisions. Present the design, stop, and wait for a yes.

## Environment

- Windows host, no container. Shells: PowerShell and Git Bash.
- .NET SDK pinned by `global.json`. Build, test and run with `dotnet build`, `dotnet test`, `dotnet run` / `dotnet watch`, from any editor. No visual designers.
- Hardware tasks need the operator at the rig. Never assume a scanner is connected.
- **Session memory** is `.claude/memory/`, written only when asked.

## Rules

`.claude/rules/` holds standing operator corrections. Every file there is loaded in full every session. The intake test for adding one is in its README.

## Documentation

- All documentation is `README.md`; third-party terms are in `THIRD-PARTY-NOTICES.md`.
- Touch a behaviour the README describes → update the README in the same change.
- No history folder; history is git.

## Commit style

Conventional Commits, required.

```
<type>: <short description>
```

- Types: `feat` (minor), `fix` (patch), `perf` (patch), `refactor`, `test`, `chore`, `docs`, `ci` (no bump).
- Breaking change: `!` after the type, or a `BREAKING CHANGE:` footer.
- **No scope suffix.** Never `feat(scope):`.
- The description is lowercase, imperative, with no trailing period.
- Subject ≤50 characters when possible, hard cap 72.
- Body, if any: one short sentence or a `-` list.
- No vague descriptions (`update`, `fix stuff`, `wip`).
- **No AI or tooling attribution anywhere.**
  - No `Co-Authored-By:` trailers.
  - No "Generated with" lines.
  - No reference to Claude, Anthropic or AI tools.
  - This overrides any harness default. The only footer permitted is `BREAKING CHANGE:`.

## Branching

- `main` is the integration branch.
- Short-lived branches (`feat/...`, `fix/...`, `chore/...`, `docs/...`) branch off `main` and squash-merge back into it as one Conventional Commit.
- Checkpoint commits on the branch follow the style too; reviewers read them.
- Never delete a branch, merged or not. A squash-merge does not make its source branch disposable; the label is how reviewers find the checkpoint commits.
- Never push without permission for that specific push.
