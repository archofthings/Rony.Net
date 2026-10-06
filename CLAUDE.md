# Rony.Net – TCP / TLS / UDP mock server for .NET tests

## Project
- Repo: `archofthings/Rony.Net` (remote `origin`), default branch `main`.
- Packages on nuget.org: `Rony.Net` (core), `Rony.Net.Xunit`, `Rony.Net.Xunit.v3`, `Rony.Net.NUnit`, `Rony.Net.MSTest`,
  `Rony.Net.Cli` (the `rony` dotnet tool, `net8.0` only), `Rony.Net.Testcontainers` (from 1.5.0). All share
  the one `<Version>` in `Directory.Build.props`; a project file never sets its own.
- `global.json` asks for the .NET 8 SDK or any newer one.
- Open work: `BACKLOG.md` (proposed features and follow-ups; update it when an item is finished or added).
- Library projects target `netstandard2.1;net8.0` (so no APIs newer than .NET Standard 2.1 without a `#if`);
  test and sample projects target `net8.0`.
- CI (`.github/workflows/ci.yml`): `dotnet build` and `dotnet test` in Release on Ubuntu and Windows, .NET 8 SDK; a
  `docker` job builds the image from the `Dockerfile`, runs the wiki's Docker example in it and checks the reply.
- `.github/workflows/badge.yml` adds up the nuget.org downloads of all packages once a day and writes them to the
  `badges` branch for the README badge. A new package must be added to its package list.
- Local environment: any SDK from .NET 8 up. If only a newer runtime is installed, tests need
  `DOTNET_ROLL_FORWARD=Major`; `.claude/settings.json` sets it for every session. Check a fresh checkout with the
  `setup-dev` skill (`.claude/skills/setup-dev/scripts/setup_dev.sh`).
- Commands, from the repo root:
  - `dotnet build` (must stay at 0 warnings)
  - `dotnet test --no-build tests/Rony.UnitTests` (one project), `--filter "FullyQualifiedName~ClassName"` (one class)
  - `dotnet test` (full suite, 638 tests at 1.4.0)
- Hooks (`.claude/hooks/`, wired in `.claude/settings.json`): every edited `.cs`/`.csproj` file gets its project
  built and compiler errors or warnings are reported immediately (fix them before continuing); a `<Version>` inside
  a `src/*/*.csproj` is reported; whole-disk searches (`find /`, `find ~`, `locate`, …) are blocked.
  NuGet package sources are in `~/.nuget/packages/`.

## Module map
Go straight to the right file and its test. Paths are relative to `src/Rony/`; unit tests are in
`tests/Rony.UnitTests/`, functional tests (real sockets) in `tests/Rony.FunctionalTests/`.

| File | What it does | Tests |
|---|---|---|
| `MockServer.cs` | Public entry point: start/stop/dispose, `Mock`, `Log`, connections, events, `BroadcastAsync`, `WaitForConnection(s)Async`, `VerifyConnections`, `Should()` | `MockServerTests.cs`, functional `MockServerFeatureTests.cs`, `ConnectionFeatureTests.cs` |
| `Handlers/RequestHandler.cs` | The `Mock` object: rules (`Send…`), matching order, `OnConnect`, `OnUnmatched`, `FailOnUnmatched`, state (`InState`, `State`, `StateScope`), `Verify*`, `VerifyInOrder`, `WaitFor*` | `Handler/RequestHandler*Tests.cs` |
| `Handlers/ResponseBuilder.cs`, `StateRequestBuilder.cs`, `MatchResult.cs` | Fluent response chain (`Receive`, `Then`, `After`, `AndDisconnect`, `GoTo`); rules inside a state; result of a match | `Handler/RequestHandler*Tests.cs` |
| `Handlers/RequestJournal.cs` | Thread-safe record of received requests, with waiting | `Handler/RequestHandlerFeatureTests.cs` |
| `Listeners/TcpServerBase.cs`, `TcpServer.cs`, `TcpServerSsl.cs`, `TcpConnection.cs` | TCP accept loop, per-connection read/write (writes serialized by a semaphore), TLS handshake | `Listeners/TCPServer*Tests.cs`, functional `MockTcpServer*Tests.cs` |
| `Listeners/UdpServer.cs` | UDP listener (state is keyed by client address) | `Listeners/UdpServerTests.cs`, functional `MockUdpServerTests.cs` |
| `Listeners/MessageFraming.cs`, `Interfaces/IMessageFraming.cs` | Framings: delimiter, length prefix, custom | `Listeners/MessageFramingTests.cs` |
| `Interfaces/IListener.cs`, `IConnectionListener.cs` | Listener contracts. `IConnectionListener` is optional; plain `IListener` must keep working | functional `ConnectionFeatureTests.cs` |
| `Models/` | `ClientConnection`, `ReceivedRequest`, `Message`, `ResponseStep`, `Config`, `StateScope` | `Models/MessageTests.cs` |
| `RecordingProxy.cs`, `Models/Recording.cs`, `Handlers/RecordingReplay.cs` | Record and replay: standalone TCP/TLS relay that records traffic, the recording and its JSON file format, `MockServer.Replay` turning a recording into rules | `Models/RecordingTests.cs`, functional `RecordingProxyTests.cs`, `RecordingReplayTests.cs` |
| `Models/JsonData.cs`, `Helpers/JsonParser.cs` | Dependency-free JSON reader for `SendJson` and recording files | `Models/JsonDataTests.cs`, `Handler/RequestHandlerPartialMatchingTests.cs` |
| `Verification/` | `Times`, `MockVerificationException`, `MockServerAssertions` (`server.Should()`) | `Verification/*Tests.cs` |
| `Helpers/`, `Extensions/`, `Wrappers/` | `AsyncQueue`, byte comparer/formatter, `GetBytes()`, thin socket wrappers | — |
| `Listeners/UnixSocketServer.cs` | Unix domain socket listener on top of `TcpServerBase` (no TLS, no RST) | functional `EndpointTests.cs` |
| `TestCertificate.cs`, `Interfaces/ITlsListener.cs`, `Models/TlsConnectionInfo.cs` | Self-signed test certificates; TLS details per connection (`connection.Tls`), mutual TLS lives in `TcpServerSsl.cs` | `TestCertificateTests.cs`, functional `TlsExtrasTests.cs` |
| `Handlers/MockConfiguration.cs` | `MockServer.FromJson` / `FromFile`: JSON configuration file (format version 1) turned into a listener and rules | `Handler/MockConfigurationTests.cs`, functional `MockConfigurationTests.cs` |
| `../Rony.Net.{Xunit,NUnit,MSTest}/` | `MockServerTest` base class and `LogTo(...)` / `LogToTestContext()` per framework | `tests/Rony.Net.*.Tests/` |
| `../Rony.Net.Testcontainers/` | `RonyBuilder` / `RonyContainer`: starts the `rony` image from a test and talks to its control endpoint. Depends on one exact minimum of `Testcontainers` (its builder API changes between minor versions), the version the tests use | `tests/Rony.Net.Testcontainers.Tests/` (the container test runs only in the CI `docker` job, with `RONY_TEST_IMAGE`) |
| `../Rony.Net.Cli/` | The `rony` tool: `run`, `validate`, `record`, `replay`; hand-written argument parsing, no dependencies. Root `Dockerfile` runs it | `tests/Rony.Net.Cli.Tests/` |

Documentation:
- `README.md` (also the NuGet readme of `Rony.Net`), `CHANGELOG.md`, and each framework package's own `README.md`.
  The README is complete but short: every feature appears in it, in the existing structure (a "Features" list,
  then one section per topic with a few one-line code examples and a "Details:" link to the wiki page). A new
  feature extends the matching Features line and adds a line or two to the matching section; explanations and
  full examples belong in the wiki.
- `docs/wiki/` is the wiki source; `.github/workflows/wiki.yml` publishes it on pushes to `main`. Never edit the
  live wiki.
- `samples/Rony.Samples/`: **every code example in the README and wiki is an xUnit test here.** Change both together.
- Examples use the fluent form `server.Should()…`; the classic `Verify*` methods are documented as equivalents.

## Roles
- **You (main session, Opus 5.5)**: architect, problem solver and orchestrator.
  Do not write production code or tests yourself for non-trivial changes.
- **developer** (Sonnet 5.5): implements code from your spec, including its unit and functional tests, samples and docs.
- **tester** (Sonnet 5): checks the tests against the spec, fills gaps, adds regression tests and revert checks,
  and hunts for flaky tests.
- **reviewer** (Opus 5.5, read-only): reviews the final change.

## Workflow
1. Analyse the request and relevant code; produce a design/spec (use plan mode for bigger changes).
   The spec must include: files to change, public API with signatures, behaviour, edge cases, acceptance
   criteria, and which wiki page(s) and sample file document it (see the `new-feature` skill for the checklist).
   Wait for my approval on bigger designs. A new public API needs no approval: choose the names you
   recommend and list them in the summary.
2. Delegate implementation to `developer` with the full spec (subagents do not see this conversation).
3. Delegate test verification to `tester`, passing the spec and the developer's summary.
4. If tests fail: diagnose the root cause yourself, then send a targeted fix spec to `developer`
   (or to `tester` if the test is wrong). Repeat 3–4.
5. Delegate review to `reviewer` with the spec. Send Critical/Warning items back to `developer`.
6. Summarise to me: what changed, test results, review verdict, anything I need to decide, and a suggested
   commit message. I commit.

## Scope discipline
Agents (and the orchestrator) change only what the task requires: no unrequested improvements, features or
abstractions; unrelated issues are reported, not fixed. Adapted from the Karpathy guidelines
(github.com/multica-ai/andrej-karpathy-skills); the full rules are in `.claude/agents/developer.md`.

## Library rules
- **No breaking changes in a minor or patch version.** Public types and members keep their signatures and
  behaviour; code written for 1.0 must compile and behave the same. New interface members go into a new optional
  interface (as `IConnectionListener` did), not into `IListener` or `IMessageFraming`.
- Every new public member gets an XML doc comment.
- The core package has no dependencies; don't add any. The framework packages depend only on their test framework,
  at the lowest supported version (xUnit v2 `xunit.abstractions`, NUnit 3.14, MSTest 4.0).
- Thread safety: listeners call into `RequestHandler` from several connections at once. Shared state is locked or
  uses concurrent collections; writes to one connection stay serialized.
- Exceptions from user callbacks (`Receive(...)` functions, predicates, `Log`) never crash a listener.

## Test rules
- Tests bind to port `0` (or the `MockServerTest` base class) and read `server.Port`; never a hard-coded port.
- No `Task.Delay`/`Thread.Sleep` to wait for something to happen: use `WaitFor*Async`, `WaitForConnection(s)Async`,
  `WaitForCloseAsync` or a `TaskCompletionSource`. A delay is only allowed when the delay itself is under test.
- Every server, client and stream is disposed (`using`), also when the test fails.
- Tests never use the network beyond loopback and never need a certificate from the machine store
  (`TestCertificate` creates one in memory).
- Unit tests for logic that needs no socket; functional tests for behaviour on a real TCP/TLS/UDP socket.
  Cover TCP, TLS and UDP only where the code path differs.

## Test budget
Keep tests lean; they are read and run by every agent, so size costs tokens.
- Per change: one test for the main behaviour, plus tests only for edge/error cases the spec explicitly names.
- Prefer `[Theory]` with `[InlineData]` over copied tests; no tests for simple properties or constants.
- The sample test for a wiki example counts as documentation, not as the feature's test.
- `tester` only runs for larger features; revert checks only for bug fixes.
- `reviewer` flags missing tests only when important behaviour is untested (no "nice to have" gaps).
- While working, run only the relevant test project or class; run the full suite once when the task is finished.
- Small fixes: no tester step; the orchestrator checks the result instead of a full review.

## When to skip the pipeline
Small, single-file changes (typo, doc fix, one-line fix): do it directly, then run `dotnet build` and the
relevant tests.

## Rules
- **I do all commits and pushes. Neither the orchestrator nor any subagent runs `git commit`, `git push`,
  `git tag`, or anything else that creates or rewrites commits (`merge`, `rebase`, `cherry-pick`, `revert`,
  `commit --amend`, `stash` included).** `.claude/settings.json` denies commit, push and tag. Leave finished work
  in the working tree.
- **Whenever a commit is due, the orchestrator proposes it as a command I can copy and run as it is:** one `bash`
  block per commit, containing `git add <the exact paths>` and `git commit -m "<message>"` joined with `&&`.
  - One logical change per commit; several commits are given in the order to run them.
  - Explicit paths only (never `git add -A` or `.`), covering every file of that change, new files included.
  - Message: imperative subject line of at most about 70 characters; a second `-m` for a body only when the
    reason is not obvious from the subject. No attribution trailer.
  - If the session runs in a worktree, say so and give the `cd` to it first, in its own block.
  - A commit is due when a task or a logical step of it is finished and verified (build with 0 warnings, tests
    pass), and before switching to unrelated work. Subagents only suggest a message; the orchestrator writes
    the command.
- **Every hand-over has four parts, in this order**, each as its own `bash` block or copyable text:
  1. the branch rename, if the branch name breaks the rule below;
  2. the commit command(s);
  3. the push command: `git push -u origin <branch>` the first time, `git push` afterwards;
  4. the pull request: a title and a description I can paste into GitHub, in a `markdown` block. Follow
     `.claude/skills/release/templates/pr.md`, describe the whole branch against `main` (not only the last
     commit), give the real test numbers, and end with the Claude Code line. When the branch already has a PR,
     give the updated description only if the new commits change what it should say; otherwise say it still fits.
- **Branch names are meaningful:** `feature/<what it adds>` or `fix/<what it fixes>`, lower case with hyphens,
  for example `feature/chunked-responses` or `fix/udp-state-key`. No random text or numbers at the end, and no
  other prefixes. If the session starts on a generated branch name (the app creates names like
  `claude/<words>-<random>` for worktrees), propose the rename as a command I can copy before the first commit:
  `git branch -m feature/<name>`. Never commit on `main`.
- Because nothing is committed between steps, each agent reports the exact files it changed, and the next agent
  reviews the working tree (`git status`, `git diff`), not a commit.
- Only the orchestrator changes `CLAUDE.md`, `BACKLOG.md` and `.claude/` (agents, skills, hooks, shared
  `settings.json`); they are tracked in the repo. `.claude/settings.local.json` stays untracked.
- Read-only git is fine: `status`, `diff`, `log`, `show`, `fetch`. To undo a temporary change, restore the file
  (`git checkout HEAD -- <file>` or `git restore <file>`); never discard changes you did not make yourself.
- GitHub: use the `gh` CLI. Read-only commands (`gh pr view/list/diff/checks`, `gh run`, `gh release view/list`)
  are allowed; anything that publishes (PRs, comments, releases, `gh api`, workflow runs) needs my approval and is
  only done when I ask. Subagents never run `gh` commands that publish.
- PRs and releases: the `/release` skill (`/release pr`, `/release bump X.Y.Z`, `/release publish`) checks and
  prepares everything, then gives me the commands to run; only when I ask.
- Releases are published by `release.yml` when a tag is pushed, never from a local machine: `dotnet nuget push`
  is blocked. The workflow runs on **lowercase** `v*` tags only, and fails unless the `<Version>` in
  `Directory.Build.props` equals the tag and `docs/releases/<version>.md` exists: that file is the release
  description (written in `/release bump`, shown to me before the commit) and becomes the GitHub release body.
- After the NuGet publish, the `image` job of `release.yml` builds the `Dockerfile` for `linux/amd64` and
  `linux/arm64` and pushes it to `ghcr.io/archofthings/rony` and Docker Hub `mojihub/rony` (tags `X.Y.Z`, `X.Y`,
  `X`, `latest`). It needs the repository secrets `DOCKERHUB_USERNAME` and `DOCKERHUB_TOKEN`. Docker is not installed
  on the local machine: image changes are proven by the `docker` job of CI on the pull request.
- The nuget.org Trusted Publishing policy uses the glob `Rony.Net*` (`Rony.Net.*` would not match `Rony.Net`
  itself). A new package must have an ID that starts with `Rony.Net`.
