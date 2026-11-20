# Run Console and Worker Encoding Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give a run a full-page, Jenkins-shaped console that follows the tail, keeps every output line on its own row, refreshes status while the run is alive, and decodes Windows child output without mangling Chinese.

**Architecture:** A new `/console` page owns the run header and a rewritten `RunLogPanel`; the panel keeps the existing server-side SignalR subscription and view model, adds block-per-line rendering, a bounded retained window, and a JS follow controller. Both the run page and the console page poll `RunService.GetRunAsync` on a 1s/2s cadence while the run is non-terminal. The Worker decodes child pipes from bytes (strict UTF-8, system OEM code page fallback) and writes Windows batch files in the code page `cmd.exe` actually reads.

**Tech Stack:** .NET 10, C# 14, Blazor Server, SignalR client, xUnit v3, vanilla JS (`wwwroot/js`), SQLite/PostgreSQL, `System.Text.Encoding.CodePages`.

**Spec:** `docs/superpowers/specs/2026-11-20-run-console-design.md` (read it with this plan; it carries the why and the approved decisions).

## Global Constraints

- No real hostnames, usernames, absolute home paths, IPs, pids or run ids in tracked files. Use generic labels (`windows-worker`, `D:\work\repo`, `<user>`). AGENTS.md's pre-commit hook enforces this.
- UI copy goes through `L10n["…"]` and must be added to `src/Server/Localization/Translations.zh-Hans.json`; the catalog test is two-way (no missing, no orphan).
- New layout uses `.zk-*` classes in `src/Server/wwwroot/app.css`. `bb:` is a purged prebuilt Tailwind: a class the package did not emit silently does nothing. If a new `bb:` utility is unavoidable, add it to `wwwroot/styles/utilities.css`.
- No schema change, no `BuildLog`/`LogLineMessage` change, no API v1 change, no new status hub.
- The log download endpoint and its bytes stay untouched.
- Worker stays ASP.NET-free and NativeAOT-compatible (I-004); any new package must survive the AOT guard tests.
- Keep the existing `ProcessRunner` contracts: streams before exit, per-stream order, pipes drained before return, cancellation kills the tree and reaps.
- No ANSI parsing, no log folding, no pagination. The download is the canonical full log.

## Review Focus

- A line with leading spaces or blank lines: the console preserves both; a block element per line and a `white-space: pre` text span are the only way that works.
- A Windows run mixing GBK (`cmd` builtins, localized tools) and UTF-8 (`git`, Node) output: every line stays readable; the fallback is per line, not per stream.
- A user scrolling up while lines arrive: auto-follow pauses immediately, the viewport does not jump, and “jump to latest” is one click; scrolling back to the bottom does not undo an explicit Pause.
- A 100k-line run: the circuit retains at most 20,000 lines, renders at most 2,000, says both bounds in the UI, and the download still has every row.
- A run finishing while the page is open: status, step badges and elapsed update within 2s, then DB polling stops; the log connection keeps delivering late lines.

---

## Task 1: Worker decodes child output from bytes (B-016)

**Files:**
- Modify: `Directory.Packages.props`
- Modify: `src/Worker/Zenkins.Worker.csproj`
- Create: `src/Worker/WindowsCodePages.cs`
- Modify: `src/Worker/ProcessRunner.cs`
- Modify: `tests/Zenkins.Tests/TestAssets/ArgvEcho/Program.cs`
- Create: `tests/Zenkins.Tests/WorkerRuntime/ProcessRunnerEncodingTests.cs`

**Interfaces:**
- Produces: `ProcessRunnerOptions.OutputEncoding` (`Encoding`, default strict UTF-8) and `ProcessRunnerOptions.FallbackEncoding` (`Encoding?`, default `WindowsCodePages.Oem`).
- Produces: `WindowsCodePages.Oem` (`Encoding?`; null off Windows), `WindowsCodePages.Register()`.
- Produces: `ProcessOutputLine(Stream, string)` unchanged; `ProcessRunner` still invokes it per line.

- [ ] **Step 1: Add the code-pages package and the platform helper**

Add to `Directory.Packages.props`:
```xml
<PackageVersion Include="System.Text.Encoding.CodePages" Version="10.0.0" />
```
(If restore says that exact version is missing, pin the latest `10.0.x` the feed has; record the number in the commit and the card.)

Add to `src/Worker/Zenkins.Worker.csproj`:
```xml
<PackageReference Include="System.Text.Encoding.CodePages" />
```

Create `src/Worker/WindowsCodePages.cs`:
```csharp
public static class WindowsCodePages
{
    public static Encoding? Oem { get; }
    public static void Register();
}
```
Implementation notes: `Register()` calls `Encoding.RegisterProvider(CodePagesEncodingProvider.Instance)` once (idempotent). `Oem` is null on non-Windows; on Windows it is `Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage)` after `Register()`. If the lookup throws, `Oem` is null (the UTF-8 replacement path still works).

- [ ] **Step 2: Write the failing encoding tests**

Extend `tests/Zenkins.Tests/TestAssets/ArgvEcho/Program.cs` with a raw-bytes mode:
```text
ArgvEcho bytes out <hex>     -> writes Convert.FromHexString(hex) to stdout, no newline added
ArgvEcho bytes err <hex>     -> same to stderr
```
Use `Console.OpenStandardOutput()` / `OpenStandardError()` and `Write`; do not go through `Console.Write`.

Create `ProcessRunnerEncodingTests` with these facts (all run on every OS):
```csharp
[Fact] public async Task Utf8_chinese_output_is_not_mangled()
// child writes Encoding.UTF8.GetBytes("中文\n"); assert one line "中文"
[Fact] public async Task Gbk_output_falls_back_to_the_configured_code_page()
// Encoding.RegisterProvider(...); child writes Encoding.GetEncoding(936).GetBytes("中文\n");
// runner: OutputEncoding = strict UTF-8, FallbackEncoding = Encoding.GetEncoding(936)
[Fact] public async Task A_utf8_bom_is_not_part_of_the_first_line()
// child writes EF BB BF + UTF-8 bytes; first line has no U+FEFF
[Fact] public async Task Crlf_lf_and_cr_each_end_a_line()
// bytes for "a\r\nb\nc\rd"; assert ["a","b","c","d"]
[Fact] public async Task A_line_without_a_trailing_newline_is_still_delivered()
// bytes for "tail"; assert one line "tail"
[Fact] public async Task Both_streams_keep_their_own_line_order()
// bytes out "o1\r\no2\r\n", err "e1\r\ne2\r\n"; each list in order
[Fact] public async Task Mixed_utf8_and_gbk_lines_are_both_readable()
// one stream writes UTF-8 "中文\n" then GBK "测试\n"; both lines decode, in order
```
Use the existing `OutputCollector` pattern. Pass `TestContext.Current.CancellationToken` where the API takes one.

- [ ] **Step 3: Run the new tests and verify they fail**

Run: `dotnet test tests/Zenkins.Tests/Zenkins.Tests.csproj --filter "FullyQualifiedName~ProcessRunnerEncodingTests"`
Expected: FAIL — GBK/BOM/CR cases are misinterpreted (or the `bytes` mode is unknown).

- [ ] **Step 4: Replace `BeginOutputReadLine` with byte-level line readers**

In `ProcessRunner`:
- Add the two options with defaults (`OutputEncoding = new UTF8Encoding(false, throwOnInvalidBytes: true)`, `FallbackEncoding = WindowsCodePages.Oem`).
- After `process.Start()`, do **not** attach `OutputDataReceived`/`ErrorDataReceived` or call `Begin*ReadLine`.
- Start two readers over `process.StandardOutput.BaseStream` and `process.StandardError.BaseStream`.
- Each reader: read chunks; on the first bytes skip `EF BB BF`; split on `\n`, `\r\n` and lone `\r`; decode each complete line by trying `OutputEncoding.GetString`, and on `DecoderFallbackException` use `FallbackEncoding?.GetString(bytes) ?? Encoding.UTF8.GetString(bytes)`; invoke the callback with `ProcessOutputLine(StdOut|StdError, text)`.
- Treat `IOException`/`ObjectDisposedException` from a read as EOF (the kill path closes the pipe); the
  reader task must complete, not fault the whole run.
- Buffer an unterminated line up to 1 MiB; beyond that emit the decoded prefix plus `"<line truncated>"` and discard until the next terminator.
- At EOF, emit the pending line if non-empty; do not emit an extra empty line for a final newline.
- Keep the per-stream order; the two readers run concurrently (same contract as before).
- After `WaitForExitAsync` (or after `KillTree()` on cancellation), await both reader tasks before returning/throwing. Do not pass the cancellation token to the byte reads; the killed process closes the pipes. Keep `process.WaitForExit()` only if it still serves the exit code; `WaitForExitAsync` already fills `ExitCode`.
- Remove the now-false comments about `BeginOutputReadLine` draining; document the new drain.

- [ ] **Step 5: Run the encoding tests and the existing ProcessRunner tests**

Run:
```bash
dotnet test tests/Zenkins.Tests/Zenkins.Tests.csproj --filter "FullyQualifiedName~ProcessRunnerEncodingTests"
dotnet test tests/Zenkins.Tests/Zenkins.Tests.csproj --filter "FullyQualifiedName~Zenkins.Tests.WorkerRuntime.ProcessRunnerTests"
```
Expected: PASS both; the streaming-before-exit and cancellation/dispose facts stay green.

- [ ] **Step 6: Verify AOT/trimming still passes**

Run the existing AOT guard/publish command used by `WorkerAotGuardTests` (or `dotnet publish src/Worker/Zenkins.Worker.csproj -c Release -r osx-arm64` on this host). Expected: no new IL2026/IL3050; if the package needs a preservation entry, add it with a comment.

- [ ] **Step 7: Commit**

```bash
git add Directory.Packages.props src/Worker/Zenkins.Worker.csproj src/Worker/WindowsCodePages.cs src/Worker/ProcessRunner.cs tests/Zenkins.Tests/TestAssets/ArgvEcho/Program.cs tests/Zenkins.Tests/WorkerRuntime/ProcessRunnerEncodingTests.cs
git commit -m "fix(B-016): worker decodes child output as UTF-8 with an OEM fallback"
```

---

## Task 2: Windows batch files are read in the code page `cmd` uses (B-016)

**Files:**
- Create: `src/Worker/Pipelines/WindowsBatchScript.cs`
- Modify: `src/Worker/Pipelines/PipelineRunner.cs` (`WriteScriptFile`)
- Create: `tests/Zenkins.Tests/WorkerRuntime/WindowsBatchScriptTests.cs`
- Create: `tests/Zenkins.Tests/WorkerRuntime/WindowsScriptExecutionTests.cs`

**Interfaces:**
- Consumes: `WindowsCodePages.Oem` from Task 1.
- Produces: `public static (byte[] Bytes, bool HasChcpPrologue) Encode(string script, Encoding oem)`.
- Produces: `PipelineRunner.WriteScriptFile` writes those bytes on Windows and UTF-8 no-BOM elsewhere.

- [ ] **Step 1: Write the failing pure tests**

Create `WindowsBatchScriptTests`:
```csharp
[Fact] public void Ascii_round_trips_through_the_oem_page_without_a_prologue()
// Encode("echo hello", oem: ASCII-with-exception-fallback) -> Bytes are ASCII, HasChcpPrologue false
[Fact] public void Chinese_round_trips_through_gbk_without_a_prologue()
// Encode("echo 中文", oem: 936) -> decodes with 936 to the same text, no prologue
[Fact] public void Characters_outside_the_oem_page_fall_back_to_utf8_and_chcp()
// Encode("echo 😀", oem: 936) -> HasChcpPrologue true; bytes start with "@chcp 65001 >nul";
// the rest decodes as UTF-8 to the script
```
Use `Encoding.GetEncoding(936)` after `Encoding.RegisterProvider(CodePagesEncodingProvider.Instance)`; for the pure ASCII case build an encoding with `EncoderFallback.ExceptionFallback`.

- [ ] **Step 2: Run and verify failure**

Run: `dotnet test tests/Zenkins.Tests/Zenkins.Tests.csproj --filter "FullyQualifiedName~WindowsBatchScriptTests"`
Expected: FAIL — `WindowsBatchScript` does not exist.

- [ ] **Step 3: Implement `WindowsBatchScript.Encode`**

Try `oem.GetBytes(script)` with `EncoderFallback.ExceptionFallback`; if it succeeds, return those bytes with `HasChcpPrologue = false`. If it throws `EncoderFallbackException`, return `UTF8.GetBytes("@chcp 65001 >nul\r\n" + script)` with `true`. Do not add a BOM. Use `\r\n` for the prologue; cmd accepts it.

- [ ] **Step 4: Wire `WriteScriptFile`**

In `PipelineRunner.WriteScriptFile`:
- On Windows, call `WindowsBatchScript.Encode(script, WindowsCodePages.Oem ?? Encoding.UTF8)` and `File.WriteAllBytes(path, bytes)`.
- On Unix, keep `File.WriteAllText(path, script, new UTF8Encoding(false))`.
- Update the method's doc comment: Windows reads `.cmd` in the code page `cmd.exe` uses; a lossless OEM encode is the normal path, the `chcp` prologue is the Unicode fallback.

- [ ] **Step 5: Add the Windows execution tests**

Create `WindowsScriptExecutionTests` with facts that return early (or use the repo's skip mechanism) when `!OperatingSystem.IsWindows()`:
```csharp
[Fact] public async Task A_gbk_batch_file_prints_chinese_unchanged()
// script: "echo 中文"; Encode with WindowsCodePages.Oem; write temp .cmd; run
// cmd.exe /d /s /c path via ProcessRunner (UTF-8 primary, OEM fallback); assert line "中文"
[Fact] public async Task A_utf8_emoji_batch_file_prints_unchanged_via_chcp()
// script with an emoji; Encode falls back to UTF-8+chcp; assert the line contains the emoji
```
Write the temp file under `Path.Combine(Path.GetTempPath(), "zenkins-script-" + Guid.NewGuid().ToString("N"))`; delete it in a `finally`. Do not commit the path.

- [ ] **Step 6: Run the Windows facts (CI) and the pure facts locally**

Run:
```bash
dotnet test tests/Zenkins.Tests/Zenkins.Tests.csproj --filter "FullyQualifiedName~WindowsBatchScriptTests|FullyQualifiedName~WindowsScriptExecutionTests"
```
Expected: pure facts PASS locally; Windows facts PASS on `windows-latest` (record the CI run in the card).

- [ ] **Step 7: Commit**

```bash
git add src/Worker/Pipelines/WindowsBatchScript.cs src/Worker/Pipelines/PipelineRunner.cs tests/Zenkins.Tests/WorkerRuntime/WindowsBatchScriptTests.cs tests/Zenkins.Tests/WorkerRuntime/WindowsScriptExecutionTests.cs
git commit -m "fix(B-016): windows scripts are written in the code page cmd reads"
```

---

## Task 3: Bound the viewer window and split explicit vs scroll pause (F-047)

**Files:**
- Modify: `src/Server/Components/Shared/RunLogViewModel.cs`
- Modify: `tests/Zenkins.Tests/RunUi/RunLogControlsTests.cs`

**Interfaces:**
- Produces: `RunLogViewModel.RetainedCap = 20_000`; `ReceivedLines`, `DroppedFromViewer`, `DroppedWhilePaused`, `IsPausedByScroll`; `PauseFromScroll()`, `ResumeFromScroll()`.
- Produces: `RunLogPlan` gains `int ReceivedLines`, `int DroppedFromViewer`, `int DroppedWhilePaused`, `string? RetentionNotice`, `string? DroppedWhilePausedNotice`.
- Preserves: `Pause()`, `Resume()`, `SetSearch`, `SetStepFilter`, `MoveToNextMatch`, `MoveToPreviousMatch`, `JumpToTail`, `Add`, `Snapshot`, `Count`, `TotalLines` (now the retained count), `Segment`.

- [ ] **Step 1: Write the failing view-model tests**

Add to `RunLogControlsTests`:
```csharp
[Fact] public void A_viewer_keeps_only_the_newest_twenty_thousand_lines()
// Add seq 1..100005; Count == 20000; ReceivedLines == 100005; DroppedFromViewer == 80005;
// the oldest retained Seq is 80006; RetentionNotice is non-null
[Fact] public void Duplicate_sequences_do_not_count_as_received_twice()
// Add seq 1 twice; ReceivedLines == 1; Count == 1
[Fact] public void Scroll_pause_is_cleared_by_returning_to_the_bottom_but_explicit_pause_is_not()
// PauseFromScroll -> IsPaused/isPausedByScroll true; ResumeFromScroll -> both false
// Pause -> ResumeFromScroll -> still paused; Resume -> not paused
[Fact] public void New_while_paused_counts_by_sequence_when_old_lines_are_evicted()
// fill seq 1..20000, Pause() (frozen bound 20000), add seq 20001..20005
// -> the store evicts seq 1..5; NewWhilePaused == 5; DroppedWhilePaused == 5
```
Use `TestText.English` for the notice strings. Pin the exact retention sentence with placeholders filled.

- [ ] **Step 2: Run and verify failure**

Run: `dotnet test tests/Zenkins.Tests/Zenkins.Tests.csproj --filter "FullyQualifiedName~RunLogControlsTests"`
Expected: FAIL — new members do not exist.

- [ ] **Step 3: Implement retention and the two pause flags**

In `RunLogViewModel`:
- `RetainedCap = 20_000`; counters `_received`, `_droppedFromViewer`, `_droppedWhilePaused`.
- `Add`: only a new Seq increments `_received`; after merging, while `_lines.Count > RetainedCap`, remove the smallest key; increment `_droppedFromViewer`; if `_paused && removedKey <= _frozenUpperSeq`, increment `_droppedWhilePaused`.
- `Pause()`: set `_paused = true`, `_pausedByScroll = false`, freeze the upper Seq, reset `_droppedWhilePaused`.
- `PauseFromScroll()`: same, but `_pausedByScroll = true`.
- `Resume()`: clear both flags, the frozen bound, `_currentMatchIndex`, and `_droppedWhilePaused`.
- `ResumeFromScroll()`: call `Resume()` only when `_pausedByScroll`.
- `Plan`: compute `NewWhilePaused` as the count of retained lines with `Seq > _frozenUpperSeq` (not `all.Count - viewport.Count`); expose `ReceivedLines`, `DroppedFromViewer`, `DroppedWhilePaused`; build `RetentionNotice` when `DroppedFromViewer > 0` and `DroppedWhilePausedNotice` when `DroppedWhilePaused > 0`.
- Exact notices (English source; the zh catalog gets the translation in Task 9):
  - `"This viewer keeps the newest {0} lines; {1} older line(s) are not shown - download the full log."`
  - `"Paused - {0} line(s) were dropped by the viewer's retention while you were paused."`
- Keep the existing indicator/cap/search/filter wording unchanged.

- [ ] **Step 4: Run the view-model suite**

Run: `dotnet test tests/Zenkins.Tests/Zenkins.Tests.csproj --filter "FullyQualifiedName~Zenkins.Tests.RunUi"`
Expected: PASS; existing pause/resume/search/filter/step-count facts unchanged.

- [ ] **Step 5: Commit**

```bash
git add src/Server/Components/Shared/RunLogViewModel.cs tests/Zenkins.Tests/RunUi/RunLogControlsTests.cs
git commit -m "feat(F-047): the log viewer bounds what it retains and splits pause sources"
```

---

## Task 4: Render one block per line (F-047)

**Files:**
- Create: `src/Server/Components/Shared/RunConsoleLines.razor`
- Modify: `src/Server/Components/Shared/RunLogViewModel.cs` (`RunLogEntry`)
- Modify: `src/Server/Components/Pages/Runs/RunLogPanel.razor`
- Create: `tests/Zenkins.Tests/RunUi/RunConsoleLinesTests.cs`

**Interfaces:**
- Consumes: `RunLogPlan.Entries` and `RunLogPlan.CurrentMatchSeq` from Task 3.
- Produces: `RunConsoleLines` parameters: `IReadOnlyList<RunLogEntry> Entries`, `bool ShowTimestamps`, `long? CurrentMatchSeq`, `EventCallback<Guid> StepClicked`.
- Produces: `RunLogEntry(string? Header, LogLineMessage Line, Guid? HeaderStepId = null)`.

- [ ] **Step 1: Write the failing component tests**

Create `RunConsoleLinesTests` using `Microsoft.AspNetCore.Components.Web.HtmlRenderer`:
```csharp
private static async Task<string> RenderAsync(ParameterView parameters)
{
    var services = new ServiceCollection().AddLogging().BuildServiceProvider();
    await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
    return await renderer.Dispatcher.InvokeAsync(async () =>
        (await renderer.RenderComponentAsync<RunConsoleLines>(parameters)).ToHtmlString());
}
[Fact] public async Task Each_entry_renders_in_its_own_block()
// 3 entries "one","two","three"; Count occurrences of "class=\"zk-console-line" == 3;
// "one" and "two" are not in the same element; no "<pre" in the HTML
[Fact] public async Task Leading_spaces_and_empty_lines_are_preserved()
// entries "  indented" and ""; assert "  indented" appears and two line divs exist
[Fact] public async Task Timestamps_render_only_when_enabled()
// ShowTimestamps false -> no "<time"; true -> one per entry, text matches HH:mm:ss
[Fact] public async Task A_step_header_renders_as_a_clickable_button()
// entry.Header "1. Build", HeaderStepId = id -> "zk-console-step" button and the header text
```
`HtmlRenderer` renders static HTML; event attributes are not expected. If the renderer rejects an `@onclick` on a component with no interactive renderer, keep the button and assert the class/text only.

- [ ] **Step 2: Run and verify failure**

Run: `dotnet test tests/Zenkins.Tests/Zenkins.Tests.csproj --filter "FullyQualifiedName~RunConsoleLinesTests"`
Expected: FAIL — the component does not exist.

- [ ] **Step 3: Create `RunConsoleLines.razor`**

Markup shape:
```razor
@foreach (var entry in Entries)
{
    if (entry.Header is { } header && entry.HeaderStepId is { } headerId)
    {
        <button type="button" class="zk-console-step" data-step="@headerId"
                @onclick="@(() => StepClicked.InvokeAsync(headerId))">@header</button>
    }
    <div @key="entry.Line.Seq" class="zk-console-line @LineClass(entry.Line)"
         data-seq="@entry.Line.Seq"
         data-current-match="@(CurrentMatchSeq == entry.Line.Seq ? "true" : null)">
        @if (ShowTimestamps)
        {
            <time class="zk-console-time" datetime="@entry.Line.Timestamp.ToString("O")">@entry.Line.Timestamp.ToString("HH:mm:ss")</time>
        }
        <span class="zk-console-text">@entry.Line.Message</span>
    </div>
}
```
Rules:
- Every line is a block-level `<div>`; the newline between lines is the block boundary, not text.
- The message is inside `.zk-console-text` so `white-space: pre` preserves leading spaces and blank lines without catching Razor formatting whitespace.
- `LineClass` returns `zk-console-line--stderr` for `LogStream.StdErr` and `zk-console-line--match` for the current match.
- Empty messages still render a `<div>` (Task 8 gives it `min-height`).
- No `<pre>` and no `@entry.Line.Message` directly inside a whitespace-preserving parent.

- [ ] **Step 4: Extend `RunLogEntry` and `Segment`**

Add `Guid? HeaderStepId = null` as the last positional parameter (existing two-argument constructions keep compiling). In `Segment`, when a new step id starts a segment and the header exists, pass that id. The header string logic is unchanged.

- [ ] **Step 5: Replace the panel's log body**

In `RunLogPanel.razor`:
- Replace the `<pre id="run-log-lines">…</pre>` block with:
  ```razor
  <div id="run-log-scroll" class="zk-console-scroll">
      <div id="run-log-lines" class="zk-console-body @(_wrap ? "zk-console-wrap" : null)">
          <RunConsoleLines Entries="Plan.Entries" ShowTimestamps="_showTimestamps"
                           CurrentMatchSeq="Plan.CurrentMatchSeq" StepClicked="FilterToStep" />
      </div>
  </div>
  ```
- Add `_showTimestamps` and `_wrap` bool fields and two toolbar checkboxes/buttons with ids `run-log-timestamps` and `run-log-wrap`.
- `FilterToStep(Guid stepId)` calls `_viewModel.SetStepFilter(stepId)`, `Replan()`, `StateHasChanged()`.
- The `RequestedStepId` parameter is applied **once** (an `_requestedStepApplied` bool), not on every
  `OnParametersSet`: the console page's 2s status poll re-renders the parent, and re-applying the query
  value would silently reset a filter the user changed.
- Remove the old `LineClass` from the panel (it moves to the component) or delegate to a shared helper.
- Keep the empty states, cap notices, search, step select, pause/resume, reconnect and the download link.

- [ ] **Step 6: Run the component and RunUi tests**

Run:
```bash
dotnet test tests/Zenkins.Tests/Zenkins.Tests.csproj --filter "FullyQualifiedName~RunConsoleLinesTests"
dotnet test tests/Zenkins.Tests/Zenkins.Tests.csproj --filter "FullyQualifiedName~Zenkins.Tests.RunUi"
```
Expected: PASS. The old `LogPanelFirstRenderTests` still builds a plan before parameters (the lazy `Plan` property stays).

- [ ] **Step 7: Commit**

```bash
git add src/Server/Components/Shared/RunConsoleLines.razor src/Server/Components/Shared/RunLogViewModel.cs src/Server/Components/Pages/Runs/RunLogPanel.razor tests/Zenkins.Tests/RunUi/RunConsoleLinesTests.cs
git commit -m "fix(F-047): every console line renders on its own row"
```

---

## Task 5: The full-page console route and navigation (F-047)

**Files:**
- Create: `src/Server/Components/Pages/Runs/RunConsole.razor`
- Modify: `src/Server/Components/Pages/Runs/RunDetail.razor`
- Modify: `src/Server/Components/Shared/RunStepList.razor`
- Modify: `src/Server/Components/Pages/Runs/ProjectRuns.razor`
- Modify: `tests/Zenkins.Tests/RunUi/RunUiHost.cs`
- Create: `tests/Zenkins.Tests/RunUi/RunConsolePageTests.cs`
- Modify: `tests/Zenkins.Tests/RunUi/RunDetailFailureSummaryTests.cs`
- Modify: `tests/Zenkins.Tests/Milestone/MilestoneHappyPathTests.cs`

**Interfaces:**
- Consumes: the Task 4 panel and `RunLogViewModel`.
- Produces: routes `/runs/{RunId:guid}/console` and `/projects/{ProjectId:guid}/runs/{Number:int}/console`; query `?step=<guid>`.
- Produces: `RunStepList.RunId` (`Guid`, `EditorRequired`); `RunUiRun.ReadConsolePageAsync()`.

- [ ] **Step 1: Write the failing page/navigation tests**

Add to `RunUiRun`:
```csharp
public Task<string> ReadConsolePageAsync() => AuthTestHost.GetStringAsync(Owner, $"/runs/{RunId}/console");
```
Create `RunConsolePageTests`:
```csharp
[Fact] public async Task The_console_page_renders_the_header_toolbar_and_step_list()
// page contains id="run-console-page", "Console Output", id="run-log-controls",
// id="run-log-download", and each step name in the toolbar's step select
[Fact] public async Task The_console_page_offers_the_run_page_and_all_runs_links()
// href="/runs/{id}", href="/projects/{projectId}/runs"
[Fact] public async Task A_foreign_run_id_is_a_404()
// owner.GetAsync($"/runs/{Guid.NewGuid()}/console") -> NotFound
```
Update `RunDetailFailureSummaryTests`:
- The step links now point at `/runs/{runId}/console?step={stepId}`; assert the URL prefix and that `href="#run-log"` / `data-enhance-nav="none"` are absent.
- The running-run page has `id="run-console"` and no `id="run-log"`/`id="run-log-download"`.
Update `MilestoneHappyPathTests` line ~128: replace `id="run-log-download"` with `id="run-console"`.

- [ ] **Step 2: Run and verify failure**

Run: `dotnet test tests/Zenkins.Tests/Zenkins.Tests.csproj --filter "Category=RunUi"`
Expected: FAIL — the console route is missing; the run-page assertions still see the inline panel.

- [ ] **Step 3: Create `RunConsole.razor`**

Copy `RunDetail`'s resolution/404/ticket pattern:
- `@page` both routes; `[Authorize]`.
- `[Parameter] Guid RunId`, `Guid? ProjectId`, `int? Number`; `[SupplyParameterFromQuery(Name = "step")] Guid? StepId`.
- Inject `UserSession`, `RunService`, `RunLogAccess`.
- `OnInitializedAsync`: resolve the run (number route inside its project, guid route directly); set a real 404 with `IStatusCodePagesFeature` disabled; mint `_ticket` after a readable run.
- Header: back link to the run page, `Run #N`, `RunStatusBadge`, project name, worker, elapsed (`RunDisplay.DurationOrElapsed`), links to the run page and `/projects/{id}/runs`.
- Body: `<RunLogPanel RunId="_run.Id" Ticket="_ticket" Steps="_run.StepList.Concat(_run.PostSteps).ToArray()" RequestedStepId="StepId" />`.
- Wrapper id `run-console-page`; no `max-w-3xl`.
- Not yet polling (Task 6); no JS yet.

- [ ] **Step 4: Change the run page and step links**

In `RunDetail.razor`:
- Remove the `<RunLogPanel …>` block.
- Add a `Console Output` button in the header actions: `<BbButton id="run-console" Href="@($"/runs/{_run.Id}/console")" Variant="ButtonVariant.Default">@L10n["Console Output"]</BbButton>`.
- Remove `RunLogAccess` injection, `_accessTicket`, `_logStepFilter`, `FilterLogToStep`, `OnLogStepFilterChanged`, and the `RequestedStepId`/`StepFilterChanged` wiring.
- Pass `RunId="@_run.Id"` to every `RunStepList` call; remove `StepSelected`.

In `RunStepList.razor`:
- Add `[Parameter, EditorRequired] public Guid RunId { get; set; }`.
- Replace the fragment link with `href="@($"/runs/{RunId}/console?step={stepRowId}")"`; remove `data-enhance-nav` and the `StepSelected` callback/parameter.
- Keep the id `run-step-log-{name}`.
- Update all three `RunStepList` call sites in `RunDetail.razor` (main, stages, post) to pass `RunId`.

In `ProjectRuns.razor`: add a small `Console` link per run row to `/runs/{id}/console` with a stable id (`run-row-console-{id:N}` or the existing row prefix).

- [ ] **Step 5: Run the RunUi and Milestone tests**

Run:
```bash
dotnet test tests/Zenkins.Tests/Zenkins.Tests.csproj --filter "Category=RunUi"
dotnet test tests/Zenkins.Tests/Zenkins.Tests.csproj --filter "Category=Milestone"
```
Expected: PASS. The console page's static render includes the header, toolbar shell and step sidebar; the copy is not localized yet (Task 9) — use existing keys where possible and add keys in Task 9.

- [ ] **Step 6: Commit**

```bash
git add src/Server/Components/Pages/Runs/RunConsole.razor src/Server/Components/Pages/Runs/RunDetail.razor src/Server/Components/Shared/RunStepList.razor src/Server/Components/Pages/Runs/ProjectRuns.razor tests/Zenkins.Tests/RunUi/RunUiHost.cs tests/Zenkins.Tests/RunUi/RunConsolePageTests.cs tests/Zenkins.Tests/RunUi/RunDetailFailureSummaryTests.cs tests/Zenkins.Tests/Milestone/MilestoneHappyPathTests.cs
git commit -m "feat(F-047): a run has a full-page console"
```

---

## Task 6: Live status and elapsed refresh (F-047)

**Files:**
- Create: `src/Server/Components/Shared/RunLiveRefresher.cs`
- Modify: `src/Server/Components/Pages/Runs/RunConsole.razor`
- Modify: `src/Server/Components/Pages/Runs/RunDetail.razor`
- Create: `tests/Zenkins.Tests/RunUi/RunLiveRefresherTests.cs`

**Interfaces:**
- Produces: `RunLiveRefresher(Func<CancellationToken, Task<RunView?>> read)` with:
  - `RunView? LastGood { get; }`
  - `bool IsRefreshing { get; }`
  - `Exception? LastError { get; }`
  - `void Apply(RunView run)`
  - `Task<bool> TryRefreshAsync(CancellationToken ct)` — true = keep polling, false = terminal.
- Consumes: `RunService.GetRunAsync(Guid, CancellationToken)`.

- [ ] **Step 1: Write the failing refresher tests**

Create `RunLiveRefresherTests`:
```csharp
[Fact] public async Task A_running_read_updates_the_last_good_run_and_continues()
[Fact] public async Task A_terminal_read_stops_polling_and_does_not_read_again()
// read returns Success; TryRefreshAsync false; a second call returns false and the read count stays 1
[Fact] public async Task A_failed_read_keeps_the_last_good_run_and_keeps_polling()
// read throws; true; LastGood unchanged; LastError set
[Fact] public async Task An_in_flight_read_is_not_started_twice()
// first read waits on a TCS; a second TryRefreshAsync returns true without a second read
```
Build `RunView` via the existing test helpers or a minimal constructor; if the record is large, use `RunTestHost` helpers or a small factory in the test.

- [ ] **Step 2: Run and verify failure**

Run: `dotnet test tests/Zenkins.Tests/Zenkins.Tests.csproj --filter "FullyQualifiedName~RunLiveRefresherTests"`
Expected: FAIL — the type does not exist.

- [ ] **Step 3: Implement `RunLiveRefresher`**

- `Apply` sets `LastGood`.
- `TryRefreshAsync`: if `IsRefreshing`, return true; if `LastGood?.IsTerminal == true`, return false; set the in-flight flag; read; on success `Apply` and return `!run.IsTerminal`; on exception set `LastError` and return true; in `finally` clear the flag.
- No timer inside; the page owns cadence.
- Thread-safe enough for one UI loop; use a simple `bool` guarded by the circuit context. (The in-flight test drives the same instance sequentially/concurrently; a `SemaphoreSlim(1,1)` with `Wait(0)` or an `Interlocked` flag is fine.)

- [ ] **Step 4: Wire the console page**

- After resolving `_run`, create `_refresher = new RunLiveRefresher(ct => RunsService.GetRunAsync(_run.Id, ct)); _refresher.Apply(_run);`.
- In `OnAfterRenderAsync(firstRender)`, if `!RendererInfo.IsInteractive` return; start `_pollTask = PollAsync(_pollCts.Token)`.
- `PollAsync`: `PeriodicTimer(1s)`; every tick `await InvokeAsync(StateHasChanged)`; every second tick call `TryRefreshAsync`; if `LastGood` changed, assign `_run = LastGood`; if false, break; on `LastError` log once at Warning.
- `IAsyncDisposable`: cancel the CTS, await the task, swallow `OperationCanceledException`.
- Do not start a poll for an already-terminal run.
- The console page passes `_run.StepList.Concat(_run.PostSteps)` to the panel each render; the panel re-renders with new step states.

- [ ] **Step 5: Wire the run page**

Same pattern in `RunDetail.razor`:
- Create the refresher after the initial read; start the loop after the first interactive render.
- On a transition into a terminal state, refresh `_artifacts` once.
- Dispose the loop.
- The existing failure/step/status markup now updates without a reload.

- [ ] **Step 6: Run the tests and build**

Run:
```bash
dotnet test tests/Zenkins.Tests/Zenkins.Tests.csproj --filter "FullyQualifiedName~RunLiveRefresherTests"
dotnet test tests/Zenkins.Tests/Zenkins.Tests.csproj --filter "Category=RunUi"
dotnet build src/Server/Zenkins.Server.csproj -v q --nologo
```
Expected: PASS; no analyzer warnings.

- [ ] **Step 7: Commit**

```bash
git add src/Server/Components/Shared/RunLiveRefresher.cs src/Server/Components/Pages/Runs/RunConsole.razor src/Server/Components/Pages/Runs/RunDetail.razor tests/Zenkins.Tests/RunUi/RunLiveRefresherTests.cs
git commit -m "feat(F-047): run status and elapsed keep moving while the run does"
```

---

## Task 7: Auto-follow the tail (F-047)

**Files:**
- Create: `src/Server/wwwroot/js/run-console.js`
- Modify: `src/Server/Components/App.razor`
- Modify: `src/Server/Components/Pages/Runs/RunLogPanel.razor`
- Create: `tests/Zenkins.Tests/RunUi/RunConsoleFollowTests.cs`

**Interfaces:**
- Consumes: `RunLogViewModel.PauseFromScroll/ResumeFromScroll/IsPausedByScroll` from Task 3; `Plan.CurrentMatchSeq`.
- Produces: `window.zenkinsConsole.init(id, dotNetRef)`, `afterRender(id, paused, matchSeq)`, `dispose(id)`.
- Produces: `[JSInvokable] Task RunLogPanel.OnConsoleScroll(bool atBottom)`.

- [ ] **Step 1: Write the failing wiring tests**

Create `RunConsoleFollowTests`:
```csharp
[Fact] public void App_loads_run_console_js_before_blazor()
// read App.razor; index of "js/run-console.js" < index of "_framework/blazor.web.js"
[Fact] public void The_panel_exposes_the_follow_contract()
// read RunLogPanel.razor; contains "zenkinsConsole.init", "zenkinsConsole.afterRender",
// "zenkinsConsole.dispose", "OnConsoleScroll"
[Fact] public void The_script_defines_init_afterRender_and_dispose()
// read run-console.js; contains "afterRender" and "dispose" and "scrollTop = "
```
These are source-contract facts, matching the repo's existing JS-source tests. They do not prove scrolling; the browser pass does.

- [ ] **Step 2: Run and verify failure**

Run: `dotnet test tests/Zenkins.Tests/Zenkins.Tests.csproj --filter "FullyQualifiedName~RunConsoleFollowTests"`
Expected: FAIL — the script and wiring do not exist.

- [ ] **Step 3: Create `run-console.js`**

Implement:
- `init(id, dotNet)`: find `document.getElementById(id)`; store `{dotNet, suppress:false, scheduled:false}`; attach a `scroll` listener that (a) returns while `suppress`, (b) schedules one `requestAnimationFrame`, (c) computes `atBottom = el.scrollHeight - el.scrollTop - el.clientHeight <= 24`, (d) calls `dotNet.invokeMethodAsync('OnConsoleScroll', atBottom)`.
- `afterRender(id, paused, matchSeq)`: if `matchSeq` is set, find `el.querySelector('[data-seq="' + matchSeq + '"]')` and `scrollIntoView({block:'center'})` with suppression; else if `!paused`, set `el.scrollTop = el.scrollHeight` with suppression.
- `dispose(id)`: remove the listener and delete the entry.
- Suppression: set `suppress = true` before the programmatic scroll, clear it in `requestAnimationFrame(() => suppress = false)`.
- No framework, no globals beyond `window.zenkinsConsole`.

- [ ] **Step 4: Load the script**

In `App.razor`, add `<script src="js/run-console.js"></script>` next to the other page scripts, before `_framework/blazor.web.js`.

- [ ] **Step 5: Wire the panel**

- Inject `IJSRuntime JS`.
- Const `ConsoleScrollElementId = "run-log-scroll"` (matches Task 4's div id).
- `OnAfterRenderAsync(bool firstRender)`: if `!RendererInfo.IsInteractive` return; on first render create `_selfRef = DotNetObjectReference.Create(this)` and call `zenkinsConsole.init`; on every render call `zenkinsConsole.afterRender(ConsoleScrollElementId, Plan.IsPaused, Plan.CurrentMatchSeq)`.
- `[JSInvokable] public Task OnConsoleScroll(bool atBottom)`:
  - `if (!RendererInfo.IsInteractive) return Task.CompletedTask;`
  - `return InvokeAsync(() => { if (atBottom && _viewModel.IsPausedByScroll) { _viewModel.ResumeFromScroll(); Replan(); StateHasChanged(); } else if (!atBottom && !Plan.IsPaused) { _viewModel.PauseFromScroll(); Replan(); StateHasChanged(); } });`
- The explicit Pause button keeps calling `_viewModel.Pause()`; Resume and “Jump to latest” call `_viewModel.Resume()`.
- `DisposeAsync`: call `zenkinsConsole.dispose`, dispose `_selfRef`, then dispose the hub connection.
- The view model's `NewWhilePaused` drives the “N new lines” indicator and the “Jump to latest” button, both already in the toolbar.

- [ ] **Step 6: Syntax-check and run the tests**

Run:
```bash
node --check src/Server/wwwroot/js/run-console.js
dotnet test tests/Zenkins.Tests/Zenkins.Tests.csproj --filter "FullyQualifiedName~RunConsoleFollowTests"
dotnet test tests/Zenkins.Tests/Zenkins.Tests.csproj --filter "Category=RunUi"
```
Expected: PASS. The static `RunLogPanel` render in tests has no interactive renderer, so no JS is invoked.

- [ ] **Step 7: Commit**

```bash
git add src/Server/wwwroot/js/run-console.js src/Server/Components/App.razor src/Server/Components/Pages/Runs/RunLogPanel.razor tests/Zenkins.Tests/RunUi/RunConsoleFollowTests.cs
git commit -m "feat(F-047): the console follows the tail until you scroll up"
```

---

## Task 8: Console layout, step sidebar and CSS (F-047)

**Files:**
- Modify: `src/Server/wwwroot/app.css`
- Modify: `src/Server/Components/Pages/Runs/RunConsole.razor`
- Modify: `src/Server/Components/Pages/Runs/RunLogPanel.razor`
- Modify: `tests/Zenkins.Tests/RunUi/RunConsolePageTests.cs`

**Interfaces:**
- Consumes: `Steps`, `Plan.StepFilter`, `Plan.StepCounts`, `RunDisplay.DurationOrElapsed`.
- Produces: CSS classes `.zk-console-page`, `.zk-console-header`, `.zk-console-toolbar`, `.zk-console-grid`, `.zk-console-scroll`, `.zk-console-body`, `.zk-console-line`, `.zk-console-line--stderr`, `.zk-console-line--match`, `.zk-console-text`, `.zk-console-time`, `.zk-console-step`, `.zk-console-aside`, `.zk-console-jump`, `.zk-console-wrap`.

- [ ] **Step 1: Write the failing static page assertions**

In `RunConsolePageTests`, add:
```csharp
[Fact] public async Task The_console_page_has_the_body_scroll_container_and_step_sidebar()
// id="run-log-scroll", id="run-console-steps", id="run-log-step-filter", id="run-log-pause",
// id="run-log-timestamps", id="run-log-wrap", id="run-log-download"
```
- Assert the page does **not** contain `id="run-log"` as the old inline card (the console body id is `run-log-scroll`); adjust the existing assertions.

- [ ] **Step 2: Run and verify failure**

Run: `dotnet test tests/Zenkins.Tests/Zenkins.Tests.csproj --filter "FullyQualifiedName~RunConsolePageTests"`
Expected: FAIL — the sidebar and toggles do not exist.

- [ ] **Step 3: Add the page layout**

In `RunConsole.razor`, wrap the header and body in `id="run-console-page" class="zk-console-page"`. Do not use `max-w-3xl`.

Initial CSS (in `app.css`):
```css
.zk-console-page { display: flex; flex-direction: column; gap: 0.75rem; height: calc(100dvh - 9rem); min-height: 24rem; }
.zk-console-header { display: flex; flex-wrap: wrap; align-items: center; justify-content: space-between; gap: 0.75rem; }
.zk-console-grid { display: grid; grid-template-columns: minmax(0, 1fr) 17rem; gap: 1rem; flex: 1; min-height: 0; }
.zk-console-scroll { position: relative; overflow: auto; border: 1px solid var(--border); border-radius: var(--radius); background: var(--muted); min-height: 0; }
.zk-console-body { padding: 0.75rem; font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, "Liberation Mono", monospace; font-size: 0.75rem; line-height: 1.5; }
.zk-console-line { display: block; min-height: 1.25em; }
.zk-console-text { white-space: pre; }
.zk-console-wrap .zk-console-text { white-space: pre-wrap; overflow-wrap: anywhere; }
.zk-console-line--stderr { color: var(--destructive); border-left: 2px solid var(--destructive); padding-left: 0.5rem; margin-left: -0.5rem; }
.zk-console-line--match { background: color-mix(in oklab, var(--primary) 18%, transparent); }
.zk-console-time { color: var(--muted-foreground); margin-right: 0.5rem; }
.zk-console-step { display: block; width: 100%; text-align: left; font: inherit; color: var(--foreground); background: transparent; border: 0; border-top: 1px solid var(--border); padding: 0.35rem 0; cursor: pointer; }
.zk-console-step:hover { color: var(--primary); }
.zk-console-aside { border: 1px solid var(--border); border-radius: var(--radius); padding: 0.75rem; overflow: auto; min-height: 0; }
.zk-console-jump { position: absolute; right: 1rem; bottom: 1rem; }
@media (max-width: 64rem) {
  .zk-console-page { height: auto; min-height: 32rem; }
  .zk-console-grid { grid-template-columns: minmax(0, 1fr); }
  .zk-console-aside { display: none; }
}
```
Use only token names that `styles/theme.css` defines (`--border`, `--radius`, `--muted`, `--foreground`, `--primary`, `--destructive`, `--muted-foreground`). If a token is missing, use the closest existing one rather than a hard-coded color. `color-mix` is supported in current browsers; if the project avoids it, use `background: var(--accent)`.

- [ ] **Step 4: Add the step sidebar**

In `RunLogPanel.razor`, inside `.zk-console-grid` after the scroll container:
```razor
<aside id="run-console-steps" class="zk-console-aside" aria-label="@L10n["Steps"]">
  @foreach (var step in Steps ?? [])
  {
    <button type="button" class="zk-console-step-link" data-active="@(Plan.StepFilter == step.Id ? "true" : null)"
            @onclick="@(() => FilterToStep(step.Id))" disabled="@(step.Id is null)">
      <span>@step.Index. @step.Name</span>
      <RunStatusBadge Status="@step.Status" />
      <span>@RunDisplay.DurationOrElapsed(step.StartedAt, step.EndedAt, DateTimeOffset.UtcNow, L10n)</span>
    </button>
  }
</aside>
```
- Use `BbBadge Variant="RunDisplay.StepBadgeFor(step.Status)"` (as `RunStepList` does), not `RunStatusBadge` (which is for run states).
- The step filter select stays in the toolbar for narrow screens.
- Clicking a step sets the filter; clicking the active step clears it (toggle) — or keep it selected. Choose toggle: `_viewModel.SetStepFilter(_viewModel.StepFilter == step.Id ? null : step.Id)`.
- The sidebar shows main + post steps because the page passes both.

- [ ] **Step 5: Add the jump chip and body controls**

- Put the “Jump to latest” button inside `.zk-console-scroll` as a child with `class="zk-console-jump"`, rendered when `Plan.IsPaused`; it calls `_viewModel.Resume(); ReplanAsync();` and is a real `<BbButton>`.
- Add the “N new lines” text next to the follow indicator.
- Toolbar: follow/pause, search, prev/next, step select, timestamps toggle, wrap toggle, download.
- The toolbar wraps on narrow widths.

- [ ] **Step 6: Run the static page tests and build**

Run:
```bash
dotnet test tests/Zenkins.Tests/Zenkins.Tests.csproj --filter "FullyQualifiedName~RunConsolePageTests"
dotnet build src/Server/Zenkins.Server.csproj -v q --nologo
```
Expected: PASS; no warnings.

- [ ] **Step 7: Commit**

```bash
git add src/Server/wwwroot/app.css src/Server/Components/Pages/Runs/RunConsole.razor src/Server/Components/Pages/Runs/RunLogPanel.razor tests/Zenkins.Tests/RunUi/RunConsolePageTests.cs
git commit -m "feat(F-047): the console fills the page with steps beside it"
```

---

## Task 9: Localization, kanban cards, CHANGELOG and the D-004 note (F-047/B-016)

**Files:**
- Modify: `src/Server/Localization/Translations.zh-Hans.json`
- Create: `docs/kanban/issues/f-047-run-console.md`
- Create: `docs/kanban/issues/b-016-worker-output-encoding.md`
- Modify: `docs/kanban/board.md`
- Modify: `docs/kanban/issues/d-004-run-log-viewer.md`
- Modify: `CHANGELOG.md`

**Interfaces:**
- No code interfaces. The catalog test is the gate.

- [ ] **Step 1: Run the catalog test to see every missing/orphan key**

Run: `dotnet test tests/Zenkins.Tests/Zenkins.Tests.csproj --filter "FullyQualifiedName~Zenkins.Tests.Localization"`
Expected: FAIL, listing every new `L10n[...]` string without a zh-Hans entry and every removed key still in the catalog.

- [ ] **Step 2: Add the zh-Hans translations and remove orphans**

For each new key, add a Simplified Chinese sentence with the same `{0}` placeholder set. Remove keys whose source string no longer appears (the old inline panel's keys that the rewrite deleted). Do not leave orphaned entries; the catalog test checks both directions.

- [ ] **Step 3: Write the cards**

Use `docs/kanban/templates/issue-template.md`.

`F-047` (feature, P1, status todo → review when done):
- Goal: a Jenkins-shaped full-page console with follow-tail, live status, step sidebar and correct line rendering.
- Background: the three observed defects; the approved design link.
- AC: the Review Focus bullets from this plan, plus the routes, the 20k/2k bounds, the download unchanged, and the run page entry.
- Test Cases: TC-001 follow/pause; TC-002 line rendering; TC-003 live status; TC-004 step filter/search; TC-005 retention/cap; TC-006 mobile/browser.
- Verification: fill with the actual commands/results after Task 10.

`B-016` (bug, P1):
- Goal: Windows child output and `.cmd` contents decode correctly.
- AC: UTF-8 + GBK lines, BOM, CR/CRLF, no trailing newline; Windows `.cmd` Chinese/emoji paths; existing streaming/cancel tests.
- Verification: the Windows CI run + the real Windows worker manual test.

`docs/kanban/board.md`: add both rows with priority/date.
`D-004`: add a `### 2026-11-20` Development Log entry: F-047 supersedes the inline panel placement and the same-page fragment navigation; the control semantics (pause/search/step filter/download) remain, with the pause split into explicit and scroll pauses.
`CHANGELOG.md` `## Unreleased`: one entry for F-047 and one for B-016, in the repo's style; say explicitly that historical logs are not rewritten and that the 20k/2k bounds are new.

- [ ] **Step 4: Run the catalog and build**

Run:
```bash
dotnet test tests/Zenkins.Tests/Zenkins.Tests.csproj --filter "FullyQualifiedName~Zenkins.Tests.Localization"
dotnet build src/Server/Zenkins.Server.csproj -v q --nologo
```
Expected: PASS; no orphan/missing keys.

- [ ] **Step 5: Privacy check before committing**

Run: `git diff --cached | grep -nE "<real-host>|<real-user>|[A-Z]:\\\\Users\\\\|/Users/[a-z]+"` — must be empty. Replace any real machine/path/user/run id with generic labels.

- [ ] **Step 6: Commit**

```bash
git add src/Server/Localization/Translations.zh-Hans.json docs/kanban/issues/f-047-run-console.md docs/kanban/issues/b-016-worker-output-encoding.md docs/kanban/board.md docs/kanban/issues/d-004-run-log-viewer.md CHANGELOG.md
git commit -m "docs(F-047,B-016): the console and the encoding fix are on the board"
```

---

## Task 10: Full verification and the real-browser pass (F-047/B-016)

**Files:**
- Modify: `docs/kanban/issues/f-047-run-console.md` (Verification)
- Modify: `docs/kanban/issues/b-016-worker-output-encoding.md` (Verification)
- Fix any file the checks surface.

- [ ] **Step 1: Run the full automated gates**

```bash
dotnet build -c Release
node --check src/Server/wwwroot/js/run-console.js
dotnet test -c Release --filter "Category=RunUi"
dotnet test -c Release --filter "Category=WorkerRuntime"
dotnet test -c Release --filter "FullyQualifiedName~Zenkins.Tests.Localization"
dotnet test -c Release --filter "Category!=Milestone"
dotnet test -c Release --filter "Category=Milestone"
```
Expected: all green. If a pre-existing flake (Q-023) fails, re-run it alone and record the result honestly.

- [ ] **Step 2: Run the worker/console browser pass**

Start a Server + Worker (the dev pair or a separate port), create/run a 3-step Freestyle:
1. step 1 prints five Chinese lines;
2. step 2 sleeps ~20s;
3. step 3 fails with exit code 3.
Verify in a real browser:
- the console page fills the viewport; lines are on separate rows; leading spaces/blank lines survive;
- auto-follow scrolls; scrolling up pauses and shows “N new lines”; jump-to-latest resumes; explicit pause survives scrolling to the bottom;
- status, step badge, elapsed update while running and stop at terminal; the failure summary appears on the run page without reload;
- step header/sidebar filters; search; download;
- 375px width: no page-level horizontal scroll; the toolbar wraps; the steps select works.

Record screenshots or exact observations in the card. If the environment cannot host a browser, say so and list the browser pass as pending, not passed.

- [ ] **Step 3: Run the real Windows-worker check**

On a Windows worker (or the CI `windows-latest` job plus a real worker if available), run a Freestyle whose steps emit Chinese through `cmd`/`svn`/`git` and echo a Chinese literal. Verify the console shows the exact text. Record the machine as a generic label — never the real hostname, path or user.

- [ ] **Step 4: Fill the card Verification sections with actual output**

Include:
- the commands and totals;
- the browser pass (or the honest "not run here" reason);
- the Windows facts;
- the known limitations (reconnect replays history; ANSI codes are not colored; the viewer retains 20k/render 2k).

- [ ] **Step 5: Final privacy sweep and commit**

```bash
git diff --cached | grep -nE "<real-host>|<real-user>|[A-Z]:\\\\Users\\\\|/Users/[a-z]+"   # must be empty
git add docs/kanban/issues/f-047-run-console.md docs/kanban/issues/b-016-worker-output-encoding.md
git commit -m "test(F-047,B-016): the console and encoding fixes are verified"
```

---

## Plan self-check

- **Spec coverage:** routes/nav §6.1 → Task 5; layout §6.2 → Task 8; follow §6.3 → Tasks 3/7; line rendering §6.4 → Task 4; view model/caps §6.5 → Task 3; step sidebar §6.6 → Task 8; live status §6.7 → Task 6; encoding §6.8 → Tasks 1/2; run page §6.9 → Task 5; localization/a11y §6.10 → Tasks 8/9; reconnect/errors §6.11 → Tasks 4/6/7; tests §8 → Tasks 1-10; cards/changelog §9 → Task 9.
- **Type consistency:** `RunLogPlan` fields, `RunLogEntry.HeaderStepId`, `RunLiveRefresher.TryRefreshAsync`, and the JS names are used identically in every task.
- **Review Focus tests:** leading spaces/blank lines (Task 4), mixed GBK/UTF-8 (Tasks 1/2/10), scroll-up pause (Tasks 3/7), 100k-line bounds (Tasks 3/10), terminal stop (Task 6/10).
- **Known gaps by design:** no automated browser interaction (the repo has no browser harness; D-004-B3 records why TestServer cannot host the WebSocket circuit) — the browser pass is manual and must be recorded honestly.
