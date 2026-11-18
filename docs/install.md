# Installation and upgrade guide

Everything needed to go from a machine that has neither a repository clone nor the .NET SDK to a
running Zenkins deployment — install, configure, run as a service, register a worker, run a real
build, upgrade with data intact, and uninstall — on macOS, Linux and Windows.

> **Verified on:** macOS 26.5 (arm64) on **2026-11-14**. Every command in §3 (macOS) was executed in
> the order printed there against the artifacts of `scripts/publish.sh osx-arm64`, including the
> upgrade rehearsal (§3.8) and the uninstall (§3.9); quoted outputs are what those runs produced.
> §4 (Linux) and §5 (Windows) are **static walkthroughs of the delivered packaging only — none of
> their commands was executed**, neither on that macOS verification machine nor on the Windows
> machine that finished this pass, following I-003's per-OS honesty rule. They document the scripts
> and the unit exactly as shipped in `deploy/`; run them as written on a host of that OS and record
> what happened. §2, §6 and §8 state facts that hold for every OS (path and configuration semantics
> are product behaviour pinned by tests; the per-OS steps are packaging).

## Contents

- [1. What ships](#1-what-ships)
- [2. Facts every OS shares](#2-facts-every-os-shares)
- [3. macOS — launchd agent (verified)](#3-macos--launchd-agent-verified)
- [4. Linux — systemd unit (not executed)](#4-linux--systemd-unit-not-executed)
- [5. Windows — Windows Service (not executed)](#5-windows--windows-service-not-executed)
- [6. Worker quick-start for build machines](#6-worker-quick-start-for-build-machines)
- [7. Remote access: reverse proxy, not a wider bind](#7-remote-access-reverse-proxy-not-a-wider-bind)
- [8. Upgrades in depth: backup, migrations, rollback](#8-upgrades-in-depth-backup-migrations-rollback)
- [9. Uninstall and exactly what remains](#9-uninstall-and-exactly-what-remains)
- [10. Troubleshooting](#10-troubleshooting)
- [11. The whole path on one page](#11-the-whole-path-on-one-page)
- [12. Appendix: TC-001 command log and what was not exercised](#12-appendix-tc-001-command-log-and-what-was-not-exercised)

## 1. What ships

Zenkins distributes as **self-contained binaries**: the .NET runtime is inside the artifact, so no
machine that only *runs* Zenkins needs the SDK, a runtime, Docker, Node.js or a database server.

| Artifact | Shape | RIDs | Purpose |
|---|---|---|---|
| **Server** | folder (`Zenkins.Server` apphost + `wwwroot/` + libraries) | `osx-arm64`, `linux-x64`, `win-x64` | The web application. A folder by design: Blazor Server ships static web assets, so there is **no single-file Server** (and no AOT Server — [`docs/native-aot.md`](native-aot.md) §6 records that verdict). |
| **Worker** | folder (`Zenkins.Worker` apphost + libraries) | the same three | Runs builds. |
| **Worker (single-file)** | one executable, `Zenkins.Worker` | the same three | The build-machine drop: one file, no folder. |
| **Worker (NativeAOT)** | one native executable | produced per OS on that OS | Optional, faster-starting variant (`--worker-aot` in `scripts/publish.sh`); `osx-arm64` is verified, win-x64/linux-x64 carry analyzer-build evidence only — see [`docs/native-aot.md`](native-aot.md) §7. |

What does **not** exist, so no step of this guide invents it: no `zenkins` CLI, no application-level
`--port` argument (the port is configuration, `ASPNETCORE_URLS`, set by each service template), no
service template for the Worker (the Worker runs as a plain process; §6), and no single-file or AOT
Server. The one application-level flag either binary understands is `--version`
(*Finding the version* below, card P-001).

**Where the artifacts come from.** A release bundle — the output of `scripts/publish.sh` produced on
a machine that *does* have the .NET 10 SDK (the only machine in the story that needs one; for AOT it
additionally needs that OS's native toolchain). The script is bash; §5 gives the equivalent
`dotnet publish` commands for a Windows publishing machine. Copy the artifact folders to the target
machine with **`tar`, not zip** — zip does not preserve the executable bit on the apphosts:

```bash
# on the publishing machine (repo checkout with the SDK)
scripts/publish.sh osx-arm64                       # or: linux-x64 / win-x64 / all three
tar -C artifacts/publish -czf zenkins-osx-arm64.tar.gz osx-arm64/Server osx-arm64/WorkerSingleFile
```

```bash
# on the target machine (no repo, no SDK)
mkdir -p ~/zenkins
tar -C ~/zenkins -xzf zenkins-osx-arm64.tar.gz
mv ~/zenkins/osx-arm64/Server ~/zenkins/server && chmod +x ~/zenkins/server/Zenkins.Server
```

(A Windows-to-Windows transfer may use zip: NTFS has no executable bit to lose.)

## 2. Facts every OS shares

**Where the data lives.** Everything persistent sits in `App_Data/` **beside the binaries**:

```text
<install-dir>/
  Zenkins.Server            # the apphost (Zenkins.Server.exe on Windows)
  wwwroot/                  # static web assets — ship the whole folder, not just the apphost
  App_Data/
    zenkins.db              # SQLite database (+ zenkins.db-wal / zenkins.db-shm while running)
    keys/                   # the Data Protection key ring — encrypts stored secrets
    artifacts/              # run artifacts
  logs/                     # (macOS template) server.log / server.err.log
```

Every persistent path resolves from `AppContext.BaseDirectory` — the directory the binaries run
from — never from the process's current directory, so *where* a service starts the app does not
move the data. This is product behaviour, pinned by tests (card I-003), not a template habit.

**How configuration is read.** Ordinary ASP.NET Core configuration: `appsettings.json` next to the
binaries, then environment variables with double underscores for nesting
(`Database__Provider`, `ConnectionStrings__Default`, `ASPNETCORE_URLS`). One subtlety an operator
must know: **file discovery of `appsettings.json` follows the working directory**, and a service's
working directory is whatever its template pins — the launchd plist and the systemd unit pin it to
the install dir, so `appsettings.json` there is read; **a Windows Service starts in
`C:\Windows\System32`**, where appsettings.json is not found, so the Windows template routes every
setting through the service's environment instead (§5). Environment variables always work.

| Key | Values | Default in the delivered templates |
|---|---|---|
| `ASPNETCORE_URLS` | URL prefix | `http://127.0.0.1:5000` (all three templates) |
| `Database__Provider` | `Sqlite`, `Postgres` | `Sqlite` |
| `ConnectionStrings__Default` | provider-specific | unset (allowed only for SQLite: `<install-dir>/App_Data/zenkins.db`) |
| `Database__ApplyMigrationsOnStartup` | `true`, `false` | **`true`** in all three templates — a single-node self-host applies pending EF Core migrations at startup. Without it, Production does not migrate (the product default is Development-only) and a fresh deployment fails on first registration (§10). |
| `DataProtection:KeysDirectory` (`DataProtection__KeysDirectory`) | path | unset — the ring lives in `keys/` beside the database (a SQLite deployment) or in `App_Data/keys` beside the binaries (a remote database) |
| `Artifacts:RootPath` (`Artifacts__RootPath`) and the `Artifacts:*` caps | paths/numbers | unset — the store is `App_Data/artifacts` beside the binaries; a relative value resolves against `AppContext.BaseDirectory`, never the working directory, and an absolute one passes through. The caps are product defaults, none of the templates overrides them |

**Switching to PostgreSQL.** The templates ship `Database__Provider=Sqlite`; a PostgreSQL
deployment changes two values where the template already sets its variables — `Database__Provider=Postgres`
and `ConnectionStrings__Default=Host=…;Port=5432;Database=zenkins;Username=zenkins;Password=zenkins` —
in the plist's `EnvironmentVariables` dict (§3.2), the unit's `Environment=` lines (§4.2) or the
service `Environment` registry value (§5.2). The database then lives on the server; the key ring
(`App_Data/keys`) and the artifact store stay beside the binaries. PostgreSQL must be reachable when
the Server starts, because that is when startup migrations connect. See
[`docs/development.md`](development.md) §4 for the provider's setup and §5 for the migration
contract both providers share.

**The key ring is part of the data.** `App_Data/keys` is what makes stored secrets readable; a
backup of the database without it is not a backup of the secrets (§8). Restrict it to the service
account like the database file itself.

**Finding the version** (card P-001). Every artifact is stamped at build time from one
`Version` property in the repository's `Directory.Build.props`, and the stamp is readable without
the repository, three ways:

```bash
./Zenkins.Server --version    # Zenkins Server 1.0.0   (the shipped folder or single file; exit 0)
./Zenkins.Worker --version    # Zenkins Worker 1.0.0   (folder, single-file and NativeAOT variants)
```

On Windows: `Zenkins.Server.exe --version` and `Zenkins.Worker.exe --version`. A running
deployment shows the same stamp in the web UI's sidebar footer (`CI server · shell preview ·
v1.0.0`), which is how an operator with only a browser reads it. All three surfaces print the same
value — the fast suite's version fact and the PublishAot fixture's probe on the native Worker
executable assert it (a Server and a Worker of the same artifact set can never disagree).

## 3. macOS — launchd agent (verified)

Everything in this section was executed in this order on 2026-11-14 (macOS 26.5, arm64). Quoted
outputs are the observed ones.

### 3.1 Prerequisites and artifact placement

- No .NET SDK, no runtime, no Docker. `git` is needed on **worker** machines only (workers clone
  repositories; the Server needs nothing beyond the artifact).
- Pick an install directory. The examples use `~/zenkins/server` for the Server and `~/zenkins/build-1`
  for a Worker — both outside any repository checkout.
- Place the artifacts (§1), then make sure the apphosts are executable (`chmod +x` after a tar
  transfer is enough; `install-launchd.sh` refuses to install without it).

### 3.2 Install the Server as a launchd agent

```bash
deploy/macos/install-launchd.sh install ~/zenkins/server 5000
```

```text
installed: com.zenkins.server (/Users/<you>/zenkins/server, port 5000) - data lives in /Users/<you>/zenkins/server/App_Data
```

The script renders `deploy/macos/com.zenkins.server.plist` into `~/Library/LaunchAgents/` with your
home and port, replaces any older definition and bootstraps the job into your login session (the
`gui` domain — no root needed). The agent starts at login (`RunAtLoad`) and is restarted by launchd
if the process dies (`KeepAlive`, 5 s throttle). The plist pins `WorkingDirectory` to the install
dir, so an `appsettings.json` there is read, sets `ASPNETCORE_URLS=http://127.0.0.1:5000`,
`Database__Provider=Sqlite` and `Database__ApplyMigrationsOnStartup=true`, and directs stdout/stderr
to `<home>/logs/server.log` and `server.err.log`.

> **Port 5000 on macOS is shared with AirPlay Receiver.** macOS's Control Center listens on `*:5000`
> whenever AirPlay Receiver is enabled (System Settings → General → AirDrop & Handoff). While the
> Zenkins Server is up, its specific bind on `127.0.0.1:5000` wins and everything works (verified —
> this guide's run used port 5000). But when the Server is *not* listening — during an upgrade stop,
> a crash — loopback clients fall through to Control Center and receive **403 Forbidden from
> something that is not Zenkins** (verified: with the Server uninstalled, `curl
> http://127.0.0.1:5000/` answers 403). A client that pooled such a connection keeps getting 403s
> until it reconnects — this guide's worker storm is §10's first entry. The robust choice on macOS
> is an explicit non-5000 port at install time, or disabling AirPlay Receiver.

`status` shows the running job:

```bash
deploy/macos/install-launchd.sh status
```

```text
	state = running
	program = /Users/<you>/zenkins/server/Zenkins.Server
	pid = 27897
```

### 3.3 Verify it runs, and where the data went

```bash
curl -s -o /dev/null -w "%{http_code} -> %{redirect_url}\n" http://127.0.0.1:5000/
ls ~/zenkins/server/App_Data
tail -2 ~/zenkins/server/logs/server.log
```

```text
302 -> http://127.0.0.1:5000/account/login?ReturnUrl=%2F
artifacts
keys
zenkins.db
zenkins.db-shm
zenkins.db-wal
      Hosting environment: Production
      Content root path: /Users/<you>/zenkins/server
```

Startup created `App_Data/` beside the binaries — the data directory, the key ring and the artifact
root are ensured before the first request is served — and the startup migration applied the schema
(`Database__ApplyMigrationsOnStartup=true`). Note `Hosting environment: Production` — the
published app is a Production host by design; nothing in this guide relies on Development. (A
`curl -I` on `/` answers 405 — the page has no HEAD handler; use GET.)

### 3.4 First-run Owner bootstrap

Open <http://127.0.0.1:5000> in a browser:

1. You are redirected to **/account/login**; go to **Register**.
2. The page says the first account becomes the **Owner** of a starter organization. Register with
   any email and a password of at least 12 characters containing upper and lower case letters, a
   digit and a symbol.
3. You land on the dashboard showing the **Starter Organization** badge. This state is permanent:
   later sign-ups get an account without an organization until an Owner adds them
   ([`docs/development.md`](development.md) §3).

Headless equivalent (verified; the antiforgery token comes from the rendered form for
`/account/register` and from `<meta name="request-verification-token">` on the other pages):

```bash
curl -s -c cookies.txt -b cookies.txt http://127.0.0.1:5000/account/register -o register.html
TOKEN=$(grep -o 'name="__RequestVerificationToken" value="[^"]*"' register.html | head -1 | sed 's/.*value="//; s/"$//')
curl -si -b cookies.txt -c cookies.txt -X POST http://127.0.0.1:5000/account/register \
  --data-urlencode "_handler=register" \
  --data-urlencode "__RequestVerificationToken=$TOKEN" \
  --data-urlencode "Input.Email=owner@example.com" \
  --data-urlencode "Input.Password=CorrectHorse9!" \
  --data-urlencode "Input.ConfirmPassword=CorrectHorse9!" | head -1     # HTTP/1.1 302 Found
curl -s -b cookies.txt http://127.0.0.1:5000/ | grep -o 'Starter Organization' | head -1
```

### 3.5 Register a worker

On the Server (browser): **Workers → Registration tokens → Create registration token**. The
plaintext token (`zreg_…`) is shown once and never stored — copy it now.

On the build machine — a different directory, a different machine, or the same Mac for a trial
(verified on the same machine):

```bash
mkdir -p ~/zenkins/build-1
cp <bundle>/WorkerSingleFile/Zenkins.Worker ~/zenkins/build-1/Zenkins.Worker
chmod +x ~/zenkins/build-1/Zenkins.Worker

cd ~/zenkins/build-1
ZENKINS_SERVER_URL=http://127.0.0.1:5000 \
ZENKINS_REGISTRATION_TOKEN=<the zreg_ token> \
ZENKINS_WORKER_NAME=build-1 \
./Zenkins.Worker
```

```text
Zenkins Worker: registering with http://127.0.0.1:5000/ as 'build-1' (macOS, Arm64, 8 CPUs, tags '').
Zenkins Worker: registered; the server assigned worker id '7d9eacef-…'.
Zenkins Worker: worker token persisted to /Users/<you>/.zenkins/worker-tokens/127-0-0-1-5000/build-1/<token-id>/worker.token.
Zenkins Worker: run execution starts on 1 loop(s); claiming every 2 second(s).
Zenkins Worker: online as worker '7d9eacef-…'; heartbeating every 15 second(s).
Zenkins Worker: log stream connected.
```

The registration token is single-use; the worker persists **its own** credential under
`~/.zenkins/worker-tokens/` and every later start reconnects with it — no token on the command line
again (verified: the restart printed *using the persisted worker token from …* and came straight
back online). The Workers page lists `build-1` as **Online**. Run the Worker in the foreground for
a trial, or under your own daemon mechanism — no Worker service template ships with the product
(§6 covers the knobs).

### 3.6 First real build

1. **Projects → New project**: name, repository URL (only `https://…` and SSH forms are accepted —
   `file://` and `ext::` are refused by design), default branch, pipeline path (`pipeline.lua`).
   The pipeline definition is a `pipeline.lua` at that path in the repository
   ([`docs/lua-runtime.md`](lua-runtime.md)); a minimal three-step one:

   ```lua
   pipeline {
     name = "demo",
     env = { ZK_GREETING = "hello-from-demo" },
     steps = {
       { name = "Restore", run = { "sh", "-c", "echo \"$ZK_GREETING\"" } },
       { name = "Build",   run = { "sh", "-c", "echo building" } },
       { name = "Publish", artifacts = { "report.txt" },
         run = { "sh", "-c", "echo 'artifact-body-42' > report.txt" } },
     },
   }
   ```

   The worker runs each `run` entry as an **argv vector, not through a shell**
   (`ProcessStartInfo.ArgumentList`, `UseShellExecute=false`), which is why the steps above spell
   `sh -c` explicitly. On Windows the same steps need `cmd /c` instead (§5.3).

2. If the repository needs credentials, store a per-project Git credential on the project page
   (an HTTPS token or an SSH key); it reaches git only through a generated ask-pass helper, never
   argv or the run environment.
3. On the project page press **Run** (blank branch = the default branch). The queued run is claimed
   by `build-1`, cloned, executed, and the run page streams the logs live.

Observed on the verification run: the run reached **Success** in 0.6 s, the run page shows the
commit and the three steps, and the artifact downloaded from the run page was byte-identical to what
the pipeline wrote (`artifact-body-42`, 17 bytes — matching the server log's
`Artifact finalized … Bytes=17`). A failed clone shows up as `git-unreachable` with git's stderr in
the run log — that is how the verification run first saw its test-rig certificate problem, before
the rig was fixed.

### 3.7 Restart-on-crash and start-on-login

`KeepAlive` is verified, boot-start is configured but not exercised (no reboot was performed for
this guide):

```bash
kill -9 <pid from install-launchd.sh status>
sleep 8 && deploy/macos/install-launchd.sh status
curl -s -o /dev/null -w "%{http_code}\n" http://127.0.0.1:5000/    # 302 again, new pid
```

Observed: launchd restarted the process (`pid = 28680` after `pid = 27897`), HTTP answered again,
and the database, artifacts and Owner session all survived the SIGKILL. Start-on-login (`RunAtLoad`)
was not exercised — log out/in or reboot to see it.

### 3.8 Upgrade (stop → replace → start; performed)

The upgrade story is the same on every OS; the macOS commands here were **executed as written**:

```bash
# 1. stop — the job definition stays on disk, only the loaded job is torn down
launchctl bootout gui/$(id -u)/com.zenkins.server

# 2. back up the data (the whole App_Data is the backup scope — see §8)
cp -R ~/zenkins/server/App_Data ~/zenkins/App_Data.backup

# 3. replace the binaries, keeping App_Data/ and logs/
rsync -a --delete --exclude App_Data --exclude logs <new-artifacts>/Server/ ~/zenkins/server/

# 4. start again
launchctl bootstrap gui/$(id -u) ~/Library/LaunchAgents/com.zenkins.server.plist
```

Observed after the upgrade in this guide's rehearsal (the replacement publish was a rebuild of the
same revision: the stop → replace → start cycle was executed end to end, but an upgrade **across
versions** was not — that rests on `Database__ApplyMigrationsOnStartup=true` applying whatever
migrations the newer binaries carry, §8's subject): the Owner session survived (dashboard still
shows the organization), the pre-upgrade artifact downloaded **byte-identical** (`cmp` clean), the
old run page still rendered, and a **new run on the upgraded binaries completed with Success**.

If you need the Server only stopped for maintenance, `bootout` + `bootstrap` is also the manual
stop/start pair; a plain `kill` works too and `KeepAlive` restarts the process.

### 3.9 Uninstall (performed)

```bash
deploy/macos/install-launchd.sh uninstall
```

```text
uninstalled: com.zenkins.server (the deployment directory and its App_Data were left in place)
```

Verified: `launchctl print gui/$(id -u)/com.zenkins.server` reports *Could not find service*, the
plist is gone from `~/Library/LaunchAgents/`, nothing of Zenkins listens on the port any more (on
this Mac the loopback port then answers 403 from AirPlay Receiver instead — §3.2's warning), and
**what remains is exactly `~/zenkins/server/`** — `App_Data/{zenkins.db, keys/, artifacts/}` and
`logs/` — plus the Worker's `~/.zenkins/` credential store and `work/` directory (§9).

The uninstaller never touches the binaries or the data. To remove those too:

```bash
rm -rf ~/zenkins/server        # the whole deployment: binaries, App_Data and logs
```

That directory holds `App_Data/`, so this deletes the data as well — back it up first (§8) if you
intend to come back.

## 4. Linux — systemd unit (not executed)

**Everything in this section is a static walkthrough of `deploy/systemd/zenkins-server.service` as
delivered — no command below was executed** (the verification machine was macOS; this pass ran on
Windows). Run them on a Linux host and record what happened; where the unit's header text is the
authority, this section follows it verbatim.

### 4.1 Prerequisites and artifact placement

- No .NET SDK or runtime (self-contained); `git` on worker machines only. systemd is assumed (the
  unit is written for it).
- Place the `linux-x64` Server folder at `/opt/zenkins/server` (the unit template's default), the
  apphost executable (`tar`, not zip; `chmod +x Zenkins.Server` if needed).

### 4.2 Service user and install

The unit runs as `User=zenkins` / `Group=zenkins` — create them (the unit does not; this guide
supplies the commands the header only names):

```bash
sudo groupadd --system zenkins
sudo useradd --system --gid zenkins --home-dir /opt/zenkins --no-create-home --shell /usr/sbin/nologin zenkins
```

Install the unit with your install dir substituted — the template has one path to substitute (the
install directory, in `WorkingDirectory`, `ExecStart` and `ReadWritePaths`) and **no port
placeholder**: the port is a literal inside the `ASPNETCORE_URLS=` line, so change it there:

```bash
install_dir=/opt/zenkins/server      # the unit template's default; use your own path here and below
sudo sed -e "s|/opt/zenkins/server|$install_dir|g" deploy/systemd/zenkins-server.service \
  > /etc/systemd/system/zenkins-server.service
sudo chown -R zenkins:zenkins "$install_dir"
sudo systemctl daemon-reload
sudo systemctl enable --now zenkins-server
```

The unit pins `WorkingDirectory=/opt/zenkins/server` (so `appsettings.json` there is read), sets
`ASPNETCORE_URLS=http://127.0.0.1:5000`, `Database__Provider=Sqlite` and
`Database__ApplyMigrationsOnStartup=true`, restarts on failure after 5 s, starts on boot
(`WantedBy=multi-user.target`), and hardens the service (`NoNewPrivileges`, `PrivateTmp`,
`ProtectSystem=strict` with `ReadWritePaths=/opt/zenkins/server` — the data directory must stay
under the install dir or you must extend `ReadWritePaths`).

### 4.3 Verify, bootstrap, first build

`systemctl status zenkins-server`, then the same browser bootstrap as §3.4 against
`http://127.0.0.1:5000` (it is the same application). Logs go to the journal:
`journalctl -u zenkins-server`. Worker registration and the first build are §3.5–§3.6 with the
linux-x64 Worker artifact and `git` installed.

### 4.4 Upgrade (not executed)

The same stop → backup → replace → start story as §3.8, in systemd form:

```bash
sudo systemctl stop zenkins-server
cp -R /opt/zenkins/server/App_Data /opt/zenkins/App_Data.backup        # as the service user or with sudo
rsync -a --delete --exclude App_Data <new-artifacts>/Server/ /opt/zenkins/server/
sudo chown -R zenkins:zenkins /opt/zenkins/server
sudo systemctl start zenkins-server
```

`Restart=on-failure` covers crashes; `enable` covers boot. Neither restart was exercised on this OS.

### 4.5 Uninstall (not executed)

```bash
sudo systemctl disable --now zenkins-server
sudo rm /etc/systemd/system/zenkins-server.service
sudo systemctl daemon-reload
```

The order is deliberate, and the pair is **not idempotent** (measured on card Q-007, Ubuntu 26.04.1,
systemd 259): `disable --now` on an installed unit exits 0, and so does a second
`systemctl stop` while the unit file still exists. Once
`/etc/systemd/system/zenkins-server.service` is gone, `systemctl disable --now zenkins-server`
exits **1** (`Failed to disable unit: Unit zenkins-server.service does not exist`) and
`systemctl stop` exits **5** (`Unit zenkins-server.service not loaded`). An uninstall script that
may run twice should test for the unit file (or treat those codes as "already uninstalled") instead
of reading silence as success — the Windows script has the same asymmetry, documented in §5.5.

Left in place: `/opt/zenkins/server/App_Data/{zenkins.db, keys/, artifacts/}`, the `zenkins` user
and group (remove with `sudo userdel zenkins && sudo groupdel zenkins` if you want them gone — they
were created outside the unit), and the journald log entries (`journalctl --vacuum-time=…` or your
journald retention applies). See §9.

The unit uninstaller does not touch the deployment directory; delete it yourself once the service is
stopped (this removes the binaries **and** the data above):

```bash
sudo rm -rf /opt/zenkins/server
```

## 5. Windows — Windows Service (not executed)

**Everything in this section is a static walkthrough of `deploy/windows/install-server-service.ps1`
as delivered and corrected for this card — no command was executed** (the verification machine was
macOS; this pass ran on Windows). The script ships as packaging for exactly this documentation pass;
run it on a Windows host and record what happened.

### 5.1 Prerequisites, publishing, artifact placement

- The Server artifact for `win-x64` is produced by the four publishes below — the authoritative
  shapes: the Server and Worker rows mirror the publish fixture in
  `tests/Zenkins.Tests/Milestone/PublishedSmokeTests.cs` (self-contained folders; the fixture also
  passes `-p:PublishAot=false`, which matters only for the Worker, whose project defaults to AOT),
  and the last row mirrors the publish matrix's optional AOT row (§1). They need a machine with the
  .NET 10 SDK; the repository's publish matrix is a bash script (§1), which is not the Windows path,
  so run the equivalents directly. On a Windows *publishing* machine (PowerShell, repository
  checkout):

  ```powershell
  dotnet publish src\Server\Zenkins.Server.csproj -c Release -r win-x64 --self-contained true -o artifacts\publish\win-x64\Server
  dotnet publish src\Worker\Zenkins.Worker.csproj -c Release -r win-x64 --self-contained true -p:PublishAot=false -o artifacts\publish\win-x64\Worker
  dotnet publish src\Worker\Zenkins.Worker.csproj -c Release -r win-x64 --self-contained true -p:PublishAot=false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o artifacts\publish\win-x64\WorkerSingleFile
  dotnet publish src\Worker\Zenkins.Worker.csproj -c Release -r win-x64 -o artifacts\publish\win-x64\WorkerAot   # optional AOT variant: needs the VS C++ workload on the publishing machine
  ```

  The `-p:PublishAot=false` on the Worker's JIT rows is deliberate: the Worker csproj defaults to
  AOT, and the JIT packaging must not drift with it (§1).
- No .NET SDK or runtime on the deployment host; `git` on worker machines only.
- Place the Server folder at `C:\zenkins\server` (the examples' install dir). Ship the whole folder.

### 5.2 Install as a Windows Service

From an **elevated** PowerShell:

```powershell
.\deploy\windows\install-server-service.ps1 -InstallDir C:\zenkins\server -Port 5000
```

The script creates service `ZenkinsServer` from the apphost (`sc.exe create … binPath= "…" start=
auto`), sets restart-on-crash (three restarts, 5 s apart, counter reset daily), and writes the
service's environment into the SCM registry value
`HKLM\SYSTEM\CurrentControlSet\Services\ZenkinsServer\Environment` **before** the first start:

- `ASPNETCORE_URLS=http://127.0.0.1:5000`
- `Database__ApplyMigrationsOnStartup=true` — the same semantics as the macOS plist and the systemd
  unit. This line was **corrected during this card**: the delivered script wrote only the URL, and
  with Production's Development-only migration default a fresh deployment would have failed its
  first registration with `no such table: BootstrapMarkers` (§10).

The per-service environment is the Windows story for configuration because a Windows Service's
working directory is `C:\Windows\System32` — an `appsettings.json` next to the binaries is **not**
found there. Put every deployment setting in the service Environment (or edit that registry value
and restart the service); persistent paths still resolve beside the binaries regardless.

### 5.3 Verify, bootstrap, first build (not executed)

`Get-Service ZenkinsServer` / `sc.exe query ZenkinsServer` for state; then the §3.4 bootstrap
against `http://127.0.0.1:5000` — the same application. The service has no console: the .NET
Windows service lifetime routes the application's logging to the **Windows Event Log**
(Application). **Verified on 2026-11-14 (card Q-007)**: the Application log really does carry that
output. The application's own framework log entries arrive under the provider `Zenkins.Server`
(hosting and EF Core messages among them), and the service's SCM start/stop records arrive under the
provider named after the service, `ZenkinsServer`. Read the provider and the message body, not a
fixed event id: the ids are the logging framework's own (an EF Core query message, for one, is
reported with the framework's id for that category, so the same application reports many ids). Worker
registration and the first build are §3.5–§3.6 with the win-x64 artifacts and Git for Windows — with
one difference: a step's `run` list is executed as an argv vector with no shell, so §3.6's `sh -c`
has no Windows equivalent. The same three-step pipeline on Windows:

```lua
pipeline {
  name = "demo",
  env = { ZK_GREETING = "hello-from-demo" },
  steps = {
    { name = "Restore", run = { "cmd", "/c", "echo %ZK_GREETING%" } },
    { name = "Build",   run = { "cmd", "/c", "echo building" } },
    { name = "Publish", artifacts = { "report.txt" },
      run = { "powershell", "-NoProfile", "-Command",
              "Set-Content -Path report.txt -Value 'artifact-body-42' -NoNewline" } },
  },
}
```

`-NoNewline` is deliberate: `'artifact-body-42'` is **16 bytes**, and `Set-Content` (like `cmd`'s
`echo …> file`) would append a line ending — CRLF on this side, while §3.6's POSIX `echo` appends a
lone LF, which is why that example reports 17 bytes. The artifact's content is whatever the step
writes, so the two platforms differ by one line ending and by nothing else.

### 5.4 Upgrade (not executed)

```powershell
Stop-Service ZenkinsServer
Copy-Item -Recurse C:\zenkins\server\App_Data C:\zenkins\App_Data.backup
# replace everything except App_Data (robocopy /MIR with /XD App_Data)
robocopy <new-artifacts>\Server C:\zenkins\server /MIR /XD App_Data
Start-Service ZenkinsServer
```

`sc.exe failure` covers crashes and `start= auto` covers boot; neither was exercised on this OS.
The service's Environment registry value survives binary replacement (it is read at service
start).

`robocopy` reports success as exit code **1** (files copied), **2** (extra files deleted) or **3**
(both), so a non-zero exit is not a failure: check `$LASTEXITCODE -lt 8` (8 and above are real
errors) instead of letting PowerShell see 1 as an error.

### 5.5 Uninstall (not executed)

```powershell
.\deploy\windows\install-server-service.ps1 -Uninstall
```

The script stops and deletes the service and removes its `Environment` registry value. Left in
place: `C:\zenkins\server\App_Data\{zenkins.db, keys, artifacts}` and the Event Log entries (§9).

The uninstaller is **not idempotent** (behaviour since card Q-007): every `sc.exe` step in the
script is checked, so running `-Uninstall` again after the service is gone fails loudly —
`sc.exe delete … 1060: The specified service does not exist as an installed service` and a non-zero
exit code — instead of printing `uninstalled:` and exiting 0. A provisioning script that may run it
twice should treat that exit code as "already uninstalled".
With the service gone, nothing holds the binaries any more — deleting the deployment directory
deletes the data with it:

```powershell
Remove-Item -Recurse -Force C:\zenkins\server
```

## 6. Worker quick-start for build machines

The build machine needs **two things**: the single-file Worker executable (§1) and `git` on
`PATH` (workers clone repositories themselves). No SDK, no runtime, no repository clone.

```bash
# one-time
ZENKINS_SERVER_URL=http://<server-host>:5000 \
ZENKINS_REGISTRATION_TOKEN=<the zreg_ token from the Workers page> \
ZENKINS_WORKER_NAME=build-1 \
./Zenkins.Worker
# every later start
ZENKINS_SERVER_URL=http://<server-host>:5000 ZENKINS_WORKER_NAME=build-1 ./Zenkins.Worker
```

```powershell
# one-time (PowerShell; the worker artifact is an .exe)
$env:ZENKINS_SERVER_URL = 'http://<server-host>:5000'
$env:ZENKINS_REGISTRATION_TOKEN = '<the zreg_ token from the Workers page>'
$env:ZENKINS_WORKER_NAME = 'build-1'
.\Zenkins.Worker.exe
# every later start - the token line is no longer needed once the credential is persisted
$env:ZENKINS_SERVER_URL = 'http://<server-host>:5000'
$env:ZENKINS_WORKER_NAME = 'build-1'
.\Zenkins.Worker.exe
```

What the worker keeps on the machine: its credential in `~/.zenkins/worker-tokens/` (one folder per
server/worker pair, `0700`/`0600`; `%USERPROFILE%\.zenkins\` on Windows) and its run directories
under `work/` **next to the worker binary** — relative paths resolve from the binary's location,
never the current directory. `work/` is disposable; the token file is not (delete it and the next
start needs a fresh registration token).

The knobs an operator actually sets (all environment variables; `WorkerConfiguration.cs` is the
authoritative list with ranges and defaults):

| Variable | Default | Meaning |
|---|---|---|
| `ZENKINS_SERVER_URL` | required | The Server's base URL. |
| `ZENKINS_REGISTRATION_TOKEN` | required once | Single-use `zreg_` token from the Workers page; the worker persists its own credential afterwards. |
| `ZENKINS_WORKER_NAME` | the machine name (`Environment.MachineName`) | Registration name; also selects the persisted credential on later starts. |
| `ZENKINS_WORKER_TAGS` | empty | Comma-separated tags; a run's `RequiredTags` are claimable only by a worker whose own tags cover all of them. |
| `ZENKINS_WORKER_CONCURRENCY` | `1` | Runs executed simultaneously (advertised to the Server as `MaxConcurrency`). |
| `ZENKINS_DATA_DIR` | `~/.zenkins` | Where the token store lives (mount a secret volume here if you prefer). |
| `ZENKINS_WORK_ROOT` | `work` beside the binary | Root of the per-run work directories. |
| `ZENKINS_WORK_RETENTION_HOURS` | `168` (7 days) | How long a **finished** run's `work/{run-id}` tree (checkout, published artifact copies, scratch) is kept on this worker's disk; the sweep removes older trees and never a running run's. `0` disables the sweep. The Server's own retention policy (PRD §3.5) removes the run's rows and artifact bytes - it cannot reach this disk, so this window is what bounds it. |
| `ZENKINS_GIT_DEPTH` | `0` (full clone) | Shallow-clone depth. Webhook runs are pinned to the pushed commit — a depth that excludes it fails the run honestly; see [`docs/development.md`](development.md) §4. |
| `ZENKINS_GIT_KNOWN_HOSTS` | unset (accept-new) | Pin an SSH `known_hosts` file and make unknown host keys a hard failure. |
| `ZENKINS_GIT_TIMEOUT_SECONDS` | `300` | Budget of one git invocation. |
| `ZENKINS_GIT_EXECUTABLE` | `git` | argv[0] of every git invocation, for a deployment that pins the binary. |
| `ZENKINS_PROBES_ENABLED` | `true` | Whether this worker answers Git remote probes ("what branches exist right now?", no clone). Off registers `canProbe: false` and the Server never hands it one; `docs/development.md` §4 has the Server-side knobs. |
| `ZENKINS_PROBE_CONCURRENCY` | `1` | Probes run at once, independent of the build slots. |
| `ZENKINS_PROBE_TIMEOUT_SECONDS` | `30` | Budget of one probe's `git ls-remote`. |

Tags and required tags: the trigger accepts a run's required tags when every tag is declared by at
least one registered worker, but a run is claimed only by a single worker whose own tags cover all
of them — `linux,cuda` does not run across a `linux`-only and a `cuda`-only worker. A set split
across workers is therefore accepted and waits in the queue until a worker carrying the full set
registers (register one, or retag a machine via `ZENKINS_WORKER_TAGS` and restart); it is never
mis-executed and never fails for lack of a worker.

No Worker service template ships: run it under your own service manager (launchd agent, systemd
user unit, Task Scheduler) if you want it supervised — the same pin-the-working-directory and
environment-variable rules as the Server's templates apply.

The optional NativeAOT Worker (`WorkerAot` artifact) is a drop-in replacement for the single-file
drop on the OS it was published for (§1); it starts several times faster (measured in
[`docs/native-aot.md`](native-aot.md) §3) with identical pipeline behaviour (the dual-mode proof of
the same document).

## 7. Remote access: reverse proxy, not a wider bind

All three service templates bind the Server to `http://127.0.0.1:5000` **on purpose**. To serve
other machines, put a TLS-terminating reverse proxy on the host and forward to the loopback
address — that gives you HTTPS for the sign-in cookie (which is `Secure` whenever the request is
HTTPS) and keeps the raw HTTP hop on one machine. Do **not** simply rebind
`ASPNETCORE_URLS=http://0.0.0.0:5000`: that exposes plaintext HTTP sign-in and nothing else in the
packaging accounts for it.

A proxy on a *different host* must be trusted explicitly, or its forwarded headers are ignored:
loopback proxies are trusted by default; others go into configuration (bound in
`src/Server/Program.cs`, covered by `tests/Zenkins.Tests/Auth/HostConfigurationTests.cs`):

```json
{
  "ForwardedHeaders": {
    "KnownProxies": [ "203.0.113.7" ],
    "KnownNetworks": [ "10.0.0.0/8" ]
  }
}
```

Set these as environment variables — as an **index-based list, not one JSON value**: the binder
reads child keys, so `ForwardedHeaders__KnownProxies=["203.0.113.7"]` collapses into a scalar that
produces no children and leaves the allowlist empty (the proxy stays untrusted, `X-Forwarded-Proto`
is discarded and the sign-in cookie is not `Secure` — exactly the failure this section exists to
prevent, and the shape `tests/Zenkins.Tests/Auth/HostConfigurationTests.cs` pins):

```text
ForwardedHeaders__KnownProxies__0=203.0.113.7
ForwardedHeaders__KnownNetworks__0=10.0.0.0/8
```

Put them where the template's other variables live — service Environment (Windows), `Environment=`
lines (systemd), the plist's `EnvironmentVariables` (launchd) — or in an `appsettings.json` that
template actually reads; the zero-based index is part of the key (`__1`, `__2`, … add entries). (§2:
the launchd plist and the systemd unit pin the working directory to the install dir; a Windows
Service starts in `C:\Windows\System32`, so on Windows the environment is the only place.)

## 8. Upgrades in depth: backup, migrations, rollback

**What an upgrade is.** The published binaries are replaceable; the data is not. An upgrade is:
stop the service → replace the binaries (everything except `App_Data/` and `logs/`) → start. The
per-OS commands are §3.8 (executed on macOS), §4.4 and §5.4.

**Backup scope — copy all of `App_Data/` while the Server is stopped** (a stopped server is a
consistent snapshot):

| Path | What it is | What losing it costs |
|---|---|---|
| `App_Data/zenkins.db` (+ `-wal`/`-shm` if present) | Every organization, project, run, step, secret row, worker registration | Everything except artifact bytes |
| `App_Data/keys/` | The Data Protection key ring | Stored secrets become **unreadable** — they must be re-entered, not restored (the rows survive; the ciphertexts don't) |
| `App_Data/artifacts/` | The artifact bytes | Downloads 404 — the file names embed the ids a restore needs |

Without the key ring a database backup is not a backup of the secrets; with it, restoring all three
paths restores everything. The same is true of the product's Data Protection application name and
purpose constants — do not "clean" configuration that changes them (see
[`docs/development.md`](development.md) §7).

**How migrations apply.** All three service templates ship with
`Database__ApplyMigrationsOnStartup=true`: the upgraded Server applies pending EF Core migrations
at startup, before serving. That is the intended story for a **single-node self-host**, and it is
the mechanism §3.8's rehearsal would have used for pending migrations (its binaries carried none to
apply). The product's default (without the variable) migrates in Development only — removing the
line turns startup migration off, which is the multi-node choice.

For an explicitly-managed migration (multi-node, or you removed the flag): run
`dotnet ef database update` **once per deployment** on a machine that has the .NET SDK and the
`dotnet-ef` tool — never on the deployment host, which has neither by design — and **point
`ConnectionStrings__Default` at the install database explicitly**:

```bash
# path shown for a Linux install; substitute your install directory
ConnectionStrings__Default="Data Source=/opt/zenkins/server/App_Data/zenkins.db" \
  dotnet ef database update --project src/Server
```

A bare `dotnet ef database update --project src/Server` creates a *second*, empty database under
`src/Server/App_Data` (the design-time content root) and leaves the deployment's untouched — the
published app then fails on `/account/register` with `no such table: BootstrapMarkers`
([`docs/development.md`](development.md) §6, where this exact symptom was verified).

**Rollback and the downgrade answer.** Rollback is: stop → restore the old binaries → restore the
`App_Data` backup taken in step 2 → start. **Downgrading a database whose schema has already
received newer migrations is not supported** — EF Core migrations only go forward and this product
ships no down-migration path, so the honest answer to *can I downgrade after the new Server ran?*
is: only to the state of your pre-upgrade backup. That is why the backup happens between stop and
replace, every time.

## 9. Uninstall and exactly what remains

Uninstalling removes the **service**; the binaries and the data are two further acts, in that order.

| OS / component | Removed by the uninstaller | Deliberately left behind |
|---|---|---|
| macOS (§3.9) | The launchd job (`bootout`) and `~/Library/LaunchAgents/com.zenkins.server.plist` | `<zenkins-home>/` — `App_Data/{zenkins.db, keys/, artifacts/}` and `logs/` |
| Linux (§4.5) | The unit file and its enablement | `<install-dir>/App_Data/{zenkins.db, keys/, artifacts/}`, the `zenkins` user and group, journald entries |
| Windows (§5.5) | The service and its `Environment` registry value | `<InstallDir>\App_Data\{zenkins.db, keys, artifacts}`, Event Log entries and their source registrations |
| Worker (§6) | Whatever you ran it under | `~/.zenkins/worker-tokens/…/worker.token` (the credential) and `work/` next to the binary (disposable run directories) |

**Removing the binaries too.** No delivered uninstaller deletes the deployment directory; delete it
yourself, service stopped and unregistered first:

```bash
# macOS — after install-launchd.sh uninstall (§3.9)
rm -rf ~/zenkins/server
# Linux — after systemctl disable --now zenkins-server (§4.5)
sudo rm -rf /opt/zenkins/server
```

```powershell
# Windows — after .\install-server-service.ps1 -Uninstall (§5.5)
Remove-Item -Recurse -Force C:\zenkins\server
```

That directory contains `App_Data/`, so this deletes the data with the binaries: back it up first
(§8) if you might come back, and remember that `App_Data/keys/` without its ciphertexts (or
ciphertexts without it) is not a usable backup. After the directories are gone, three leftovers stay
outside them: the `zenkins` service account and journald entries on Linux (remove with
`sudo userdel zenkins && sudo groupdel zenkins` and `journalctl --vacuum-time=…`), the Event Log
entries on Windows, and — on every Worker machine — `~/.zenkins/worker-tokens/…/worker.token` plus
`work/`.

On Windows the [Event Log *source registrations*](../deploy/windows/install-server-service.ps1) are
part of that residue, and they are easy to miss because they are registry keys of their own, not
children of the service key: after `-Uninstall` removed
`HKLM\SYSTEM\CurrentControlSet\Services\ZenkinsServer`, both
`HKLM\SYSTEM\CurrentControlSet\Services\EventLog\Application\Zenkins.Server` (the application's own
framework logging) and `…\Application\ZenkinsServer` (the SCM channel) were still present and the
`Zenkins.Server` provider still resolved — verified with card Q-007. They hold no credentials or
build data, so leaving them is harmless; remove them explicitly
(`Remove-Item -Recurse HKLM:\SYSTEM\CurrentControlSet\Services\EventLog\Application\Zenkins.Server`,
the same for `ZenkinsServer`) if the host must come back to a pristine registry. They are not part of
the service key: an event-log source name is a machine-wide namespace entry (one source of a given
name per host), so it survives the service's deletion and no delivered uninstaller touches it.

"Uninstall" in the delivered scripts never deletes user data — deleting `App_Data/` is the
operator's explicit act, and it is irreversible (§8's table says what each subdirectory costs).

## 10. Troubleshooting

**403 Forbidden from something that is not Zenkins (macOS, port 5000).** AirPlay Receiver
(Control Center) listens on `*:5000`; when the Zenkins Server is not listening (crash window,
upgrade stop), loopback clients fall through to it and get 403 (verified for both `127.0.0.1:5000`
and `localhost:5000` with the Server uninstalled). A client that pooled that connection keeps
getting 403s after the Server returns — this guide's worker did, and a worker restart fixed it.
Fixes: install on a non-5000 port (§3.2), disable AirPlay Receiver, or restart the client. Check
who owns the port with `lsof -nP -iTCP:5000 -sTCP:LISTEN`.

**Port already in use.** The same check on any OS (`lsof`/`ss -tlnp`/`Get-NetTCPConnection`), or
install on another port — the Windows script's `-Port`, the macOS script's port argument, or the
unit's `ASPNETCORE_URLS=` line on Linux (there is no port placeholder in the unit). The port lives
in the service template's `ASPNETCORE_URLS`, never in an application flag (there is no `--port`).

**`no such table: BootstrapMarkers` on first registration.** The database exists but no migration
applied. Either the service's `Database__ApplyMigrationsOnStartup=true` is missing (Windows
services installed with the pre-correction script: re-install or add the value per §5.2), or
migrations were meant to be explicit and ran against the wrong database (§8: point
`ConnectionStrings__Default` at the install database; a bare `dotnet ef` run lands in
`src/Server/App_Data`).

**Configuration changes silently ignored under a service.** `appsettings.json` discovery follows
the working directory: the launchd plist and systemd unit pin it to the install dir, a Windows
Service's is `C:\Windows\System32` and reads nothing useful — on Windows every setting belongs in
the service `Environment` registry value (§5.2), and a running service only re-reads it after the
next restart.

**Where the logs are.** macOS: `<zenkins-home>/logs/server.log` and `server.err.log` (the plist's
`StandardOutPath`/`StandardErrorPath`). Linux: `journalctl -u zenkins-server`. Windows: the Windows
Event Log (Application), via the service lifetime — verified in its own right: the provider
`Zenkins.Server` carries the application's framework log entries there (§5.3). Startup failures print
before any request is served — that is the first place to look.

**The worker says `the server rejected the worker token with 401/403 (unknown, used or revoked)`.**
The persisted credential no longer matches a server-side token (server database replaced, token
revoked, or §10's 403-not-Zenkins trap). Delete `~/.zenkins/worker-tokens/…/worker.token` for that
server/worker pair and start the worker again with a freshly minted registration token
(§3.5).

**A published server started by hand answers 500 for `/_framework/…` and `/_content/…`.** You ran
an apphost out of a half-copied folder. The Server is a folder artifact: ship `wwwroot/` and the
libraries with the apphost (§1), or re-run the copy. (This symptom from a *source* run is
[`docs/development.md`](development.md) §6's Production-from-source case — a different cause.)

## 11. The whole path on one page

The shortest correct route from a fresh host — no repository clone, no .NET SDK — to a green build,
written as the sequence the numbered sections execute. Nothing here is new; it exists so an operator
can follow the path without reconstructing the order from three per-OS sections.

| # | Step | Section |
|---|---|---|
| 1 | Publish the artifact bundle on a machine that has the .NET 10 SDK (the target machine never needs one) | §1 (POSIX), §5.1 (Windows) |
| 2 | Move it with `tar` — zip drops the POSIX apphosts' executable bit (a Windows-to-Windows copy may use zip, §1) — and unpack it beside the install directory | §1 |
| 3 | Install the Server as a service. The templates already set `ASPNETCORE_URLS`, `Database__Provider` and `Database__ApplyMigrationsOnStartup=true` | §3.2, §4.2, §5.2 |
| 4 | Verify it answers on the loopback address and created `App_Data/` beside the binaries | §3.3, §4.3, §5.3 |
| 5 | Bootstrap the first Owner in the browser | §3.4 |
| 6 | Mint a registration token, then start the Worker on the build machine with it | §3.5, §6 |
| 7 | Create the project (repository URL and `pipeline.lua`), press **Run**, watch the run page | §3.6 |
| 8 | Later: expose it to other hosts through a proxy (§7), upgrade with data intact (§8), uninstall (§9) | §7–§9 |

Two rules decide most failures before they happen: the data directory travels with the binaries
(`App_Data/` beside the install directory, never in the working directory), and every setting goes
where that OS's template reads settings — the plist's `EnvironmentVariables`, the unit's
`Environment=` lines, or the Windows service's `Environment` registry value.

## 12. Appendix: TC-001 command log and what was not exercised

TC-001 asks the guide to work verbatim on a machine that has neither the repository nor the SDK. The
log below is what the macOS verification run did, in order; the result column repeats the output
quoted in that section, so a future run can compare row by row.

| # | Section | Command block | Observed |
|---|---|---|---|
| 1 | §1 | `scripts/publish.sh osx-arm64` on the publishing Mac | `Server/`, `Worker/`, `WorkerSingleFile/` under `artifacts/publish/osx-arm64`; any warning would have failed the publish |
| 2 | §1 | `tar` bundle → `tar` unpack → `mv` → `chmod +x` | Server runnable from `~/zenkins/server` |
| 3 | §3.2 | `install-launchd.sh install ~/zenkins/server 5000` | `installed: com.zenkins.server (…, port 5000) - data lives in …/App_Data` |
| 4 | §3.2 | `install-launchd.sh status` | `state = running`, `pid = 27897`, `program = …/Zenkins.Server` |
| 5 | §3.3 | `curl -s -o /dev/null -w "%{http_code} -> %{redirect_url}"` on `/` | `302 -> …/account/login?ReturnUrl=%2F` |
| 6 | §3.3 | `ls <zenkins-home>/App_Data` | `artifacts`, `keys`, `zenkins.db`, `zenkins.db-shm`, `zenkins.db-wal` |
| 7 | §3.3 | `tail -2 <zenkins-home>/logs/server.log` | `Hosting environment: Production`, `Content root path: …/zenkins/server` |
| 8 | §3.4 | browser registration, then the headless `curl` POST | `302 Found`; dashboard contains `Starter Organization` |
| 9 | §3.5 | first Worker start with `ZENKINS_REGISTRATION_TOKEN` | registered, worker id assigned, token persisted, online, log stream connected |
| 10 | §3.5 | Worker restart with only `ZENKINS_SERVER_URL` and `ZENKINS_WORKER_NAME` | `using the persisted worker token from …`, back online with no token |
| 11 | §3.6 | project + `pipeline.lua` + **Run** | run `Success` in 0.6 s; artifact download 17 bytes, byte-identical to the pipeline's output |
| 12 | §3.7 | `kill -9 <pid>`, `sleep 8`, `status`, `curl` | new `pid = 28680`, `302` again, database, artifacts and session intact |
| 13 | §3.8 | `bootout` → `cp -R App_Data` → `rsync` → `bootstrap` | Owner session survived, old artifact `cmp`-identical, new run `Success` on the replaced binaries |
| 14 | §3.9 | `install-launchd.sh uninstall` | service unknown to launchd, plist gone, port closed, `App_Data/` and `logs/` left in place |

What the same run did **not** exercise, per OS — the per-OS labels in §3–§5 say the same, this table
is the checklist:

| OS | Status | Not exercised |
|---|---|---|
| macOS 26.5 | verified (§3) | `RunAtLoad` start-on-login/boot (§3.7 — the rebuild/`bootstrap` path was); an upgrade **across versions** (the rehearsal replaced the binaries with a rebuild of the same revision, and the product's `Category=PublishSmoke` fixture repeats stop → replace → start with one publish output, so no run here proves a version-to-version upgrade); a PostgreSQL deployment (§2) |
| Linux | not executed (§4) | Every row above in its systemd form: user and group creation, unit install, `enable --now`, `Restart=on-failure`, boot start, upgrade, uninstall, `journalctl` |
| Windows | not executed (§5) | The four `dotnet publish` rows on a Windows publishing machine, `sc.exe` create/start, the `Environment` registry value including this pass's `Database__ApplyMigrationsOnStartup=true` correction, Event Log output, the `robocopy` upgrade, `-Uninstall` |

Running one of those sections and recording what the host printed — replacing the result column with
the observed one — is what turns *not executed* into *verified*; until then the label stays.
