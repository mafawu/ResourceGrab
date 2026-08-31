# ResourceGrab 架构速览

> 基于 2026-08-28 对源码的实地核对编写。与 `视频模块前后端补全开发计划.md` 互补：
> 那份文档讲「接下来怎么改」，这份讲「现在是什么样」。

## 1. 一句话概括

.NET 10 / WPF 写的 Windows 桌面应用，把多个在线漫画/视频站的协议差异收敛到 `IComicSource` /
`IVideoSource` 接口后面，上层只面对统一的领域模型；本地侧提供漫画库、视频库、小说阅读器三套媒体库。

## 2. 解决方案结构

| 项目 | TFM | 职责 |
| --- | --- | --- |
| `ResourceGrab.Core` | `net10.0` | 内容源适配、下载引擎、图片重组、本地库服务、配置与日志 |
| `ResourceGrab.App` | `net10.0-windows10.0.19041.0` | WPF 界面层，`UseWPF` |
| `tests/ResourceGrab.Core.Tests` | `net10.0` | 93 个单元测试（**不在 slnx 里，需单独运行**） |

- 解决方案文件是 `.slnx`（新格式），只登记 App + Core 两个项目。
- 输出统一重定向到仓库根 `bin\`（两个 csproj 都设了 `BaseOutputPath=..\..\bin\`），
  所以是 `bin\Debug\` / `bin\Release\`，不是默认的 `src\X\bin\...`。
- 代码量：约 29.4k 行（`.cs` + `.xaml`，不含 obj），Core : App ≈ 1 : 1.4。

## 3. 分层与关键抽象

```
ResourceGrab.App (WPF)          MainWindow → Shell → Views/Controls/Dialogs/Themes
        │  App.Services (DI 容器，静态单例)
        ▼
ResourceGrab.Core               Sources / Downloading / Services / Http / Utils
```

### 3.1 内容源（Core/Sources）

四个能力接口，源按需实现，UI 按能力自动适配导航项：

| 接口 | 能力 | 实现者 |
| --- | --- | --- |
| `IComicSource` | 搜索 / 详情 / 章节图片列表（**必选**） | jm、copymanga、baozimh、hitomi、wnacg |
| `ICategorySource` | 分类浏览 | 部分源 |
| `IRankSource` | 日/周/月/年排行 | jm、wnacg 等 |
| `IVideoSource` | 在线视频搜索 + 详情 | 目前仅 MissAV |

配套的 `ComicSourceInfo` 是**能力声明对象**，UI 和下载引擎都读它：
`SupportsRank` / `SupportsCategories` / `SupportsFavorites` 决定左侧导航显示哪些入口
（见 `Shell/Providers/MangaNavSectionProvider`）；
`MaxImageConcurrency` / `MaxChapterConcurrency` / `MaxUrlFetchConcurrency` 决定该源的下载并发上限。

> 这是整个项目最漂亮的设计：加一个新站 = 写一个实现 + 在 `App.ConfigureSources()` 注册一行，
> 导航、下载限流、并发策略全自动。

### 3.2 下载引擎（Core/Downloading）

`DownloadManager` 是核心，基于 `System.Threading.Channels` 的生产者-消费者：

```
SubmitChapterAsync → BoundedChannel(32) → ReceiverLoopAsync → ProcessChapterAsync
                                                                  ├─ GetChapterPagesAsync  (Semaphore: Urls)
                                                                  ├─ throttle.Chapters.Wait (Semaphore: Chapters)
                                                                  └─ DownloadImageAsync × N (Semaphore: Images)
```

要点：
- **按源隔离限流**：`ConcurrentDictionary<string, SourceThrottle>`，每个源一组三把 `SemaphoreSlim`，
  上限取自该源自己的 `ComicSourceInfo`，源之间互不影响。
- **断点续传**：先落到 `.下载中-{章节名}` 临时目录，全部成功后 `Directory.Move` 到正式目录；
  已存在且非空的文件直接跳过（`IsFileComplete`），整章完整则直接跳过（`IsChapterComplete`）。
- **图片重组**：`ImageReassembler` 负责禁漫的分块乱序图还原，`BlockNumCalculator` 算分块数，
  `WebpImageDecoder` 处理 webp。输出格式（jpg/png/webp）由配置决定。
- **事件驱动 UI**：`ChapterPending` / `ChapterStart` / `ImageSuccess` / `ImageError` /
  `ChapterEnd` / `OverallProgress` / `SpeedChanged` 七个事件，速度每秒上报一次。
- 章节事件 ID 用运行期自增编号（`$"{SourceId}|{ChapterId}"` 映射），
  因为拷贝漫画的章节 ID 是 UUID，不能直接当数值 ID 用。

### 3.3 应用层外壳（App/Shell + App/Navigation）

设计上分三层：`MediaKind`（漫画 / 视频 / 小说）→ `ShellRoute`（路由，含视图工厂与侧栏策略）
→ `ShellController`（状态机）+ `ShellNavigator`（路由表）。
`INavSectionProvider` 按媒体类型生成左侧导航分组，`IFilterProvider` 生成右侧筛选面板。

**⚠️ 但注意：这套抽象目前只被部分采用。** 详见第 6 节。

### 3.4 数据持久化（Core/AppPaths + Services）

便携式设计，数据全部在 exe 同级 `config\` 目录，不写注册表。
`AppPaths` 集中定义所有路径，`MigrateLegacyData()` 负责把旧版 `%APPDATA%\jmcomic-downloader` 迁过来。

| 文件 | 用途 |
| --- | --- |
| `config.json` | 全局配置（账号、下载参数、API 域名、翻译接口） |
| `download-history.json` | 下载历史 |
| `video-library.json` / `video-folders.json` | 视频库数据与根目录 |
| `novel-*.json` | 小说索引 / 进度 / 排版 |
| `video-scrape-metadata.json` | 刮削报告 |
| `logs\` | 运行日志 + 崩溃日志 |

敏感字段（用户名、密码、翻译 API key）走 DPAPI 当前用户作用域加密，旧明文配置首次启动自动迁移。

## 4. DI 容器

`App.xaml.cs` 里手写 `ServiceCollection`，构建结果挂到静态 `App.Services`。
全部服务都是 **Singleton**。派生应用可通过覆盖 `DataDirName` / `RegisterSessionService` /
`ConfigureSources` 产出独立 exe（如免登录的 copymanga 版）。

主要注册：5 个 `IComicSource`、`SourceManager`、`AggregateSearchService`、`DownloadManager`、
`SessionService`、`LocalLibraryService`、三个 Novel 服务、`VideoLibraryService`、
`VideoScrapeService`、`VideoScrapeTaskQueue(4)`、3 个 `IVideoActorSource`、1 个 `IVideoSource`(MissAV)。

## 5. 视频模块现状（重点）

这是当前迭代的主战场。存在**两套并存的刮削体系**：

| 体系 | 位置 | 状态 |
| --- | --- | --- |
| 旧引擎 | `VideoScrapeService` + `JavBusScraper` / `JavDbScraper` / `AiravScraper` | ✅ 运行中，UI 走进度条 |
| 新管道 M1–M12 | `Services/VideoScrape/`（40 个文件，含 20 个 `Sources/` 实现） | ❌ **死代码，DI 未注册，运行时从未调用** |

新管道的设计其实相当完整：`VideoFetchGraphScheduler`（来源编排）、`VideoMetadataMerger`（字段合并）、
`VideoActorMerger`（演员信息合并）、`DiskJsonSnapshotCache`（快照缓存）、`PluginLoader`（插件）、
`VideoScrapeTaskQueue`（并发队列）。测试也写了（`tests/.../VideoScrape/` 下 9 个测试文件）——
**但因为没接线，这些测试验证的是一段从未被生产代码执行的代码。**

详细的接线方案见 [`视频模块前后端补全开发计划.md`](视频模块前后端补全开发计划.md)（阶段 0–7）。

## 5.1 刮削流程详解

### 触发入口

| 入口 | 位置 | 说明 |
| --- | --- | --- |
| 详情页「刮削」按钮 | `VideoView.ScrapeDetail_Click` | 单条 |
| 批量刮削 | `VideoView.BatchScrape_Click` | 勾选集 |
| 重试未成功 / 失败 | `VideoView.RetryByStatus` | 按 `ScrapeStatus` 过滤 |
| 自动刮削 | `VideoView.RescanRoots_Click` | 重扫根目录后对新条目自动跑 |

全部汇聚到 `VideoView.StartScrape(ids, autoStarted, title)`：建 `CancellationTokenSource`，
`Task.Run` 里调 `_scrapeService.ScrapeAsync(idList, progress => …)`，
进度回调通过 `Dispatcher.Invoke` 刷新进度条、摘要和日志列表。

### 单条处理（`VideoScrapeService.ScrapeAsync`）

**第 1 步 · 番号兜底重解析。** 若 `item.Number` 为空，用 `VideoNumberParser.Parse(filePath)`
再试一次；仍为空则标记 `Skipped` 并跳过。

**第 2 步 · 番号解析规则**（`VideoNumberParser`，纯正则，按置信度从高到低）：

| 类型 | 正则 | 置信度 |
| --- | --- | --- |
| FC2 | `(?:FC2\|FPPV)[-_ ]?(?:PPV[-_ ]?)?(\d{5,8})` | 0.95 |
| 标准番号（带分隔符） | `([A-Za-z]{2,8})[-_ ]?(\d{2,6})` | 0.92 |
| 标准番号（无分隔符，短前缀） | 同上 | 0.72 |
| 无码 `123456-789` | `(\d{6})-(\d{3})` | 0.85 |
| 无码 `1234_567` | `(\d{3,4})[_-](\d{3})` | 0.70 |
| 父目录名回退 | 同上标准规则 | 0.35 |

解析前会先剥离域名噪声（`.com` / `.net` / `.xyz` 等 30 种后缀）、
`@番号` 标记、分辨率与编码标签（`1080p` / `hevc` / `x264` / `uncensored` …），
并单独用 `\bCD\s*(\d+)\b` 提取分卷。

**第 3 步 · 三源串行抓取。** 顺序固定、前一步成功才走下一步：

1. **JavBus**（`JavBusScraper`，主源）：`/search/{番号}&type=1` → 取首个 `a.movie-box` → 详情页解析。
   提供标题、封面、演员、标签、发行日等基础字段。**失败即整条失败**（抛异常 → `ScrapeStatus.Failed`）。
2. **JavDB**（`JavDbScraper`，补充源）：补中文标题、评分、短评、剧照、磁力标记、中字标记。
   异常被吞掉，只记日志，不中断。
3. **AirAv**（`AiravScraper`，条件触发）：**仅当** 开了 `AiravEnabled`
   且（标题仍是日文 **或** 简介为空）时才请求。补中文标题和简介。
   AirAv 有 Cloudflare，失败会降级到 curl 子进程（`GetWithCurlAsync`）。

**第 4 步 · 字段合并。** `MergeJavDb` / `MergeAirav` 的策略统一是 **只补空字段，绝不覆盖已有值**。
标题是特例：如果 JavBus 的标题是日文（`LooksJapanese` 检测平假名/片假名），
会先把它挪到 `OriginalTitle` 再用中文标题覆盖 `Title`——否则日文原题就丢了。
最后 `ApplyMetadata` 把合并结果写回 `VideoItem`。

**第 5 步 · 落盘。** `DownloadImagesAsync` 下载封面到
`config\artwork\videos\{番号}-fanart.jpg`，用 `VideoImageService.CreatePosterAsync` 生成竖版海报，
最多补 10 张剧照。`WriteNfo` 写 Kodi 兼容的 `.nfo` 到视频同目录。
`SaveFieldSourcesReport` 记录每个字段来自哪个源、各源的尝试结果。

**网络层**（`VideoScrapeHttpClient`）：全局 `SemaphoreSlim` 限并发（默认 2，可配 1–8），
`RequestIntervalMs`（默认 1000）做请求间隔节流，失败指数退避重试 2 次。
三个源**共用同一个 HttpClient**，所以限流是全局的而非按源隔离——这点与漫画下载引擎不同。

### 刮削状态机

```
Pending → Success / NoMatch / Failed / Skipped
```

`NoMatch` 是「源里查无此番号」，`Failed` 是「请求或解析出错」——UI 上分开提供重试按钮。

### 演员信息不在刮削流程里

`VideoActorMerger` + 三个 `IVideoActorSource`（GFriends / Wikipedia / Minnano）确实注册了 DI，
但**只有打开演员档案页 `ActorProfileView` 时才会调用**，刮削过程完全不碰。

### ⚠️ 任务队列是「影子系统」

`VideoView` 第 836 行把 id 入队 `_taskQueue.Enqueue(...)`，第 69 行启动了消费者
`_taskQueue.StartProcessingAsync(ProcessQueueTaskAsync, …)`——
但 `ProcessQueueTaskAsync` 是个**空实现**（只有 `await Task.CompletedTask`）。

结果是：真正的刮削在队列外的 `ScrapeAsync` 里跑，队列里的任务被取出后立刻标记完成，
两者毫无关联。所以 `VideoTaskDashboard` 看到的状态和进度**都是假的**。
要修就得让 `ScrapeAsync` 真正通过队列调度，而不是靠 UI 侧假装入队。

## 6. 已知架构问题（按严重度排序）

1. **`ScrapeReportService` 没注册 DI。** `ScrapeReportPanel` 里
   `GetRequiredService<ScrapeReportService>()` 被 try/catch 吞掉异常，面板永远空白。
   更糟的是 `VideoScrapeService` 和 `VideoLibraryService` 各自 `new` 了自己的实例，
   三份实例各写各的 `video-scrape-metadata.json`，数据互相覆盖。
2. **`docs/` 和 `tests/` 都被 `.gitignore` 排除了**（根级 `/docs`、`/tests`），
   288 个跟踪文件里 0 个来自这两个目录。开发计划文档和 93 个测试**完全没有版本管理**，
   换台机器就丢了。建议尽快把这两条规则删掉。
3. **导航抽象名存实亡。** `ShellNavigator` 的注释写着「不允许页面直接给 `PageHost.Content` 赋值」，
   但 `MainWindow` 里到处是直接赋值（第 318/336/391 行等）。
   同时 `ShellController` 和 `ShellNavigator` **各维护一套返回栈**，状态可能不一致。
4. **`ShellNavigator.SidebarPolicyFor()` 两个分支返回同一个值**（都是 `Hidden`），
   `SidebarMode.Filter` / `Context` / `Task` 三个枚举值实际没人用，侧栏策略是空壳。
5. **`MainWindow` 是 1147 行的上帝对象**：缓存了 13 个视图实例、维护章节页 LRU、
   处理 DWM 圆角、主题切换、下载徽章、对话框……是后续重构的主要负担。
6. **`VideoFileCard` 已标 `[Obsolete]` 但仍在用**（`VideoView:272`、`ActorProfileView:54`），
   替代方案是 `MediaCard` + `MediaCardAdapters`，迁移未完成。
7. **`OnlineVideoView` 与 `VideoView.SearchPage` 是两套平行的在线搜索实现**，前者是孤儿代码。
8. 构建有 26 个警告，其中几个是真问题：`MainWindow:310` 不可达代码、
   `VideoTaskDashboard` 5 处空引用解引用、`VideoView:728` 未 await 的异步调用、
   `VideoView._scrapeRunning` 赋值后从未读取。
9. `tests/ScanTest` 只剩 `bin`/`obj`，是空壳目录，可以删。
10. `Core.csproj` 引了 `System.Text.Encoding.CodePages`（NU1510 警告称非必需），
    但 `App.xaml.cs` 确实调了 `CodePagesEncodingProvider`——先确认再决定是否移除。

## 7. 常用命令

```powershell
dotnet build ResourceGrab.slnx -c Release          # 或双击 build-release.bat
.\publish.ps1                                       # 发布到 publish\（框架依赖，约 27MB）
cd tests/ResourceGrab.Core.Tests; dotnet test       # 93 个测试（不在 slnx 中）
```

免安装自包含版（约 160MB）：

```powershell
dotnet publish src/ResourceGrab.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o BIN\release
```
