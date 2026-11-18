# Zenkins — Product Requirements Document

**版本：** 1.0
**项目名称：** Zenkins
**产品类型：** Self-hosted CI Server
**技术栈：** C# / .NET / Blazor Server / Lua
**目标平台：** Windows / Linux / macOS
**开源协议：** Apache-2.0（见仓库根目录的 [`LICENSE`](../LICENSE)）

---

## 1. 产品概述

### 1.1 产品定位

Zenkins 是一个轻量、跨平台、自托管的持续集成与构建自动化平台（CI Server）。

它借鉴 Jenkins 的核心功能，但不追求复刻 Jenkins 的庞大生态和复杂度。产品专注于提供软件构建自动化所需的基本能力：

- Git 仓库管理
- Pipeline 定义与执行
- 构建任务调度
- 多 Worker 管理
- 实时构建日志
- 构建产物管理
- 多租户与权限管理

Zenkins 采用 .NET 技术栈，使用 Lua 作为 Pipeline 脚本语言，支持在 Windows、Linux、macOS 上原生运行构建任务。

### 1.2 产品理念

**Simple, Lightweight, Cross-platform.**

核心设计原则：

1. **简单优先（Simplicity First）**：只提供 CI/CD 的核心功能，不引入不必要的抽象和复杂机制。
2. **原生跨平台（Native Cross-platform）**：直接在操作系统上执行构建任务，不强制依赖 Docker。
3. **轻量化（Lightweight）**：Server 和 Worker 均使用 .NET 实现，避免引入庞大的外部依赖。
4. **可扩展（Extensible）**：通过独立 Worker 支持多机器、多操作系统和并行构建。
5. **多租户（Multi-tenancy）**：多个组织可以在同一 Server 上独立管理项目和构建任务。
6. **开发者友好（Developer Friendly）**：使用 Lua 定义 Pipeline，提供简洁的 CI SDK。

### 1.3 目标用户

- 游戏开发团队
- .NET / C++ / Go / Java 开发团队
- 独立开发者
- 小型研发团队
- 希望自建 CI 系统、不希望维护 Jenkins 的组织

特别关注需要原生 Windows、macOS、Linux 构建环境的项目，例如 Unity、Unreal Engine、.NET 和 C++ 项目。

### 1.4 产品差异化

| 维度 | Jenkins | Zenkins |
|---|---|---|
| 技术栈 | Java | C# / .NET |
| UI | Web | Blazor Server |
| Pipeline | Groovy / Jenkinsfile | Lua |
| Worker | Agent | .NET Worker |
| 部署 | Java Runtime | .NET Runtime / Self-contained |
| 多平台 | 支持 | 原生支持 |
| Docker | 可选 | 非必需 |
| 插件生态 | 庞大 | 第一版不提供 |
| 多租户 | 依赖插件及配置 | 原生设计 |
| 产品理念 | 通用自动化平台 | 轻量 CI Server |

---

## 2. 技术架构

### 2.1 整体架构

采用 Server / Worker 分离架构。

```text
                       Browser
                          |
                          |
                  Blazor Server UI
                  Blazor Blueprint
                          |
                          |
                  ASP.NET Core Server
                          |
             +------------+------------+
             |            |            |
           Auth         Scheduler    SignalR
             |            |            |
             +------------+------------+
                          |
                    Database Layer
                          |
                 SQLite / PostgreSQL
                          |
              +-----------+-----------+
              |           |           |
           Worker 1    Worker 2    Worker 3
           Windows      Linux       macOS
              |           |           |
           Execute     Execute     Execute
           Pipeline    Pipeline    Pipeline
```

Server 负责管理、调度和协调，Worker 负责实际执行构建任务。

### 2.2 技术选型

| 模块 | 技术 |
|---|---|
| Backend | ASP.NET Core |
| UI | Blazor Server |
| UI Component | Blazor Blueprint |
| ORM | Entity Framework Core |
| Database | SQLite / PostgreSQL |
| Realtime | SignalR |
| Pipeline Runtime | MoonSharp |
| Pipeline Language | Lua |
| Worker | .NET Console |
| Git | System Git CLI |
| Artifact Storage | Local Filesystem |
| Authentication | ASP.NET Core Identity |
| Logging | Microsoft.Extensions.Logging |
| Configuration | Microsoft.Extensions.Configuration |

### 技术选型原则

- 所有核心组件优先采用 .NET 生态。
- 尽量使用 .NET 内置能力。
- 不引入 Redis、RabbitMQ 等外部基础设施作为运行前提。
- 不依赖 Docker。
- 不要求安装 Java、Node.js 等额外 Runtime 来运行 Zenkins。
- Worker 应支持 Self-contained 发布。

---

## 3. 功能需求

### 3.1 用户与多租户管理

#### 3.1.1 Organization

Organization 是系统的基本租户单位。

每个 Organization 拥有独立的：

- Projects
- Pipelines
- Build Runs
- Secrets
- Artifacts
- 成员与权限
- Worker 资源配置
- 环境变量（Environment Variables）

用户可以加入多个 Organization，并在不同 Organization 之间切换。

**组织环境变量（F-034）。** 一个 Organization 可以定义一组普通的 `name=value` 环境变量（最多 100 个、
单个值最多 4 KiB），供本组织所有项目、所有步骤读取，因此 registry host、工具路径、代理设置只需
配置一次。

- 名称规则与 Secret 名称完全一致（`^[A-Za-z_][A-Za-z0-9_]*$`，最多 64 字符，组织内唯一），因为它同样要能
  作为环境变量名。
- **值不是 Secret**：明文存储，在组织环境变量页、Run 详情页以及步骤日志中都会原样可见；需要保密的值
  应放入 Secrets（§3.8）。
- **Owner 可写、Member 可读**：新增、编辑、删除仅 Owner；Member 在页面上只读（这正是与 Secrets 的关键
  差别 - Secrets 整页 Owner-only）。
- `ZENKINS_*` 是服务端保留的运行元数据命名空间，组织变量不允许使用该前缀；保存即拒绝，并给出点名字段的
  错误。
- 触发时把解析后的组织层快照到 Run 上：之后修改或删除变量都不影响已排队、运行中或已完成的 Run；
  Run 详情页展示该 Run 当时使用的组织层（含值）。
- 层叠顺序与注入细节见 §3.11.3。

#### 3.1.2 用户角色

第一版只提供两种角色：

| 角色 | 权限 |
|---|---|
| Owner | 完整管理权限 |
| Member | 查看、创建和执行项目及 Pipeline |

Owner 负责：

- Organization 设置
- 成员管理
- Worker 管理
- Secret 管理
- 项目管理

Member 可以：

- 查看项目
- 创建和编辑 Pipeline
- 手动触发构建
- 查看构建日志
- 下载 Artifact

#### 3.1.3 数据隔离

所有租户业务数据必须关联 TenantId。

要求：

- 所有数据查询必须进行租户范围过滤。
- 禁止通过修改 URL 或 API 参数访问其他租户的数据。
- Git Credentials、Secrets、Artifacts 必须按租户隔离。
- Worker 任务必须绑定对应租户和项目。
- 不允许跨租户读取构建日志和构建产物。

第一版采用逻辑隔离，不要求每个租户使用独立数据库。

### 3.2 Project 管理

Project 是 Zenkins 的作业单元，分两种 Kind：

- **Pipeline**（原有 Kind）：构建定义来自一段 `pipeline.lua`。定义来源（Source，v1.2 新增，见 §3.3.1）
  有两种：
  - `repository`（原有来源）：Project 代表一个 Git Repository，`pipeline.lua` 随代码存放在仓库中。
  - `inline`（v1.2 新增）：不要求 Git Repository，`pipeline.lua` 正文直接保存在 Server 上（Pipelines
    标签页的文本框），每次 Run 在触发时快照该脚本。
- **Freestyle**（v1.1 新增，见 §3.10）：不要求 Git Repository，构建步骤在服务端 UI 中配置为脚本。

每个 Project 包含：

| 字段 | 说明 |
|---|---|
| Name | 项目名称 |
| Kind | 项目类型：`pipeline` 或 `freestyle`，创建后不可变更 |
| Pipeline Source | Pipeline 项目的定义来源：`repository` 或 `inline`，创建后不可变更（仅 Pipeline） |
| Inline Script | `inline` Pipeline 的 Lua 正文（仅 `inline`；上限 64 KiB） |
| Repository URL | Git 仓库地址（仅 `repository` 来源的 Pipeline；Freestyle 与 `inline` 为空） |
| Default Branch | 默认分支（仅 `repository` 来源的 Pipeline；Freestyle 与 `inline` 为空） |
| Branch Discovery | 分支发现模式：`single`（默认，沿用原有单分支行为）或 `branches`（每个含 Pipeline 文件的分支成为一个 Branch Job，v1.2 新增）。只有 `repository` 来源的 Pipeline 可设为 `branches` |
| Credentials | Git 认证信息（仅 `repository` 来源的 Pipeline；Freestyle 与 `inline` 拒绝赋值） |
| Pipeline Path | Pipeline 文件路径，默认 `pipeline.lua`（仅 `repository` 来源的 Pipeline；`inline` 的项目行留空，Run 快照记录 `pipeline.lua`） |
| Disabled | 是否已禁用（见下）。禁用只停触发，不删历史 |
| Disabled Reason | 禁用原因，禁用时必填——被拒绝的触发会把原因原样展示给用户 |
| Disabled At / Disabled By | 何时、由谁禁用（无外键，账号删除不改写项目历史） |
| Created At | 创建时间 |
| Updated At | 更新时间 |

#### 功能要求

- 创建、编辑、删除 Project。
- 支持 HTTPS 和 SSH Git Repository（`repository` 来源的 Pipeline）。
- 支持从文本框保存 `inline` Pipeline 脚本；保存本身不证明脚本有效，Validate 在 Worker 上运行
  definition phase 并返回步骤列表（不创建 Run、不占用构建槽位）。
- 支持 Private Repository。
- 支持配置 Git Credentials（仅 `repository` 来源的 Pipeline）。
- 支持指定默认分支（仅 `repository` 来源）。
- 支持分支发现（v1.2 新增，见 §3.3.7）：`single` 为默认，行为与 v1.1 完全一致；`branches` 把一个项目的每个分支作为独立 Branch Job，各有自己的 Run 历史与最后状态。
- 支持指定 Pipeline 文件路径（仅 `repository` 来源）。
- 支持手动触发构建。
- 支持 Git Webhook 触发构建（仅 `repository` 来源的 Pipeline；Freestyle 与 `inline` 无仓库可推送）。
- 支持参数化构建（两种 Kind 通用，见 §3.11）。
- 支持启用/禁用项目：禁用后所有触发路径一律拒绝，历史、Artifact 与设置仍可读。

#### 启用 / 禁用

禁用是「可逆的触发制动」，不是软删除：不写删除标记，不删任何行。

- 禁用者：Owner 或 Member（与项目其他变更一致）；删除项目仍仅限 Owner，且仍然是一个独立的确认动作。
- 禁用时已排队未开始的 Run 会被取消（`project-disabled`），正在执行的 Run 不受影响——中止在跑的构建是另一个独立决定。
- 重新启用只清除状态：已取消的 Run 不会复活，漏掉的触发不会补播。
- 各触发路径的拒绝契约：手工表单回到项目页并显示 `project-disabled`；Webhook 记录
  `project-disabled` 并返回 2xx（永久 4xx 会让多数 Git 托管方自动停用 Webhook，就破坏了
  「重新启用后自动恢复」的承诺）；计划/轮询类触发记录 `skipped-disabled`。
- 状态在项目列表、Dashboard、Runs 列表与 Run 详情页可见。

Git 操作由 Worker 执行，Server 不负责在本地 Clone 项目。

第一版使用系统 Git CLI，避免引入额外 Git Runtime，也便于兼容 Git LFS、Submodule 等常见能力。

### 3.3 Pipeline

Pipeline 是 Zenkins 的核心功能。

采用 Lua 作为 Pipeline 定义语言，使用 MoonSharp 在 Worker 中执行。

#### 3.3.1 Pipeline 文件

默认文件名：

`pipeline.lua`

Pipeline 项目有两种定义来源：

- **repository**（原有）：Pipeline 文件存放在 Git Repository 中，跟随代码一起进行版本管理；Worker Clone
  仓库后从 checkout 读取并运行 definition phase。
- **inline**（v1.2）：文件正文保存在 Server 上，由 Pipelines 标签页的文本框编辑（纯文本，最多 64 KiB）。
  触发时脚本快照到 Run 上；之后编辑项目不影响已排队/已运行的 Run。Worker 不 Clone，
  将快照写到本次 Run 的 `temp/pipeline.lua` 后运行同一套 definition phase，因此冻结 spec 与
  `pipeline.lua:(line,col)` 诊断与 repository 来源完全一致。

不采用 YAML，不采用 JSON，也不提供可视化 Pipeline 编辑器；`inline` 仍然是一个文本编辑器。

#### 3.3.2 Pipeline 示例

```lua
pipeline {
    name = "Build",

    triggers = {
        branches = { "main", "develop" }
    },

    steps = {
        {
            name = "Restore",
            run = {
                "dotnet",
                "restore"
            }
        },

        {
            name = "Build",
            run = {
                "dotnet",
                "build",
                "-c",
                "Release"
            }
        },

        {
            name = "Test",
            run = {
                "dotnet",
                "test",
                "-c",
                "Release"
            }
        },

        {
            name = "Publish",
            run = {
                "dotnet",
                "publish",
                "-c",
                "Release"
            }
        }
    }
}
```

#### 3.3.3 Pipeline SDK

Zenkins 提供内置 Lua SDK。

建议提供以下基础 API：

| API | 功能 |
|---|---|
| `pipeline()` | 定义 Pipeline |
| `step()` | 定义执行步骤 |
| `run()` | 执行外部命令 |
| `shell()` | 显式执行 Shell 脚本 |
| `checkout()` | 检出代码 |
| `artifact()` | 发布构建产物 |
| `env()` | 读取或设置环境变量 |
| `secret()` | 读取 Secret |
| `log()` | 输出日志 |
| `fail()` | 主动终止构建 |
| `is_windows()` | 判断 Windows |
| `is_linux()` | 判断 Linux |
| `is_macos()` | 判断 macOS |

以上 API 为产品接口设计，具体 Lua 函数签名在技术设计阶段确定。

#### 3.3.4 命令执行

默认使用 Native Process 执行外部程序，而不是通过 Shell 拼接命令。

例如：

```lua
run("dotnet", {
    "build",
    "-c",
    "Release"
})
```

这样可以避免不同操作系统的 Shell 语法差异，也减少命令注入风险。

如确实需要 Shell 特有功能，可以显式调用：

```lua
shell("bash", {
    "-c",
    "echo hello"
})
```

Windows 可以使用 PowerShell 或 cmd，Linux/macOS 可以使用 Bash 或其他可用 Shell。

Shell 只是逃生口，不是主要 Pipeline 编排方式。

#### 3.3.5 Pipeline 执行规则

- Steps 默认按顺序执行。
- 前一个 Step 失败后，默认停止后续执行。
- 每个 Step 有独立的执行日志。
- 每个 Step 记录开始时间、结束时间和退出码。
- Pipeline 执行结果由所有 Step 的最终状态决定。
- 支持显式配置 `continue_on_error`。
- Step 可声明自己的 `timeout`（秒，1..86 400）与 `retry`（尝试次数，1..5，默认 1），两者都是可选的加法式字段：
  - 步骤超时与 Run 剩余时间取**较早者**：步骤自己的上限更早时，超时只杀掉这一步（含进程树），原因为 `step-timeout`，其他步骤的预算不受影响，Run 按普通失败处理（尊重 `continue_on_error`）；Run 的 deadline 更早时由 F-009 的取消生效，该步骤记为 `timed-out`，Run 记为 `timeout`。Run 日志在步骤开始前就写明生效的是哪一个上限。
  - 失败或超时的尝试按声明次数重试，**取消永不重试**；每次尝试是步骤日志中一段带分隔标记的内容，步骤记录仍只有一条（带 `Attempts` 与最终退出码），步骤列表与定义保持 1:1；重试用尽且最后一次是普通非零退出时，Run 的原因为 `step-retry-exhausted`。
  - secret 引用每步只解析一次，重试不重新取材质。
  - 两个字段都不填的步骤，行为与引入本字段前完全一致。
- 支持 Pipeline 级别和 Step 级别的环境变量。
- 支持条件执行。
- 支持 Lua 函数、循环、条件判断和变量。

第一版只支持线性 Pipeline，不支持 DAG 和并行 Step。

#### 3.3.6 Pipeline Runtime

使用 MoonSharp 作为 Lua Runtime。

要求：

- 使用纯 C# 实现，不依赖 Native Lua Runtime。
- 仅开放 Zenkins 提供的 SDK。
- 不向 Lua 脚本暴露任意 CLR 类型访问能力。
- 不允许脚本直接加载任意本地 .NET Assembly。
- 默认关闭不需要的标准库和模块加载能力。
- 对脚本执行时间和资源使用设置限制。

特别说明：Lua Runtime 的 API 限制不等于操作系统级安全沙箱。真正执行外部构建命令的 Worker 仍然具有其操作系统账户的权限，必须通过专用账户、工作目录隔离和权限控制保护宿主环境。

#### 3.3.7 多分支（Multibranch）

`Branch Discovery = branches` 的 `repository` Pipeline 项目把自己的分支当作独立作业，而不是为每个分支复制一个项目：

- **容器仍是项目行**：设置、凭据、Secrets、参数只在项目上定义一次；分支不复制设置。项目 Kind 不变，分支发现是一个设置有，不是第三种 Kind。
- **Branch Job 的键是 `(ProjectId, Branch)`**：分支名按 git ref 的大小写精确比较（`Main` 与 `main` 是两个分支），并与 Run 快照的 `GitBranch` 同一口径。Run 的快照仍是权威：没有分支行、但已有 Run 的分支（包括本功能之前的历史行）仍然出现在分支列表中。
- **发现来源**：Webhook 投递（推送里已经带有分支与 commit）在 `branches` 模式下把分支过滤器当作发现规则——过滤器的空值表示「所有分支」；非空时仍是原来的精确名/尾部通配符语法。
- **无推送的发现**：`branches` 项目按间隔通过 I-005 的 Git remote probe 读取仓库 heads（每个 repository 每 tick 一次 probe，绝不每分支一次）。新的 head 注册为分支并请求一次树读取；**成功的** refs 应答中缺失的分支被标记为 orphaned（`refs-absent`），probe 失败不注册、不改状态、也不标记 orphan。
- **Pipeline 文件的有无必须是观察到的证据**：一个分支只有在真正读取过它的树之后才被记为 `has-pipeline` 或 `no-pipeline`；状态与所读的 commit 一起保存。读取由 F-022 的 definition-only 任务族承担（不建 Run、不占构建槽位），按 `(branch, commit)` 记忆，未变化的 head 不重复读取。「没有 Run」、probe 失败或无人应答都**不能**推断为 `no-pipeline`。
- **未知不排队**：没有针对该 commit 的判定时不排 Run，而是记录 `awaiting-observation` 并请求读取；只有 `has-pipeline` 才在该 commit 上排 Run，`no-pipeline` 记录后不排。读取失败（clone/checkout 失败、无 Worker）保持 prior 判定并退避重试（指数增长、有上限）；分支行上的 pending commit 使该请求跨 Server 重启存活。
- **`no-pipeline` 不是失败**：没有 Pipeline 文件的分支只出现在分支列表里（标记为 no pipeline），不产生 Run，也不占用构建槽位。已有 `no-pipeline` 证据的分支在拿到新证据之前不会被再次入队。
- **上游删除分支不删历史**：分支行被标记为 orphaned（证据只有两种：push 的 delete 事件，或一次**成功**的 refs 应答不再列出该分支），其 Run 与日志全部保留；不存在隐式删除。
- **编号仍按项目**：F-036 的 `#N` 仍在一个项目内唯一（不改唯一索引）；分支列表只展示每个分支最近一次 Run 的编号。Retention（F-038，规划中）应以 Branch Job 为作用域。
- **手动触发**：`branches` 项目的手动触发可选择分支（默认项目的默认分支），校验与原有分支覆盖一致。

### 3.4 Worker 管理

Worker 是实际执行构建任务的独立进程。

#### 3.4.1 Worker 类型

支持两种部署方式：

**Local Worker**

与 Server 部署在同一台机器上。

适用于：

- 单机部署
- 小型项目
- 个人开发者
- 快速体验

**Remote Worker**

独立部署在其他机器上，通过网络连接 Server。

适用于：

- 多操作系统构建
- 多机器并行构建
- 大型项目
- 分布式构建环境

Local Worker 和 Remote Worker 使用相同的 Worker 实现，不维护两套执行逻辑。

#### 3.4.2 Worker 注册

Worker 首次启动时，通过注册 Token 与 Server 建立连接。

注册信息包括：

| 字段 | 说明 |
|---|---|
| Worker ID | 唯一标识 |
| Name | Worker 名称 |
| OS | Windows / Linux / macOS |
| Architecture | x64 / ARM64 等 |
| Version | Worker 版本 |
| Tags | 用户配置标签 |
| CPU Count | CPU 核心数 |
| Status | Online / Offline / Busy |

Worker 注册后，由 Server 管理其生命周期和可用状态。

#### 3.4.3 Worker 通信

采用 HTTP API + SignalR。

- Worker 主动连接 Server。
- Server 分配构建任务。
- Worker 通过 API 拉取任务数据。
- Worker 通过 SignalR 或 HTTP Streaming 上传日志。
- Worker 通过 API 报告执行状态。
- Worker 定期发送 Heartbeat。

不采用 Server 主动连接 Worker 的方式，避免要求 Worker 暴露公网端口。

#### 3.4.4 Worker Tags

每个 Worker 可以配置多个 Tags。

例如：

- `windows`
- `linux`
- `macos`
- `unity`
- `unreal`
- `dotnet`
- `ios`

Pipeline 可以通过 Tags 指定运行环境：

```lua
pipeline {
    name = "Cross Platform Build",

    steps = {
        {
            name = "Build",
            runs_on = { "windows" },
            run = { "dotnet", "publish" }
        }
    }
}
```

调度器只将任务分配给满足要求且空闲的 Worker。

#### 3.4.4.1 步骤级 Worker 要求（`runs_on`）

一个步骤可以声明它需要的 Worker 标签，写法三种：逗号串（`runs_on = "linux,arm64"`，全都要满足）、
Lua 表（`runs_on = { "linux", "arm64" }`，同上）、布尔表达式（`runs_on = "linux && (arm64 || x64)"`、
`runs_on = "!windows"`；支持 `&&`、`||`、`!` 和括号）。

- **归一化存储。** 表达式在保存时归一化为 DNF（析取范式），此后所有问题都是集合运算：某个 Worker 是否
  满足、两个要求是否矛盾、并集是什么。原始写法保留用于展示：页面读 `linux and arm64`，不读
  `arm64&&linux`。历史 run 的语义因此不会被后续语法升级重读。
- **上限是策略，不是列宽。** 一个要求最多 64 个析取项；存储形式超过 4000 字符时保存会被拒绝并说明
  原因。数据库列本身是无界文本，改列宽不影响、也不应该改变这两个数字。
- **一次运行只在一个 Worker 上执行。** 因此一个 run 的要求是它所有步骤要求的并集，两个步骤的要求若
  “永远不可能同时成立”，在**读定义时**就报 `pipeline-invalid`，并点名两个步骤与冲突的标签。
- **两层拒绝，两种情形。**（a）**构造上永不可能**（`linux && !linux`）：定义期（`pipeline-invalid`）
  或排队时（`tag-invalid`）拒绝，不建 run。（b）**此刻没有 Worker 满足**：**不拒绝**，run 排队并记下
  它等的要求；run 页说“当前没有 worker 具备：X”，run 行上保留可读要求，队列可按“排队中且带要求”
  查询。不做“排队超时自动失败”。
- **Webhook 与观测路径也留痕**：不可能的要求在 delivery 记 `refused:requirement`（仍 2xx），而不是
  回一个没人看的错误。

#### 3.4.5 Worker 并发

每个 Worker 支持配置最大并发任务数量。

默认值建议为 1。

Worker 必须能够：

- 接受任务
- 执行任务
- 上报日志
- 上报结果
- 接收取消请求
- 定期发送心跳

Worker 离线后，Server 将其标记为 Offline，不再分配新任务。

正在执行的任务标记为 Lost 或 Unknown，不能简单地假定任务已失败或自动重试。

#### 3.4.6 Worker 工作目录

每个 Build Run 使用独立工作目录。

例如：

```text
worker/
  work/
    {run-id}/
      repository/
      artifacts/
      temp/
```

不同构建任务之间不得共享可写工作目录。

构建完成后，根据配置清理临时目录。

### 3.5 Build Run 管理

每次 Pipeline 执行产生一个 Build Run。

#### Run 状态

| 状态 | 说明 |
|---|---|
| Queued | 等待调度 |
| Running | 正在执行 |
| Success | 执行成功 |
| Failed | 执行失败 |
| Canceled | 用户取消 |
| Lost | Worker 意外失联，执行结果未知 |

#### Run 信息

- Run ID
- Build Number（每个 Project 内递增的正整数，显示为 `#N`）
- Project
- Pipeline
- Git Branch
- Git Commit
- Trigger Type
- Trigger User
- Worker
- Build Parameters（触发时的值快照）
- Start Time
- End Time
- Duration
- Status

#### Build Number 与固定链接

Run ID 是 guid，人读不出来、也打不出来；因此每个 Run 另有一个**项目内**的构建号。

| 规则 | 含义 |
|---|---|
| 分配时机 | 触发写入的同一个事务内、持有项目行锁时分配（`MAX(Number) + 1`），并由 `(ProjectId, Number)` 唯一索引兜底 |
| 单调性 | 只增不减，不复用、不重排；取消或删除队列中的 Run 也不回收编号 |
| 正整数 | 数据库约束 `CHECK (Number > 0)` 保证；没有「第 0 号构建」这种东西，带外写入会被拒 |
| 显示 | 所有面向人的地方都显示 `#N`：Run 列表、Run 页、项目页最近一次 Run、Dashboard、日志下载文件名、通知、API |
| 环境变量 | Step 的环境里 `ZENKINS_BUILD_NUMBER` 是本编号，`ZENKINS_BUILD_ID` 是 guid（与既有的 `ZENKINS_RUN_ID` 相同） |
| 历史数据 | 迁移按 `(CreatedAt, Id)` 的确定性顺序一次性回填，且不可能产生重复 |

固定链接（固定链接是给人贴的地址，guid 路由继续保留，已有书签与日志票据都不受影响）：

- `/projects/{id}/runs/{number}` 渲染该 Run（唯一地址，非跳转）。
- `/projects/{id}/runs/last` 跳转到该项目最新一个 Run；没有 Run 时 404。
- `/projects/{id}/lastSuccessful` 跳转到该项目最新一个成功的 Run；没有成功过就 404（绝不拿失败的顶替）。

#### 重建与重放（Rebuild / Replay）

对**已结束**的 Run，可以一键再跑一次。两个意图必须分开，因为混在一起会悄悄换掉要构建的代码：

| 意图 | 复用什么 | 不读什么 |
|---|---|---|
| Rebuild | 原 Run 的全部输入：分支、**原 Run 钉住的 commit**、参数、必需标签、超时，以及原 Run 实际执行的定义（Freestyle 步骤改了也仍跑旧的） | 不读当前位置的分支头 |
| Replay | 只有原 Run 自己的定义文本（今天即 Freestyle 的冻结 spec） | 不读项目当前定义、也不读仓库当前内容 |

- 仓库流水线的 Run 存的是 worker 产出的冻结 spec、而不是产出它的源码，所以它对 Replay **不提供**按钮：把 spec 当定义重跑会抹掉定义阶段。按钮缺席时会说明原因。
- Rebuild 的 commit 在远端不存在时，克隆按 `git-*` 失败原因如实失败，**不会**退回去构建分支头；Run 页会写明它钉的是哪个 commit、来自 `#N`。
- 重建与重放都是新的 Run（`Trigger Type` 为 `Rebuild`/`Replay`，并记录来源 Run），所以原 Run 的 artifacts、日志与 steps 永远不被改写；新 Run 页显示「rebuilt from #N」。
- 重建走的是与新触发**同一条**插入路径：项目禁用（F-028）、队列上限（F-029）、并发上限与编号规则一律同样生效。
- 参数可改：「Rebuild with parameters…」一个表单，默认填原 Run 的值，校验仍是触发表单那套；今天的定义不再声明这些名字时直接拒绝。

#### 功能要求

- 查看所有 Build Runs。
- 按项目、分支、状态筛选。
- 查看单次 Run 详情。
- 查看每个 Step 的状态。
- 查看执行日志。
- 取消正在执行的任务。
- 下载 Artifact。
- 查看历史构建记录。

第一版不提供复杂的构建趋势分析。

#### 队列控制

每个 Project 可声明三条限制（均为可选项，默认为「不限」）：

| 限制 | 含义 | 默认 |
|---|---|---|
| Max Concurrent Runs | 同一项目最多多少个 Run 同时处于 Running（含已 claim 未 accept，占用名额直到 lease 被清） | 不限 |
| Quiet Period Seconds | 触发后需要等待多少秒 Run 才可被 claim；静默期内的触发仍是普通排队 Run，不做合并 | 0 |
| Max Queued Runs | 项目最多多少个 Run 处于 Queued；超过上限的触发在写入任何行之前被拒 | 不限 |

生效顺序（claim 与 trigger 两处一致）：静默期 → 项目并发上限 → 队列上限。

- 并发上限在 claim 的同一个事务内判定：先锁项目行，再数该项目的活跃 Run，最后执行 lease 的
  compare-and-set。两个 worker 同一瞬间抢同一个项目时不可能同时拿到第 2 个名额（B-002 在 worker
  维度的同一做法，搬到项目维度）。
- 队列上限在触发的同一个事务内判定：先锁项目行，再数排队 Run，再插入，所以两个并发触发不可能
  都通过；被拒绝时不会产生孤儿 Run 行。
- 拒绝时的文案带观测值：「queue full: 20 queued runs, cap 20」；表单上是字段级错误，API 是 409，
  Webhook 投递行记录 `queue-full` 并返回 2xx。
- 三条限制在项目设置页可见可改；三者都为空/0 时，项目行为与引入这些限制之前完全一致。

#### 定时触发的重叠与错过（F-024）

定时触发（§3.13）也走上面这套队列与闸门，所以它必须回答两个问题：到点时已经有一个 Run 在跑怎么办，以及
Server 停机期间错过的时刻怎么办。两个答案都写死在下面，并且每一次决策都留记录（`ScheduleOccurrence`
行），页面照实显示。

**重叠：默认跳过，不叠加。**

- 到点时项目里已有任意 `Queued` 或 `Running` 的 Run（含已 claim 未 accept 的），该 due time 记为
  `skipped-overlap`，并把在飞 Run 的编号一起记下；不排队。
- 计划上可以打开「Queue anyway」：那就照样排队，但仍受上面 F-029 的队列上限约束，超限记为
  `skipped-queue-full`（闸门仍然在插入之前判定，不会产生孤儿 Run）。

**错过：不补跑，只记账。**

- 一次评估最多只让「最近一个」due time 排队，而且它必须同时满足：距现在不超过
  `Schedules:MaxDueLatenessSeconds`（默认 300 秒），并且上一次评估本身也在这段时间内。两个条件任一不满足，
  就意味着这台 Server 当时根本没在评估这个计划（停机、挂起、系统睡眠、刚启动），于是窗口内**所有** due time
  一律记 `missed`，一条 Run 都不补。这就是「停机后不补跑一大片」。
- 记录本身有上限：每次评估最多写 `Schedules:MaxRecordedMisses`（默认 20）条 `missed` 行，多出来的数量折进
  最旧那一行的 `CollapsedDueTimes`，所以**总数依然精确**；枚举窗口只有最近
  `Schedules:MissWindowHours`（默认 24 小时），更早的用一条「更早的错过未列出」说明代替，不逐条枚举——停机
  一个月也只花一次有界的查询。
- 计划行上的 `LastMissedCount` 记住最近一次评估的错过数，页面上的「N 个 due time 被记为 missed」因此不会被
  记录裁剪悄悄改小；而这次评估的窗口本身被 24 小时上限截断时，同一句话里还要说明「只统计最近 N 小时、更早的
  错过未列出」——那个数字是窗口内的，不是整段停机的总量，页面不能让人误读。
- 结论：停机期间的点只留下证据，不会在启动时变成一串 Run；下一个**正常到达**的 due time 照常排队。

**禁用：** 项目被禁用（F-028）或计划本身被禁用时，due time 记 `skipped-disabled`（F-028 里那个跨触发源的
同一个词），不排队。

**一次 due time 一个 Run：** 评估时先锁计划行，再让 `(ScheduleId, DueAt)` 的唯一索引兜底，所以两台 Server
（或同一台的两个 tick）不可能各排一个 Run；输掉的那次连它 stage 的 Run 一起整笔回滚。

### 3.6 实时日志

构建日志是 CI 系统最重要的交互功能之一。

要求：

- 实时显示 Worker 输出。
- 支持 stdout 和 stderr。
- 支持按 Step 查看日志。
- 自动滚动到最新日志。
- 支持暂停自动滚动。
- 支持搜索日志内容。
- 支持下载完整日志。
- 支持构建完成后查看历史日志。

日志通过 SignalR 推送到浏览器。

Worker 负责采集进程输出，Server 负责持久化和转发。

日志必须支持较大输出量，不能将整个日志文件长期保存在 Server 内存中。

### 3.7 Artifact 管理

Artifact 是 Pipeline 执行后产生的构建产物。

Lua SDK 提供：

```lua
artifact("bin/Release")
```

支持：

- 单文件
- 文件夹
- 多个 Artifact
- 下载 Artifact
- 按 Run 查询 Artifact

第一版使用本地文件系统存储。

Artifact 以 Organization、Project、Run 组织存储路径，不能允许租户通过路径穿越访问其他租户的文件。

未来可以扩展 S3 兼容存储，但不作为第一版依赖。

### 3.8 Secrets 管理

Organization 可以维护构建所需的敏感信息。

例如：

- NuGet API Key
- Git Token
- SSH Private Key
- 云服务访问密钥
- 部署密码

要求：

- Secret 加密存储。
- Secret 不允许在 UI 中重新读取明文。
- Pipeline 通过 `secret()` API 使用。
- Secret 不应直接写入构建日志。
- Secret 按租户隔离。
- 仅 Owner 可以管理 Secret。

需要注意：日志脱敏只能作为辅助保护，不能保证任意脚本都无法泄露 Secret。拥有构建脚本修改权限的成员，原则上应被视为具有对应构建环境的执行权限。

#### 3.8.1 Secret 类型与引用语法

一个 Secret 有且只有一种**类型**（Kind），在创建时选定，之后不可更改——换类型等于换一个 Secret
（删除后重建）。类型决定哪个引用语法可以读它，也决定密文里存的是什么形状：

| 类型 | 存什么 | 引用语法 | 步骤拿到什么 |
|---|---|---|---|
| `text` | 一个字符串 | `${secret:NAME}` | 值本身 |
| `usernamePassword` | 用户名与密码，一起加密 | `${secret:NAME.username}` / `${secret:NAME.password}` | 指定的那一半，两半各自可独立解析（`docker login` 要一个进参数、一个进 stdin） |
| `file` | 一个文件的内容，上限 64 KiB | `${secretFile:NAME}` | 该 Run 自己目录内的**路径** |

规则：

- 三种类型共用同一个密文信封、同一套 Owner-only 管理与 write-only 回读规则：值只在创建/轮换时
  进入表单，之后任何页面、任何接口都不返回它。
- 引用错配是**定义期/步骤准备期的明确错误**，不是空值：对 `text` 用 `${secret:NAME.password}`、
  对配对用 `${secret:NAME}`、对文件用带 part 的引用，都会让该步骤以一条点名该引用的消息失败，
  不启动进程。旧脚本不受影响——`${secret:NAME}` 对 `text` 的语义与 F-015 完全一致。
- **文件类型写在 Run 自己的目录里**：`work/{run-id}/temp/secrets/{NAME}`，Unix 上文件 `0600`、
  目录 `0700`（创建时即设定，不做事后 chmod），随该 Run 的 temp 清理一起删除，绝不写到共享临时
  目录——那会在一台繁忙的 Worker 上让不同 Run 互相看见。
- **脱敏覆盖材料，不覆盖位置**：配对的 password 与文件的内容在取到的瞬间注册进该 Run 的脱敏
  过滤器，因此不会出现在日志、Run 页面、artifact 或任何 API 响应里；配对的 username 与文件的
  **路径**不注册——它们不是秘密，打印路径正是脚本说明自己的方式。
- 冻结 spec 的 `secretReferences`（D-006）记录**精确的引用形式**（含 `.password`、`file:NAME`），
  所以 Secrets 页的"哪些流水线在用这个 Secret"不会因为用了某一半或文件形式而说谎；记录里仍然
  只有名字与形式，没有任何值。
- Run 的 secret 交付接口对三种类型都可用，但**拒绝契约不变**：外部 Run、外部名字、别人的 Run、
  已结束的 Run 都还是一个无 body 的 404。claim 载荷只带名字（不带宽大文件的内容），Worker 需要
  什么就按名字去取。

### 3.9 Webhook 与 SCM 轮询

支持 Git 平台通过 Webhook 触发构建。

第一版优先支持通用 Webhook 格式，兼容常见 Git 服务。

支持：

- Push
- Branch 更新
- 指定 Branch 过滤
- Webhook Secret 验证
- 防止重复触发

不需要第一版就分别开发 GitHub、GitLab、Gitea 的完整集成管理模块。

#### 3.9.1 SCM 轮询（Poll SCM）

给无法把 Webhook 送到 Server 的 Git 主机（内网、没有外网出口、托管在防火墙后、或本身没有 Webhook）
一个 Jenkins 风格的替代路径：Server 自己按间隔去看一眼远端的分支头。

- **按项目开关**：默认关闭；开启后填写轮询间隔（分钟，最小 1，默认 5，上限 1440）。
- **范围**：多分支模式下轮询该项目的**全部**分支头；单分支模式只轮询默认分支。
- **变化判定**：分支头相对上一次观察到（或上一次触发）的 commit 变化时触发一次 Run，并把
  `RunClaim.GitCommit` 钉在那一个观察到的 commit 上。分支头没变则不触发。
- **首次观察不触发**：开启轮询时先记下当前的分支头，把它当作基线；否则一个五十个分支的仓库会在
  第一秒排队五十个 Run。若该 commit 已经由 Webhook 触发过，首次观察记录为 `duplicate`。
- **多个漏掉的提交合并为一个**：远端从 A 直接到 C（中间的 B 没被观察到）时只对 C 触发一次——轮询
  只回答"现在是什么"，它不重建历史。
- **去重键 `(project, branch, commit)`，两条触发路径共用**：Webhook 先到、轮询后到（或反过来）观察
  到同一个 commit 时只排一次 Run；后来的一方记录 `ignored:duplicate` / `duplicate`，不新建 Run。
  Webhook 自己的投递去重（delivery id / body hash）保持不变：同一个 commit 用两个 delivery id 推送
  仍然是两次投递。
- **探测失败不等于"没有变化"**：失败时分支头保持原值，失败原因与时间显示在项目设置页，轮询间隔按
  1× → 2× → 4× 退避，上限 30 分钟；一次成功立即清零。
- **跳过必须留痕**：项目被禁用、队列已满、并发已到上限时这一轮不触发，但跳过的原因记在项目上，
  设置页可见——不允许"什么都没发生"。
- **没有仓库或没有可用凭据**时，设置页直接说明原因（`no-repository` / `missing-credential`），而不是
  显示一个静默的"上次轮询：无"。

### 3.10 Freestyle Job（无仓库作业）

Freestyle 是 v1.1 新增的 Project Kind：不需要关联 Git Repository，构建步骤在服务端 UI 中配置，
直接执行脚本（例如运行一个 Python 脚本）。

步骤另有可选的 **Worker 要求**（`runs_on`）：与 Pipeline 的 §3.4.4.1 完全同一套解析器、同一套归一化与
同一个上限，只是字段来自步骤编辑器而不是 `pipeline.lua`。非法或自相矛盾的要求在**保存时**按步骤
路径拒绝。

#### 3.10.1 定义

一个 Freestyle Project 包含一组有序的步骤（Steps），每个步骤包含：

| 字段 | 说明 |
|---|---|
| Name | 步骤名称，必须唯一 |
| Script | 多行脚本文本（Windows 由 `cmd /d /s /c` 执行，Unix 由 `/bin/sh -e` 执行） |
| Env | 步骤级环境变量（`NAME=value`） |
| Continue On Error | 失败后是否继续执行后续步骤 |
| Artifacts | 该步骤成功后收集的产物路径（相对于运行工作区） |

#### 3.10.2 保存与冻结

- 步骤列表保存在 Project 行上（冻结的 PipelineSpec JSON，schema v2 的 `script` 步骤）。
- 保存由服务端构建并校验；浏览器提交的是编辑器草稿，从不直接生成 spec。
- 步骤脚本中引用的 Secret 名称（`${secret:NAME}`）在保存时被记录，语义与 Pipeline 一致。

#### 3.10.3 执行模型

- 触发时把当时保存的步骤快照冻结到 Run 上；之后编辑步骤不影响已排队或运行中的 Run。
- Worker 收到的 claim 携带该快照，跳过 Clone 与定义阶段，直接执行步骤。
- 步骤的工作目录是该 Run 的工作区 `work/{run-id}/workspace`（没有仓库，不使用 `repository/`）。
- Worker 将解析引用后的脚本文本写入独立文件（`temp/step-N.cmd|sh`，无 BOM），由平台 Shell 执行；
  不与其它参数做任何拼接或插值（§8 约束 6）。
- 失败语义、continue_on_error、条件、环境变量分层、Secrets、Artifact、取消与超时与 Pipeline 完全一致。
- Run 不记录 Git Commit；Branch/Pipeline Path 记录为空字符串。
- Freestyle Project 不支持 Webhook 触发，也没有 Git Credentials。

### 3.11 参数化构建（Build Parameters）

Parameters 是 Project 上的一组输入定义，两种 Kind 通用。触发时由触发者填写（或由无人工触发的
场景使用默认值），解析后的值作为运行快照冻结到 Run 上，并注入每个步骤的环境变量。

#### 3.11.1 定义

| 字段 | 说明 |
|---|---|
| Name | 参数名，同时是步骤读取的环境变量名（字母/下划线开头，字母、数字、下划线，≤64，项目内唯一） |
| Type | `string` / `text` / `boolean` / `choice` |
| Default | 无用户输入时使用的值；`boolean` 缺省为 `false`，其余类型缺省为空字符串（`choice` 缺省为第一个选项） |
| Choices | 仅 `choice`：允许值列表，默认值必须属于其中 |
| Description | 触发面板显示的说明 |

定义保存在 Project 行上（JSON），由服务端校验；一个项目最多 32 个参数。

#### 3.11.2 触发与快照

- 手动触发面板按定义动态渲染：文本框、多行文本、true/false 单选、下拉选项，并预填默认值。
- 服务端按定义解析：未知参数名拒绝（`parameter-unknown`）；类型不符拒绝（`parameter-invalid`，
  例如 boolean 非 true/false、choice 不在选项内、超长）；未提交的参数使用默认值。
- 解析后的值冻结到 Run 上；之后编辑项目定义不影响已排队或运行中的 Run。
- Webhook 等无人工触发使用定义默认值。
- 参数是配置而非 Secret：值显示在 Run 详情页、随 claim 传给 Worker，不做脱敏；敏感值应放入
  Secrets（§3.8）。

#### 3.11.2.1 更多参数类型（F-041）

F-021 的四种（`string` / `text` / `boolean` / `choice`）之上再加四种。定义仍然在项目上，值仍然在触发时
冻结到 Run 上、注入每个步骤的环境变量，旧的定义与旧的 Run JSON 都照常可读。

| 类型 | 触发时提交什么 | 步骤读到什么 | 页面/接口如何展示 |
|---|---|---|---|
| `password` | 表单里的密码框（或 API 值） | 值本身 | Run 页与所有列表显示 `***`；任何 API 不返回；日志里注册进 redactor，echo 出来是 `***` |
| `run` | 本项目最近 N 个 Run 的单选（`#N status branch`），默认预填最近一次成功 | Run 的**编号**，外加 `<NAME>_RUN_ID` 与 `<NAME>_COMMIT` | Run 页显示 `#N` 并链到那个 Run |
| `gitRef` | 自由文本 | 值本身 | Run 页按 ref 展示 |
| `file` | 随触发上传的有界文件（≤1 MiB） | **路径**（在 Run 自己的目录里） | Run 页显示 run 内相对位置，**不提供下载** |

四种新类型的共同规则：

- **`password` 不是 Secret**：它是一次 Run 的输入，不是组织状态。因此触发一次部署不需要 Owner 权限，
  而管理 Secret 需要。代价是它的生命周期与 Run 绑定：值随 Run 冻结、随 Run 结束而无意义。
- **`password` 的存储**：Run 的值映射里只存掩码 `***`，真正的值以 Data Protection（purpose
  `Zenkins.RunParameters.v1`）加密后单独存一列，只在**为执行该 Run 的 Worker 组装 claim 时**解开。因此
  Run 的 JSON 快照无法用来还原密码，数据库转储离开 key ring 也一样。
- **`password` 不允许默认值**：默认值会把明文写进项目行，正是这个类型要避免的。无人工触发（定时、
  轮询、API 不带值）解析为空字符串。
- **`run` 的默认值不是定义里的常量**：它解析为"本项目最近一次成功 Run"，定义里写死编号会随删除/新增
  Run 失效。项目还没有成功 Run 时拒绝触发（`parameter-no-run`），而不是排一个读不到编号的 Run。
- **`run` 生成的两个变量不可被同名参数覆盖**：声明 `NAME` 为 `run` 类型时，再声明 `NAME_RUN_ID` /
  `NAME_COMMIT` 会在保存时被拒绝（与 `ZENKINS_*` 保留前缀同一条"不静默覆盖"规则）。
- **`gitRef` 只做形状校验**：非空、无空白、不以 `-` 开头、≤200 字符。前两条是安全边界：ref 迟早会作为
  值进入 argv，开头的 `-` 就是把值变成选项的方式，空白就是把一个参数变成两个的方式。
- **`gitRef` 不是检出目标**：触发展示的 ref 只作为**步骤读到的值**。当触发已经钉住 commit（webhook push、
  rebuild），检出的就是那个 commit——run 的 commit 是权威，参数不是。这一点在触发表单与本文档都写明。
- **Rebuild 继承而不重问**：rebuild 直接沿用原 Run 的密码密文（表单留空 = 保持原值，填新值 = 替换），
  所以重新部署不需要重新输入密码，也不需要把密码显示出来。文件参数同理：rebuild 沿用原 Run 的文件，
  要换文件就重新触发一次。
- **`file` 的字节不进 claim**：claim 只带名字（F-040 已把这条写成契约），字节放在 Run 上以同一个
  purpose 密封，由**执行该 Run 的 Worker** 通过 run 级、worker 鉴权的投递路由按名字取。拒绝契约与
  F-040 的 secret 投递逐字对齐：外部 Run、外部名字、别人的 Run、已结束的 Run 都是**同一个无 body 的 404**。
- **`file` 落在 Run 自己的目录**：`work/{run-id}/temp/parameters/{NAME}`，Unix 上目录 `0700`、文件
  `0600`（创建时即设定）；随 Run 的 temp 清理一起删除。步骤读到的是该文件的**绝对路径**；服务端在 Run
  上记录的是 run 内相对位置（`temp/parameters/{NAME}`），页面上不提供任何下载入口——它可能是秘密。
- **取不到就在步骤前失败**：Worker 无法物化某个文件参数时，整个 Run 以 `parameter-file-unavailable`
  失败，连定义阶段都不跑——让步骤去读一个空路径只会把失败点推得离原因更远。
- **`file` 的三个闸门**：定义里不许有默认值；请求管线（端点级 body 上限）与 handler（单个文件长度）两次
  校验同一 1 MiB 上界；请求体超限是 413，handler 超限是 `parameter-file-too-large` 的 PRG 错误。

#### 3.11.3 注入

一个步骤的环境由**六层**构成，从低到高（同名时后者胜）：

| 顺序 | 层 | 来源 | 同名冲突时的行为 |
|---|---|---|---|
| 0 | Worker 自身进程环境 | Worker 启动时的环境 | 最低层，可被其上层任何一层覆盖 |
| 1 | `ZENKINS_*` 运行元数据 | 服务端（claim + 运行时补充的 `ZENKINS_STEP_NAME`） | 保留前缀，任何层都不可覆盖 |
| 2 | 组织环境变量 | 触发时快照到 Run（§3.1.1、F-034） | 被 3/4/5 覆盖 |
| 3 | 构建参数值 | 触发时解析并快照（§3.11.2、F-021） | 被 4/5 覆盖 |
| 4 | Pipeline 级 `env` | `pipeline.lua` | 被 5 覆盖 |
| 5 | Step 级 `env` | `pipeline.lua` / Freestyle 步骤 | 最高层 |

- 层 2 与 3 在服务端合并为**一张** name→value 表后随 claim 下发（参数覆盖同名组织变量），Worker 只做注入，
  不自行比较两个服务端层 - 否则顺序就存在两份，会各自漂移。Run 行上两层分别存一份快照：页面上能说清
  某个值来自哪一层，Rebuild 只重解析参数那份。
- **保留名前缀不可被声明层覆盖**：组织变量与构建参数在保存时直接拒绝 `ZENKINS_*`；Pipeline/Step 的
  `env` 无法保存时校验，因此由合成处丢弃该名并在 Run 日志里写一行诊断（不静默丢弃，也不静默让它赢）。
- 层 2 在触发时冻结，与分支、spec、参数值一样；之后编辑或删除组织变量不影响该 Run，Run 详情页展示
  它当时使用的组织层（含值 - 它是配置而非 Secret）。
- 层叠顺序在 Worker 的**唯一**一个合成函数（`StepEnvironment.Compose`）里实现；`${env:NAME}` 是延迟引用，
  在步骤边界对合成后的同一张表求值，因此脚本与子进程不会对“哪一层胜”产生分歧。
- 参数值与组织变量都是配置而非 Secret：随 claim 传给 Worker，在 Run 详情页可见，不脱敏；敏感值应放入
  Secrets（§3.8）。**F-041 的 `password` 类型是这条规则的唯一例外**：它是每 Run 的输入而非组织状态，
  因此不入 Secrets，但 Run 页显示 `***`、日志脱敏、API 不返回（§3.11.2.1）。

### 3.12 组织 API（API v1）

为一个组织提供机器可用的 HTTP 接口：脚本或其他 CI 不需要浏览器即可触发、读取、跟随日志和取消
构建。API 与页面共用同一个 RunService 与同一套校验/闸门，不产生第二条触发路径。

#### 3.12.1 令牌与作用域

| 字段 | 说明 |
|---|---|
| Label | Owner 起的名字，组织内唯一；只用于列表与归因 |
| Scopes | `trigger` / `read` / `cancel` 的任意组合，逐路由校验 |
| TokenHash | 明文（`zapi_…`）仅在创建时显示一次，库中只存 SHA-256 |
| LastUsedAt | 最近一次成功认证（最多每分钟写一次） |
| ExpiresAt / RevokedAt | 过期或吊销后一律 401；吊销即时生效 |

- 仅 Owner 可创建、查看、吊销令牌；Member 在页面上看不到任何令牌标签，端点返回 403。
- 使用 `Authorization: Bearer <token>`；**不提供** query string 形式的令牌（会进入访问日志与
  代理日志）。Cookie 不能授权 API 调用，令牌也不能授权页面。

#### 3.12.2 路由

| 方法 | 路径 | 作用域 |
|---|---|---|
| POST | `/api/v1/projects/{id}/runs` | `trigger` |
| GET | `/api/v1/projects` | `read` |
| GET | `/api/v1/projects/{id}/runs` | `read` |
| GET | `/api/v1/runs/{id}` | `read` |
| GET | `/api/v1/runs/{id}/log?from=<seq>` | `read` |
| POST | `/api/v1/runs/{id}/cancel` | `cancel` |

触发返回 201（响应体即本次 run 资源，`id` 即 run id）；请求体接受 branch、requiredTags、
timeoutMinutes、parameters，校验规则与错误码与表单完全一致。可选 `Idempotency-Key` 使重试只排
一个 run（第二次返回 200 与同一 run，带 `Idempotency-Replayed: true`）。日志接口返回
`text/plain`，`from` 表示“已有到该序号为止的行”，`X-Zenkins-Last-Seq` 给出下次应使用的偏移。

#### 3.12.3 拒绝形状

| 状态 | 场景 |
|---|---|
| 401 | 无凭证、未知、已吊销、已过期令牌；实体为空，不泄露租户 |
| 403 | 令牌缺少该路由的作用域（在任何查询之前） |
| 404 | 项目/run 不属于令牌所在组织（与不存在同一答案，无存在性预言机） |
| 429 | 该令牌每分钟预算耗尽，带 `Retry-After`；在任何资源查询之前判定 |
| 400 | 请求格式或字段错误，`error` 使用与表单相同的错误码 |
| 409 | 项目自身拒绝触发：`project-disabled` / `queue-full` |

#### 3.12.4 范围与非目标

- 响应不包含参数值、Secret、凭证与任何 artifact 内容；run 编号由 F-036 提供，本期不发明编号。
- 不提供 GraphQL、SDK、CLI，也不提供 query string 令牌。

### 3.13 定时触发（Scheduled Triggers）

每个 Project 可以带若干条 cron 计划（上限 20 条），到点由 Server 排队一个 Run。这是把 PRD 原先的
「定时任务」非目标（§7.2）转成 v1.0 范围内的决定，卡号 F-024，日期 2026-11-16。

#### 3.13.1 定义

| 字段 | 说明 |
|---|---|
| Cron | 5 段表达式 `分钟 时 日 月 周`；Vixie 语义——日与周都被限定（都不以 `*` 开头）时取「或」，否则两者都必须命中。支持区间、列表、步长、`5/10` 这种简写，以及月份/星期英文名；`0` 与 `7` 都是周日 |
| TimeZone | IANA id（`Europe/Berlin`、`Asia/Tokyo`、`UTC`），由运行环境解析，夏令时规则不冻结在行里 |
| Enabled | 关闭的计划照样被评估，但每个 due time 记 `skipped-disabled`，不排队 |
| Queue anyway | 到点时有在飞 Run 是否照样排队（默认否，见 §3.5 的重叠策略） |

明确不支持：秒段、`@daily` 一类宏、`?`/`L`/`W`、Jenkins 的 `H` 哈希。保存时这几类都会被字段级拒绝，而不是
「大致支持」。时区不存在、表达式永不触发（例如 2 月 30 日）同样在保存时拒绝。

#### 3.13.2 语义

- 触发类型是 `Schedule`，**没有触发用户**（没有人登录），参数取定义的默认值（F-021），分支取项目默认分支
  （Freestyle 项目跑项目冻结的步骤快照）。
- 与手动触发、Webhook 投递走**同一条插入路径**、同一处项目闸门、同一套编号（F-036）：禁用/队列上限/校验
  一律同样生效，不存在「定时专用」的捷径。
- 夏令时前跳导致不存在的本地时刻（例如 `Europe/Berlin` 2026-03-29 02:30）记 `skipped-nonexistent`，不排队，
  也不挪到别的时刻；后跳导致重复的本地时刻只触发一次，取较早的那个瞬间。
- 重叠与错过的策略见 §3.5。

#### 3.13.3 页面与记录

项目设置页的 Schedules 卡片：列出每条计划的表达式、时区、开关、下次三次触发时间（同时给出 UTC 与该时区的
本地时间）和最近 10 条结果；支持新增、启用/停用、切换 Queue anyway、删除。新增表单提供「预览接下来三次」
按钮——先算给你看，再决定保存，预览本身不写任何行。

每条结果都是 `ScheduleOccurrence` 的一行：`queued`、`skipped-overlap`、`skipped-disabled`、
`skipped-queue-full`、`skipped-refused`、`missed`、`skipped-nonexistent` 之一，带 due time、Run 编号和
折叠数量；每条计划保留最近 20 行。

计划存在表里，不在 `pipeline.lua` 的 `triggers{}` 块里：Freestyle 项目没有仓库文件，时钟也在 Server 侧。
未来 `triggers` 的 cron 块可以在 freeze 时物化成行，但不在 F-024。

---

### 3.14 上下游作业（Upstream / Downstream）

一个项目可以声明“X 成功后跑我”（upstream）与“我成功后跑 Y”（downstream），让 build → test → deploy
这类链条不需要外部编排，且**环永远不能自转**。

- **链路是一等数据**（`ProjectLink`）：上游项目、下游项目、“仅成功时触发”、分支策略。同一对项目只能有一条
  同向边；删项目时两个方向的边一起消失（外键级联），不会留下悬空触发。
- **分支策略**：`sameBranch`（默认，跟随上游构建的分支，配合多分支）或 `defaultBranch`（下游构建自己的默认
  分支）。**下游构建不了这个分支时记为 `skipped-branch`，不是失败**——链做了它被告知要做的事，只是那个分支
  在这个项目里不存在。
- **两级环保护**：
  1. **保存时图检查**：新边若让上游从下游可达，直接拒绝，并在消息里给出会闭合的路径（`A -> B -> C -> A`）。
     自连同样拒绝。
  2. **运行期跳数上限（20）**：只在分支策略下自转的链（A main → B main → A main）在图上没有环，图检查看不见；
     跳数上限在第 21 跳停下，并在上游 run 上记 `skipped-hop-limit`。两条各自有独立的事实与变异。
- **正好一次，且服务器死机不丢**：上游 Run 到达终态时，为每条匹配的下游链路写一行 **outbox**，与终态状态在
  **同一个事务**里提交；提交后由派发器（后台服务）结算。行在被真正结算前一直是 `pending`，所以“排了下游”与
  “标记已结算”之间崩掉会重派，而不是丢失。
- **拒绝是结果，不是沉默**：派发器把每条链路的结局写回那一行——`queued` / `skipped-upstream-failed` /
  `skipped-branch` / `skipped-disabled` / `skipped-hop-limit` / `skipped-missing` / `failed-trigger`，附一句
  可渲染的原因。永久性拒绝（禁用、项目或链路已删、凭据缺失、下游无效、跳数上限）首趟即结算，**不无限重试**；
  只有队列类拒绝退避重试，上限 5 次后结算为 `failed-trigger`。
- **下游 Run 是普通 Run**：同一个 F-028/F-029 闸门、同一套编号、同一个 Worker 契约；额外记
  `TriggerType = Upstream`、触发它的 run id 与跳数。参数取定义默认值（与定时触发同一条解析路径）。
- **`UPSTREAM_*` 环境变量**：下游步骤额外读到 `UPSTREAM_PROJECT`、`UPSTREAM_RUN_ID`、`UPSTREAM_STATUS`、
  `UPSTREAM_BRANCH`、`UPSTREAM_COMMIT`（在上游 Run 的终态上读取，不落到下游行里——上游被删就没有这些变量，
  而不是编造值）。

## 4. UI 设计

### 4.1 设计原则

- 使用 Blazor Server。
- UI 组件使用 Blazor Blueprint。
- 简洁、现代、信息密度适中。
- 以开发者工具的视觉风格为主。
- 不追求复杂的动画和视觉效果。
- 优先保证构建状态、日志、任务调度信息的可读性。

### 4.2 页面结构

#### Organization

- Organization 切换
- 成员管理
- Organization 设置
- Worker 管理
- Secrets 管理

#### Dashboard

展示：

- 最近构建
- 正在运行的任务
- 排队中的任务
- Worker 在线状态
- 最近失败的构建

#### Projects

项目列表展示：

- 项目名称
- Kind（Pipeline / Freestyle 徽标）
- Git Repository（Freestyle 显示"无仓库，步骤配置于服务端"）
- 默认分支（仅 Pipeline）
- 最近一次构建状态
- 最近构建时间

#### Project Detail

包含：

- Overview（Freestyle 不展示仓库信息，展示步骤保存状态；触发面板按参数定义动态渲染）
- Pipelines（Freestyle 下该 Tab 为 Steps：服务端步骤编辑器）
- Parameters（参数定义编辑器，两种 Kind 通用）
- Runs
- Settings（Freestyle 不含 Git Credentials 与 Webhook 区块）

#### Pipeline

展示：

- Pipeline 名称
- Pipeline 文件路径
- 默认分支
- 最近执行记录
- 手动触发按钮

不提供图形化 Pipeline 编辑器。

Pipeline 内容由 Git Repository 中的 `pipeline.lua` 管理。Freestyle 项目的同一 Tab 是步骤编辑器
（§3.10）：每个步骤为一段多行脚本，支持步骤级环境变量、continue_on_error 与 artifact 声明。

#### Build Run Detail

展示：

- Run 状态
- Git Commit
- Branch
- Worker
- Build Parameters（本次 Run 的值快照）
- 执行时间
- Step 列表
- 实时日志
- Artifact

#### Workers

展示：

- Worker 名称
- 操作系统
- 架构
- Tags
- 在线状态
- 当前运行任务
- 最大并发数
- 最后心跳时间

---

## 5. 非功能需求

### 5.1 跨平台

Server 和 Worker 均支持：

- Windows x64 / ARM64（按 .NET 支持情况）
- Linux x64 / ARM64
- macOS x64 / ARM64

具体平台和架构以所选 .NET 版本的官方支持矩阵为准。

Worker 必须能够直接执行所在操作系统的原生程序。

不要求三种平台安装相同的编译器和构建工具。

### 5.2 部署

支持两种发布模式：

**Framework-dependent**

用户预先安装对应 .NET Runtime。

**Self-contained**

将 .NET Runtime 一并发布，降低部署门槛。

支持：

- Windows Service
- Linux systemd
- macOS launchd
- 命令行运行

第一版不要求提供 Docker 镜像。

### 5.3 性能

设计目标：

- Server 空闲时资源占用较低。
- 不运行构建任务时，不应持续占用大量 CPU。
- 实时日志不应造成明显的内存增长。
- Worker 可以并行执行多个构建任务。
- 数据库操作应避免长时间锁定。
- 长时间构建不应依赖 HTTP 请求保持连接。

具体性能指标在实现阶段通过基准测试确定。

### 5.4 可靠性

- Server 重启后能够恢复未完成的 Run 状态。
- Worker 意外断开时能够检测。
- 构建任务取消必须能够传递到 Worker。
- Worker 重启后能够重新注册。
- 构建日志必须持久化。
- 任务状态更新必须具备幂等性。
- 避免同一个 Run 被多个 Worker 同时执行。

### 5.5 安全性

- 所有 Worker 通信必须经过身份认证。
- Worker 注册 Token 必须支持撤销和轮换。
- Webhook 必须验证签名或 Secret。
- Git Credentials 和 Secrets 必须加密。
- 租户数据必须严格隔离。
- Lua Runtime 不开放任意 CLR 访问。
- Worker 使用独立工作目录。
- 构建任务必须支持取消和超时。
- 不允许通过用户输入构造不安全的 Shell 命令。

**安全边界：** Worker 执行用户提供的构建脚本，本质上是在执行不可信代码。第一版采用可信租户、可信 Worker 管理员的部署模型，不承诺对恶意构建脚本提供操作系统级沙箱隔离。不要将来自不可信租户的任务分配到承载敏感数据的共享 Worker 上。

---

## 6. 数据模型

建议的核心实体：

| Entity | 说明 |
|---|---|
| User | 用户 |
| Organization | 租户 |
| OrganizationMember | 租户成员 |
| Worker | Worker 节点 |
| WorkerToken | Worker 注册凭证 |
| Project | Git 项目 |
| GitCredential | Git 认证信息 |
| Pipeline | Pipeline 元数据 |
| BuildRun | 构建任务 |
| BuildStep | Step 执行记录 |
| BuildLog | 构建日志 |
| Artifact | 构建产物 |
| Secret | 敏感信息 |
| Webhook | Webhook 配置 |

主要关系：

```text
User
  |
  +-- OrganizationMember
          |
          +-- Organization
                  |
                  +-- Project
                  |     |
                  |     +-- Pipeline
                  |     |
                  |     +-- BuildRun
                  |             |
                  |             +-- BuildStep
                  |             +-- BuildLog
                  |             +-- Artifact
                  |
                  +-- Worker
                  +-- Secret
                  +-- GitCredential
                  +-- Webhook
```

业务实体统一包含 OrganizationId（或 TenantId）。

BuildRun 必须记录执行时的 Git Commit 和 Pipeline 版本信息，以便追溯历史构建。

---

## 7. 第一版范围与非目标

> v1.1 在本节 MVP 范围之外新增了 Freestyle Job（§3.10）与参数化构建（§3.11）。

### 7.1 第一版必须实现（MVP）

#### 基础设施

- [ ] ASP.NET Core Server
- [ ] Blazor Server UI
- [ ] Blazor Blueprint
- [ ] EF Core
- [ ] SQLite
- [ ] PostgreSQL 支持
- [ ] Windows / Linux / macOS 部署

#### 多租户

- [ ] 用户注册与登录
- [ ] Organization 管理
- [ ] 成员管理
- [ ] Owner / Member 权限
- [ ] 租户数据隔离

#### Git 与 Pipeline

- [ ] Git Repository 管理
- [ ] HTTPS / SSH 认证
- [ ] Git Clone / Fetch / Checkout
- [ ] pipeline.lua
- [ ] MoonSharp Runtime
- [ ] 基础 Lua SDK
- [ ] 线性 Pipeline
- [ ] Native Process 执行

#### Worker

- [ ] Local Worker
- [ ] Remote Worker
- [ ] Worker 注册
- [ ] Worker 心跳
- [ ] Worker Tags
- [ ] Worker 并发配置
- [ ] Worker 离线检测
- [ ] 任务取消

#### 构建管理

- [ ] 手动触发
- [ ] Webhook 触发
- [ ] 定时触发（cron 计划，§3.13；2026-11-16 由 F-024 从 §7.2 的非目标移入）
- [ ] Build Run 管理
- [ ] 实时日志
- [ ] 构建历史
- [ ] Artifact
- [ ] Secrets

#### UI

- [ ] Dashboard
- [ ] Organization
- [ ] Projects
- [ ] Pipeline
- [ ] Build Run Detail
- [ ] Workers
- [ ] Secrets

### 7.2 第一版明确不做

以下功能不纳入 MVP：

- DAG Pipeline
- 并行 Step
- Pipeline 可视化编辑器（`inline` 来源只提供 Lua 文本编辑器，不是可视化编辑器）
- 插件系统
- Docker 集成
- Kubernetes 集成
- 自动扩缩容
- 分布式构建缓存
- Matrix Build
- 复杂 RBAC
- 企业级 SSO
- 复杂部署审批流程
- 蓝绿部署
- 金丝雀发布
- 代码质量分析
- 内置代码仓库托管
- 内置 Package Registry
- 构建市场
- 第三方扩展市场

这些功能不是产品核心，不应该影响第一版交付。

变更记录：「定时任务」已于 **2026-11-16** 由 **F-024** 移出本清单——它不再是明确不做的事，而是 §3.13
定义、已经交付的定时触发（每个 Project 一条或多条 cron 计划，重叠与错过策略见 §3.5）。

---

## 8. 关键设计约束

以下约束应贯穿整个开发过程：

1. Server 不直接执行构建任务，所有构建均由 Worker 执行。
2. Local Worker 与 Remote Worker 使用同一套实现。
3. Worker 必须主动连接 Server。
4. Pipeline 使用 Lua，不使用 YAML。
5. Lua 只负责流程编排，不替代原生进程执行。
6. 通过 ProcessStartInfo 启动外部进程，不在进程参数中拼接 Shell 命令。Pipeline（Lua）的步骤
   保持纯 argv，任何地方不存在 Shell 解释；Freestyle 的脚本步骤是配置者显式提供的 Shell 文本，
   Worker 将其写入独立文件后交给平台 Shell 执行，不与其它参数做任何拼接或插值。
7. 所有构建任务必须具有独立工作目录。
8. 多租户隔离必须在数据访问层强制实施。
9. 不引入 Docker 作为基础设施依赖。
10. 不为了未来可能存在的需求提前引入复杂架构。
11. 优先使用 .NET 内置组件和成熟的小型依赖。
12. 所有功能必须能够在 Windows、Linux、macOS 的目标环境下验证。

---

## 9. 开发阶段建议

建议按以下顺序开发。

### Phase 1：基础框架

- 创建 ASP.NET Core 项目。
- 创建 Blazor Server UI。
- 集成 Blazor Blueprint。
- 配置 EF Core。
- 建立 Organization、User、Project 等核心模型。
- 完成基本登录和租户隔离。

**交付结果：** 可以部署、登录、创建 Organization 和 Project。

### Phase 2：Worker Runtime

- 创建独立 Worker 项目。
- 实现 Worker 注册。
- 实现 Heartbeat。
- 实现 Server 与 Worker 通信。
- 实现任务队列。
- 实现 Process 执行。
- 实现实时日志。
- 实现任务取消。

**交付结果：** Server 可以将任务分配给本机和远程 Worker。

### Phase 3：Lua Pipeline

- 集成 MoonSharp。
- 实现 Lua SDK。
- 实现 Pipeline 定义。
- 实现 Step 顺序执行。
- 实现 Native Process 调用。
- 实现失败处理。
- 实现环境变量与 Secrets。
- 实现 Artifact。

**交付结果：** 用户可以通过 Git Repository 中的 `pipeline.lua` 完成实际构建。

### Phase 4：Git 与 Webhook

- Git Clone / Fetch / Checkout。
- HTTPS / SSH Credentials。
- Webhook。
- Branch Filter。
- 手动触发。
- Git Commit 追踪。

**交付结果：** 代码提交后能够自动触发构建。

### Phase 5：管理 UI

- Dashboard。
- Project 页面。
- Pipeline 页面。
- Build Run 页面。
- Worker 页面。
- Secrets 页面。
- 构建日志交互优化。

**交付结果：** 完整的基础 CI Web UI。

### Phase 6：稳定性与发布

- 多 Worker 并发测试。
- Worker 断线恢复测试。
- Server 重启恢复测试。
- 多租户隔离测试。
- Windows / Linux / macOS 集成测试。
- Self-contained 发布。
- 文档和安装指南。

**交付结果：** Zenkins v1.0。

---

## 10. v1.0 验收标准

Zenkins v1.0 至少需要满足以下场景：

**场景一：单机构建**

用户部署 Zenkins Server，创建 Organization、Project，配置 Git Repository，使用 Local Worker 执行 `pipeline.lua`，成功完成构建并查看日志、下载 Artifact。

**场景二：跨平台构建**

用户部署一个 Server，并分别注册 Windows、Linux、macOS Worker。Pipeline 根据 Worker Tags 将任务分配到指定平台执行。

**场景三：多 Worker 并行**

多个 Worker 同时在线，Server 能够正确调度不同构建任务，不重复分配任务，并正确记录执行状态。

**场景四：Git 自动触发**

开发者向指定 Branch Push 代码，Git Webhook 自动触发 Pipeline，构建完成后可以在 UI 中查看结果。

**场景五：多租户隔离**

两个 Organization 可以在同一个 Server 上独立创建项目、Worker、Secrets 和构建记录。任何一个租户都不能访问另一个租户的数据。

**场景六：异常恢复**

Worker 在构建过程中意外退出，Server 能够检测异常、更新 Run 状态，并允许用户重新触发构建。

**场景七：跨平台部署**

Server 和 Worker 均能够在 Windows、Linux、macOS 上独立部署运行，不依赖 Docker。

---

## 11. 产品愿景

Zenkins 不打算成为另一个 Jenkins。

它的目标是：

- 比 Jenkins 简单。
- 比传统 CI 系统轻量。
- 比自写 Shell 脚本更容易管理。
- 比依赖 Docker 的构建平台更贴近原生开发环境。
- 对 .NET、游戏和跨平台开发团队更加友好。

**Zenkins — CI, without the complexity.**
