# ResourceGrab 优化改造方案

> 基于 2026-09-04 对源码的实地核对。每条都附证据位置（`文件:行`）与建议动作，
> 便于直接拆成工作项。按「影响用户 → 影响维护性 → 性能 → 工程化」四级排序。
>
> **进度标记**（2026-09-05 核对，标记加在各条标题后）：
> ✅ 已完成　⚠️ 部分完成　❌ 未开始　➖ 按计划无需动作。

## 现状速览

- **规模**：src 243 个 `.cs` / 约 3.8 万行 + 61 个 `.xaml` / 约 8 千行。
- **最大单体**：`Views/VideoView.xaml.cs`（约 1800 行）、`MainWindow.xaml.cs`（1172）、
  `ReaderView.xaml.cs`（865）。
- **当前主战场**：视频刮削双引擎（legacy / graph）+ 在线搜索预览（LibVLC + 本地中继）。
- **持久化**：22 个 JSON 文件，纯文件无数据库，数据放 exe 同级 `config\`。
- **测试**：213 个 Fact+Theory，但覆盖严重倾斜，App/UI 层与多个核心服务零测试。

---

## P0 · 影响用户的实质缺陷（优先修）

### P0-1　在线预览代理被 `--noproxy '*'` 架空 ✅ 已完成（2026-09-05）
- **证据**：`Common/HlsLocalRelay.cs:325` 传 `-x {proxy}`，`:328` 又传 `--noproxy '*'`。
  curl 文档明写后者「effectively disables the proxy」，两次请求都不走代理。
- **影响**：配置了 `VideoScraping.Proxy` 的用户，本地中继取流必然超时，在线预览不可用。
- **动作**：去掉 `--noproxy '*'`；若需保留「直连失败再走代理」的兜底，改为 `--noproxy ''`
  （空值仅禁用直连绕过，不覆盖 `-x`）。
- **验证**：配置代理后对强制 Referer 的 CDN 跑一次在线预览，确认中继能取到子播放列表。
- **风险**：低，单行改动。

### P0-2　`ScrapeReportService` 未注册 DI + 三实例互写覆盖 ✅ 已完成（2026-09-05）
- **证据**：`VideoScrapeService` 与 `VideoLibraryService` 各自 `new ScrapeReportService(...)`，
  `ScrapeReportPanel` 里 `GetRequiredService<ScrapeReportService>()` 被 try/catch 吞异常。
  `App.xaml.cs:110` 实际已注册单例。
- **影响**：三份实例各写各的 `video-scrape-metadata.json` 互相覆盖，刮削报告面板永远空白。
- **动作**：
  1. `VideoScrapeService` / `VideoLibraryService` 构造改为从 DI 注入同一 `ScrapeReportService`；
  2. 删掉两处 `new ScrapeReportService(...)`；
  3. 确认 `ScrapeReportPanel` 的 `GetRequiredService` 不再进 catch。
- **风险**：低，但要核对两个服务的构造链不引入循环依赖。

### P0-3　视频刮削任务队列是「影子系统」 ✅ 已完成（2026-09-05）
- **证据**：`VideoView` 入队 `_taskQueue.Enqueue(...)` 并启动消费者，但历史实现里
  `ProcessQueueTaskAsync` 是空实现（`await Task.CompletedTask`）。App.xaml.cs:249 已改为
  `scrapeExecutor.ExecuteAsync` 接管队列，需确认 `VideoView` 是否还在跑队列外的旧 `ScrapeAsync`。
- **影响**：若旧路径仍并行存在，`VideoTaskDashboard` 显示的状态和进度可能与实际不符。
- **动作**：
  1. grep `VideoView` 里所有 `ScrapeAsync` / `Enqueue` 调用，确认唯一入口是队列；
  2. 若旧路径仍在用，改为只入队，由 `VideoScrapeTaskExecutor` 统一执行；
  3. 若短期不接，先把 Dashboard 标注不可用并隐藏，避免误导用户。
- **风险**：中，涉及刮削主流程，需回归测试单条/批量/重试三个入口。

### P0-4　构建警告里的真问题 ✅ 已完成（2026-09-05：构建零警告）
- **证据**（26 个警告中需处理的）：
  - `MainWindow:310` 不可达代码；
  - `VideoTaskDashboard` 5 处空引用解引用；
  - `VideoView:728` 未 await 的异步调用（异常被吞）；
  - `VideoView._scrapeRunning` 赋值后从未读取。
- **动作**：逐个清理；未 await 的异步调用至少包 `try/catch` 记日志；死状态位删除。
- **进度**（2026-09-05 第二轮）：`_scrapeRunning` 已删，MainWindow 不可达代码与 VideoTaskDashboard
  空引用警告已消失；7 处 CS4014 改为 `_ = Dispatcher.BeginInvoke(...)` 显式丢弃；
  `MissAvSource.cs` 未使用的 `FieldJoined` 已删；两处 CS8604 已修
  （`VideoScrapeService` 系列页 href 先取值判空、`VideoLibraryService` 番号解析 `FileName ?? ""`）。
  全量构建 0 警告 0 错误。
- **风险**：低。

---

## P1 · 架构债（影响可维护性与后续迭代）

### P1-1　`MainWindow.xaml.cs` 上帝对象（1172 行） ❌ 未开始（2026-09-05 仍为 1178 行，VideoView 反而涨到 2260 行）
- **现状**：缓存 13 个视图实例、维护章节页 LRU、处理 DWM 圆角、主题切换、下载徽章、对话框。
- **动作**（分步，不要一次大重构）：
  1. 13 个视图缓存抽成 `ViewCache` 服务；
  2. 章节页 LRU 下沉到 `ReaderView` / `OnlineReaderView` 自管；
  3. DWM 圆角调用从 MainWindow 移到 `Common/DwmWindowCorner.cs`（已存在，但调用点仍在 MainWindow）；
  4. 下载徽章状态抽成独立 `DownloadBadgeService`。
- **验收**：MainWindow 行数降到 600 以下，每个抽出的服务有明确单一职责。
- **风险**：中高，按视图逐步迁移，每步保持可编译可运行。

### P1-2　导航抽象名存实亡 ❌ 未开始（2026-09-05：两套 `_backStacks` 均在，MainWindow 直接赋值 `PageHost.Content` 8 处）
- **证据**：`ShellNavigator` 注释「不允许页面直接给 `PageHost.Content` 赋值」，
  但 `MainWindow:318/336/391` 多处直接赋值；`ShellController` 与 `ShellNavigator` 各维护一套返回栈；
  `ShellNavigator.SidebarPolicyFor()` 两分支返回同值（`Hidden`），
  `SidebarMode.Filter/Context/Task` 三枚举值无人用。
- **动作**：做一个明确决定——
  - **方案 A（统一）**：所有页面跳转走路由表，删 `ShellController` 的返回栈只留 `ShellNavigator` 一套；
  - **方案 B（精简）**：承认抽象过度，删 `ShellController`/`ShellNavigator` 返回栈与无用枚举，
    直接用 `PageHost.Content` + 一个简单的返回栈。
  当前「半采用」状态比删掉更糟，必须二选一。
- **推荐**：方案 B，当前项目规模不值得维持两层路由抽象。
- **风险**：中，但变更面集中在 MainWindow 与 Shell 目录。

### P1-3　两套平行的在线视频搜索实现 ✅ 已完成（2026-09-05：OnlineVideoView 源文件已删除）
- **证据**：`OnlineVideoView` 与 `VideoView.SearchPage` 是两套独立实现，前者疑似孤儿代码。
- **动作**：grep `OnlineVideoView` 的引用入口；无引用则删；有引用则统一到 `VideoView.SearchPage`。
- **风险**：低（删除）到中（合并）。

### P1-4　`VideoFileCard` 已标 `[Obsolete]` 仍在用 ✅ 已完成（2026-09-05）
- **证据**：`VideoView:272`、`ActorProfileView:54` 仍用 `VideoFileCard`，
  替代方案是 `MediaCard` + `MediaCardAdapters`。
- **动作**：补全这两处迁移到 `MediaCard`，删 `VideoFileCard`。
- **进度**（2026-09-05）：`VideoCardAdapter` 重写为包装 `VideoItem` 的完整适配器
  （点卡片=详情、框选模式=切换选中、悬停浮层播放/打开目录、收藏心形、刮削徽章、未观看标记）；
  `MediaCard` 的 WideThumb 模板补齐上述交互元素与点击→OpenCommand 行为；
  `VideoView` / `ActorProfileView` 改绑适配器，`VideoFileCard` 控件与专属样式已删除。
- **风险**：低。

### P1-5　`VideoScrapeService` 内建 legacy 源实例与 DI 适配器并存 ✅ 已完成（2026-09-05）
- **证据**：`App.xaml.cs:160-169` 通过 `LegacyJavBusSource` 等适配器把老三样注册进图引擎，
  但 `VideoScrapeService` 内部仍自建 `JavBusScraper`/`JavDbScraper`/`AiravScraper` 实例。
  切 `legacy` 用服务内建的，切 `graph` 用 DI 的。
- **动作**：`VideoScrapeService` 内部改为从 DI 取实例，legacy 路径统一走 `Legacy*Source` 适配器，
  消除「两套构造方式」的漂移风险。
- **进度**（2026-09-05）：构造函数新增三个可选 scraper 参数，App 注册处注入 DI 单例
  （legacy 与 graph 共用同批 `JavBusScraper`/`JavDbScraper`/`AiravScraper`）；
  未注入时（测试直连构造）回退自建，现有测试不受影响。
- **风险**：中，需确认 legacy 路径行为不变。

### P1-6　`System.Text.Encoding.CodePages` 的 NU1510 警告 ✅ 已完成（2026-09-05：包引用与 RegisterProvider 调用均已移除）
- **证据**：`Core.csproj` 引了它，`App.xaml.cs:67` 调了 `CodePagesEncodingProvider.Instance`。
- **动作**：grep Core 层是否有 GB2312/Shift-JIS 解码依赖（漫画源 HTML 可能用）；
  有则保留并抑制警告，无则删引用。
- **风险**：低。

---

## P2 · 性能与可靠性

### P2-1　视频刮削全局限流，非按源隔离 ➖ 核实后接受现状（2026-09-05）
- **证据**：三个 legacy 源共用 `VideoScrapeHttpClient`，全局 `SemaphoreSlim`（默认 2）+ 1000ms 间隔。
  与漫画下载引擎「按源隔离三级 Semaphore」不一致。graph 路径用 `MultiTierFetcher` + per-source fetcher，
  方向正确。
- **动作**：确认 legacy 是否仅作回滚兜底；若是，可接受现状；若仍有用户长期开 `legacy`，
  按 source 拆分限流。
- **风险**：低（接受现状）到中（拆分）。
- **进度**（2026-09-05 核实）：`VideoScrapeHttpClient` 内部已按 host 记录下次请求时间
  （`_nextRequestByHost`，注释「三个源各自独立限流，互不占用车道」），并发闸门全局共享但
  间隔按源隔离；legacy 仅作回滚兜底，按计划接受现状，不再拆分。

### P2-2　便携式 JSON 持久化的并发写风险 ✅ 已完成（2026-09-05）
- **证据**：22 个 JSON 文件，多个服务各自读写，无文件锁。崩溃时序不当可能写坏库文件。
- **动作**：写入统一用「临时文件 + 原子 `File.Move`」模式（核对每个服务的 Save 方法），
  并保留 `.bak` 上一次成功写入；高频写（如下载历史）考虑加 `Mutex` 或单写线程。
- **风险**：低，逐步替换 Save 实现即可。
- **进度**（2026-09-05 第二轮）：DownloadManager、LocalLibraryService、VideoLibraryService、
  ComicUserDataService、VideoThumbnailService 等早已用 `File.Move`；
  **ConfigService 与 ScrapeReportService（Persist/PersistPathStore）本轮已补齐
  临时文件 + 原子 `File.Move`**。计划中「保留 .bak」一项未做：与库内既有
  temp+move 模式保持一致优先，如需回滚能力再单独加。

### P2-3　`VideoView` 视图缓存策略 ❌ 未开始（2026-09-05：VideoView.xaml 无 VirtualizingStackPanel，仍用 VideoFileCard 重建）
- **证据**：`VideoView` 内联了大量列表渲染与详情回填逻辑（约 1800 行），每次搜索重建卡片。
- **动作**：配合 P1-1 的视图缓存抽离，对在线结果列表引入 `VirtualizingStackPanel`（若未用），
  并对卡片复用 `ItemContainerGenerator` 而非每次 `Children.Clear/Add`。
- **风险**：中，需回归搜索/分页/滚动行为。

---

## P3 · 测试与工程化

### P3-1　测试目录未进 slnx ✅ 已完成（2026-09-05：slnx 已登记 tests 项目）
- **证据**：`tests/` 不在 `ResourceGrab.slnx` 里（已修复 .gitignore，但 slnx 没登记）。
- **动作**：在 `ResourceGrab.slnx` 加 `<Project Path="tests/ResourceGrab.Core.Tests/..." />`。
- **风险**：低。

### P3-2　核心逻辑模块零测试 ⚠️ 基本完成（2026-09-05，仅剩服务层集成测试）
- **现状**：`DownloadManager`、5 个漫画源、`LocalLibraryService`(819 行)、
  `VideoLibraryService`(869 行)、`ConfigService`、整个 App/UI 层零测试。
- **动作**（按投入产出比排序，优先补纯逻辑模块）：
  1. `VideoScrapeConfigExtensions`（配置默认值/引擎切换）
  2. `VideoNumberParser`（番号正则，已有置信度表，易测）
  3. `VideoMetadataMerger`（字段合并策略「只补空字段」）
  4. `BlockNumCalculator`（禁漫分块数计算）
  5. `ImageReassembler`（分块乱序图还原，纯算法）
  6. `OfflineLexicon`（繁简转换/标签归一化）
  7. 服务层 `LocalLibraryService` / `VideoLibraryService`（用 JSON 快照做集成测试）
- **风险**：低，逐步补不破坏现有代码。
- **进度**（2026-09-05 第二轮）：第 1/2/3/4/5/6 项已有测试（ConfigExtensionTests、
  VideoNumberParserStrictnessTests、MergerTests、BlockNumCalculatorTests、ImageReassemblerTests、
  OfflineLexiconTests），另有大量 graph 源测试，全量 267 个测试通过；
  **仅剩第 7 项（LocalLibraryService / VideoLibraryService 服务层集成测试）未补**。

### P3-3　离线词典内嵌 1.5MB 增加包体 ➖ 按计划保持现状，无需动作
- **证据**：`mapping_actor.xml` / `mapping_info.xml` / `zhcdict.json` 内嵌为 EmbeddedResource。
- **动作**：若发布包体敏感，改按需加载（首次使用落盘到 `config\lexicon\`）；
  否则保持现状（无网络可用是明确优势）。
- **决策**：当前框架依赖包约 27MB，1.5MB 占比小，**建议保持现状**。

---

## 推进顺序与里程碑

| 阶段 | 范围 | 预估工作量 | 验收 | 状态（2026-09-05 第二轮后） |
| --- | --- | --- | --- | --- |
| **阶段 0** | P0-1 ~ P0-4 | 1-2 天 | 代理在线预览可用；报告面板有数据；警告清零 | ✅ 全部完成，构建 0 警告 0 错误 |
| **阶段 1** | P1-4、P1-6、P3-1 | 1 天 | 死代码删除，slnx 含测试 | ✅ 全部完成 |
| **阶段 2** | P1-3、P1-5 | 2-3 天 | 单一搜索实现，legacy 走 DI | ✅ 全部完成 |
| **阶段 3** | P1-2（导航精简） | 2 天 | 路由只有一套返回栈 | ❌ 未开始（需单独排期） |
| **阶段 4** | P1-1（MainWindow 拆分） | 5-7 天 | MainWindow < 600 行，按视图分步迁移 | ❌ 未开始（需连续时间窗口） |
| **阶段 5** | P2-2、P3-2 | 穿插进行 | Save 全原子写；6 个纯逻辑模块有测试 | ✅ 基本完成（P3-2 剩服务层集成测试；P2-1 核实后接受现状） |

阶段 0 全是影响用户的真问题，改动小见效快，**应立即开始**。
阶段 4 是最大工程，需连续时间窗口，建议放最后或单独排期。

---

## 核对记录

- **2026-09-05**：逐条对照源码与一次全量 `dotnet build` 核对。结论：阶段 0 基本完成
  （P0-1/2/3 ✅，P0-4 只剩 7 处 CS4014 未 await + 3 个新警告）；阶段 1 完成 3/4（P1-3/6、P3-1 ✅，
  P1-4 未做）；阶段 2 仅完成删 OnlineVideoView 一半；阶段 3~5 未实质性开始。
- **2026-09-05 第二轮（实施）**：完成剩余全部可独立推进项——
  ① P0-4 收尾：7 处 CS4014 显式丢弃、删 `FieldJoined`、修两处 CS8604，构建 0 警告；
  ② P1-4：`VideoFileCard` 迁移到 `MediaCard` + `VideoCardAdapter` 后删除（含 WideThumb 模板交互补全）；
  ③ P1-5：legacy 三源改由 DI 注入，与 graph 适配器共用单例；
  ④ P2-2：ConfigService、ScrapeReportService 补原子写；P2-1 核实 `VideoScrapeHttpClient`
  已按 host 独立限流，接受现状；
  ⑤ P3-2：新增 BlockNumCalculatorTests、ImageReassemblerTests（像素级行序断言）。
  验证：全量 `dotnet build` 0 警告 0 错误，`dotnet test` 267/267 通过。
  剩余：阶段 3（导航精简）、阶段 4（MainWindow 拆分）需单独排期；
  P3-2 服务层集成测试；P2-3 VirtualizingStackPanel（建议随阶段 4 一起做）。