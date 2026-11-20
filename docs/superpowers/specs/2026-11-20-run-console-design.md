# 运行日志控制台（Jenkins 形状）设计

- 日期：2026-11-20
- 状态：**待批准**。未批准前不写产品代码。
- 范围：运行详情页、日志查看器、Worker 进程输出、Windows 脚本执行
- 计划卡片：F-047（运行控制台与实时状态）、B-016（Worker 输出编码）；编号在开工前写入看板

## 1. 要解决的问题

一次真实的 Freestyle 运行暴露了三件事，而它们其实是三个不同层的问题：

1. **日志的所有行挤成一行。** `RunLogPanel.razor` 给每一行包了一个 `<span>`，span 默认是行内元素；
   Razor 又不会在两个 span 之间稳定保留换行空白，于是 `<pre>` 里没有任何换行符。看起来像“一行
   会折行的长字符串”，每个步骤的输出都粘在前一个步骤后面。这是渲染 bug，不是审美问题。
2. **状态不刷新。** `RunDetail.razor` 的 `OnInitializedAsync` 只读一次 run；之后状态、步骤、耗时、
   失败原因都不会再变。日志本身走 SignalR 会进来，但面板不会自动滚动到底部，所以看起来也像“卡住”。
3. **日志不是主界面。** 日志卡片放在 `max-w-3xl` 窄栏的最下面、所有卡片之后，只有 384px 高，
   没有整页阅读、自动跟随、时间戳、步骤联动。排障时不可用。

另外，Worker 是 Windows + `cmd`：`.cmd` 文件按 UTF-8 写入，但 `cmd.exe` 按系统代码页读；子进程
（`svn`、`git`、本地化工具）的输出编码也各不相同。中文因此两头都可能坏。

## 2. 目标与成功标准

一个人盯着一个 15 分钟的构建：

- 打开整页 Console，日志是主界面，占满可用高度；
- 新行到达时自动滚到底部；往上翻去读旧行时自动停止跟随，并出现“N 行新输出 / 跳到最新”；
- 每个步骤的输出保持真正的换行；stderr 有颜色但不只靠颜色；
- 状态徽标、运行耗时、步骤状态在运行中持续变化，结束后停止刷新；
- 点步骤标题或右侧步骤列表，日志过滤到该步骤；
- 搜索、下载、暂停/恢复都还在；
- 中文（以及任意 UTF-8 输出）按原样显示；Windows `cmd` 脚本里的中文也不再乱码；
- 375px 宽的手机上仍然能读、能跟随、能跳到最新。

## 3. 非目标 / 反目标

非目标：

- 不做 ANSI 颜色渲染、日志折叠、日志分页、按行分享链接；
- 不改日志的存储结构、Wire 格式（`LogLineMessage`/`BuildLog`）、API v1 的 tail 契约；
- 不新增“运行状态”SignalR Hub；
- 不复制 Jenkins 的皮肤，只复制它看日志的交互；
- 不修复历史 run 里已经存坏的中文；只保证新 run 正确（诚实标注）。

反目标（即使技术上能跑也算失败）：

- 查看器把 Worker 或 Server 拖慢；日志流为慢速浏览器做服务端缓冲；
- 第二个日志数据源与下载内容不一致；
- 为了“实时”在 run 结束后继续每秒打数据库；
- 为了编码正确而静默丢行或改写文本。

## 4. 约束

- Server 是 Blazor Server；浏览器日志面板用服务端 `HubConnection` + 订阅票据（F-008 的既有机制）。
- 日志先落库再广播；查看器只读已持久化的行。
- 所有界面文案都走 `L10n[...]`，`Translations.zh-Hans.json` 双向校验；新增/删除文案两边一起改。
- `bb:` 是预构建并已 purge 的 Tailwind，不能随手写；新布局用 `app.css` 的 `.zk-*`。
- Worker 的 NativeAOT 约束（I-004）：新增依赖必须 AOT 安全。
- CI 三平台（ubuntu/windows/macos）；编码修复需要 Windows 事实，不能只在 macOS 上“看起来对”。
- AGENTS.md 的隐私规则：设计、卡片、证据里不出现真实机器名、路径、用户名、Run ID。

## 5. 方案总览

新增一个独立整页 Console 路由，运行页只保留摘要和一个入口；日志面板重写为“整页控制台”的
主体。运行页与 Console 页共用一个轻量的运行状态轮询。

```text
/runs/{id}/console
/projects/{projectId}/runs/{number}/console?step={stepId}

┌────────────────────────────────────────────────────────────────────┐
│ ← 运行 #12   [Running]   项目名    Worker: windows-worker   00:15:32 │
│ Console Output · 全量日志                          [运行页] [所有运行] │
├────────────────────────────────────────────────────────────────────┤
│ [暂停跟随] 跟随中 · 3 行新输出   [搜索________] 3/17 ↑ ↓   [步骤 ▾] │
│ [时间戳] [自动换行]                                     [下载完整日志] │
├──────────────────────────────────────────────┬─────────────────────┤
│ 1. 更新 SVN  Success 1.8s          ← 可点击过滤 │ 步骤                │
│ zenkins: step 1/3 ... started                  │ 1 更新 SVN   Success │
│ ...                                            │ 2 更新 GIT   Success │
│ 2. 更新 GIT  Success 1.1s                      │ 3 重启服务   Running │
│ ...                                            │     00:12:41        │
│ 3. 重启服务  Running                           │                     │
│ ...                                            │                     │
│ 中文输出按原样显示                              │                     │
│                                                │                     │
│                                      [跳到最新 ↓]                    │
└──────────────────────────────────────────────┴─────────────────────┘
```

## 6. 详细设计

### 6.1 路由与导航

- 新增 `Components/Pages/Runs/RunConsole.razor`，两个路由：
  - `/runs/{RunId:guid}/console`
  - `/projects/{ProjectId:guid}/runs/{Number:int}/console`
- `?step=<guid>` 预选步骤过滤；`?search=` 不进入 URL，搜索保持本地状态。
- 运行页（`RunDetail.razor`）：
  - 移除内嵌的 `RunLogPanel` 卡片；
  - 头部按钮区新增 `Console Output`（主按钮/默认按钮）；
  - 步骤列表的 `View log` 改成普通链接 `/runs/{id}/console?step={stepId}`（不再做同页 fragment + 回调）；
  - 失败摘要、步骤列表、产物、参数等卡片保持。
- 运行列表（`ProjectRuns.razor`）每行增加一个 `Console` 链接。
- Console 页头提供回运行页、项目所有运行的链接；标题 `Console · #N · Project · Zenkins`。
- 两个路由沿用运行页的解析与 404 语义；订阅票据在页面解析到 run 后按当前 session 铸造。

### 6.2 页面布局

- 页面外层占满内容区宽度（不再 `max-w-3xl`）。
- 用 flex 列：页头（固定）→ 工具条（固定）→ 控制台主体（`flex:1; min-height:0; overflow:auto`）；
  右侧步骤栏在 `>= 64rem` 显示，窄屏收进工具条的步骤下拉。
- 高度用 `.zk-console-page { height: calc(100dvh - <页头+壳层> ); }`；具体值在真实浏览器/手机上量一遍，
  写进 `app.css`，不写魔法 `bb:h-[...]`。
- 控制台主体是唯一的滚动容器；页头和工具条不随日志滚走。
- 空状态：还没步骤、还没输出、选中步骤没有输出、搜索无匹配，各自一句话。

### 6.3 跟随（auto-scroll）

- 默认跟随：新批次到达后滚到底部。
- 用户往上滚 → 自动暂停跟随，工具条显示“已暂停 · N 行新输出”和“跳到最新”。
- 点“跳到最新”/“恢复” → 视图跳到最新并重新跟随。
- 状态用两个标记：`paused`（显式暂停）和 `pausedByScroll`（滚动暂停），有效暂停 = 两者之一。
  - 往上滚只设置 `pausedByScroll`；
  - 滚回底部只清除 `pausedByScroll`，不会解除显式暂停；
  - 工具条的“暂停/恢复”设置/清除 `paused`，并同时清除 `pausedByScroll`；
  - “跳到最新”/“恢复”清除两者。
- 程序化滚到底部时 JS 先置 `suppress`，避免 `scroll` 回调把刚恢复的跟随又关掉。
- 渲染只覆盖最新 2,000 行（§6.5）；更早的行不提供分页，界面明确说“更早的行请下载”，
  不假装能滚回去。
- 新增 `js/run-console.js`：
  - `afterRender(elementId, paused)`：未暂停时 `scrollTop = scrollHeight`；
  - 监听 `scroll`（节流），用 `DotNetObjectReference` 回调 `OnConsoleScroll(atBottom)`；
  - 搜索导航到某个匹配时，滚到 `[data-current-match]` 而不是底部；
  - 组件销毁时移除监听。
- 暂停时仍接收并保存行（视图冻结，服务端照常持久化），这是 D-004 的既有语义。
- 搜索/步骤过滤下，跟随作用于“当前视图的尾部”。

### 6.4 行渲染（真正的换行）

- 一行一个块级元素：`<div class="zk-console-line" data-seq="@line.Seq">`，`@key=Seq`。
- 文本用 `white-space: pre`（默认不折行，横向滚动）；工具条的“自动换行”切换
  `.zk-console-wrap { white-space: pre-wrap; overflow-wrap: anywhere; }`。
- stderr：`.zk-console-line--stderr` 红色 + 左侧色条，保证不只靠颜色区分。
- 时间戳开关：行前 `<time>`，格式 `HH:mm:ss`，标注 UTC（服务端渲染无法知道浏览器时区，不假装本地时间）。
- 步骤分隔：`RunLogViewModel.Segment` 继续负责，但渲染成可点击的 `.zk-console-step` 行：
  `1. 更新 SVN · Success · 1.8s`，点击把过滤设到该步骤。
- 行内出现的 ANSI 转义暂不解析，按原字符显示（非目标）。
- `\r\n`、`\n`、单独的 `\r` 都按行结束处理（与旧 `ReadLine` 行为一致）；超长行保持原样。

### 6.5 视图模型与内存上界

沿用 `RunLogViewModel` 的搜索/过滤/暂停语义，增加：

- **查看器保留上界 20,000 行**（PRD §3.6 不允许把整份日志长期放在 Server 内存）。
  - `Add` 按 Seq 合并后丢弃最旧的行，并计数 `DroppedFromViewer`；
  - 计划区分 `ReceivedLines`（本查看器收到过的总数）和 `RetainedLines`（当前保留数）；
  - 说明分两句：保留截断（“查看器只保留最近 20,000 行；完整日志请下载”）和渲染截断
    （D-004 的“显示最新 2,000 行”）；
  - 暂停冻结的是 Seq 上界，不是列表下标：即使旧的保留行被淘汰，`NewWhilePaused` 仍按
    “Seq > 暂停时上界”计算，并单独显示“暂停期间有旧行被淘汰”；
  - 搜索/步骤计数只覆盖保留窗口；下载仍是全量且字节不变。
  - 渲染上界保持 2,000 行（D-004），两者不是一回事。
- `Pause()`/`Resume()` 保持不变；新增“滚动暂停”只是调用它们。
- 搜索保持“缩小视图到匹配行”（D-004 的决定），浏览器自带 Ctrl+F 仍可用于纯高亮。
- 步骤过滤按 `StepId`，计数与步骤栏一致；静默步骤显示“没有输出”。

### 6.6 步骤栏

- 右侧列出主步骤与后置步骤：
  - 序号、名称、状态徽标、已记录耗时；运行中的步骤显示从 `StartedAt` 起的实时耗时；
  - 退出码、重试次数、失败原因沿用 `RunDisplay` 的文案；
  - 点击一行 → 设置步骤过滤；再次点击同一行 → 清除并回到全部。
- 窄屏：工具条的步骤下拉承担同样功能；不显示右侧栏。
- 步骤栏的数据来自页面的运行状态轮询；新步骤出现时自动补上，不重置当前过滤。

### 6.7 实时运行状态

- 新增一个可复用的小组件/助手 `RunLiveRefresher`（纯类 + 页面里的 `PeriodicTimer`）：
  - 1 秒 tick：更新“运行耗时”的显示（不读库）；
  - 每 2 秒：重新读取 `RunView`（状态、步骤、Worker、失败原因、产物状态）；
  - 状态进入终态：再读一次，然后停止读库；日志连接保持到页面关闭（迟到的行仍要显示）；
  - 页面销毁/导航离开：`IAsyncDisposable` 取消定时器；
  - 读库失败：保留最后一次好数据，只在第一次失败时 `LogWarning`，避免刷屏。
- 运行页与 Console 页都用它；运行页在终态时额外刷新一次产物。
- 读库用 in-flight 标记防止重入：上一次读没回来就跳过这一拍。
- 停止规则抽成可测的纯函数（`IsTerminal`/`ShouldPoll`），耗时格式化与“失败保留旧值”有单元测试。
- 选择轮询而不是新 Hub 的理由：不新增服务端契约，跨进程简单，Jenkins 本身就是轮询；
  日志已经是推送，状态 2 秒内可见足够。未来可以换成状态 Hub 而不改界面。
- 排队态：状态、位置、步骤栏都能看到；计时从 `StartedAt` 开始，未开始显示 `-`。

### 6.8 Worker 输出编码

目标：新 run 的中文输出（含 `cmd` 内建命令、`svn`、`git`、Python、Node）不再乱码。

1. **读取端：显式 UTF-8 + 系统代码页兜底**
   - `ProcessRunnerOptions` 增加 `OutputEncoding`（默认严格 UTF-8）和 `FallbackEncoding`
     （Windows 默认系统 OEM 代码页，Unix 为 null）。
   - `ProcessRunner` 改为直接读 stdout/stderr 的字节流，按 `\r\n`/`\n`/`\r` 切行：
     - 每行先按严格 UTF-8 解码；
     - 解码失败（`DecoderFallbackException`）时用 Fallback 解码该行；
     - 首行去掉 UTF-8 BOM；行尾 `\r` 去掉；
     - 两个流并发读取，进程退出前仍逐行回调（保持 F-007 的“流式、不是退出后一次性交付”）；
     - 未终止的超长行有 1 MiB 上限，超出时截断并写一行标记（防止病态进程吃光内存）；
     - 取消/退出路径先把两条读取任务 drain 完再返回，保持 F-007 的“管道已排空”承诺。
   - Windows 需要 `System.Text.Encoding.CodePages` + `CodePagesEncodingProvider.Instance` 注册；
     版本进 `Directory.Packages.props`，只被 Worker 引用；AOT 兼容性在实现时用现有 AOT 守卫测试确认。
   - 兜底是逐行启发式：极少数 GBK 字节串恰好是合法 UTF-8 时会误判，接受并在文档说明。

2. **Windows 脚本文件：优先按系统代码页写，必要时 UTF-8 + chcp**
   - `WriteScriptFile` 在 Windows 上先尝试把脚本编码到系统 OEM 代码页
     （`CultureInfo.CurrentCulture.TextInfo.OEMCodePage`，`EncoderFallback.ExceptionFallback`）：
     - 能无损编码（中文构建脚本的常态）→ 直接写该代码页，`cmd.exe` 原生读取，不需要 `chcp`；
     - 不能无损编码（emoji、生僻字等）→ 写 UTF-8 无 BOM，并在首行加 `@chcp 65001 >nul`。
   - 这样中文脚本走最稳的路径，Unicode 脚本仍有退路；`LogScript` 回显的仍是 C# 原文，不受影响。
   - 不改环境变量（不替用户设置 `PYTHONIOENCODING` 等）；兜底解码覆盖仍按本地代码页输出的工具。
   - Windows 事实用 CI 的 windows-latest 跑（见 §8）：
     - 一个以 OEM 代码页写的 `.cmd` 里 `echo 中文` 逐字相等；
     - 一个 UTF-8 `.cmd`（含 emoji）在 `chcp` 路径上也能逐字相等；
     - 一个直接输出 GBK 字节的子进程被正确解码；
     - UTF-8 子进程、混合换行、无末尾换行保持既有行为。

3. **历史数据**
   - 已存坏的日志不迁移、不重写；新 run 生效。卡片和 CHANGELOG 写明这一点。

### 6.9 运行页的变化

- 删除内嵌日志面板后，`RunDetailFailureSummaryTests` 里对 `id="run-log"`、`id="run-log-download"` 的
  断言改为指向 Console 入口；失败摘要本身不变。
- 步骤 `View log` 不做 enhanced navigation；普通 `<a href>` 即可（新页面没有要保留的电路状态）。
- 运行页页面标题、状态、耗时继续由轮询刷新；失败时失败摘要自动出现。

### 6.10 本地化与可访问性

- 新增文案：Console 标题、跟随/暂停、行新输出、跳到最新、时间戳、自动换行、步骤栏、保留上界说明、
  空状态；同时写入 `Translations.zh-Hans.json`。
- 工具条控件有 `aria-label`；暂停/恢复有 `aria-pressed`；新输出提示用 `role="status"`；
  stderr 行有文本/结构标记，不只颜色。
- 键盘：Tab 可达所有控件；`End` 在日志获得焦点时跳到最新（可选，实现时确认不抢输入框）。

### 6.11 重连与错误

- 沿用现有 hub 重连、票据、`Reconnect` 按钮；重连后重新 join，历史按 Seq 去重。
- 轮询失败不破坏连接；日志广播失败不影响已落库的数据。
- 日志 hub 的连接关闭/重连状态在工具条可见，不静默冻结。

## 7. 文件清单（预计）

新增：

- `src/Server/Components/Pages/Runs/RunConsole.razor`
- `src/Server/Components/Shared/RunLiveRefresher.cs`（或等价的小助手）
- `src/Server/wwwroot/js/run-console.js`
- `tests/Zenkins.Tests/RunUi/RunConsolePageTests.cs`
- `tests/Zenkins.Tests/RunUi/RunConsoleFollowTests.cs`
- `tests/Zenkins.Tests/WorkerRuntime/ProcessRunnerEncodingTests.cs`
- 看板卡片与设计文档

修改：

- `src/Server/Components/Pages/Runs/RunLogPanel.razor`（重写为整页主体，或拆成 `RunConsoleBody.razor`）
- `src/Server/Components/Shared/RunLogViewModel.cs`（保留上界、块状行、时间戳、步骤栏数据）
- `src/Server/Components/Pages/Runs/RunDetail.razor`（移除面板、加入口、轮询；删除 `_logStepFilter`/
  `FilterLogToStep`/`OnLogStepFilterChanged`/`RequestedStepId`/`StepFilterChanged` 与不再使用的票据注入）
- `src/Server/Components/Shared/RunStepList.razor`（新增 `RunId` 参数，View log → Console 链接；三个调用点同步）
- `src/Server/Components/Pages/Runs/ProjectRuns.razor`（Console 链接）
- `src/Server/wwwroot/app.css`（`.zk-console*`）
- `src/Server/Components/App.razor`（加载 `js/run-console.js`）
- `src/Server/Localization/Translations.zh-Hans.json`
- `src/Worker/ProcessRunner.cs`（字节级行读取 + 编码）
- `src/Worker/Pipelines/PipelineRunner.cs`（Windows `.cmd` 的 `chcp` 前置行）
- `Directory.Packages.props`、`src/Worker/Zenkins.Worker.csproj`（CodePages 包）
- `tests/Zenkins.Tests/RunUi/RunDetailFailureSummaryTests.cs`（`href="#run-log"`、`data-enhance-nav`）
- `tests/Zenkins.Tests/RunUi/LogPanelFirstRenderTests.cs`（组件被重写/拆分后的首帧事实）
- `tests/Zenkins.Tests/RunUi/RunLogControlsTests.cs`（新计划字段/保留说明）
- `tests/Zenkins.Tests/RunUi/RunUiHost.cs`（新路由）
- `tests/Zenkins.Tests/Milestone/MilestoneHappyPathTests.cs`（`id="run-log-download"` 改为 Console 入口）
- `tests/Zenkins.Tests/TestAssets/ArgvEcho/Program.cs`（原始字节输出模式）
- `docs/kanban/issues/d-004-run-log-viewer.md`（记录 F-047 取代内嵌面板/fragment 导航）

不修改：

- `RunEndpoints` 的下载字节行为；`BuildLog`/`LogLineMessage` 结构；API v1；运行状态机。

## 8. 测试与验收

### 8.1 自动化

- `ProcessRunnerEncodingTests`（Win/mac/Linux 可跑的字节级事实）：
  - UTF-8 中文逐字相等；
  - GBK 字节行被兜底解码；
  - CRLF/LF/CR、无末尾换行、BOM；
  - 两个流并发、退出前已收到行；取消/释放语义不回归。
- Windows 专属：`.cmd` 中 `echo 中文` 的输出与 `Progress`/`Write` 无关地逐字相等。
- `RunConsolePageTests`（静态 SSR）：
  - 两个路由都存在，未授权/外来 run 是 404；
  - 一行一个 `.zk-console-line`，没有把多行拼进同一行；
  - 工具条、步骤栏、下载链接存在；
  - 运行中状态与终态都渲染；空状态文案。
- `RunConsoleFollowTests`（视图模型/纯逻辑）：
  - 暂停冻结视图、恢复跳到尾部、N 行新输出；
  - 搜索缩小、计数、无匹配；
  - 步骤过滤与计数；
  - 20,000 行保留上界与说明；
  - 2,000 行渲染上界。
- `RunLogMidRunAttachTests`、`RunLogDownloadPinTests`、`RunLogStepAgreementTests` 保持绿色
  （下载字节、join 历史、按步骤归属）。
- `RunLiveRefresher` 单元测试：终态停止、失败保留旧值、in-flight 跳过、耗时格式化。
- 既有测试的有意更新：`RunLogControlsTests`、`LogPanelFirstRenderTests`、`RunDetailFailureSummaryTests`、
  `RunUiHost`、`MilestoneHappyPathTests`；D-004 卡片补一条“被 F-047 取代”的说明，不删旧卡。
- `Localization` 目录测试保持绿色（新增键 + 删除失效键）。
- `node --check src/Server/wwwroot/js/run-console.js`；滚动/跟随的最终行为靠真实浏览器手工验证
  （TestServer 托管不了 WebSocket 电路，D-004-B3 已记录这个测试边界）。

### 8.2 手工/真实浏览器

- 本机起 Server + Worker，跑一个 3 步 Freestyle：
  - 步骤 1 输出 5 行中文；步骤 2 sleep 20 秒；步骤 3 故意失败；
  - 验证：整页 Console、自动跟随、往上滚暂停、跳到最新、步骤过滤、搜索、失败摘要、状态/耗时变化。
- 用一台真实 Windows Worker 跑 `svn`/`git`/`cmd` 中文输出，确认不再乱码；把结果写进卡片 Verification。
- 手机宽度（375px）检查：无横向整页滚动，工具条换行，步骤下拉可用。
- 5,000 行 fixture：渲染上界、滚动流畅、暂停/恢复。

### 8.3 验证命令（实现后按仓库习惯）

```bash
dotnet build -c Release
node --check src/Server/wwwroot/js/run-console.js
dotnet test -c Release --filter "Category=RunUi"
dotnet test -c Release --filter "Category=WorkerRuntime"
dotnet test -c Release --filter "FullyQualifiedName~Zenkins.Tests.Localization"
dotnet test -c Release --filter "Category!=Milestone"
dotnet test -c Release --filter "Category=Milestone"
```

## 9. 我做出的决定（你可以在批准时推翻）

1. **独立整页 Console，运行页不再内嵌日志。** 日志只保留一个查看器入口（Console）；下载是另一条路。
2. **状态用 1s/2s 轮询，不新增状态 Hub。** 日志继续 SignalR。
3. **暂停语义沿用 D-004（冻结视图），额外加“往上滚自动暂停”。** 不是“暂停推送”。
4. **搜索保持缩小视图**（D-004），不改成仅高亮。
5. **查看器保留 20,000 行、渲染 2,000 行。** 超过的旧行需要下载。
6. **时间戳默认关闭，UTC，格式 `HH:mm:ss`。**
7. **默认不自动换行**（横向滚动），工具条可切换。
8. **Windows 脚本：能无损编码时按系统 OEM 代码页写；否则 UTF-8 + `chcp 65001`。
   读取端逐行严格 UTF-8 → 系统代码页兜底。**
9. **不动历史日志**；只在 CHANGELOG/卡片里说明。
10. **跟随用两个标记**（显式暂停、滚动暂停），往上滚只暂停跟随，不停止接收。
11. **未知的 `?step=` 忽略并显示全部步骤**；不报错、不猜。
12. **重连后仍重放全部历史**（现有 hub 契约）；接受这一次的带宽/CPU 代价，
    把 `fromSeq` 增量重放列为后续卡片，不在本设计里改 hub 契约。

## 10. 风险与待确认

- **Windows `cmd` 的代码页**：中文脚本走“按 OEM 代码页写”的主路径，避免 `chcp` 的历史怪癖；
  含 emoji/生僻字的脚本才走 `chcp 65001`。两条路径都必须有 windows-latest 的事实；
  `chcp` 路径若在真实 Windows 上仍有问题，退化为“该脚本的非常规字符显示为替代字符”，
  不影响其余输出。兜底解码让 `svn`/`git`/Python 的混合编码退化为可读。
- **渲染/CPU 成本**：日志每 250ms 刷一批（`RunLogOptions`），每批都会重算一次计划和 2,000 个
  keyed 节点。5,000 行 fixture 必须保持流畅；如果实测卡顿，先降渲染窗口（例如跟随态只渲染尾部
  500 行，上滚时再补），而不是改推送频率。
- **多查看器**：20,000 行 × 每个打开的电路；50 个查看器是几十 MB。保留上界是硬上限；
  轮询是每个查看器每 2 秒 1 次多查询读，若成为负担再加短 TTL 缓存（不在本卡）。
- **重连重放**：现有 `JoinRunAsync` 会重放全部历史；长日志重连会重新传一遍。查看器的保留上界
  保证内存不失控，带宽成本接受；`fromSeq` 增量重放列为后续卡。
- **自动滚动的回授环**：程序化滚动会触发 `scroll`；JS 用 `suppress` 标记 + 节流，且
  `DotNetObjectReference` 在销毁时释放。真实浏览器手工验证，不作为自动化事实。
- **20,000 行保留上界**会改变 D-004“搜索计数等于全量”的旧承诺；D-004 卡片补“被 F-047 取代”的
  说明，界面以下载为准。
- **轮询间隔** 2 秒是体验与数据库负载的折中；如果不满可以降到 1 秒或改推送。
- **整页高度**依赖外层壳的布局；需要在桌面/手机真实浏览器量一次。
- **移除运行页内嵌日志**会改变现有测试与用户习惯；这是有意的 Jenkins 形状。
