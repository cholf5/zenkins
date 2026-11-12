# Zenkins — Product Requirements Document

**版本：** 1.0
**项目名称：** Zenkins
**产品类型：** Self-hosted CI Server
**技术栈：** C# / .NET / Blazor Server / Lua
**目标平台：** Windows / Linux / macOS
**开源协议：** TBD（建议 MIT 或 Apache-2.0）

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

用户可以加入多个 Organization，并在不同 Organization 之间切换。

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

Project 代表一个 Git Repository。

每个 Project 包含：

| 字段 | 说明 |
|---|---|
| Name | 项目名称 |
| Repository URL | Git 仓库地址 |
| Default Branch | 默认分支 |
| Credentials | Git 认证信息 |
| Pipeline Path | Pipeline 文件路径，默认 `pipeline.lua` |
| Created At | 创建时间 |
| Updated At | 更新时间 |

#### 功能要求

- 创建、编辑、删除 Project。
- 支持 HTTPS 和 SSH Git Repository。
- 支持 Private Repository。
- 支持配置 Git Credentials。
- 支持指定默认分支。
- 支持指定 Pipeline 文件路径。
- 支持手动触发构建。
- 支持 Git Webhook 触发构建。

Git 操作由 Worker 执行，Server 不负责在本地 Clone 项目。

第一版使用系统 Git CLI，避免引入额外 Git Runtime，也便于兼容 Git LFS、Submodule 等常见能力。

### 3.3 Pipeline

Pipeline 是 Zenkins 的核心功能。

采用 Lua 作为 Pipeline 定义语言，使用 MoonSharp 在 Worker 中执行。

#### 3.3.1 Pipeline 文件

默认文件名：

`pipeline.lua`

Pipeline 文件存放在 Git Repository 中，跟随代码一起进行版本管理。

不采用 YAML，不采用 JSON，也不提供可视化 Pipeline 编辑器。

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
- Project
- Pipeline
- Git Branch
- Git Commit
- Trigger Type
- Trigger User
- Worker
- Start Time
- End Time
- Duration
- Status

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

### 3.9 Webhook

支持 Git 平台通过 Webhook 触发构建。

第一版优先支持通用 Webhook 格式，兼容常见 Git 服务。

支持：

- Push
- Branch 更新
- 指定 Branch 过滤
- Webhook Secret 验证
- 防止重复触发

不需要第一版就分别开发 GitHub、GitLab、Gitea 的完整集成管理模块。

---

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
- Git Repository
- 默认分支
- 最近一次构建状态
- 最近构建时间

#### Project Detail

包含：

- Overview
- Pipelines
- Runs
- Settings

#### Pipeline

展示：

- Pipeline 名称
- Pipeline 文件路径
- 默认分支
- 最近执行记录
- 手动触发按钮

不提供图形化 Pipeline 编辑器。

Pipeline 内容由 Git Repository 中的 `pipeline.lua` 管理。

#### Build Run Detail

展示：

- Run 状态
- Git Commit
- Branch
- Worker
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
- Pipeline 可视化编辑器
- 插件系统
- Docker 集成
- Kubernetes 集成
- 自动扩缩容
- 分布式构建缓存
- Matrix Build
- 定时任务
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

---

## 8. 关键设计约束

以下约束应贯穿整个开发过程：

1. Server 不直接执行构建任务，所有构建均由 Worker 执行。
2. Local Worker 与 Remote Worker 使用同一套实现。
3. Worker 必须主动连接 Server。
4. Pipeline 使用 Lua，不使用 YAML。
5. Lua 只负责流程编排，不替代原生进程执行。
6. 默认通过 ProcessStartInfo 启动外部进程，不拼接 Shell 命令。
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
