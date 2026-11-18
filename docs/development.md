# Development setup

Everything needed to go from a clean machine to a signed-in dashboard: prerequisites, build, first
run, configuration, repository layout, and troubleshooting.

Deploying instead of developing — the published binaries on a machine with no SDK, installed as a
service, upgraded with data intact — is [`install.md`](install.md); this guide covers the developer
loop (`dotnet run` from a clone).

> **Verified on:** macOS 26.5 (arm64) with .NET SDK **10.0.403**, on 2026-11-12; re-verified after
> project CRUD landed. The test suites (§2) were re-verified on 2026-11-14, when the recovery
> acceptance suite (Q-004) added a third category and the fast filter grew its second exclusion;
> I-003's publish smoke added a fourth category and the third exclusion the same day (fix round).
> I-004 added a fifth category (PublishAot) and the fourth exclusion on the same day, together with
> the AOT analyzer surface on the Worker and Shared projects (§1's toolchain note only applies to
> that suite). Q-009 added a sixth category (GitSshd) and the fifth exclusion on 2026-11-15, with
> the OpenSSH-server prerequisite of §1 for that suite only. P-001 stamped the product version (the
> one `Version` property in `Directory.Build.props`, surfaced by the binaries' version flag and the
> shell footer's version line) and re-verified the whole gate through `scripts/verify.sh` on
> 2026-11-15, when the fast suite grew the version-agreement fact. B-002 added the claim-cap
> regression on 2026-11-15. Q-010 added the split-tag pin on 2026-11-15 (a `Category=Concurrency`
> test, which runs in the fast suite). F-028 and F-029 added the project gate and the queue controls
> on 2026-11-16: 18 tests in the fast suite (7 project enable/disable, 8 queue-control facts, 2
> webhook gate, 1 project-cap race) and a seventh category, `Postgres`, for the concurrency fact
> that only a real PostgreSQL can show (§2). F-027 added the organization API on 2026-11-16: 18
> tests in the fast suite (13 API contract facts, 5 API tenancy probes). F-036 and F-037 added build
> numbers plus rebuild/replay on 2026-11-16: 23 tests in the fast suite (6 build-number facts, 1
> migration/backfill fact, 8 rebuild/replay facts, 2 worker-layer pin facts, 3 `ZENKINS_BUILD_*`
> composition facts, 3 Q-006 permalink and re-run probes) plus 1 `Category=Postgres` fact for the
> numbering retry - no new category and no new exclusion, so the sextuple filter is unchanged.
> B-008, Q-015 and Q-016 followed on 2026-11-16: 7 tests in the fast suite (5 facts for the
> `Idempotency-Key`'s project and payload scope, 2 for the token stamp's best-effort bound and the
> token list's Owner check) plus 1 `Category=Postgres` fact for the trigger-side queue cap. Still no new
> category and no new exclusion.
> F-024 (scheduled triggers) added **48** tests on 2026-11-16: 24 for the cron arithmetic and its
> time-zone behaviour (9 facts plus a 15-row `Theory` for the refused expressions - the DST gap and the
> repeated hour in both directions, the Vixie day rule, IANA zones, "this never fires"), 18 facts for
> the schedule itself (one run per due time with the definitions' defaults, the overlap skip naming the
> in-flight run, "queue anyway" and its F-029 cap, the no-backfill miss accounting and its folded count,
> the disabled project/schedule skips, the already-recorded due time - including the shape where a tick
> has both a missed backlog and a run to queue, and the fact that a lost due time does not stall the
> next one - a DST gap through a real tick, and the settings card's create/preview/toggle/delete forms),
> and 6 for the tenancy harness (the two new tenant-owned entities: read isolation, cross-tenant write
> refusal and Find-after-switch, in both directions). Still no new category and no new exclusion - the
> sextuple filter is unchanged.
> I-005 (the Worker Git remote probe) added **18** tests on 2026-11-16: 6 for the probe's own git layer
> (heads + remote HEAD in one invocation against a real bare repository, an empty repository as a
> success, and the typed failures a real wrong token / refused connection / killed budget produce) and 7
> for the primitive over the product's surfaces (the full in-process worker path with no run created and
> the dashboard untouched, a probe answered while every build slot is busy, a recorded failure that is
> never "no change", the bounded `no-worker` answer, three callers sharing one probe and one worker
> conversation, the two credential scopes not replaying each other, and the tenant boundary), plus 2 for
> the capability promise (a pre-I-005 re-registration clears `canProbe` and the worker page says so; a
> heartbeat that omits the field leaves the stored value alone), and 3 pinning F-013's clone diagnosis that
> the shared classifier now carries (the missing branch answers before an auth-sounding stderr, the two
> shared kinds survive, and a silent git still yields "no stderr output" rather than a bare `: )`). Still no
> new category and no new exclusion - the sextuple filter is unchanged.
> B-007 and Q-013 (the theme palette and the theme shell tests) added **3** tests on 2026-11-16: the
> served-stylesheet cascade that pins the base token palette (the default state's tokens in both modes,
> their equality with the library's own zinc, and a picked accent still overriding them), alongside
> D-007's tightened ordering, wiring and cookie-contract facts. No new category, no new exclusion.
> Q-020 added **1** test on 2026-11-16: the offline-window margin fact (a ring worker behind a shut
> loopback gate must keep its run and lose none of twelve, with the silence measured from the
> server's own heartbeat row). It moved a harness constant (the concurrency ring's offline window,
> 5 s → 15 s) rather than a product one, and added no category and no exclusion.
> Q-019 (a short organization name colliding with a machine-generated base64 payload in the tenant page
> scan) added **20** facts on 2026-11-17: the two-sided fact for the reader the scan now uses, over both
> encodings the pages carry - an escaped standard-base64 run, a bare base64 run and a base64url
> antiforgery token that each spell the other organization's name verbatim are ignored, with the marker
> asserted to have fired so no case can pass vacuously, and the underscore-first base64url token that the
> reproducer caught; a rendered value still trips the scan as a table cell, an `h1`, an href/title/text, a
> script's JSON literal, a real `<meta>` shell and a real `<!--Blazor:…-->` shell; all twelve scanned
> values are proven visible in rendered markup, including the 29-symbol ciphertext and the 36-symbol GUID,
> and a foreign id stays visible inside a link and inside a generated `rotate-{guid}` DOM id; both
> run-length minimums are pinned (39-vs-40 symbols for base64url, 31-vs-32 for base64); and the two known
> limitations (a value touching a payload, a 40-symbol hyphenated rendered value) are pinned as behaviour.
> They sit in `Category=TenantIsolation`, which the fast suite already contains, so there is no new
> category and no new exclusion - the sextuple filter is unchanged. Q-014 (the artifact-zip temp snapshot)
> moved no count: it retargeted the zip fact and two harness self-tests at the download's own scoped temp
> root, 2 rewrites and 0 deletions.
> F-034 (organization environment variables) added **25** tests on 2026-11-17: 19 in the fast suite
> (`OrganizationEnvironmentTests` - the store/name/role/cap facts, including a 5-row `Theory` that holds
> the organization validator's answer together with the stored-secret validator's and a 4-row `Theory`
> for the reserved `ZENKINS_*` prefix at save; TC-001's two projects running real steps; TC-002's frozen
> snapshot and its run-page view; a deleted variable whose old run keeps its value; and a headless webhook
> run), 1 net in `RunEnvironmentTests` (the reserved name dropped and reported by the composition, plus
> the inherited/metadata/server-map order) and 5 in the tenancy harness (the new tenant-owned entity's
> three probes, the snapshot reader asked with no request tenant at all, and a foreign-id mutation probe;
> the page scan also covers `/organizations/environment`). No new category and no new exclusion - the
> sextuple filter is unchanged, and the fast suite goes from 1341 to 1366 discovered tests.
> Q-021 (the theme cascade model, which counted element selectors as zero and ignored `@layer`) added
> **8** facts on 2026-11-17, all in `ThemeCascadeModelTests.cs`, and every one of them pins a dimension a
> browser decides and the old reader got by luck: specificity is the (ids, classes, types) triple compared
> lexicographically, so an unlayered `html:root` (0,1,1) beats a later `[data-base-color="zinc"]`
> (0,1,0) in *both* sheet orders while `:root[data-base-color]` (0,2,0) still beats `html:root`, and a lone
> `html` (0,0,1) does not outrank a bare attribute; an unlayered normal declaration beats every layered one
> whichever sheet comes first, and layer order itself comes from the `@layer a, b;` statement and first
> appearance rather than from block order, with nested layers weaker than unlayered rules; and on the real
> served sheets `--sidebar-width` is exactly `15rem`, `--sidebar-width-icon` exactly `3rem` (that pair is
> deliberate - the first is only right if layers are compared, the second is declared *only* inside
> `@layer base`, so skipping or refusing layered declarations cannot pass both) and
> `--sidebar-width-mobile` `18rem`. The rest pin the refusals: an `@media`/`@supports` prelude the reader
> cannot evaluate makes the properties inside it answer unknown - per property, not per sheet or block, and
> a conditional declaration that provably loses still lets the answer through - and unknown is asserted to
> be distinguishable from "never declared"; a rule that cannot select `<html>` (`body`, `@container`,
> `@keyframes` frames) supplies it nothing; and `!important` is ranked, never left in the value, so
> `red !important` beats a later `blue` and `IsOpaqueColor("oklch(1 0 0) !important")` is false. **One
> value on the real sheets moved, and it was the wrong one:** `--sidebar-width` `16rem` → `15rem`, because
> the `16rem` sits in `blazorblueprint.css`'s `@layer base` and the `15rem` is unlayered in
> `styles/theme.css`. No existing assertion was removed or loosened (two were tightened), and the reader
> now answers unknown for the 74 tokens it cannot place - 73 `--tw-*` declarations inside Tailwind's
> `@supports` fallback blocks plus `--bb-menu-accent` - none of which any fact ever asserted. No new
> category and no new exclusion - the sextuple filter is unchanged.
> Q-012 (the behaviours the F-020/F-021 review had proved with throwaway probes) added **9** facts on
> 2026-11-17, all in the fast suite: 2 for a Freestyle project's webhook gate (the settings form refuses
> the kind with `webhookError=project-not-pipeline`, and a delivery to a webhook row seeded on that kind is
> recorded `ignored:no-repository` with no run queued - the delivery half is defence in depth, unreachable
> through the product, so the fact seeds the row); 1 for the trigger's snapshot (a run still keeps the spec
> it froze through two later step edits, one while it is queued, one after it was claimed and accepted); 1
> for `Project.Kind` (an edit that posts a different kind leaves the stored kind - and the other kind's
> repository fields - untouched, in both directions); 2 for F-021's trigger surface (every parameter input
> pre-filled with its definition's default - the string's value, the multi-line text's own content, the
> boolean's checked radio and the choice's selected option - and an over-bound value refused at the trigger
> endpoint with no run queued, while the value exactly at the bound is accepted and snapshotted); and 3 for
> the script step's on-disk shape (`temp/step-N.sh|cmd` by 1-based position, UTF-8 **without a BOM** byte for
> byte, `sh -e` pinned by the behaviour only it produces - a failing line ends the script and the second one
> never runs - plus a source-level pin of the Windows `cmd /d /s /c` invocation, because no Windows host was
> in the loop when the fact was written; the card records that reason). No new category and no new
> exclusion - the sextuple filter is unchanged.
> Q-017 (the `Idempotency-Key` fingerprint and F-029's queue-cap replay) added **5** facts on
> 2026-11-17, all in the fast suite and all in `ApiV1RunApiTests`: 1 for the tag dimension (a resend
> differing only in a **refused** tag - over-long, or carrying a non-whitespace control character, the
> two kinds validation answers `tag-invalid` for - is a `409 idempotency-key-reused` instead of a
> replay, including the adversarial `lin\u0001ux` that a sanitizing fingerprint would collapse onto the
> first `linux`, while a merely re-spelled legal list - another order, padded, a repeated entry, or
> padding made of the whitespace controls tab/vertical-tab/form-feed that are trimmed and never refused -
> still replays `200`, and a fresh key sending each refused body still reads `400 tag-invalid`); 1 for the
> tag-set dimension (a legal second tag no worker covers is a `409` from the key, and the same body under
> a fresh key reads `400 tag-unknown` - which is what proves the key was decided first); 1 for the
> parameter dimension (one changed value is a `409`, and the same body under a fresh key is a `201`); 1
> that the fingerprint of a legal request is still the exact byte the shipped algorithm produced (two
> frozen `RequestHash` digests, so a key already in the table cannot start refusing its own retry); and 1
> that fills a `MaxQueuedRuns = 1` queue with the key's own queued run and requires a **new** key to
> answer `409 queue-full` (`fieldErrors[0].field = queue`, no replay marker, no binding written) and the
> **same** key to answer `200` with `Idempotency-Replayed`, the same run id and no new run - the
> queue-cap half of F-029's precedence, which until then rested on statement order alone. No existing
> fact was removed or loosened; the fingerprint now hashes the tags as sent
> (`RunValidation.ParseTagsAsSent`) instead of the list validation accepted. No new category and no new
> exclusion - the sextuple filter is unchanged.
> Q-018 (the schedule's lost-race path) added **1** `Category=Postgres` fact on 2026-11-17, in
> `Schedules/PostgresScheduleRaceTests.cs`: a second connection holds one of the window's **missed** due
> times uncommitted, the tick's insert blocks on `IX_ScheduleOccurrences_ScheduleId_DueAt`, the fact
> *observes* that wait in `pg_stat_activity` before committing the holder, and the tick takes the 23505
> from inside `DecideAsync` - the shape review M1 widened the guard for. It is in `PostgresCollection`, so
> it moves no fast count and no new category or exclusion: the fast suite stays 1284, Postgres 3 → 4 and
> all seven 1310 → 1311 (the same arithmetic below).
> B-006 (two inconsistencies the F-020/F-021 review left behind) added **3** facts on 2026-11-17, all in
> the fast suite: 2 for a `choice` parameter's repeated entry (`fast,fast` is refused at save with the
> stable `parameters-invalid` the parameters tab renders, the stored JSON stays byte for byte what it
> was, and the trigger form renders one `<option>` per declared value; the catalog fact also pins that
> only an exact repeat is refused - `fast,Fast` stays legal, because the value resolver matches values
> ordinally - and that a list breaking both the 32-entry bound and the repeat reports the bound, which
> fixes the order the two rules are asked in) and 1 for a Freestyle project's pipeline path (two rows
> written straight through the context with no `ProjectService.Validate` - one stating `pipeline.lua`,
> one leaving the field to the entity's own initializer - both produce a run whose pipeline path is empty
> and a run page that shows the empty marker, because the run snapshot now decides by kind). No existing
> fact was removed or loosened, and the entity initializer stayed: removing it would have left eight
> `new Project { }` harness sites without a path and turned five F-014 assertions red. No new category
> and no new exclusion - the sextuple filter is unchanged: fast 1284 → 1287 and all seven 1311 → 1314.
> F-022 (the inline Pipeline source) added **23** facts on 2026-11-17: 11 server-side facts in
> `Runs/InlinePipelineRunTests.cs` (the project row stores the script with null repository fields and the
> source is immutable in both directions; a repository project keeps `repository`; a run snapshots the
> script, keeps `pipeline.lua` as its path, and the claim reads the run row - not the edited project - and
> carries `pipelineScript` + `definitionSource` with no repository; an out-of-band `DefaultBranch` never
> reaches an inline run's snapshot; empty/over-bound scripts are refused; Validate runs a definition phase
> on a worker while the project is at its concurrency cap, creates no run, step or number; a broken script
> settles `invalid` with `pipeline.lua:(line,col)`; no worker settles `no-worker`; a repository project is
> refused; a foreign tenant's editor/validate/worker-api calls are 404s; and an inline run offers
> Rebuild/Replay while a rebuild carries the same script), 5 runner cases in
> `Pipelines/PipelineRunnerTests.cs` (the inline script runs its steps in `workspace/` with `repository/`
> empty and `temp/pipeline.lua` byte-identical; one Theory proves a syntax error is `pipeline-syntax` with
> the same **anchored** `zenkins: pipeline.lua:(line,col)` position for both sources; a definition carries
> its `triggers`, pipeline `env` and `secretReferences`; and `while true do end` is `pipeline-timeout`),
> 2 real-Worker facts in `WorkerRuntime/WorkerDefinitionValidationLoopTests.cs` (the actual
> `WorkerDefinitionValidationLoop` claims the script, describes it through the real runner, reports its own
> step list and deletes the `validations/{id}` scratch tree; a syntax error is reported with
> `pipeline.lua:(line,col)`), 2 webhook facts in `Webhooks/WebhookInlineGateTests.cs` (creation refused
> with `webhookError=project-not-pipeline`; a seeded row's real signed delivery is
> `ignored:no-repository` with no run), 2 Git facts in `Git/GitCredentialInlineGuardTests.cs` (the server
> refuses an assignment for inline **and** Freestyle, not only the hidden UI) and 1 real-Worker artifact
> fact in `WorkerRuntime/WorkerRunLoopTests.cs` (an inline step's `report.txt` is published from
> `workspace/`). It also added 1 `Category=Postgres` fact
> (`Concurrency/PostgresInlinePipelineMigrationTests.cs`) that **seeds a row on the pre-F-022 schema and
> then migrates**: the historical row reads `repository`, the check constraint admits `inline` and
> refuses a third word, and `BuildRuns.PipelineScriptText` exists. No new category and no new exclusion -
> the sextuple filter is unchanged: fast 1287 → 1310, Postgres 4 → 5 and all seven 1314 → 1338 (the same
> arithmetic below).
> F-023 (multibranch jobs) added **23** fast facts and **1** `Category=Postgres` fact on 2026-11-17,
> all in `tests/Zenkins.Tests/Branches`, `ProjectUi/ProjectUiBranchesTests.cs` and the tenancy harness:
> 6 pure-rule facts (the discovery-mode vocabulary with `single` as the default; the branch-identity
> bound of 200 - a probe may report 255 but a run's `GitBranch` column holds 200 - and the "never an
> empty name" rule; the pipeline-state and orphan-evidence vocabularies; and the two branch-filter
> rules side by side: F-017's three-argument `Matches`, whose empty filter still means the project's
> *current* default branch, versus F-023's discovery rule, whose empty filter means every branch);
> 11 host facts driven through the real webhook endpoint, the real worker API and the real services
> (a single-branch project still queues a branch the registry says has no pipeline, proving the new
> gate sits behind the mode switch, and writes nothing on the registry; a branch outside a
> single-branch webhook's filter is still `ignored:branch`; a new branch in `branches` mode is
> registered (its run is queued only after a definition-only observation reads its tree), with the
> pipeline state left `unknown`; a branch
> whose tree was read as no-pipeline is listed and never queued, even at a new head; an over-long
> branch is recorded `ignored:branch-too-long` instead of failing the insert, while the name exactly
> at the bound builds; the filter is the discovery rule and a branch it excludes is not registered; a
> push-delete orphans a branch and keeps its runs, and a later push revives it; two branches never
> share history and neither the runs filter nor the branch list crosses a project or tenant boundary;
> a run with no registry row is still listed, with its frozen definition as the has-pipeline proof;
> a frozen definition records has-pipeline and a `pipeline-missing` completion records no-pipeline at
> the commit it is evidence about, after which a delivery queues nothing; and a manual trigger picks
> its branch, defaulting to the project's default);
> 3 UI facts (the create form stores `branches` and the edit form switches it back, while a Freestyle
> project is refused `branch-discovery-unavailable`; the Branches tab renders per-branch status,
> no-pipeline and orphaned rows and answers a foreign id with a real 404; the runs page filters by
> branch and the filter cannot cross projects); and 3 tenancy facts (branch jobs are invisible across
> tenants, a cross-tenant write is refused, and `Find` after a tenant switch returns null). The
> `Category=Postgres` fact seeds a project and a numbered run on the pre-F-023 schema and then
> migrates: the existing row reads `BranchDiscovery = 'single'` **from the migration's own default**,
> the branch table is empty, `Main` and `main` coexist under the unique `(ProjectId, Branch)` key, the
> closed vocabularies and the orphan-evidence pairing are database rules, and deleting the project
> cascades only its own branch rows. No new category and no new exclusion - the sextuple filter is
> unchanged.
> F-023's **second round** added **7** more fast facts on 2026-11-17, once the definition-only
> observation landed: 6 in `Branches/BranchObservationTests.cs` (an `unreadable` read keeps the
> previous verdict, stays pending and moves the retry clock, and a later successful read queues the
> run; two deliveries for one commit share one observation - one claim, then 204; the definition
> credential route is bound to the task's lease and Worker, 200 for the holder and the same bodyless
> 404 for another Worker or an unknown id, and the material is `no-store`; the retry sweeper abandons
> a stale task, moves the clock and does not storm on the next tick; interval discovery registers new
> heads, asks for their reads, orphans only the branch a successful refs answer omits and queues no
> run itself; a failed probe registers nothing and orphans nothing) and 1 in
> `WorkerRuntime/WorkerDefinitionValidationLoopTests.cs` (the real definition loop against a real
> repository: the branch that deleted `pipeline.lua` is recorded no-pipeline with no run, the branch
> that has it is recorded has-pipeline and its run is queued pinned to the observed commit). The
> task family stayed F-022's (one claim/report/lease, extended with a repository-branch kind), so no
> existing fact moved. No new category and no new exclusion - the sextuple filter is unchanged: fast
> 1333 -> 1341 (the review's D2 fact added one more: the Branches tab states remote discovery from
> the project's own probe record), Postgres stays 6 and all seven 1362 -> 1370
> (12 + 4 + 2 + 2 + 3 + 6).
> **Earlier revisions of this
> note carried counts that
> were behind HEAD** - some cards' tests had never been counted (the Milestone suite by four, the
> fast suite by more than fifty) - so this revision drops those figures rather than repeating them
> and re-baselines as **fast 1341 (a counted run) and all seven 1370 (that count plus the other six
> suites' figures)** - Milestone 12, Recovery 4, PublishSmoke 2, PublishAot 2, GitSshd 3, Postgres 6,
> the last one skipping without `ZENKINS_TEST_POSTGRES`. The fast figure is a counted run of the tree
> that carries Q-018, B-006, F-022 and F-023 (1310 plus F-023's thirty-one fast facts); the all-seven
> total is that same arithmetic on those thirty-one facts and F-023's one `Category=Postgres` fact:
> 12 + 4 + 2 + 2 + 3 + 6 = 1370. Every command in the POSIX
> (macOS/Linux) form below was run in that order from a fresh `git clone`; the expected outputs
> quoted here are the ones those runs produced. PowerShell equivalents differ only in shell syntax
> (`$env:`, `Remove-Item`, `Tee-Object`) and are given wherever they differ.

## 1. Prerequisites

**.NET 10 SDK — that is the only prerequisite.**

| Needed | Not needed |
|---|---|
| .NET 10 SDK (`10.0.100` or newer) | Java / JVM |
| Git (to clone) | Docker or any container runtime |
| | Node.js / npm / Tailwind CLI |
| | PostgreSQL, MySQL or any database server (SQLite is the default) |

Not needed, and not merely optional: the UI ships pre-built CSS in the `BlazorBlueprint` NuGet
packages (`Directory.Packages.props`), so there is no Node/Tailwind build step, and the default
database is a single SQLite file created on first run.

One extra prerequisite exists for **one suite only**: `Category=PublishAot` (I-004) publishes the
Worker as NativeAOT, which needs the OS's native toolchain on the publishing machine — macOS: Xcode
Command Line Tools; Windows: the Visual Studio C++ workload; Linux: clang plus zlib development
headers. Nothing else in this guide needs it (`dotnet build` runs the AOT analyzers without any
native toolchain, and .NET does not cross-compile AOT for another OS — each OS publishes its own
RID). See [`docs/native-aot.md`](native-aot.md) for the decision and its evidence.

A second named prerequisite exists for **one suite only**: `Category=GitSshd` (Q-009) runs a real
OpenSSH server as a user process to verify the SSH clone path end to end, so it needs the machine's
OpenSSH tools — `sshd` and `ssh-keygen` (`ssh` for the recording shim), found on `PATH` or at the
standard locations (`/usr/sbin/sshd`, `/usr/bin/ssh-keygen`, `/usr/bin/ssh`). macOS and Linux
distributions ship them; Windows ships no `sshd` by default. Where the tools are absent the suite's
facts **skip with the reason recorded** (never a silent omission), and the fast suite never reaches
that suite at all (§2's fifth exclusion). The fixture generates every key it uses into its own
temporary directory and reads, writes and trusts nothing in the host's `~/.ssh` — the same synthetic
environment discipline as card Q-008.

### Which SDK version

- Verified on this machine: `10.0.403` (also `10.0.101` is installed; either works).
- Floor enforced by [`global.json`](../global.json): `10.0.100` with `rollForward: latestFeature`, so
  any 10.0.x SDK in feature band `1xx` or newer is accepted (a 9.x SDK is not).
- The NativeAOT compiler is **not hard-coded; it is derived from your SDK and guarded**. It cannot be a
  constant: `Microsoft.DotNet.ILCompiler` has to carry the same 10.0.x patch as the SDK's
  `Microsoft.NETCore.App.Runtime.*` pack, because ILC refuses to publish an AOT pack whose
  `System.Private.CoreLib.dll` is older than the runtime pack's. (`Directory.Build.targets` therefore
  takes `ZenkinsILCompilerVersion` from the SDK's own runtime band; an earlier constant pin of 10.0.14
  built fine on one machine and failed every CI runner on 10.0.15 — B-009.) Two build errors hold the
  rule instead of a note in a guide: **ZENKINS0002** when an override disagrees with the band this SDK
  reports (fails `dotnet build` and `dotnet restore`, before a publish ever runs), and **ZENKINS0003**
  when the band is below `ZenkinsILCompilerMinimumVersion` = **10.0.14**, the first 10.0 release
  outside GHSA-p3jp-9v83-76w9 — such a machine would compile the shipped worker with a compiler that
  has an open high-severity advisory, so install a current `10.0.4xx` SDK rather than disabling NuGet
  audit. `rollForward` stays wide because the compiler no longer comes from it.

```bash
dotnet --version     # 10.0.403
dotnet --list-sdks   # lists the installed SDKs and their feature bands
```

If `dotnet --version` fails, install the SDK from <https://dotnet.microsoft.com/download/dotnet/10.0>
(macOS: `brew install --cask dotnet-sdk`; Linux: the Microsoft package feed or `dotnet-install.sh`;
Windows: the installer or `winget install Microsoft.DotNet.SDK.10`) and open a new terminal.

## 2. Clone, restore, build, test

```bash
git clone https://github.com/your-org/zenkins.git zenkins   # replace with the real clone URL
cd zenkins

dotnet restore
dotnet build -c Release
dotnet test
```

Expected results, as observed:

| Command | Observed |
|---|---|
| `dotnet restore` | restores 4 projects (`Zenkins.Shared`, `Zenkins.Worker`, `Zenkins.Server`, `Zenkins.Tests`) |
| `dotnet build -c Release` | `Build succeeded.` / `0 Warning(s)` / `0 Error(s)` — warnings are errors (`Directory.Build.props`) |
| `dotnet test` | `Passed!` / `failed: 0` / `skipped: 0` — the total grows with every card; treat it as a snapshot of the revision you have, not a target. No test may fail and none may be skipped |

`dotnet test` builds the Debug configuration unless you pass `-c Release`; that is why its log says
`bin/Debug/net10.0/Zenkins.Tests.dll` even after a Release build. Tests use the Microsoft Testing
Platform runner configured in `global.json`; `dotnet test` is the supported entry point.

The first build downloads NuGet packages and takes noticeably longer than later ones.

**The current baseline is counted, and it is 1502 fast / 1534 all seven.** The fast figure was measured
with `--list-tests` on the documented filter rather than added up from per-card attribute counts; the
all-seven figure adds the other harnesses counted in the same pass - 23 across the five slow categories
and 9 in Postgres - so 1502 + 32 = 1534. Every per-card note in this file quotes the
counts of *its own* tree and they are history, not a target: F-034's "1341 -> 1366" and this file's own
earlier "1341 / 1370" re-baseline were both accurate when written and are both behind HEAD now. When a
card changes the count, re-baseline here in the same breath rather than leaving the next reader to
notice - that is what went wrong twice.

### Six suites: the fast one, and five acceptance harnesses

The test project has five deliberately slow categories, and all of them are acceptance harnesses
that prove the product end to end instead of proving its parts:

- **`Category=Milestone`** (Q-002) starts a real Server process, a real Worker process, a real Git
  repository (with a real `pipeline.lua`), a real webhook delivery and a real artifact download.
- **`Category=Recovery`** (Q-004) reuses the same launcher and adds restarts: the server is stopped and
  started again over the same database, workers are killed and restarted as processes, and the real
  lease sweeper and offline monitor run against compressed configuration (a one-second sweep interval,
  a five-second offline threshold against the production sixty - time compressed, never faked) to
  prove what a crash really does: a queued run survives the restart, a killed worker's run becomes
  `Lost` with the offline reason, the killed worker's late report is refused, and a restarted worker
  claims new work instead of resuming the abandoned one.
- **`Category=PublishSmoke`** (I-003) publishes self-contained artifacts for the local RID (Server
  as a folder, Worker as a folder plus a single-file variant) with the product's
  warnings-as-errors, runs the same real scenario against the published apphosts executed directly
  (no `dotnet exec`, no Development crutch), and proves the packaging claims themselves: the data
  lands beside the binaries, an upgrade cycle (stop → replace → start) preserves the database, the
  key ring and the artifacts, and the same binaries honour a second environment without rebuilding.
- **`Category=PublishAot`** (I-004) publishes the **NativeAOT** Worker (the csproj's
  `<PublishAot>` default) plus the JIT Server and the JIT Worker for the local RID, asserts the AOT
  artifact's packaging on the output itself (one native executable: no managed assembly, no
  runtimeconfig, no CoreCLR files), runs the full Q-002 scenario — including the cancellation path
  — against the published pair, and proves the MoonSharp dual-mode contract: the same
  `pipeline.lua` under the JIT and the AOT worker produces identical step outcomes, log lines and
  artifact bytes. It needs the native toolchain of §1 (macOS: Xcode CLT; Windows: VS C++; Linux:
  clang + zlib) and each OS publishes its own RID.
- **`Category=GitSshd`** (Q-009) starts a **real `sshd`** — as a user process, with the fixture's
  own generated host key, authorized key and `sshd_config` on loopback — and extends F-018's
  credential proof to the SSH transport: a real Server + Worker pair clones a private repository
  from the delivered SSH key and runs to `Success` (the sshd's own accepted-fingerprint log is the
  counter that proves nothing ambient authenticated), the no-credential / rejected-key /
  wrong-host-key refusals each surface with their named reason, and the ssh invocation the real run
  used is captured through a recording shim and matched word for word against the hardened
  `GIT_SSH_COMMAND` pins. It needs the OpenSSH tools of §1 and nothing on the host's `~/.ssh`;
  where the tools are absent the facts skip with the reason recorded.
- **`Category=Postgres`** (F-029, extended by F-036) runs facts a **real PostgreSQL** server is needed
  for, because SQLite cannot show them: every claim begins with a write (the worker-row no-op
  `UPDATE`), so the database's single writer serializes the statements and the project lock's
  necessity becomes unobservable. The suite needs a disposable database whose connection string is
  passed in `ZENKINS_TEST_POSTGRES` (its `public` schema is dropped and recreated before each fact);
  without the variable the facts **skip with that reason recorded** - the GitSshd discipline - so it
  costs a machine with no PostgreSQL nothing. What it carries: (a) with `MaxConcurrentRuns = 1`, two
  workers with tag-disjoint queues firing eight concurrent claims admit **exactly one** run per
  round, five rounds, read from the rows; (b) a **negative control** built from the product's own
  compare-and-set: count the active runs, then lease, twice - the sequence a check-then-act
  implementation would run - admits **two** on the same provider, so fact (a) is evidence about the
  lock rather than about a race that never happened. F-036 adds (c) the **build-number retry**: a second
  connection holds an uncommitted `#2` that the product's READ COMMITTED `MAX(Number)` read cannot see, so
  the product picks `#2` too and its INSERT blocks on the unique index; the fact commits the rival only
  after it observes that lock wait in `pg_stat_activity`, then requires the product to answer success with
  `#3` - which is only reachable through the savepoint rollback and re-read (deleting the retry turns the
  fact red, the mutation record is on the card). Q-016 adds (d) the **trigger-side queue cap**: six
  concurrent real trigger posts against `MaxQueuedRuns = 1` admit exactly one run per round, three
  rounds, with a count-then-insert control that leaves two queued runs in the same provider - and
  deleting the staging routine's project row lock turns it red in round 1 while every SQLite `queue-full`
  fact stays green (the mutation is recorded on F-029). Q-018 adds (e) the **schedule's lost race**: a second
  connection holds one of a schedule's *missed* due times uncommitted, the tick's own insert blocks on
  `IX_ScheduleOccurrences_ScheduleId_DueAt`, and the fact commits the holder only after it observes that
  wait in `pg_stat_activity` - so the 23505 needs no timed window. The refusal surfaces from inside
  `DecideAsync` (the missed backlog is flushed by the run's staging save), which is the path review M1
  widened the guard for; the fact then requires the due time that ran into it to be **queued** in the
  same tick (`Failed = 0`, `Raced = 1`, `Queued = 1`, its ledger row naming the run), and the pre-Q-018
  settle - watermark straight to `now`, no re-read - turns it red at `Queued = 0`. Postgres also has to
  be in the fast filter's exclusion list for exactly this reason: the retry is unreachable on SQLite,
  where every write serializes.

None of the harnesses is part of the fast loop, so the fast filter has to exclude **six**
categories. The runner accepts the `&`-joined exclusions - verified on 2026-11-14 (I-003's fix
round for the triple, I-004 for the quadruple), on 2026-11-15 (Q-009 for the quintuple) and on
2026-11-16 (F-029 for the sextuple): the sextuple exclusion runs green, and dropping
`Category!=GitSshd` alone pulls the sshd suite in (dropping `Category!=Postgres` pulls that suite
in, where it skips without `ZENKINS_TEST_POSTGRES`).

**One command for the whole gate: [`scripts/verify.sh`](../scripts/verify.sh)** (card P-001). The
script runs exactly the commands below, in this order - `dotnet build -c Release`, the fast suite
with its sextuple exclusion filter, then one filtered run per harness - and exits non-zero at the
first red suite, naming it in its output; nothing after a red suite runs. §2 stays the written gate
(the prerequisites, what each harness needs, the counts per suite) and the script is its executable
form: the filters below are the text the script runs, and the two change together. On Windows, run
the script under Git Bash (Git for Windows ships one) - that is also how the CI workflow
([`.github/workflows/ci.yml`](../.github/workflows/ci.yml)) runs it on every runner, so there is
deliberately no second PowerShell transcript of the gate to drift against.

```bash
dotnet build -c Release                                                                                                        # the harnesses run the built binaries
dotnet test -c Release --filter "Category!=Milestone&Category!=Recovery&Category!=PublishSmoke&Category!=PublishAot&Category!=GitSshd&Category!=Postgres"   # the fast suite
dotnet test -c Release --filter "Category=Milestone"                                                                           # the milestone acceptance harness
dotnet test -c Release --filter "Category=Recovery"                                                                            # the recovery acceptance harness
dotnet test -c Release --filter "Category=PublishSmoke"                                                                        # the publish-smoke acceptance harness
dotnet test -c Release --filter "Category=PublishAot"                                                                          # the AOT acceptance harness (needs the native toolchain)
dotnet test -c Release --filter "Category=GitSshd"                                                                             # the real-sshd acceptance harness (needs the OpenSSH tools; skips with a recorded reason without them)
dotnet test -c Release --filter "Category=Postgres"                                                                            # the real-PostgreSQL concurrency fact (needs ZENKINS_TEST_POSTGRES; skips with a recorded reason without it)
```

| Suite | Filter | Needs | Typical duration |
|---|---|---|---|
| Fast | `Category!=Milestone&Category!=Recovery&Category!=PublishSmoke&Category!=PublishAot&Category!=GitSshd&Category!=Postgres` | `dotnet build -c Release` | ~6 min on the reference machine, ~10-11 min on a slow one (1502 tests, snapshot of the revision) |
| Milestone | `Category=Milestone` | `dotnet build -c Release`, `git` on `PATH`, loopback only | ~35 s (12 tests) |
| Recovery | `Category=Recovery` | the same as Milestone: `dotnet build -c Release`, `git` on `PATH`, loopback only, no network | ~30 s (4 tests) |
| PublishSmoke | `Category=PublishSmoke` | the same as Milestone, plus the SDK (it publishes three self-contained artifacts for the local RID) | ~40 s warm (2 tests); a cold NuGet cache pays the runtime-pack downloads first |
| PublishAot | `Category=PublishAot` | the same as PublishSmoke, plus the OS's native AOT toolchain (§1; it publishes the AOT Worker, the JIT Server and the JIT Worker for the local RID) | ~1 min warm (2 tests); the AOT compile is minutes on a cold `obj/` tree |
| GitSshd | `Category=GitSshd` | the same as Milestone, plus the OpenSSH tools of §1 (`sshd` + `ssh-keygen`; without them the facts skip with the recorded reason) | ~20 s (3 tests) |
| Postgres | `Category=Postgres` | the same as Milestone, plus a disposable PostgreSQL server named by `ZENKINS_TEST_POSTGRES` (its `public` schema is dropped and recreated); without the variable the facts skip with the recorded reason | ~20 s warm, plus the migration runs on a fresh schema (9 tests) |

**The acceptance gate is the per-suite runs**: the fast suite green plus each harness green in its
own filtered run - `scripts/verify.sh` runs them all in one command (P-001). `dotnet test` with no
filter runs **all seven** (1534 on the tree that carries F-041, by the arithmetic below) but
is *not* the gate: it executes everything in one process, and the publish smokes' self-contained
and AOT publishes load the machine while the process-sensitive harnesses run - that combined
run is the flakiest way to execute the suite, so run the suites by their filters. The harnesses only
ever launch the Release-build output of
`Zenkins.Server.dll` and `Zenkins.Worker.dll` from the `src` project trees, and fail fast with a
message naming `dotnet build -c Release` if those binaries are absent (the same rule the F-005/F-007
process E2Es follow). A self-contained publish under the same `bin` tree does not count as a
harness binary: its runtimeconfig names no framework, so `dotnet exec` cannot run it, and the
harnesses deliberately select only framework-dependent binaries.

What the harnesses need, and what they do not:

- **A Release build first.** They run the real binaries; they never compile on the fly.
- **`git`** on `PATH` (the fixture builds a real bare repository) and the **dotnet host** next to the
  running runtime. **No network**: the fixture repository is a local `file://` bare clone, the Server
  listens on a port the OS picks (`http://127.0.0.1:0`), and the webhook is delivered to that loopback
  address by the test itself.
- **Nothing in the repository.** Each test gets its own temporary root - SQLite database, Data
  Protection key ring, artifact store, worker data directory and work root - and the harness deletes it
  on dispose, after killing the Server and Worker process trees. The recovery harness kills workers
  mid-test as well, and every killed process stays tracked so dispose can never leak one.
- **One worker per host (milestone).** The no-worker case starts with none and starts it later on
  purpose. The recovery harness deliberately holds several workers over its lifetime and restarts
  them - with the same name and the same data directory, so the restarted process loads the persisted
  token, which is the product's real reconnect path.

The harnesses assert only observable state (database rows, HTTP responses, files on disk, process
ids and exit codes); they do not read worker-internal objects, so they cannot pass because of a
mocked seam. (One deliberate file read belongs to that set: the late-report test reads the worker
token file the worker itself persisted under its own data directory, because the replayed report must
present exactly the credential the dead worker would have presented.)

## 3. First run

```bash
dotnet run --project src/Server
```

Expected log, as observed:

```text
info: Microsoft.EntityFrameworkCore.Migrations[20402]
      Applying migration '20261112051428_Initial'.
...
info: Microsoft.Hosting.Lifetime[14]
      Now listening on: http://localhost:5067
info: Microsoft.Hosting.Lifetime[0]
      Hosting environment: Development
info: Microsoft.Hosting.Lifetime[0]
      Content root path: /path/to/zenkins/src/Server
```

Open <http://localhost:5067>. `dotnet run` picks up `src/Server/Properties/launchSettings.json`,
whose `http` profile sets the port *and* `ASPNETCORE_ENVIRONMENT=Development`. Because the launch
profile wins, you do **not** need to set the environment variable for this command — and setting
`ASPNETCORE_ENVIRONMENT` or `ASPNETCORE_URLS` in the shell has no effect while the profile is used
(see §6).

The profile also sets `launchBrowser: true`, which **IDE launch configurations** (Visual Studio, Rider,
VS Code) honour by opening the page for you. A plain `dotnet run` in a terminal opens nothing — use
the URL printed in the log.

### Where the database lives

Two locations, and which one applies depends on how the app starts (card I-003's path discipline:
relative persistent paths resolve from `AppContext.BaseDirectory` — the directory the binaries run
from — never from the current working directory):

- **`dotnet run` (development):** `src/Server/bin/Debug/net10.0/App_Data/zenkins.db` — under the
  build output, because that is where the binaries the runtime executes live. While the app runs
  you will also see `zenkins.db-wal` and `zenkins.db-shm`.
- **`dotnet ef` (design time):** `src/Server/App_Data/zenkins.db` — the design-time factory
  deliberately targets the source tree, so EF tooling finds the repository's database. That fork is
  deliberate; point `ConnectionStrings:Default` at the database you actually mean when the two must
  agree.

Everything `App_Data` is git-ignored wherever it lands (`**/App_Data/`); nothing database-related
is ever committed. A published run (§6) keeps its data in `App_Data/` under the **publish
directory**, regardless of the directory it was started from.

### What Development changes

Two things, both of which matter locally:

1. **Startup migration.** `Database:ApplyMigrationsOnStartup` defaults to
   `app.Environment.IsDevelopment()`, so a Development run applies pending EF Core migrations before
   the first request and you get a ready database. Production does not do this (see §4).
2. **Static assets from source.** In Development, `dotnet run` serves `_framework/*` and
   `_content/*` assets from the build output (HTTP 200). In Production, a run *from source* returns
   **500** for those two prefixes — the static web asset manifest is a publish artifact. Use
   Development for local work, or run the published output (§6).

### First-run registration

1. `GET http://localhost:5067/` redirects to `/account/login` (302, `ReturnUrl=%2F`).
2. Go to **Register** (`/account/register`). Because the deployment has no users yet, the page says
   the first account becomes the **Owner** of the starter organization.
3. Register with any email and a password of at least 12 characters containing upper and lower case
   letters, a digit and a symbol (Identity policy in `src/Server/Auth/IdentitySetup.cs`).
4. The account is signed in automatically and lands on the dashboard, which shows a
   `Starter Organization` badge and an empty *Build activity* card.

What registration writes, in **one** transaction: the `User` row (ASP.NET Identity), the
`Organization` row named `Starter Organization`, an `OrganizationMember` row with role `Owner`, and
the single-row bootstrap marker that makes the first-run state permanent. Later sign-ups get an
account **without** an organization; they are redirected to `/no-organization` and stay there until
an Owner adds them as a member (Organizations → Members).

### Sign out

`/account/logout` is a POST form (deliberate: a GET sign-out is CSRF-able). It clears the
`Zenkins.Auth` cookie. The cookie is `HttpOnly`, `SameSite=Lax`, 8-hour sliding expiry, and `Secure`
whenever the request is HTTPS.

### Reset the local database

Stop the app (Ctrl+C), then delete the data directory; the next run recreates and migrates it. For
`dotnet run` that directory is under the build output (see *Where the database lives* above);
`src/Server/App_Data` only holds what `dotnet ef` (or a pre-I-003 dev run) put there — delete it
too when it exists:

```bash
rm -rf src/Server/bin/Debug/net10.0/App_Data src/Server/App_Data
dotnet run --project src/Server
```

```powershell
Remove-Item -Recurse -Force src/Server/bin/Debug/net10.0/App_Data, src/Server/App_Data
dotnet run --project src/Server
```

The register page says "first account becomes the Owner" again afterwards. This is the standard
recovery for a dev database you no longer trust — see §6. A *published* run keeps its database in
`App_Data/` under the **publish directory** (§6), not in the repository: delete that directory's
`App_Data` instead.

### Headless smoke test (optional, macOS/Linux)

The browser path above is the normal one. To check the same flow from a terminal:

```bash
curl -s -c cookies.txt http://localhost:5067/account/register -o register.html
TOKEN=$(grep -o 'name="__RequestVerificationToken" value="[^"]*"' register.html | head -1 | sed 's/.*value="//; s/"$//')

curl -si -b cookies.txt -c cookies.txt -X POST http://localhost:5067/account/register \
  --data-urlencode "_handler=register" \
  --data-urlencode "__RequestVerificationToken=$TOKEN" \
  --data-urlencode "Input.Email=owner@example.com" \
  --data-urlencode "Input.Password=CorrectHorse9!" \
  --data-urlencode "Input.ConfirmPassword=CorrectHorse9!" | head -1   # HTTP/1.1 302 Found

curl -s -b cookies.txt http://localhost:5067/ | grep -o 'Starter Organization' | head -1

rm -f cookies.txt register.html   # scratch files from this smoke test
```

The POST answers `302` with `Location: http://localhost:5067/` and sets the `Zenkins.Auth` cookie; the
last command prints `Starter Organization`. Antiforgery is required: the token comes from the
rendered form (`__RequestVerificationToken`) together with the antiforgery cookie in `cookies.txt`.
A second registration with a different email redirects to `/no-organization` instead.

## 4. Configuration

Configuration is ordinary ASP.NET Core configuration: `appsettings.json`, then
`appsettings.{Environment}.json`, then user secrets, then environment variables. Environment variables
use double underscores for nesting (`Database__Provider`, `ConnectionStrings__Default`). Note that
`appsettings.Development.json` is git-ignored, so put machine-specific local overrides in environment
variables or user secrets, never in a committed file.

### Database provider

[`src/Server/appsettings.json`](../src/Server/appsettings.json):

```json
{
  "Database": {
    "Provider": "Sqlite"
  }
}
```

| Key | Values | Default |
|---|---|---|
| `Database:Provider` | `Sqlite`, `Postgres` | `Sqlite` |
| `ConnectionStrings:Default` | provider-specific connection string | `Data Source=<AppContext.BaseDirectory>/App_Data/zenkins.db` — the directory the binaries run from, never the working directory (I-003 path discipline) |
| `Database:ApplyMigrationsOnStartup` | `true`, `false` | `true` in Development, `false` otherwise |
| `DataProtection:KeysDirectory` | directory path, absolute or relative to `AppContext.BaseDirectory` | `keys/` next to the SQLite database file (`App_Data/keys` beside the binaries for the default deployment); `App_Data/keys` under `AppContext.BaseDirectory` for PostgreSQL |

The Data Protection key ring encrypts stored secrets and must be backed up with the database; see
[Secrets key ring](#secrets-key-ring).

A missing or blank connection string is only allowed for SQLite (it falls back to the file above);
`Postgres` without `ConnectionStrings:Default` fails at startup with an explicit error.

### Switching to PostgreSQL

Start a PostgreSQL server you can reach, create a database and a user (for example
`createdb zenkins` and a `zenkins` role), then point the app at it and apply the migrations:

```bash
export Database__Provider=Postgres
export ConnectionStrings__Default="Host=localhost;Port=5432;Database=zenkins;Username=zenkins;Password=zenkins"
dotnet ef database update --project src/Server
ASPNETCORE_ENVIRONMENT=Development dotnet run --project src/Server --no-launch-profile
```

```powershell
$env:Database__Provider = 'Postgres'
$env:ConnectionStrings__Default = 'Host=localhost;Port=5432;Database=zenkins;Username=zenkins;Password=zenkins'
dotnet ef database update --project src/Server
$env:ASPNETCORE_ENVIRONMENT = 'Development'
dotnet run --project src/Server --no-launch-profile
```

`dotnet ef` comes from the `dotnet-ef` global tool, pinned to the EF Core version in
`Directory.Packages.props`:

```bash
dotnet tool install --global dotnet-ef --version 10.0.15   # only if `dotnet ef --version` fails
```

`--no-launch-profile` is what lets the exported variables take effect, but it also drops the profile's
`ASPNETCORE_ENVIRONMENT=Development` — and without an environment variable the host runs in
**Production**, where `_framework`/`_content` assets return 500 from a source run (§6). That is why
the run command sets `ASPNETCORE_ENVIRONMENT=Development` explicitly. Use the profile
(`dotnet run --project src/Server`) if you only want the local SQLite development setup.

> **Status of this section (historical — see the superseding note below):** on the machine where this
> guide was verified there is no PostgreSQL server and no Docker, so the switch could not be completed
> end to end. The provider selection, connection-string binding and migration path were exercised up to
> the connection attempt: `dotnet ef database update --project src/Server` under
> `Database__Provider=Postgres` stops with `Failed to connect to 127.0.0.1:5432`. The first command in
> a real environment that proves the switch is that same `dotnet ef database update` exiting 0,
> followed by a successful first-run registration against PostgreSQL. Installing a database server or
> Docker was deliberately avoided (PRD §2.2 and §8.9: no Docker as an infrastructure dependency). The
> server-side setup commands (`createdb`, role creation) were not run either.

> **Status of this section, superseded on 2026-11-14 (Q-008):** the switch above is now verified on a
> **real PostgreSQL 16.15**, not only up to the connection attempt. A published Server configured
> through environment variables alone applied all **16** migrations from an empty database at startup,
> served login and the first-run Owner bootstrap, ran a real Worker pipeline to `Success` (the run,
> step and log rows verified through `psql`; the `Artifacts` row read there too, with the downloaded
> bytes compared by `sha256` against the row's manifest), and migrated a database created at the
> previous migration forward **on PostgreSQL**: the new nullable attribution column appeared while
> pre-upgrade rows kept their old values with a null attribution. Authentication was real
> `scram-sha-256` over TCP with a password, not `trust`. Not exercised by that run: the `win-x64`
> published artifact (no Windows host), Docker (not installed on the machine), an upgrade **across
> product versions**, and the `local` / `replication` lines of `pg_hba.conf`, which stayed `trust`.

### Production database path

Startup migration is Development-only. For anything else, run the migration once per deployment and
leave `Database:ApplyMigrationsOnStartup` unset (or `false`):

```bash
Database__Provider=Postgres ConnectionStrings__Default="Host=localhost;Port=5432;Database=zenkins;Username=zenkins;Password=zenkins" \
  dotnet ef database update --project src/Server
```

```powershell
$env:Database__Provider = 'Postgres'
$env:ConnectionStrings__Default = 'Host=localhost;Port=5432;Database=zenkins;Username=zenkins;Password=zenkins'
dotnet ef database update --project src/Server
```

The same two variables (or `ConnectionStrings__Default` alone for SQLite) are what the deployed
process needs; nothing else about the production run differs from §6's published-run example.

### Artifact store

Run artifacts (PRD §3.7, card F-016) live in a local directory, not the database. Six keys, all in
[`src/Server/appsettings.json`](../src/Server/appsettings.json):

| Key | Default | Meaning |
|---|---|---|
| `Artifacts:RootPath` | `App_Data/artifacts` | Where the bytes live, relative to `AppContext.BaseDirectory` — the directory the binaries run from (an absolute path wins). Layout: `{root}/{organizationId}/{runId}/{artifactId}/{relativePath}` - the database row stores only the id. **Back this up with `App_Data/zenkins.db`**: the file names embed the ids a restore needs, and the store is created and written at startup, so an unwritable root fails the process immediately (like the Data Protection key ring). |
| `Artifacts:MaxFileBytes` | `536870912` (512 MB) | Largest single file. An upload over it fails the step and names the key. |
| `Artifacts:MaxArtifactBytes` | `2147483648` (2 GB) | Largest total of one artifact. |
| `Artifacts:MaxFiles` | `20000` | Most files one artifact may hold. |
| `Artifacts:SweepIntervalSeconds` | `60` | How often the sweeper closes abandoned uploads and removes the bytes of deleted runs. |
| `Artifacts:PendingTimeoutMinutes` | `30` | How long an artifact may stay `Pending` with no write before the sweeper marks it `Incomplete`. Measured from the last write, so a legitimately slow large upload is not touched. |

The caps are enforced by the server on every request (the worker also pre-checks with the same keys to
fail early; a worker's console app reads them as `Artifacts__MaxFileBytes`). There is **no retention
policy** in v1: an artifact lives until its run is deleted.

### Scheduled triggers (the sweeper's knobs)

Cron schedules (PRD §3.13, card F-024) are evaluated by a background sweeper. Five keys tune it, all in
[`src/Server/appsettings.json`](../src/Server/appsettings.json); the policy they express is PRD §3.5's
overlap/miss rule, and the defaults are that policy verbatim:

| Key | Default | Meaning |
|---|---|---|
| `Schedules:TickSeconds` | `30` | How often every schedule is evaluated. A due time is queued on the first tick at or after it, so this is also the normal lateness of a run. Keep it below `MaxDueLatenessSeconds`. |
| `Schedules:MaxDueLatenessSeconds` | `300` | How late a due time may still be queued. Later than this - or an evaluation whose own watermark is that old, i.e. nothing was evaluating the schedule - records every due time in the window as `missed` instead. This is the "no backfill after downtime" bound. |
| `Schedules:MissWindowHours` | `24` | How far back one evaluation enumerates missed due times. Older than this is summarised ("earlier misses are not listed") rather than enumerated, so a long outage costs one bounded pass. |
| `Schedules:MaxRecordedMisses` | `20` | The most individual `missed` rows one evaluation writes. The remainder is folded into the oldest kept row's `CollapsedDueTimes`, so the total the page states stays exact. |
| `Schedules:RetainedOutcomes` | `20` | How many due-time rows one schedule keeps. Older rows are deleted by the same tick that writes new ones. |

### Worker Git behaviour (shallow clones and pinned commits)

The worker clones each run's repository itself, and it also answers Git remote probes (card I-005: one
`git ls-remote`, no clone). Both use the same git plumbing, so the same variables configure them - see
[`WorkerConfiguration`](../src/Worker/WorkerConfiguration.cs) for the accepted ranges.

| Variable | Default | Meaning |
|---|---|---|
| `ZENKINS_GIT_DEPTH` | `0` (full clone) | Passed as `--depth` when positive. A speed knob for large repositories; git ignores it on a plain local path. |
| `ZENKINS_GIT_KNOWN_HOSTS` | unset (accept-new per run) | Pins SSH host keys. Never the operator's `~/.ssh/known_hosts`. |
| `ZENKINS_GIT_TIMEOUT_SECONDS` | 300 | Budget of one git invocation, further capped by what is left of the run's deadline. |
| `ZENKINS_GIT_EXECUTABLE` | `git` | argv[0] of every git invocation (a pinned binary instead of the one on `PATH`). |
| `ZENKINS_PROBES_ENABLED` | `true` | Whether this worker answers Git remote probes. Off registers `canProbe: false`, and the Server then never hands it a probe; a pre-I-005 worker binary omits the flag and is read the same way. |
| `ZENKINS_PROBE_CONCURRENCY` | `1` | Probes run at once, independent of `ZENKINS_WORKER_CONCURRENCY`: a probe is a light remote read and must not wait for a build slot. |
| `ZENKINS_PROBE_TIMEOUT_SECONDS` | `30` | Budget of one probe's `git ls-remote`; expiry kills the process tree and reports `git-timeout`. |

**Shallow clones and webhook runs pull in opposite directions.** A manual run clones its branch and
builds the tip. A webhook run (card F-017) is pinned to the commit its push named, so after the clone
the worker runs `git fetch origin` and `git checkout --detach <that commit>`, then verifies `HEAD`
really is that object - the branch may have moved between the push and the clone, and a run must build
the tree its own push named. With the default full clone the commit is always in history, so the pin
always succeeds. With `ZENKINS_GIT_DEPTH > 0` the named commit can fall **outside** the shallow window
(the branch moved several commits after the push); the checkout then fails and the run ends with a
`git-*` failure. That is the honest outcome - it never silently builds the branch tip - but if you
enable a shallow depth, size it to cover the push-to-clone gap, or keep webhook-triggered projects on
`0`.

### Git remote probes (the branch-discovery primitive)

A probe answers "what heads does this repository have right now?" without cloning, and it is the
primitive F-023/F-025 build on. It rides the existing pull channel: the Server queues a probe, a Worker
claims it, runs `git ls-remote` with the project's assigned credential, and reports the refs or a typed
failure. The Server never runs git and never dials a Worker. Five keys tune it, all in
[`src/Server/appsettings.json`](../src/Server/appsettings.json) (defaults also in
`ProbeOptions.Defaults`):

| Key | Default | Meaning |
|---|---|---|
| `Probes:WaitSeconds` | `60` | How long one requesting caller waits for a claimant before the probe is settled as `no-worker`. This is the bounded wait: "nobody is there" is an answer with a timestamp. |
| `Probes:PollMilliseconds` | `250` | How often the waiting caller re-reads the probe's state. |
| `Probes:LeaseSeconds` | `120` | How long one Worker's lease lasts. A worker that dies before reporting loses nothing - the probe is re-offered after this window, and the caller waits at most one lease past `WaitSeconds`. |
| `Probes:DedupeSeconds` | `15` | How long a completed probe stays joinable, so every caller inside the interval shares one probe (and one credential use) instead of starting its own. |
| `Probes:RetentionSeconds` | `600` | How long a settled probe is remembered by the in-memory queue. |

The Worker's own `ZENKINS_PROBE_TIMEOUT_SECONDS` (30s by default) is the probe's git budget; it sits
well inside `Probes:WaitSeconds`, so a hung remote is a typed `git-timeout` the caller receives rather
than a wait that expires first.

The queue and its leases live **in server memory** on purpose: the product is single-Server, and a
probe is a short-lived question whose answer nobody replays - if the process restarts, the caller is
gone too and F-023/F-025 ask again. What survives a restart is the outcome's visibility: the last
probe result is persisted on the project (`Projects.LastProbeOutcome`/`LastProbeAt`/`LastProbeMessage`)
and stated on its overview. A probe is **not a run**: it takes no build slot, never appears in the queue,
the dashboard counts or the run history, and its credential is delivered only to the Worker holding that
probe's lease - a probe id cannot fetch a run's material and a run id cannot fetch a probe's.

### Reverse proxy (TLS termination)

`X-Forwarded-Proto` is what makes `Request.IsHttps` true behind a proxy, and therefore what makes the
auth cookie `Secure` (`CookieSecurePolicy.SameAsRequest`). Loopback proxies are trusted by default;
a proxy on another host must be listed, otherwise its headers are ignored:

```json
{
  "ForwardedHeaders": {
    "KnownProxies": [ "203.0.113.7" ],
    "KnownNetworks": [ "10.0.0.0/8" ]
  }
}
```

Both keys are bound in `src/Server/Program.cs` and covered by
`tests/Zenkins.Tests/Auth/HostConfigurationTests.cs`. A remote client cannot make the app trust its
own `X-Forwarded-Proto`.

## 5. Repository layout and where work belongs

```text
global.json                  SDK floor (10.0.100, latestFeature)
Directory.Build.props        shared build settings (net10.0, nullable, warnings as errors)
Directory.Packages.props     central package versions — add packages here only
Zenkins.slnx                 solution (slnx format)
src/Server/                  ASP.NET Core host, Blazor Server UI, Identity, EF Core
  Auth/                      Identity setup, registration, first-run bootstrap, tenant resolution
  Components/                Razor components: layout, account, organizations, projects pages
  Data/                      DbContext, entities, tenancy, migrations
  Organizations/             membership management + POST endpoints
  Projects/                  project service, validation and CRUD endpoints (POST forms)
  Secrets/                   encrypted secrets: protector, Owner-only service/endpoints, key-ring constants
  Properties/                launchSettings.json — local port and Development environment
  wwwroot/                   project CSS and the two small JS files (theme, antiforgery bridge)
src/Shared/                  code shared by Server and Worker (must not reference ASP.NET Core)
src/Worker/                  console worker (placeholder in v0.1)
tests/Zenkins.Tests/         xUnit v3 tests (Microsoft Testing Platform runner)
docs/                        PRD, development guide, roadmap, kanban
docs/kanban/board.md         the board: two columns, Todo and Done
docs/kanban/issues/          one card per change, named <ID>-<slug>.md
docs/kanban/templates/       the card template
```

Where new work belongs:

- **A change starts as a card** in `docs/kanban/issues/`, copied from
  `docs/kanban/templates/issue-template.md`, and is added to the Todo table in
  `docs/kanban/board.md`. One card per change.
- Workflow: a developer implements the card and sets `status: review` (the card stays in Todo); an
  independent reviewer checks it against its acceptance criteria; only a PASS moves it to Done with
  `status: done`. A FAIL goes back to the same developer.
- New projects go into `src/` and `Zenkins.slnx`; new packages go into `Directory.Packages.props`
  (central package management, versionless `PackageReference`s); build-wide settings go into
  `Directory.Build.props`. `src/Shared` must not reference ASP.NET Core — `Directory.Build.targets`
  fails the build if it tries.

### The migration contract

There is **one** migration set, and it is written for PostgreSQL: the generated files contain
`uuid`, `timestamp with time zone` and `character varying(450)` — exactly those strings, so
`grep -r "character varying" src/Server/Data/Migrations` finds them and a SQLite-generated set would
not. SQLite is dynamically typed, so the same files apply there unchanged, which keeps a single
history for both providers. Consequences:

- **Generate migrations with the PostgreSQL provider**, never with SQLite:

  ```bash
  Database__Provider=Postgres ConnectionStrings__Default="Host=localhost;Port=5432;Database=zenkins;Username=zenkins;Password=zenkins" \
    dotnet ef migrations add AddProjectTag --project src/Server
  ```

  ```powershell
  $env:Database__Provider = 'Postgres'
  $env:ConnectionStrings__Default = 'Host=localhost;Port=5432;Database=zenkins;Username=zenkins;Password=zenkins'
  dotnet ef migrations add AddProjectTag --project src/Server
  ```

  Replace `AddProjectTag` with your migration name (a bare name; `<MigrationName>` is a placeholder
  that the shell would try to read as input redirection).

  (Generating a migration needs no running database; only `database update` does.) Generating with
  SQLite rewrites `ZenkinsDbContextModelSnapshot.cs` with SQLite type names and silently breaks the
  contract.
- **Check the model before committing** — also with the PostgreSQL provider:

  ```bash
  Database__Provider=Postgres ConnectionStrings__Default="Host=localhost;Port=5432;Database=zenkins;Username=zenkins;Password=zenkins" \
    dotnet ef migrations has-pending-model-changes --project src/Server
  ```

  ```powershell
  $env:Database__Provider = 'Postgres'
  $env:ConnectionStrings__Default = 'Host=localhost;Port=5432;Database=zenkins;Username=zenkins;Password=zenkins'
  dotnet ef migrations has-pending-model-changes --project src/Server
  ```

  It must print `No changes have been made to the model since the last migration.` With the SQLite
  provider the same command reports pending changes (store type names differ), which is why the
  `PendingModelChangesWarning` is suppressed for SQLite only.
- **Apply to SQLite for development**: either let the Development startup migration do it, or run
  `dotnet ef database update --project src/Server` with the default provider.
- If you need to undo a migration without a PostgreSQL server, delete the generated `<timestamp>_<Name>.cs`
  and `...Designer.cs` files and run `git checkout -- src/Server/Data/Migrations/ZenkinsDbContextModelSnapshot.cs`.
  `dotnet ef migrations remove` is not a substitute here: it needs a reachable database, and under the
  SQLite provider it rewrites the snapshot with SQLite type names (verified).

## 6. Troubleshooting

### Port 5067 is already in use

Observed when a second instance starts:

```text
System.IO.IOException: Failed to bind to address http://127.0.0.1:5067: address already in use.
```

Stop the other instance, or choose another port. `--urls` is passed through to the app and overrides
the launch profile:

```bash
dotnet run --project src/Server --urls http://localhost:5098
```

`ASPNETCORE_URLS` only wins when the launch profile is skipped — otherwise `applicationUrl` from
`launchSettings.json` overrides it (verified: without `--no-launch-profile` the app still listened on
5067, with it on the requested port):

```bash
ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS=http://localhost:5097 dotnet run --project src/Server --no-launch-profile
```

On Windows: set `$env:ASPNETCORE_ENVIRONMENT='Development'` and `$env:ASPNETCORE_URLS='http://localhost:5097'`
before the same command. Without the environment variable the host would run in Production here too,
with the asset 500s described below.

### Reset the local database

See §3. Use it when the schema or data is suspect; there is no local data worth keeping.

### `_framework` / `_content` return 500 in a local Production run

Symptom: `/` renders unstyled and interactive parts are dead while `/_framework/blazor.web.js` and
`/_content/BlazorBlueprint.Components/blazorblueprint.css` return 500. Observed log entry:

```text
System.IO.FileNotFoundException: Could not find file '.../src/Server/wwwroot/_framework/blazor.web.js'.
```

Cause: running from source in Production, where the static web asset manifest is not available.
Fix: use Development for local work, or publish and run the output, which serves both prefixes with
200:

```bash
dotnet publish src/Server -c Release -o ./artifacts/publish
cd artifacts/publish
ASPNETCORE_ENVIRONMENT=Production ASPNETCORE_URLS=http://localhost:5095 dotnet Zenkins.Server.dll
cd ../..   # back to the repository root
```

```powershell
dotnet publish src/Server -c Release -o .\artifacts\publish
Set-Location .\artifacts\publish
$env:ASPNETCORE_ENVIRONMENT = 'Production'
$env:ASPNETCORE_URLS = 'http://localhost:5095'
dotnet .\Zenkins.Server.dll
Set-Location ..\..   # back to the repository root
```

`artifacts/` is git-ignored, so the publish output cannot accidentally be committed. Since I-003
the **published app keeps its data beside its own binaries**: the SQLite file, the Data Protection
key ring and the artifact store are always `<publish dir>/App_Data/*` — they resolve from
`AppContext.BaseDirectory`, whichever directory you start the app from, so `cd` no longer matters
for the data. What still follows the working directory is **appsettings.json discovery** (the
content root): start the published app from its own directory, or configure everything through
environment variables — the service templates in `deploy/` do exactly that (the launchd plist and
the systemd unit pin `WorkingDirectory`; the Windows service sets a per-service environment, and
its header documents why appsettings.json is unreliable there).

The supported way to produce publish output is the I-003 matrix, `scripts/publish.sh`
(win-x64 / linux-x64 / osx-arm64; Server as a folder, Worker as a folder plus a single-file
variant), with install/uninstall packaging per OS under `deploy/`. The operator-facing side of that
packaging — install as a service, configure, upgrade with data intact, uninstall, and which of those
steps were actually executed on each OS — is [`install.md`](install.md).

Production does not migrate on startup, so that database starts empty — and
`dotnet ef database update --project src/Server` on its own does **not** fill it: the design-time
content root is `src/Server`, so the migrations land in `src/Server/App_Data/zenkins.db` instead and
the published app fails on `/account/register` with `SQLite Error 1: 'no such table: BootstrapMarkers'`
(verified). Point `ConnectionStrings:Default` at the publish database:

```bash
ConnectionStrings__Default="Data Source=$(pwd)/artifacts/publish/App_Data/zenkins.db" \
  dotnet ef database update --project src/Server
```

```powershell
$env:ConnectionStrings__Default = "Data Source=$($PWD.Path)\artifacts\publish\App_Data\zenkins.db"
dotnet ef database update --project src/Server
```

Run that from the repository root, before starting the published app. Alternatively let the published
process migrate its own content root with `Database__ApplyMigrationsOnStartup=true` (both were
verified: every migration applies, `/account/register` then answers 200).

### Sign-in bounces back to the sign-in page

`ReturnUrl` is sanitized: only site-relative paths are accepted, so `https://evil.example`,
`//evil.example` and `/\evil.example` all fall back to `/` (verified). A *valid but wrong* `ReturnUrl`
is echoed as-is — a hand-crafted `/account/login?returnUrl=%2Faccount%2Flogin` lands you back on the
sign-in form after a successful sign-in, which looks like a loop. Nothing is broken: navigate to
`http://localhost:5067/` and continue. If sign-in silently fails instead, check for a lockout (5 bad
passwords lock the account for 15 minutes) or for cookies being blocked or dropped (see §4 on the
`Secure` flag behind a proxy).

### `SQLite Error 19: FOREIGN KEY constraint failed` after upgrading an old dev database

Only affects a dev database created before the Identity migration landed (a schema with membership
rows that reference users which do not exist). Observed on such a database when the migration rebuilds
`OrganizationMembers` to add the user foreign key:

```text
Microsoft.Data.Sqlite.SqliteException (0x80004005): SQLite Error 19: 'FOREIGN KEY constraint failed'.
```

Recovery: delete the dev database and let it be recreated (§3) — verified, the subsequent
`dotnet ef database update` applies every migration and exits 0. Nothing in the repository needs to
change.

### Forms do nothing, or return 400, when JavaScript is blocked

The organization switcher, the Owner-only organization management forms, the project forms
(create, edit and delete — `Components/Shared/ProjectFormCard.razor`,
`Components/Pages/Projects/DeleteProject.razor`) and the secret forms
(`Components/Pages/Secrets.razor`) are plain HTML POSTs. They carry
`data-zenkins-antiforgery`, and `wwwroot/js/antiforgery.js` copies the request token rendered into
`<meta name="request-verification-token">` into a hidden `__RequestVerificationToken` field on submit.
Without that script the POST has no token and antiforgery rejects it with **400** (verified). If you
see this, enable JavaScript for `localhost:5067` — or use the pages that do carry the token in the
form itself (login, register, logout).

### Styles look wrong, or you are looking for the CSS build step

There is none. `src/Server/wwwroot/app.css` and `src/Server/wwwroot/styles/theme.css` are
hand-written and committed; all component and theme CSS comes from the `BlazorBlueprint.Components`
NuGet package as a static web asset (`_content/BlazorBlueprint.Components/blazorblueprint.css`). No
Node, npm or Tailwind CLI is involved, so there is nothing to regenerate after changing a `.razor`
file — the package's utility classes are `bb:`-prefixed and already compiled. A page with no styling
is almost always the Production-from-source asset problem above.

### Reading the app log

There is no log file: the app logs to standard output through `Microsoft.Extensions.Logging`
(`appsettings.json` sets `Default: Information`, `Microsoft.AspNetCore: Warning`).
`Microsoft.EntityFrameworkCore.Database.Command` is filtered to `Warning` in `Program.cs` so SQL and
parameter values never end up in the log. To keep a log while reproducing something:

```bash
dotnet run --project src/Server 2>&1 | tee run.log
```

```powershell
dotnet run --project src/Server 2>&1 | Tee-Object run.log
```

`logs/` and `*.log` are git-ignored. Startup failures (bad configuration, migration errors) are
printed before `Now listening on:` appears — that is the first place to look.

## 7. Production hardening

The worker executes repository content, so the process it runs in is part of the security boundary.
The Lua sandbox limits what a `pipeline.lua` can reach from *inside* the interpreter; it says nothing
about what the worker process itself can reach. Treat the two as separate layers
([`lua-runtime.md`](lua-runtime.md) §3.3).

### Dedicated account and work root

- Run the worker as a dedicated, unprivileged account — not a developer's login and not an
  administrator. Everything a build can write lives under `ZENKINS_WORK_ROOT`
  (default `work/` relative to the worker), and the worker must be able to write there and nowhere
  else it does not need.
- Keep the work root out of the repository checkout and out of the server's data directory:
  `work/{run-id}/repository|artifacts|temp` is disposable, `App_Data/` is not.
- On a shared machine, give the worker its own account rather than reusing one that can read other
  users' files; a build script runs with exactly that account's permissions.

### Memory caps

MoonSharp cannot bound memory from inside the interpreter (`collectgarbage('count')` returns `nil`),
so the limit is the operating system's:

| Platform | Mechanism | Status |
|---|---|---|
| Linux | `ulimit -v` for the worker, or a cgroup v2 `memory.max` on the service | Recommended; size it above the largest real build |
| Windows | A Job Object memory limit on the worker process | Recommended |
| macOS | None that we have verified — `ulimit -v` fails with `setrlimit failed: invalid argument` on macOS 26.5 and the process keeps allocating | Accepted gap; do not run the worker next to something you need |

A run killed by the OS is reported `Failed` with the worker's report; it is never silently retried.

### Secrets key ring

The server's Data Protection key ring is what makes stored secret ciphertexts readable (F-012) — and
the same ring mints the F-008 log-subscription tickets (`src/Server/LogStreaming/RunLogAccess.cs`), so
losing it also invalidates live log streams.

**Where it lives.** The ring is persisted next to the data it protects: `App_Data/keys/` for the
default SQLite deployment (the database is `App_Data/zenkins.db`), and `App_Data/keys/` under
`AppContext.BaseDirectory` when `Database:Provider` is `Postgres` (a server database has no local
file to sit next to). `DataProtection:KeysDirectory` pins another directory (absolute, or relative
to `AppContext.BaseDirectory`). `Program.cs` registers the ring with the application name `Zenkins`,
resolves this path when
the Data Protection options are materialised — after every configuration source, including container
and test overrides — and asserts at startup that the directory is writable: a path that is not a
writable directory fails startup with the resolved path in the message, instead of failing later at
the first decryption (verified).

**Back it up with the database.** Losing the ring does not lose the rows, but it makes every stored
secret unreadable: the ciphertexts cannot be decrypted, so each secret has to be **re-entered, not
restored**. A backup of the database without this directory is not a backup of the secrets. The same
is true of changing the application name (`Zenkins`) or the protector purpose
(`Zenkins.Secrets.v1`) — both derive different keys and orphan existing ciphertexts.

Restrict the directory's permissions to the service account (`chmod 700` on Unix, an ACL on Windows):
the key files are unencrypted key material by default, exactly like the database file.

## Current state (what you can actually do)

Working today: first-run Owner bootstrap, cookie sign-in/out, organizations and members (invite,
role change, remove), server-side organization switching, and projects scoped to the active
organization; workers register with an Owner-minted token and keep heartbeats; runs can be triggered
from a project page, queued, claimed by a matching idle worker, executed as a native process in an
isolated per-run work directory, watched live through streamed logs, cancelled, timed out, and
marked `Lost` when their worker disappears. The project surface is `/projects`, `/projects/new`,
`/projects/{id}`, `/projects/{id}/edit` and `/projects/{id}/delete/confirm`: Members may create and
edit, only an Owner may delete, the repository URL, default branch and pipeline path are validated,
and a project name is unique per organization (`AddProjectUniqueName`). Organization secrets can be
created, rotated and deleted by an Owner on `/secrets`: values are encrypted at rest with Data
Protection, are never readable again through a page or an API (there is no value route at all), and a
Member's request for the page or a write is a 403.

A run is a **pipeline** run (F-014 and later): the worker clones the project's repository at the
branch the run pinned (F-013, with a per-project Git credential), resolves and freezes the step list
from `pipeline.lua` (F-011/F-014), runs the steps in order, records each step and its `secret()` values
(F-015), publishes artifacts (F-016) and reports the commit it actually built. A run can also be started
by a `git push` through a project webhook (F-017): the endpoint is signature-verified with the
webhook's own secret, filtered by branch, replay-protected, rate-limited, and a webhook run is pinned to
the pushed commit so an overlapping push to the same branch cannot move it.

Not implemented yet: nothing in the run path above — the remaining shell is the standalone Workers page
(worker registration/heartbeat work; the page is a preview), and unknown/duplicate-trigger handling for
provider-specific webhook event types beyond `push`.

- Publishing the Worker AOT from a Windows shell needs the Visual Studio Installer directory on
  `PATH`: without `vswhere.exe`, `findvcvarsall.bat` captures the "not recognized" text into the
  linker path and ILCompiler reports `MSB3073 error 123`. This is a Windows toolchain quirk, not a
  repository setting — the compile itself succeeds with the pinned compiler.
