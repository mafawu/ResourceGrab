# 视频模块前端补全开发指南

> **目标读者**：任何一位开发者。只需阅读本文档 + 浏览当前源代码即可完成全部前端补全工作，无需其他上下文。

---

## 1. 项目概况

ResourceGrab 是一个 WPF (.NET 9) 本地桌面应用，使用 **MVVM-light（无框架，纯 code-behind + ObservableObject）** 模式。项目分为两层：

| 项目 | 职责 | 关键目录 |
|---|---|---|
| `ResourceGrab.Core` | 领域模型、服务、刮削引擎、配置 | `Models/`, `Services/`, `Sources/` |
| `ResourceGrab.App` | WPF UI、控件、视图、对话框 | `Views/`, `Controls/`, `Dialogs/`, `Themes/`, `Shell/` |

### 1.1 DI 注册入口

所有服务注册在 `App.xaml.cs` 中。关键视频相关注册：

```csharp
// Config + Logger
services.AddSingleton(new ConfigService(AppPaths.ConfigPath));
services.AddSingleton<ILogger>(sp => new FileLogger(AppPaths.LogsDir));

// 视频库
services.AddSingleton<VideoLibraryService>(sp => new VideoLibraryService(
    AppPaths.VideoLibraryPath, AppPaths.VideoFoldersPath, sp.GetRequiredService<ILogger>()));

// 视频刮削（旧版：直接依赖 JavBus/JavDB/AirAv scraper）
services.AddSingleton<VideoScrapeService>(sp => new VideoScrapeService(
    sp.GetRequiredService<VideoLibraryService>(),
    ...));

// 在线视频源（仅 MissAV）
services.AddSingleton<ResourceGrab.Core.Sources.IVideoSource>(sp => new MissAvSource(...));
```

**注意**：新架构的 `IVideoScrapeSource` 系列接口（M1/M7/M11）尚未接入 DI，需要在实现前端页面时一并注册。设计文档中列出了完整的注册代码（见 `VideoScrapeConfigExtensions.cs`）。

### 1.2 导航架构

应用使用 **Shell 模式**，核心类位于 `Shell/` 目录：

```
ShellController    -- 维护当前媒体类型 (Manga/Video/Novel)、路由、侧栏模式
ShellNavigator     -- 统一导航服务，管理路由工厂和返回栈
NavModels          -- NavItem / NavSection / INavSectionProvider
ShellRoute         -- 单次路由跳转的描述
SidebarPolicy      -- 右侧面板模式 (Hidden/Filter/Context/Task)
```

左侧导航栏由 `NavRail.xaml` 渲染，数据来源为 `INavSectionProvider`。视频类型的导航在 `VideoNavSectionProvider` 中定义：

```csharp
// Shell/Providers/VideoNavSectionProvider.cs
public sealed class VideoNavSectionProvider : INavSectionProvider
{
    public MediaKind Kind => MediaKind.Video;

    public IReadOnlyList<NavSection> GetSections() =>
    [
        new("发现",
        [
            new NavItem("video.online", "在线搜索", Icons.Search),
            new NavItem("video.recommend", "推荐", Icons.Rank, IsEnabled: false),
        ]),
        new("我的",
        [
            new NavItem("video.local", "本地", Icons.Local),
            new NavItem("video.tasks", "刮削任务", Icons.Setting, IsEnabled: false),
        ]),
    ];
}
```

**新增导航项时**：
1. 在对应 `INavSectionProvider` 中添加 `NavItem`
2. 在 `MainWindow.xaml.cs` 的 `OnNavItemClicked` 中添加路由处理
3. 在 `RegisterRoutes` 中注册工厂

### 1.3 视图实例化模式

所有视图在 `MainWindow.xaml.cs` 中以 **懒加载字段** 管理：

```csharp
private VideoView? _videoView;
private VideoSearchPanel? _videoSearchPanel;

// 懒加载属性
private VideoSearchPanel VideoSearchPanelView => _videoSearchPanel ??= new VideoSearchPanel();
```

页面切换通过 `SetPage(UserControl)` 方法完成，`PageHost.Content` 为实际内容区域。右侧面板通过 `ShowRightContent(UserControl)` 设置。

---

## 2. 后台能力全景

以下是 `ResourceGrab.Core` 中与视频相关的全部后台能力，按模块分组。

### 2.1 核心服务

| 服务 | 文件 | 已注册 DI | 状态 |
|---|---|---|---|
| `VideoLibraryService` | `Services/VideoLibraryService.cs` | Yes | 已接入前端 |
| `VideoScrapeService` | `Services/VideoScrapeService.cs` | Yes | 已接入前端（旧版） |
| `VideoThumbnailService` | `Services/VideoThumbnailService.cs` | -- | 已通过 VideoView 使用 |
| `VideoEnrichmentService` | `Services/VideoEnrichmentService.cs` | -- | 已通过 VideoView 使用 |
| `VideoImageService` | `Services/VideoImageService.cs` | -- | -- |
| `VideoNumberParser` | `Utils/VideoNumberParser.cs` | -- | 内部使用 |
| `VideoMetadataReader` | `Utils/VideoMetadataReader.cs` | -- | 已通过 VideoView 使用 |

### 2.2 新版刮削系统（M1-M12）

这些类已实现但 **未接入前端 UI**，是本次补全的核心目标。

| 模块 | 关键类 | 文件位置 | 说明 |
|---|---|---|---|
| **M1 来源契约** | `IVideoScrapeSource` | `Services/VideoScrape/VideoScrapeInterfaces.cs` | 统一来源接口 |
| **M1 注册中心** | `IVideoScrapeSourceRegistry` / `VideoScrapeSourceRegistry` | 同上 | DI 来源注册 |
| **M1 请求/返回** | `VideoSourceRequest` / `VideoSourceFetchResult` | `Services/VideoScrape/VideoScrapeModels.cs` | 来源请求与返回 |
| **M1 来源追踪** | `VideoSourceAttempt` | 同上 | 每个来源的执行记录 |
| **M1 快照** | `VideoSourceSnapshot` | 同上 | 站点级缓存快照 |
| **M2 内容路由** | `ContentRouteEntry` / `VideoScrapeAdvancedSettings` | `Services/VideoScrape/VideoScrapeConfigExtensions.cs` | 7种内容类型的来源路由 |
| **M2 字段优先级** | `FieldPriorityEntry` | 同上 | 每个字段的来源优先链 |
| **M2 来源配置** | `VideoSourceConfig` | 同上 | per-source 启用/URL/cookie/rateLimit |
| **M2 配置校验** | `VideoScrapeConfigValidator` | 同上 | 路由/优先级合法性检查 |
| **M3 字段合并** | `VideoMetadataMerger` | `Services/VideoScrape/VideoMetadataMerger.cs` | 标量择优、集合并集、字段溯源 |
| **M4 快照缓存** | `IVideoSourceSnapshotCache` / `DiskJsonSnapshotCache` | `Services/VideoScrape/IVideoSourceSnapshotCache.cs` + `DiskJsonSnapshotCache.cs` | 磁盘 JSON 快照 |
| **M5 任务队列** | `VideoScrapeTaskQueue` | `Services/VideoScrape/VideoScrapeTaskQueue.cs` | Channel-based 并发队列 |
| **M5 任务模型** | `VideoScrapeTask` / `VideoTaskType` / `VideoTaskStatus` / `VideoTaskProgress` | `Services/VideoScrape/VideoTaskModels.cs` | 任务状态与进度 |
| **M6 资源存储** | `IVideoResourceStore` / `DiskVideoResourceStore` | `Services/VideoScrape/VideoResourceStore.cs` | URL hash 去重图片存储 |
| **M7 抓取图** | `VideoFetchNode` / `VideoFetchGraphBuilder` / `VideoFetchGraphScheduler` | `Services/VideoScrape/VideoFetchGraphScheduler.cs` | 波次调度、字段剪枝 |
| **M7 请求** | `VideoScrapeRequest` | 同上 | 抓取图入口请求 |
| **M11 演员模型** | `VideoActorMetadata` / `IVideoActorSource` / `VideoActorMerger` | `Services/VideoScrape/VideoActorModels.cs` + `VideoActorMerger.cs` | 多源演员数据合并 |
| **M12 插件** | `VideoScrapePluginManifest` / `PluginLoader` / `LoadedPlugin` | `Services/VideoScrape/PluginModels.cs` + `PluginLoader.cs` | 插件 manifest 与加载 |

### 2.3 来源实现清单

| 来源 ID | 实现文件 | 状态 |
|---|---|---|
| `javbus` | `Services/VideoScrape/LegacyJavBusSource.cs` | 已实现（Legacy 适配器） |
| `javdb` | `Services/VideoScrape/LegacyJavDbSource.cs` | 已实现（Legacy 适配器） |
| `airav` | `Services/VideoScrape/LegacyAiravSource.cs` | 已实现（Legacy 适配器） |
| `missav` | `Sources/VideoSources/MissAvSource.cs` | 已实现（在线视频源，非刮削源） |
| `dmm` | `Services/VideoScrape/Sources/DmmSource.cs` | 已实现 |
| `avsox` | `Services/VideoScrape/Sources/AvsoxSource.cs` | 已实现 |
| `dahlia` | `Services/VideoScrape/Sources/DahliaSource.cs` | 已实现 |
| `faleno` | `Services/VideoScrape/Sources/FalenoSource.cs` | 已实现 |
| `fc2` | `Services/VideoScrape/Sources/Fc2Source.cs` | 已实现 |
| `fc2club` | `Services/VideoScrape/Sources/Fc2ClubSource.cs` | 已实现 |
| `fc2ppvdb` | `Services/VideoScrape/Sources/Fc2ppvdbSource.cs` | 已实现 |
| `freejavbt` | `Services/VideoScrape/Sources/FreejavbtSource.cs` | 已实现 |
| `getchu` | `Services/VideoScrape/Sources/GetchuSource.cs` | 已实现 |
| `gfriends` | `Services/VideoScrape/Sources/GFriendsSource.cs` | 已实现 |
| `giga` | `Services/VideoScrape/Sources/GigaSource.cs` | 已实现 |
| `iqqtv` | `Services/VideoScrape/Sources/IqqtvSource.cs` | 已实现 |
| `kin8` | `Services/VideoScrape/Sources/Kin8Source.cs` | 已实现 |
| `mgstage` | `Services/VideoScrape/Sources/MgstageSource.cs` | 已实现 |
| `minnano` | `Services/VideoScrape/Sources/MinnanoSource.cs` | 已实现 |
| `official` | `Services/VideoScrape/Sources/OfficialSource.cs` | 已实现 |
| `prestige` | `Services/VideoScrape/Sources/PrestigeSource.cs` | 已实现 |
| `theporndb` | `Services/VideoScrape/Sources/ThePornDbSource.cs` | 已实现 |
| `wikipedia` | `Services/VideoScrape/Sources/WikipediaActorSource.cs` | 已实现 |
| `xcity` | `Services/VideoScrape/Sources/XcitySource.cs` | 已实现 |

### 2.4 核心数据模型

**`VideoItem`**（`Models/VideoFolder.cs`）— 本地视频库主实体：

```csharp
// 已有字段（前端已使用）
public string Number, Title, OriginalTitle, Description;
public List<string> Actors, Tags;
public string Series, Studio, Publisher, Director;
public DateTime? ReleaseDate; public double Score;
public CensorType CensorType; public ScrapeStatus ScrapeStatus;
public string CoverPath, PosterPath; public List<string> PreviewImages;
public Dictionary<string, string> SourceUrls;
public List<string> RelatedNumbers, SeriesNumbers, Reviews;
public bool IsFavorite; public int UserRating;

// 新增字段（刮削系统产出，前端需展示）
// 注意：当前 VideoItem 没有 FieldSources / Attempts 字段
// 这些数据存储在 video-scrape-metadata.json 侧车文件中
```

**`VideoScrapeSettings`**（`Models/VideoScrapeModels.cs`）— 基础刮削配置：

```csharp
public bool Enabled, AutoScrapeNewFiles, DownloadExtraFanart, WriteNfo;
public string Proxy;
public int TimeoutSeconds, Retry, Concurrency, RequestIntervalMs;
public string JavBusBaseUrl, JavBusCookie, JavDbBaseUrl, JavDbCookie;
public bool AiravEnabled;
```

**`VideoScrapeAdvancedSettings`**（`Services/VideoScrape/VideoScrapeConfigExtensions.cs`）— 高级配置：

```csharp
public List<ContentRouteEntry> ContentRoutes;        // 7种内容类型的来源路由
public List<FieldPriorityEntry> FieldPriorities;     // 字段级来源优先链
public Dictionary<string, VideoSourceConfig> SourceConfigs; // per-source 配置
```

---

## 3. 前端需要补全的页面

按优先级排序，每个页面独立可实现。

### 3.1 刮削设置页 — `ScrapeSettingsView`

**优先级**：P0（不实现此页，25+ 来源无法在 UI 中启用/禁用）

**后台对接**：

| 数据 | 类型 | 读写方式 |
|---|---|---|
| 来源开关矩阵 | `VideoScrapeAdvancedSettings.SourceConfigs` | `ConfigService.Current.VideoScraping` 扩展字段 |
| 内容路由 | `VideoScrapeAdvancedSettings.ContentRoutes` | 同上 |
| 字段优先级 | `VideoScrapeAdvancedSettings.FieldPriorities` | 同上 |
| 基础设置 | `VideoScrapeSettings` | `ConfigService.Current.VideoScraping` |

**注意**：当前 `VideoScrapeSettings` 中没有 `VideoScrapeAdvancedSettings` 字段。需要先在 `VideoScrapeSettings` 类中添加：

```csharp
// Models/VideoScrapeModels.cs — 需要新增
[JsonPropertyName("advanced")]
public VideoScrapeAdvancedSettings? Advanced { get; set; }
```

然后在 `ConfigService` 保存时一并序列化。

**UI 结构**（建议做成 SettingsDialog 内的新 Tab 或独立子页面）：

```
ScrapeSettingsView
├── 基础设置区
│   ├── [ ] 启用刮削          ← ScrapeSettings.Enabled
│   ├── [ ] 自动刮削新文件    ← ScrapeSettings.AutoScrapeNewFiles
│   ├── [ ] 写入 NFO          ← ScrapeSettings.WriteNfo
│   ├── [ ] 下载额外剧照      ← ScrapeSettings.DownloadExtraFanart
│   ├── 代理地址              ← ScrapeSettings.Proxy
│   ├── 超时 (秒)             ← ScrapeSettings.TimeoutSeconds
│   ├── 重试次数              ← ScrapeSettings.Retry
│   └── 全局并发              ← ScrapeSettings.Concurrency
│
├── 来源开关区
│   ├── 标题 "影片元数据来源"
│   ├── CheckBox 网格 (4列)   ← 遍历 SourceConfigs
│   │   [x] JavBus     [x] JavDB     [x] AirAv     [ ] DMM
│   │   [ ] IQQTV      [ ] Avsox     [ ] MGStage   [ ] FC2PPVDB
│   │   ... 共 23 个影片来源
│   ├── 标题 "演员来源"
│   ├── CheckBox 网格          ← 4 个演员来源
│   │   [ ] Minnano   [ ] GFriends   [ ] Wikipedia   [x] JavDB(双料)
│
├── 内容路由区
│   ├── 标题 "内容类型路由"
│   ├── 每种类型一个可排序列表
│   │   有码 (Censored):  [DMM] [JavDB] [JavBus] [Official]  ← 可拖拽排序
│   │   无码 (Uncensored): [JavDB] [JavBus] [Avsox] [FreeJavBT]
│   │   FC2:  [JavDB] [FC2PPVDB] [FC2] [FreeJavBT]
│   │   中文 (Chinese): [IQQTV] [JavDB] [AirAv] [FreeJavBT]
│   │   素人 (Amateur): [MGStage] [DMM] [JavDB] [JavBus]
│   │   欧美 (Western): [ThePornDB] [JavDB] [FreeJavBT]
│   │   里番 (Hentai):  [Getchu] [DMM] [JavDB]
│
├── 字段优先级区
│   ├── 标题 "字段来源优先级"
│   ├── 每个字段一个可排序列表
│   │   标题 (默认):    [DMM] [JavDB]
│   │   标题 (中文):    [IQQTV] [AirAv] [JavDB]
│   │   简介:           [AirAv] [DMM] [JavDB]
│   │   封面:           [DMM] [JavBus] [Official]
│   │   ... 共 8 个字段
│
├── 来源详情配置区 (可折叠)
│   ├── 展开某个来源后显示：
│   │   基础 URL: [     ]
│   │   Cookie:   [     ]
│   │   速率限制: [     ] req/s
│   │   API Key:  [     ]  (theporndb)
│   │   DSN:      [     ]  (r18dev)
│
├── 缓存设置区
│   ├── [ ] 启用来源快照缓存
│   ├── 缓存有效期 (天): [  7 ]
│   ├── [ ] 重刮时优先使用缓存
│   ├── [ ] 强制刷新全部来源
│   └── 清除全部缓存 (按钮)
│
└── 底部操作栏
    ├── 配置校验结果 (警告/错误列表) ← VideoScrapeConfigValidator
    ├── 保存设置
    └── 恢复默认
```

**实现要点**：

1. 在 `VideoScrapeSettings` 类中添加 `Advanced` 属性
2. 创建 `ScrapeSettingsView.xaml` + `.cs`，放在 `Views/` 目录
3. 将其嵌入 SettingsDialog 的视频 Tab（替代当前只有3个控件的简陋区域）
4. 排序列表可复用 `WrapPanel` + 上移/下移按钮（无需拖拽库）
5. 保存时调用 `ConfigService` 的保存方法，并触发 `VideoScrapeAdvancedSettings` 的重新加载
6. "清除全部缓存" 按钮调用 `DiskJsonSnapshotCache.ClearAllAsync()`

**图标参考**：
- 来源列表图标：`Icons.Setting`
- 保存按钮：`Icons.Check`
- 校验警告：`Icons.Info` + `WarningBrush`
- 校验错误：`Icons.Close` + `DangerBrush`

---

### 3.2 刮削任务监控面板 — `VideoTaskDashboard`

**优先级**：P1

**后台对接**：

| 数据 | 类型 | 获取方式 |
|---|---|---|
| 任务队列 | `VideoScrapeTaskQueue` | DI 容器获取 |
| 任务列表 | `IReadOnlyCollection<VideoScrapeTask>` | `GetAllTasks()` |
| 进度报告 | `VideoTaskProgress` | `GetProgress()` |
| 任务取消 | `Cancel(taskId)` / `CancelAll()` | 实例方法 |
| 任务完成/失败事件 | `TaskCompleted` / `TaskFailed` | 事件订阅 |

**注意**：`VideoScrapeTaskQueue` 尚未接入 DI。需要在 `App.xaml.cs` 中注册：

```csharp
services.AddSingleton<VideoScrapeTaskQueue>(sp => new VideoScrapeTaskQueue(
    maxConcurrency: sp.GetRequiredService<ConfigService>().Current.VideoScraping?.Concurrency ?? 4,
    logger: sp.GetRequiredService<ILogger>()));
```

**UI 结构**（作为视频 Tab 下的独立子页面，导航项 `video.tasks`）：

```
VideoTaskDashboard
├── 顶部统计栏
│   ├── 总任务: 42 | 运行中: 2 | 等待: 5 | 完成: 33 | 失败: 2
│   └── 操作按钮: [全部取消] [重试失败]
│
├── 任务列表 (DataGrid 或虚拟化列表)
│   ├── 列: 番号 | 类型 | 状态 | 进度 | 来源详情 | 耗时 | 操作
│   ├── 状态着色:
│   │   Pending → 灰色
│   │   Running → 蓝色脉动
│   │   WaitingRetry → 橙色
│   │   Completed → 绿色
│   │   Failed → 红色
│   │   Cancelled → 灰色删除线
│   ├── 操作列:
│   │   Pending/WaitingRetry → [取消] [立即重试]
│   │   Running → [取消]
│   │   Failed → [重试]
│   │   Completed → [查看详情]
│
├── 任务详情面板 (点击某任务展开)
│   ├── 番号: SSIS-405
│   ├── 类型: ScrapeVideo
│   ├── 尝试次数: 2/3
│   ├── 错误信息: (如果有)
│   ├── 来源执行记录列表:
│   │   ├── javbus  ✓ 成功  980ms
│   │   ├── javdb   ✗ 被拦截  cloudflare_challenge  3020ms
│   │   └── airav   ✓ 成功  820ms
│   └── 字段来源归属:
│       ├── title → airav
│       ├── coverUrl → javbus
│       └── actors → javbus+javdb
│
└── 底部日志滚动区 (可折叠)
    └── 最近 20 条完成日志 ← VideoTaskProgress.RecentLogs
```

**实现要点**：

1. 创建 `VideoTaskDashboard.xaml` + `.cs` 在 `Views/` 目录
2. 在 `VideoNavSectionProvider` 中将 `video.tasks` 的 `IsEnabled` 改为 `true`
3. 在 `MainWindow.OnNavItemClicked` 中处理 `"video.tasks"`，调用 `ShowVideoWithNav("tasks")`
4. 在 `VideoView` 中增加 `SwitchNav("tasks")` 分支，显示任务面板
5. 订阅 `VideoScrapeTaskQueue.ProgressChanged` 事件刷新列表
6. 刷新频率由 DispatcherTimer 控制（建议 500ms 间隔）

**状态颜色参考**（复用现有主题 Token）：

| 状态 | 前景色 | 背景色 |
|---|---|---|
| Completed | `SuccessBrush` 或 `PrimaryBrush` | `HoverBgBrush` |
| Running | `PrimaryBrush` | `HoverBgBrush` |
| Failed | `DangerBrush` | `HoverBgBrush` |
| WaitingRetry | `WarningBrush` | `HoverBgBrush` |
| Pending | `TextSecondaryBrush` | `Transparent` |
| Cancelled | `TextDisabledBrush` | `Transparent` |

---

### 3.3 来源执行状态 / 字段溯源面板 — `ScrapeReportPanel`

**优先级**：P1

**后台对接**：

| 数据 | 类型 | 说明 |
|---|---|---|
| 聚合结果 | `VideoScrapeAggregate` | 包含 FieldSources + Attempts |
| 字段溯源 | `Dictionary<string, string>` | 每个字段来自哪个来源 |
| 来源尝试 | `List<VideoSourceAttempt>` | 每个来源的结果/耗时 |

**当前问题**：`VideoItem` 类中没有 `FieldSources` 和 `Attempts` 字段。需要在 `VideoScrapeService.ScrapeAsync` 完成后，将 `VideoScrapeAggregate` 的 `FieldSources` 和 `Attempts` 持久化到 `video-scrape-metadata.json` 侧车文件。

**侧车文件结构**（设计文档第13节）：

```json
// config/video-scrape-metadata.json
{
  "items": {
    "<videoItemId>": {
      "fieldSources": {
        "title": "airav",
        "coverUrl": "javbus",
        "actors": "javbus+javdb"
      },
      "attempts": [
        { "sourceId": "javbus", "outcome": "Success", "elapsedMs": 980, "at": "..." },
        { "sourceId": "javdb", "outcome": "Blocked", "reason": "cloudflare", "elapsedMs": 3020, "at": "..." },
        { "sourceId": "airav", "outcome": "Success", "elapsedMs": 820, "at": "..." }
      ],
      "lastAggregateAt": "2026-08-27T12:00:00Z"
    }
  }
}
```

**UI 结构**：嵌入 VideoView 的本地详情面板（`DetailScroll` 区域内），在"观看与文件"区块下方：

```
ScrapeReportPanel (Border, 可折叠)
├── 标题行: "刮削报告" + 来源状态摘要 (✓2/3 成功) + [展开/收起]
│
├── 字段溯源表 (Grid, 两列)
│   ├── 标题      → airav         (Badge: 来源名)
│   ├── 简介      → airav
│   ├── 封面      → javbus
│   ├── 演员      → javbus+javdb
│   ├── 标签      → javbus+javdb
│   ├── 评分      → javdb
│   ├── 原题      → dmm
│   └── 预览图    → javbus+dmm
│
└── 来源执行列表 (每行一个来源)
    ├── [icon] javbus    ✓ 成功    980ms
    ├── [icon] javdb     ✗ 被拦截  cloudflare_challenge  3020ms
    ├── [icon] airav     ✓ 成功    820ms  (缓存命中)
    └── [icon] dmm       ✗ 未启用  跳过
```

**实现要点**：

1. 新建 `ScrapeReportService`（或在 `VideoLibraryService` 中添加方法）读写 `video-scrape-metadata.json`
2. 在 `ScrapeReportService` 中提供 `GetReport(videoItemId)` 方法返回 `FieldSources` + `Attempts`
3. 在 `VideoView.RenderDetail` 中调用该方法，填充 `ScrapeReportPanel`
4. 面板中的来源名称需要映射为中文显示名，可通过 `IVideoScrapeSourceRegistry.Get(id).DisplayName` 获取
5. 缓存命中状态通过 `VideoSourceOutcome.CacheHit` 判断

---

### 3.4 多源在线视频搜索页 — `OnlineVideoSearchView`

**优先级**：P2

**后台对接**：

| 数据 | 类型 | 说明 |
|---|---|---|
| 在线源列表 | `IEnumerable<IVideoSource>` | DI 容器注册的所有 `IVideoSource` |
| 搜索结果 | `OnlineVideoSearchResult` | 包含 `List<OnlineVideoSummary>` + 分页 |
| 在线视频详情 | `OnlineVideoSummary` | CoverUrl, Title, Number, DetailUrl 等 |

**当前问题**：
- `MissAvSource` 是唯一注册的 `IVideoSource`
- 搜索结果卡片在 `VideoView.xaml.cs` 和 `OnlineVideoView.xaml.cs` 中内联构建，没有独立控件
- `VideoSearchPanel.SourceSelector` 中的 Hanime1/SupJav/JableTV 被标记为 `IsEnabled=False`

**需要做的**：

1. 在 `IVideoSource` 接口上确认是否需要扩展（例如 `GetDetailAsync` 方法返回完整元数据）
2. 为每个在线源创建独立的 `XXXSource : IVideoSource` 实现并注册到 DI
3. 创建 `OnlineVideoCard.xaml` 控件（复用 `VideoCard.xaml` 的样式模式）
4. 创建 `OnlineVideoDetailView.xaml` 页面（类似 `VideoView` 的详情面板，但数据来自在线源）
5. 启用 `VideoSearchPanel.SourceSelector` 中的源选项

**UI 结构**：

```
OnlineVideoSearchView (替换当前 SearchPage 区域)
├── 顶部: 搜索框 + 源切换下拉框 (已存在于 SourceTabs)
├── 结果网格: OnlineVideoCard 网格 (已有 VirtualizedCardGrid 可复用)
│   └── 每个 OnlineVideoCard:
│       ├── 封面图 (异步加载)
│       ├── 标题 (TextWrapping)
│       ├── 番号 (如果可用)
│       ├── 评分 (如果可用)
│       └── 操作: [查看详情] [刮削到本地]
├── 分页栏 (已有)
└── 详情弹窗/页面 (新增):
    ├── 完整封面
    ├── 标题 + 原题
    ├── 演员列表
    ├── 标签
    ├── 简介
    ├── 预览图
    ├── [下载到本地] 按钮 → 将在线元数据写入 VideoItem 并触发刮削
    └── [在浏览器打开] 按钮
```

**实现要点**：

1. 检查 `OnlineVideoSummary` 是否有足够的字段，必要时扩展
2. 如果在线源不提供详情 API，考虑用 AngleSharp 抓取详情页
3. "下载到本地" 功能需要：
   - 用户先选择本地目录
   - 创建 `VideoItem` 并填充在线搜索结果的元数据
   - 触发 `VideoScrapeService` 用本地文件重新刮削
4. 卡片样式参考 `VideoCard.xaml`（`Controls/` 目录）

---

### 3.5 演员档案页 — `ActorProfileView`

**优先级**：P2

**后台对接**：

| 数据 | 类型 | 来源 |
|---|---|---|
| 演员档案 | `VideoActorMetadata` | `VideoActorMerger.MergeAsync()` |
| 演员来源 | `IVideoActorSource` | Minnano/GFriends/Wikipedia/JavDB |
| 本地作品 | `List<VideoItem>` | `VideoLibraryService.Query(actorFilter=name)` |

**当前问题**：
- `IVideoActorSource` 系列接口尚未接入 DI
- 没有独立的演员页面

**需要先做的**：在 `App.xaml.cs` 中注册演员来源：

```csharp
// 需要先确认各 Source 的构造函数参数
services.AddSingleton<IVideoActorSource, MinnanoSource>();
services.AddSingleton<IVideoActorSource, WikipediaActorSource>();
// GFriendsSource 和 JavDB 的演员部分可能需要特殊处理
```

**UI 结构**（点击视频详情页中的演员标签时打开）：

```
ActorProfileView
├── 顶部: 演员头像 + 姓名 + 别名列表
│
├── 基本信息区
│   ├── 生日: 1990-01-01
│   ├── 出生地: 日本东京
│   ├── 身高: 165cm
│   └── 来源链接: [minnano] [wikipedia] [javdb]
│
├── 本地作品区 (标题: "本地收藏")
│   └── 卡片网格: 按该演员筛选的本地 VideoItem
│       └── 每个卡片: VideoFileCard (已有控件)
│
└── 在线资料区 (标题: "在线资料")
    └── 来源信息列表: 每个来源的原始链接 + 数据摘要
```

**实现要点**：

1. 创建 `ActorProfileView.xaml` + `.cs` 在 `Views/` 目录
2. 入口：`VideoView.RenderDetail` 中的 `ActorChips` 点击事件
3. 当前 `ActorChips` 的点击调用 `FilterByActor`，需要改为打开 `ActorProfileView`
4. 异步调用 `VideoActorMerger.MergeAsync(actorName, ct)` 获取档案
5. 本地作品通过 `_library.Query(new VideoQueryOptions { ActorFilter = actorName })` 获取
6. 头像从 `VideoActorMetadata.ImageUrls` 加载（优先 GFriends 的 GitHub 头像）

---

### 3.6 缓存管理页 — `VideoCacheView`

**优先级**：P3

**后台对接**：

| 数据 | 类型 | 说明 |
|---|---|---|
| 缓存目录 | `config/video-source-cache/` | 按番号组织 |
| 单条缓存 | `DiskJsonSnapshotCache` | GetAsync / ClearAsync / ClearAllAsync |
| 资源存储 | `DiskVideoResourceStore` | GetAllRecords / CleanupAsync |

**UI 结构**（嵌入刮削设置页底部，或作为独立页面）：

```
VideoCacheView
├── 缓存概览
│   ├── 快照缓存: 142 条, 8.3 MB
│   ├── 图片资源: 89 条, 156.7 MB
│   └── 缓存目录: config/video-source-cache/
│
├── 操作按钮
│   ├── [清除全部快照缓存]
│   ├── [清理无引用图片资源]
│   └── [打开缓存目录]
│
└── 按来源统计 (可选)
    ├── javbus: 42 条快照
    ├── javdb: 38 条快照
    ├── airav: 62 条快照
    └── dmm: 0 条快照
```

**实现要点**：

1. 遍历 `config/video-source-cache/` 目录统计条数和大小
2. "清除全部" 调用 `DiskJsonSnapshotCache.ClearAllAsync()`
3. "清理无引用" 调用 `DiskVideoResourceStore.CleanupAsync(path => ...)` 需要传入引用检查函数
4. 引用检查函数：遍历 `VideoLibraryService.Items` 中所有 `CoverPath`/`PosterPath`/`PreviewImages`

---

### 3.7 推荐页 — `VideoRecommendView`

**优先级**：P3

**后台对接**：`VideoLibraryService` 的现有查询能力，无需新后台服务。

**数据来源**：
- **最近添加**：`_library.Query(sortBy: AddedDesc, pageSize: 20)`
- **高评分**：`_library.Query(sortBy: ScoreDesc, pageSize: 20)`
- **按系列分组**：`_library.GetSeriesCounts()` + 按系列查询
- **按演员分组**：`_library.GetActorCounts()` + 按演员查询

**UI 结构**：

```
VideoRecommendView
├── 横向滚动区: "最近添加"
│   └── VideoFileCard 网格 (VirtualizedCardGrid)
│
├── 横向滚动区: "高评分"
│   └── VideoFileCard 网格
│
├── 横向滚动区: "热门系列"
│   └── 每个系列一个标题 + VideoFileCard 横向列表
│
└── 横向滚动区: "热门演员"
    └── 每个演员一个标题 + VideoFileCard 横向列表
```

**实现要点**：

1. 在 `VideoNavSectionProvider` 中将 `video.recommend` 的 `IsEnabled` 改为 `true`
2. 创建 `VideoRecommendView.xaml` + `.cs` 在 `Views/` 目录
3. 复用 `VideoFileCard` 控件
4. 每个横向区域使用 `ScrollViewer HorizontalScrollBarVisibility="Auto"` + `StackPanel Orientation="Horizontal"`

---

## 4. 文件组织与命名约定

### 4.1 新增文件的预期位置

```
src/ResourceGrab.App/
├── Views/
│   ├── ScrapeSettingsView.xaml + .cs        ← 3.1
│   ├── VideoTaskDashboard.xaml + .cs        ← 3.2
│   ├── OnlineVideoDetailView.xaml + .cs     ← 3.4
│   ├── ActorProfileView.xaml + .cs          ← 3.5
│   ├── VideoRecommendView.xaml + .cs        ← 3.7
│   └── VideoCacheView.xaml + .cs            ← 3.6 (或嵌入 ScrapeSettingsView)
│
├── Controls/
│   ├── OnlineVideoCard.xaml + .cs           ← 3.4
│   ├── ScrapeReportPanel.xaml + .cs         ← 3.3
│   ├── SourceStatusBadge.xaml + .cs         ← 3.2/3.3 共用
│   └── FieldSourceRow.xaml + .cs            ← 3.3
│
├── Dialogs/
│   └── (无需新增，设置页嵌入 SettingsDialog)
│
src/ResourceGrab.Core/
├── Services/
│   ├── VideoScrapeService.cs                ← 需要修改：刮削完成后保存 FieldSources
│   └── ScrapeReportService.cs              ← 3.3 新增：读写 video-scrape-metadata.json
│
├── Models/
│   └── VideoScrapeModels.cs                 ← 需要修改：添加 Advanced 属性
│
├── Services/VideoScrape/
│   └── VideoResourceStore.cs                ← 已实现，需确认 DI 注册
```

### 4.2 代码风格约定

基于现有代码的模式：

**控件代码风格**：

```csharp
// 1. code-behind 模式，无独立 ViewModel 文件
// 2. 构造函数中 InitializeComponent + 获取服务 + 订阅事件
public partial class ScrapeSettingsView : UserControl
{
    private readonly ConfigService _config;
    private VideoScrapeAdvancedSettings _settings = new();

    public ScrapeSettingsView()
    {
        InitializeComponent();
        _config = App.Services.GetRequiredService<ConfigService>();
        Loaded += (_, _) => LoadSettings();
    }

    private void LoadSettings() { /* 读取 _config.Current.VideoScraping */ }
    private void SaveSettings_Click(object sender, RoutedEventArgs e) { /* 保存 */ }
}
```

**XAML 风格**：

```xml
<!-- 1. 使用 DynamicResource 引用主题 Token -->
<!-- 2. 使用现有样式：LibraryPrimaryButtonStyle / LibraryActionButtonStyle / GhostButtonStyle -->
<!-- 3. 按钮内 Path 图标用 StaticResource IconXxx -->
<!-- 4. 卡片边框使用 HoverBgBrush + CardBorderBrush + CornerRadius -->
<Border Style="{StaticResource CardPanelStyle}" Padding="16" Margin="0,0,0,14">
    <StackPanel>
        <TextBlock Text="标题" FontSize="13" FontWeight="Bold" />
        <!-- 内容 -->
    </StackPanel>
</Border>
```

**异步加载模式**：

```csharp
// 使用 CancellationTokenSource 防止页面卸载后的回调
private CancellationTokenSource? _loadCts;

private async void OnLoaded()
{
    _loadCts?.Cancel();
    _loadCts = new CancellationTokenSource();
    var ct = _loadCts.Token;

    ShowLoading();
    try
    {
        var data = await SomeService.GetAsync(ct);
        if (ct.IsCancellationRequested) return;
        Dispatcher.Invoke(() => RenderData(data));
    }
    catch (Exception ex)
    {
        if (!ct.IsCancellationRequested)
            ToastService.ShowError(ex, "加载失败：");
    }
    finally { HideLoading(); }
}
```

---

## 5. 实施路线图

### Phase 1: 基础设施 (需要先完成)

| 步骤 | 内容 | 涉及文件 |
|---|---|---|
| 1.1 | 在 `VideoScrapeSettings` 中添加 `Advanced` 属性 | `Models/VideoScrapeModels.cs` |
| 1.2 | 在 `ConfigService` 中添加读写 `VideoScrapeAdvancedSettings` 的方法 | `Services/ConfigService.cs` |
| 1.3 | 将 `VideoScrapeTaskQueue` 注册到 DI | `App.xaml.cs` |
| 1.4 | 创建 `ScrapeReportService` 读写 `video-scrape-metadata.json` | 新文件 |
| 1.5 | 修改 `VideoScrapeService.ScrapeAsync` 完成后保存 FieldSources | `Services/VideoScrapeService.cs` |
| 1.6 | 将 `IVideoScrapeSourceRegistry` + Legacy sources 注册到 DI | `App.xaml.cs` |

### Phase 2: P0 页面

| 步骤 | 内容 | 涉及文件 |
|---|---|---|
| 2.1 | 实现 `ScrapeSettingsView` | 新文件 + `SettingsDialog.xaml` 修改 |
| 2.2 | 在 SettingsDialog 视频 Tab 中嵌入 ScrapeSettingsView | `Dialogs/SettingsDialog.xaml` |

### Phase 3: P1 页面

| 步骤 | 内容 | 涉及文件 |
|---|---|---|
| 3.1 | 启用 `video.tasks` 导航项 | `Shell/Providers/VideoNavSectionProvider.cs` |
| 3.2 | 实现 `VideoTaskDashboard` | 新文件 |
| 3.3 | 在 `MainWindow.OnNavItemClicked` 中路由 video.tasks | `MainWindow.xaml.cs` |
| 3.4 | 实现 `ScrapeReportPanel` | 新文件 |
| 3.5 | 在 `VideoView.RenderDetail` 中嵌入 ScrapeReportPanel | `Views/VideoView.xaml` + `.cs` |

### Phase 4: P2 页面

| 步骤 | 内容 | 涉及文件 |
|---|---|---|
| 4.1 | 创建 `OnlineVideoCard` 控件 | 新文件 |
| 4.2 | 改造 `OnlineVideoView` 使用 `OnlineVideoCard` | `Views/OnlineVideoView.xaml` + `.cs` |
| 4.3 | 启用 SourceSelector 中的源选项 | `Views/VideoSearchPanel.xaml` |
| 4.4 | 实现 `ActorProfileView` | 新文件 |
| 4.5 | 修改 `VideoView` 中演员标签点击事件 | `Views/VideoView.xaml.cs` |
| 4.6 | 注册演员来源到 DI | `App.xaml.cs` |

### Phase 5: P3 页面

| 步骤 | 内容 | 涉及文件 |
|---|---|---|
| 5.1 | 启用 `video.recommend` 导航项 | `Shell/Providers/VideoNavSectionProvider.cs` |
| 5.2 | 实现 `VideoRecommendView` | 新文件 |
| 5.3 | 实现 `VideoCacheView` | 新文件（或嵌入 ScrapeSettingsView） |

---

## 6. 关键注意事项

### 6.1 VideoScrapeAdvancedSettings 与 VideoScrapeSettings 的关系

`VideoScrapeAdvancedSettings` 是 `VideoScrapeSettings` 的扩展。前者定义在 `Services/VideoScrape/VideoScrapeConfigExtensions.cs`，后者定义在 `Models/VideoScrapeModels.cs`。当前 `Config` 类通过 `Config.VideoScraping` 持有 `VideoScrapeSettings`。

建议将 `VideoScrapeAdvancedSettings` 作为 `VideoScrapeSettings` 的嵌套属性：

```csharp
// Models/VideoScrapeModels.cs
public sealed class VideoScrapeSettings
{
    // ... 现有字段 ...
    [JsonPropertyName("advanced")] public VideoScrapeAdvancedSettings? Advanced { get; set; }
}
```

这样序列化到 `config.json` 时自动包含高级配置，无需新增文件。

### 6.2 旧版 VideoScrapeService 与新版 Pipeline 的共存

当前 `VideoScrapeService` 直接依赖 `JavBusScraper` / `JavDbScraper` / `AiravScraper`（旧版）。新版 M1-M12 的 `IVideoScrapeSource` + `VideoFetchGraphScheduler` 是独立的 Pipeline。

**过渡策略**：
1. 保留旧版 `VideoScrapeService.ScrapeAsync` 作为默认刮削入口
2. 新增一个方法 `ScrapeAsync` 使用新版 Pipeline（通过 `IVideoScrapeSourceRegistry` + `VideoFetchGraphScheduler`）
3. 在设置页中添加开关让用户选择使用旧版/新版
4. 默认使用旧版，新版作为实验性功能

### 6.3 侧车文件 video-scrape-metadata.json

这是设计文档第13节定义的数据持久化方案。`VideoItem` 本身不包含 `FieldSources` 和 `Attempts`，这些数据存储在独立文件中。

**文件路径**：`AppPaths.ConfigDir + "/video-scrape-metadata.json"`

**API 设计**（`ScrapeReportService`）：

```csharp
public sealed class ScrapeReportService
{
    public VideoScrapeReport? GetReport(string videoItemId);
    public void SaveReport(string videoItemId, VideoScrapeAggregate aggregate);
    public void RemoveReport(string videoItemId);
    public int GetTotalCacheSize();  // 字节
}

public sealed class VideoScrapeReport
{
    public Dictionary<string, string> FieldSources { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<VideoSourceAttempt> Attempts { get; set; } = [];
    public DateTimeOffset LastAggregateAt { get; set; }
}
```

### 6.4 OnlineVideoSummary 字段确认

当前 `OnlineVideoSummary`（`Sources/VideoSources/MissAvSource.cs`）可能字段有限。需要确认并可能扩展：

```csharp
// 需要至少包含这些字段才够用：
public string Title { get; set; }
public string Number { get; set; }       // 番号（如果能解析）
public string CoverUrl { get; set; }
public string DetailUrl { get; set; }    // 详情页 URL
public string Actor { get; set; }        // 演员（如果有）
public string Duration { get; set; }     // 时长（如果有）
public string Tags { get; set; }         // 标签（如果有）
```

### 6.5 DI 注册清单（新增）

以下是实现前端页面时需要在 `App.xaml.cs` 中添加的 DI 注册：

```csharp
// 新版刮削系统
services.AddSingleton<IVideoScrapeSourceRegistry, VideoScrapeSourceRegistry>();
services.AddSingleton<IVideoScrapeSource, LegacyJavBusSource>();
services.AddSingleton<IVideoScrapeSource, LegacyJavDbSource>();
services.AddSingleton<IVideoScrapeSource, LegacyAiravSource>();
// ... 其他已实现的 Source（DmmSource, AvsoxSource 等）

// 任务队列
services.AddSingleton<VideoScrapeTaskQueue>();

// 抓取图调度器
services.AddSingleton<VideoFetchGraphScheduler>();

// 字段合并器
services.AddSingleton<VideoMetadataMerger>();

// 快照缓存
services.AddSingleton<IVideoSourceSnapshotCache>(sp =>
    new DiskJsonSnapshotCache(
        Path.Combine(AppPaths.ConfigDir, "video-source-cache"),
        sp.GetRequiredService<ILogger>()));

// 资源存储
services.AddSingleton<IVideoResourceStore>(sp =>
    new DiskVideoResourceStore(
        Path.Combine(AppPaths.ConfigDir, "video-resources"),
        null));  // httpFactory 需要按实际情况传入

// 刮削报告
services.AddSingleton<ScrapeReportService>();

// 演员来源（按需）
services.AddSingleton<IVideoActorSource, MinnanoSource>();
services.AddSingleton<IVideoActorSource, WikipediaActorSource>();
services.AddSingleton<VideoActorMerger>();
```

### 6.6 可用图标参考

`Themes/Icons.cs` 和 `Themes/Core.Controls.xaml` 中可用的图标：

| 名称 | 用途 | 适合的页面 |
|---|---|---|
| `Icons.Search` | 搜索 | 在线搜索 |
| `Icons.Setting` | 设置 | 刮削设置 |
| `Icons.TaskProgress` | 任务 | 任务监控 |
| `Icons.Local` | 本地 | 本地视频 |
| `Icons.Rank` | 排行 | 推荐 |
| `Icons.Refresh` | 刷新 | 重扫描 |
| `Icons.Check` | 完成 | 保存/成功 |
| `Icons.Info` | 信息 | 校验提示 |
| `Icons.Close` | 关闭/错误 | 校验错误 |
| `Icons.FolderOpen` | 文件夹 | 打开目录 |
| `Icons.Play` | 播放 | 播放按钮 |
| `Icons.Document` | 文档 | NFO 文件 |
| `Icons.History` | 历史 | 浏览历史 |
| `Icons.Heart` / `HeartOutline` | 收藏 | 收藏按钮 |
| `Icons.FolderPlus` | 添加文件夹 | 添加目录 |
| `Icons.PanelToggle` | 面板切换 | 展开/收起 |
| `Icons.SignOut` | 退出 | 退出按钮 |
| `Icons.ChevronLeft/Right/Up/Down` | 箭头 | 展开/收起/导航 |
| `Icons.Loading` | 加载中 | 加载动画 |

### 6.7 现有样式参考

| 样式名 | 用途 | 适用场景 |
|---|---|---|
| `LibraryPrimaryButtonStyle` | 主要操作按钮 | 保存、确认 |
| `LibraryActionButtonStyle` | 次要操作按钮 | 取消、重试 |
| `GhostButtonStyle` | 幽灵按钮 | 分页、展开 |
| `IconButtonStyle` | 纯图标按钮 | 关闭、切换 |
| `NavRailStyle` | 左侧导航 | 导航项 |
| `CardPanelStyle` | 卡片面板 | 设置区块容器 |
| `CountBadgeStyle` | 数量徽章 | 统计数字 |
| `SearchFieldBorderStyle` | 搜索框边框 | 搜索输入 |
| `LibrarySortComboBoxStyle` | 下拉框 | 排序/筛选 |
| `VideoChipStyle` | 标签/演员芯片 | 标签显示 |

---

## 7. 验收标准

每个页面完成后应满足：

1. **数据正确**：UI 展示的数据与后台服务返回值一致
2. **异步安全**：所有网络/IO 操作使用 `CancellationToken`，页面卸载时不崩溃
3. **状态完整**：加载中、空状态、错误状态、正常状态均有 UI 展示
4. **样式一致**：遵循现有主题 Token（`DynamicResource XxxBrush`），不硬编码颜色
5. **交互完整**：所有按钮有 `Click` 处理，所有文本有 `TextWrapping` 或 `TextTrimming`
6. **内存安全**：异步操作的 `CancellationTokenSource` 在 `Unloaded` 时 `Cancel()`
7. **导航可达**：通过左侧导航栏或点击操作可达该页面
8. **返回可退**：支持返回上一页（通过 `ShellNavigator.Back()` 或手动导航）

---

## 8. 参考文件索引

实现时需要频繁参考的文件：

| 文件 | 用途 |
|---|---|
| `App.xaml.cs` | DI 注册、路由注册、视图实例化 |
| `MainWindow.xaml.cs` | 导航处理、页面切换、侧栏控制 |
| `Shell/ShellController.cs` | 路由状态管理 |
| `Shell/ShellNavigator.cs` | 导航跳转 API |
| `Shell/Providers/VideoNavSectionProvider.cs` | 视频导航栏定义 |
| `Views/VideoView.xaml` + `.cs` | 本地视频主页面（需修改嵌入报告面板） |
| `Views/VideoSearchPanel.xaml` + `.cs` | 右侧筛选面板（需启用多源） |
| `Controls/VideoFileCard.xaml` + `.cs` | 本地视频卡片（推荐页复用） |
| `Controls/VideoCard.xaml` + `.cs` | 在线视频卡片 |
| `Dialogs/SettingsDialog.xaml` + `.cs` | 设置对话框（需嵌入刮削设置） |
| `Themes/Icons.cs` | 矢量图标库 |
| `Themes/Core.Controls.xaml` | 主题样式 + XAML 图标 |
| `Services/VideoScrape/VideoScrapeModels.cs` | M0-M3 核心模型 |
| `Services/VideoScrape/VideoScrapeInterfaces.cs` | M1 来源接口 |
| `Services/VideoScrape/VideoScrapeConfigExtensions.cs` | M2 配置模型 |
| `Services/VideoScrape/VideoTaskModels.cs` | M5 任务模型 |
| `Services/VideoScrape/VideoFetchGraphScheduler.cs` | M7 抓取图调度 |
| `Services/VideoScrape/VideoResourceStore.cs` | M6 资源存储 |
| `Services/VideoScrape/VideoActorModels.cs` | M11 演员模型 |
| `Models/VideoScrapeModels.cs` | 基础刮削配置 |
| `Models/VideoFolder.cs` | VideoItem / VideoFolder 定义 |
