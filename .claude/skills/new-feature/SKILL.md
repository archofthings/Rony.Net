---
name: new-feature
description: Checklist for adding a feature to Rony.Net (a backlog item or a new public API). Use when writing the spec for a feature and again before reporting it as done, so that code, tests, wiki page, sample test, API reference and changelog all land together.
argument-hint: "<feature or backlog item>"
---

# New feature checklist

Feature: `$ARGUMENTS`

A feature in Rony.Net is finished only when every part below is done. Put the list into the spec you hand to
`developer` (with the concrete file names filled in) and check it again before you summarise the result.

## 1. Design (orchestrator)
- [ ] Public API written out with signatures and one usage example in the style of the existing fluent API
      (`server.Mock.Send(...).Receive(...)`, `server.Should()...`).
- [ ] No breaking change: nothing removed or changed; new listener or framing capabilities are a new optional
      interface. If a break seems unavoidable, stop and ask the user.
- [ ] Behaviour stated for TCP, TLS and UDP (or "not supported on UDP" and what happens then).
- [ ] Edge cases and error behaviour named: these become the tests.

## 2. Code (`developer`)
- [ ] Implementation in `src/Rony` (or the framework packages), building for `netstandard2.1` and `net8.0`
      with 0 warnings.
- [ ] XML doc comments on every new public member.
- [ ] What the feature does is written to `server.Log` where that helps a user debug a failing test.

## 3. Tests (`developer`, then `tester` for larger features)
- [ ] Unit tests in `tests/Rony.UnitTests` for logic without sockets.
- [ ] Functional tests in `tests/Rony.FunctionalTests` for behaviour on a real socket.
- [ ] Port `0`, no fixed delays, everything disposed (see "Test rules" in `CLAUDE.md`).
- [ ] If the framework packages changed: `tests/Rony.Net.{Xunit,NUnit,MSTest}.Tests`.

## 4. Documentation (`developer`)
- [ ] Wiki page in `docs/wiki/`: a section on the matching page, or a new page that is also linked from
      `_Sidebar.md` and `Home.md`.
- [ ] `docs/wiki/API-Reference.md` lists the new members.
- [ ] `README.md` when the feature belongs in the overview (it is also the NuGet readme, so keep it short).
- [ ] **Every code example exists as a passing xUnit test in `samples/Rony.Samples`**, in the file that matches
      the wiki page. Examples use `server.Should()…`.
- [ ] `CHANGELOG.md`: an entry under the upcoming version (create the heading if it does not exist yet).

## 5. Finish (orchestrator)
- [ ] `dotnet build` with 0 warnings and `dotnet test` green (state the number of tests).
- [ ] `reviewer` verdict is APPROVE.
- [ ] `<Version>` in `Directory.Build.props` untouched: it changes only in `/release bump`.
- [ ] Nothing committed: report the changed files and a suggested commit message; the user commits.
- [ ] If the item came from `BACKLOG.md`, remove it there and tell the user what is left.
