---
id: B-016
title: Windows child output and batch files are decoded in the code page the tools actually write
type: bug
priority: P1
size: M
status: review
created: 2026-11-20
updated: 2026-11-20
blocked: none
---

## Goal

A Windows Worker's log shows the Chinese a step printed, not replacement characters. The process
runner decodes each captured line as UTF-8 and falls back to the host's OEM code page for a line a
native tool wrote in ANSI/OEM; batch files are written in the code page `cmd.exe` reads, so a
Chinese literal in the script is not misread before the first command runs.

## Background

`ProcessRunner` left `StandardOutputEncoding`/`StandardErrorEncoding` unset, so `BeginOutputReadLine`
decoded with the host's console encoding. `WriteScriptFile` wrote UTF-8 without a BOM, while
`cmd.exe` reads a batch file in the console's code page. A run with Chinese text could therefore be
wrong in two independent places (design: §6.8 of the run-console design).

## Acceptance Criteria

- [x] A UTF-8 line is decoded as UTF-8; a line that is not valid UTF-8 is decoded with the configured
      fallback (the host OEM code page on Windows).
- [x] Mixed UTF-8 and GBK lines in one stream are both readable, per line, not per stream.
- [x] A UTF-8 BOM is not part of the first line; CRLF, LF and lone CR all end a line; a line with no
      trailing newline is still delivered; an unterminated line is bounded at 1 MiB.
- [x] The existing `ProcessRunner` contracts stay: streaming before exit, per-stream order, pipes
      drained before return, cancellation kills the tree.
- [x] On Windows, a script that fits the OEM code page is written in that page with no `chcp`; one
      that does not (an emoji) is written as UTF-8 with `@chcp 65001 >nul`.
- [ ] CI windows-latest and a real Windows worker confirm `cmd.exe` agrees. Pending; see Verification.

## Subtasks

- [x] Byte-level line readers with strict UTF-8 and an OEM fallback.
- [x] `WindowsCodePages` (provider registration, OEM lookup off Windows = null).
- [x] `WindowsBatchScript.Encode` and the `WriteScriptFile` path.
- [x] Extend the ArgvEcho asset with a raw-bytes mode; pure and Windows-only facts.

## Dependencies

- F-007 (the process runner), F-020 (script step files), I-004 (AOT; no new package was needed).

## Test Cases

### TC-001: The runner decodes the bytes it received

`ProcessRunnerEncodingTests`: UTF-8 Chinese, GBK via an explicit 936 fallback, mixed UTF-8/GBK,
BOM, CRLF/LF/CR, no trailing newline, both streams' order.

### TC-002: The batch-file choice is lossless or loud

`WindowsBatchScriptTests`: ASCII and Chinese round-trip through the OEM page with no prologue; an
emoji falls back to UTF-8 with the prologue and decodes back to the original text.

### TC-003: A real `cmd.exe` agrees

`WindowsScriptExecutionTests` (skipped off Windows): a GBK `.cmd` prints Chinese unchanged; a UTF-8
emoji `.cmd` prints through the `chcp` path. These run on CI's windows-latest.

## Development Log

### 2026-11-20

- The plan expected an explicit `System.Text.Encoding.CodePages` package. On net10.0 the runtime
  already provides `CodePagesEncodingProvider`, and NU1510 (warnings-as-errors) rejects the explicit
  reference; the helper registers the provider without the package. Ruling recorded in the branch
  ledger.
- Batch scripts choose the OEM page first because that is the common, chcp-free path for a Chinese
  build script; UTF-8 + `chcp` remains for characters the page cannot represent.
- Historical logs are not rewritten. New runs are correct; old rows stay as they were recorded.
- Review pass: the OEM fallback now comes from `GetOEMCP` (the machine's System code page), not the
  display language's culture - an English UI on a Chinese System locale is the common case that
  broke the culture lookup. The existing `PipelineRunnerTests` byte assertion is platform-branched:
  Unix pins UTF-8, Windows pins the encoder's bytes.

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|
| — | — | — | — | — |

## Verification

- Related files: `src/Worker/ProcessRunner.cs`, `WindowsCodePages.cs`,
  `src/Worker/Pipelines/WindowsBatchScript.cs`, `PipelineRunner.cs` (`WriteScriptFile`),
  `tests/Zenkins.Tests/WorkerRuntime/ProcessRunnerEncodingTests.cs`, `WindowsBatchScriptTests.cs`,
  `WindowsScriptExecutionTests.cs`, `tests/Zenkins.Tests/TestAssets/ArgvEcho/Program.cs`.
- How to run/verify:
  - `dotnet test --filter "Category=WorkerRuntime"` → 46 passed, 2 skipped (the Windows facts on
    macOS).
  - `dotnet test --filter "Category=Pipeline"` → 209/209 (the script-file byte facts).
  - `dotnet test --filter "FullyQualifiedName~ProcessRunnerEncodingTests"` → 7/7.
  - `dotnet build src/Worker/Zenkins.Worker.csproj -c Release` → 0 warnings (AOT analyzers run on
    every build).
  - CI windows-latest and a real Windows Worker: **not run here**; the skipped facts are the gate.
- Results: the platform-independent facts and the existing Worker suite are green; the real
  `cmd.exe` facts await Windows.
