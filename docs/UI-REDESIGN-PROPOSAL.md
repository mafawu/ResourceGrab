# 界面重构方案：统一漫画 / 视频浏览与在线搜索体验

> **⚠️ 本文档已被取代**（2026-09-05）：合并后的最新版见 [`UI统一重构合并执行计划.md`](UI统一重构合并执行计划.md)。
> 本文的现状诊断（`文件:行` 证据）与 BrowserToolbar / 双壳详情 / 播放器接力等设计已合并进新版；导航机制按新版裁定统一走 `ShellNavigator` 路由。

> 基于 2026-09-04 对全部界面文件的实地核对。先讲清「现在的问题」，再给「应该长什么样」，
> 最后是可落地的迁移路径。每条问题都附 `文件:行` 证据。

---

## 一、现状问题诊断

### 1.1 漫画侧 vs 视频侧：两套工程化水平

| 维度 | 漫画侧 | 视频侧 |
| --- | --- | --- |
| 视图拆分 | 每页独立 View（`SearchView`/`LocalView`/`CategoryView`...） | **`VideoView.xaml` 665 行塞 6 个子页**（Search/Recommend/Local/Task/Actor/ActorProfile），全靠 `Visibility` 切换 |
| 结果网格 | `StateHostControl` + `VirtualizedCardGrid`（虚拟化） | 在线搜索用**裸 `WrapPanel`**（`VideoView.xaml:126` `ResultCardsPanel`），手动 `Children.Add`，**不虚拟化** |
| 状态机 | `StateHostControl` 统一 empty/loading/error/results 四态 | 三个 StackPanel 手动 `Visibility` 切换（`:129` `OnlineEmptyState` / `:138` `OnlineLoadingState`） |
| 卡片模板 | 共享 `ComicCardResultTemplate` + `AlbumCard`/`LocalComicCard` | `VideoFileCard`（已标 `[Obsolete]`）+ `VideoPosterCard` + `MediaCard` 三套并存，迁移未完成 |
| 分页 | 统一的 `PagingPanel` 模式 | 在线与本地两套分页，样式不一致 |

**结论**：漫画侧有清晰的「`CardGridViewBase` 基类 → `StateHostControl` 状态宿主 → `VirtualizedCardGrid` 虚拟化网格 → 共享卡片模板」四层抽象；视频侧完全没用上，是 VideoView 自己手搓的。

### 1.2 详情面板：四种并存的模式

| 模式 | 位置 | 用于 | 行数 |
| --- | --- | --- | --- |
| `DetailShell`（通用详情壳） | `Controls/DetailShell.xaml` | 漫画章节详情 | 简洁 |
| `LocalComicDetailPanel`（侧栏） | `Views/LocalComicDetailPanel.xaml` | 漫画本地详情 | 中等 |
| `OnlineDetailDrawer`（悬浮抽屉）+ `OnlineFullDetailPage`（整页） | `VideoView.xaml:151-418` | 视频在线详情 | **268 行内联** |
| `DetailScroll`（内联侧栏） | `VideoView.xaml:436-552` | 视频本地详情 | **117 行内联** |

四种详情 UI 渲染的内容本质相同（海报 + 番号 + 评分 + 标签 + 简介 + 操作按钮），但布局、样式、交互各不相同。用户从「在线搜索结果」点到「本地库同名影片」时，详情面板长得完全不一样。

### 1.3 导航：双重导航栏

- `MainWindow.xaml:160` 有一个 196px 的 `LeftNavHost`（`NavRail` 数据驱动，按媒体类型生成分组）。
- `VideoView.xaml:21` **又自带一个 196px 导航栏**（`Visibility="Collapsed"`，死代码），与主窗导航栏样式一致但从未显示。

视频子页的切换（搜索/推荐/本地/任务/演员）本应由 `MainWindow` 的 `NavRail` 统一管，现在却被 VideoView 内部用 `Visibility` 切换，导致：
- 主窗 NavRail 的视频分组只有「本地」一个入口，其他子页进不去（要靠 VideoView 内部切，但那个导航栏又是隐藏的）。
- 用户实际只能通过顶部 Kind 切换 + 某个按钮才能到任务看板/演员列表。

### 1.4 工具栏：三种不同的搜索/筛选栏

| 页面 | 工具栏样式 | 证据 |
| --- | --- | --- |
| 漫画搜索 | `CardPanelStyle` 边框 + `SearchBoxControl` + `SourceTabs` + `CardSizeSlider` | `SearchView.xaml:13-47` |
| 视频在线搜索 | 同款 `CardPanelStyle` 但内联在 VideoView，源筛选用 `SourceTabs` WrapPanel | `VideoView.xaml:87-119` |
| 视频本地 | **无外框**，标题 + 排序 + `CardSizeSlider` + 按钮平铺 | `VideoView.xaml:562-587` |

同样是「搜索框 + 源/筛选 + 排序 + 卡片尺寸」的组合，三种不同的布局和间距。

### 1.5 其他结构性问题

- **`OnlineVideoView`**（`Views/OnlineVideoView.xaml`）是孤儿代码，与 `VideoView.SearchPage` 功能重叠。
- **`VideoFileCard`** 已标 `[Obsolete]` 仍在 `VideoView.xaml:595` 使用，替代品 `MediaCard` 未完成。
- **本地视频详情侧栏**（`DetailScroll`）552 行全内联在 VideoView，无法复用。
- 在线搜索结果用 `WrapPanel` + 手动 `Children.Add`，几百条结果时内存和滚动都会卡。

---

## 二、目标设计：统一媒体浏览层

### 2.1 设计原则

1. **漫画 / 视频同一套浏览骨架**——只有卡片宽高比和详情区段不同。
2. **每个页面独立 View**，不靠巨型 `Visibility` 切换。
3. **复用漫画侧已有的四层抽象**（`CardGridViewBase` / `StateHostControl` / `VirtualizedCardGrid` / 共享卡片模板），视频侧对齐。
4. **一种详情模板，两种外壳**（紧凑抽屉 / 整页富详情），内容区段按媒体类型组合。

### 2.2 统一页面骨架

所有「列表型」页（搜索 / 本地 / 分类 / 排行 / 收藏 / 推荐）统一长这样：

```
┌─ BrowserToolbar ──────────────────────────────────────────┐
│ [搜索框............] [源/筛选 chips] [排序▾] [卡片尺寸滑杆] │
├─ StateHostControl（empty / loading / error / results）────┤
│                                                            │
│   ┌──┐ ┌──┐ ┌──┐ ┌──┐ ┌──┐                                │
│   │卡│ │卡│ │卡│ │卡│ │卡│   ← VirtualizedCardGrid        │
│   │片│ │片│ │片│ │片│ │片│     （横向/纵向卡片自适应）      │
│   └──┘ └──┘ └──┘ └──┘ └──┘                                │
│                                                            │
├─ PaginationHost ──────────────────────────────────────────┤
│ ‹ 上一页    第 2 / 15 页    下一页 ›   [跳转][每页30▾]      │
└────────────────────────────────────────────────────────────┘
```

**新控件**：`BrowserToolbar`（搜索框 + 筛选 chips + 排序 + 卡片尺寸，可选隐藏任一段），替换现在三种手搓工具栏。

### 2.3 统一卡片：完成 MediaCard 迁移

`MediaCard` + `MediaCardAdapters` 已存在但未完成迁移。目标：

| 媒体对象 | 适配器 | 卡片宽高比 | 角标/状态 |
| --- | --- | --- | --- |
| `ComicAlbum`（漫画在线结果） | `ComicAlbumCardAdapter` | 纵向 2:3 | 章节数 |
| `LocalComic`（漫画本地） | `LocalComicCardAdapter` | 纵向 2:3 | 已读/未读、章节数 |
| `OnlineVideoSummary`（视频在线结果） | `OnlineVideoCardAdapter` | **横向 1.5:1** | 有流可播 ▶ |
| `VideoItem`（视频本地） | `VideoCardAdapter` | 横向 1.5:1 | 刮削状态、收藏 ♥ |

一个 `MediaCard`，适配器决定宽高比和角标，彻底删除 `VideoFileCard`、`VideoPosterCard` 各自的实现。卡片交互统一：单击开侧栏详情、双击进整页详情、hover 露动作按钮。

### 2.4 统一详情：MediaDetailHost（一套模板，两种外壳）

```
┌─ MediaDetailHost ────────────────────────────────────┐
│ [海报 / 播放器槽位]        ← 视频 16:9 / 漫画 2:3      │
│                                                       │
│ 番号  ★评分       ← 标题行                              │
│ 标题（可换行）                                          │
│                                                       │
│ ┌─ 信息表 ──────────┐  ┌─ 简介 ─────────────────────┐ │
│ │ 发行日 2024-01-01  │  │                            │ │
│ │ 演员  A/B/C（可点）│  │  （可选中复制）              │ │
│ │ 类型  标签胶囊     │  │                            │ │
│ │ 厂商  ...          │  └────────────────────────────┘ │
│ └───────────────────┘                                 │
│                                                       │
│ [标签 chips（点击搜索）]                                │
│ [剧照横滚]                                             │
│ [媒体特有区段]                                          │
│   视频：磁力列表 / 同系列推荐 / 同演员                   │
│   漫画：章节列表 / 评论                                 │
│ [操作栏：播放/下载/刮削/编辑/打开目录/收藏]               │
└───────────────────────────────────────────────────────┘
```

**两种外壳**（内容相同，chrome 不同）：
- **`DetailDrawerShell`**：右侧悬浮抽屉（宽 620），快速预览，点遮罩关闭。对应现在的 `OnlineDetailDrawer`。
- **`DetailPageShell`**：整页覆盖列表区，返回按钮回到列表。对应现在的 `OnlineFullDetailPage`。

一套 `MediaDetailHost` 内容模板，包在哪个外壳里由交互决定。**删除** `VideoView` 内联的 268 行抽屉 + 117 行本地侧栏 + `LocalComicDetailPanel`，统一到这里。

### 2.5 导航：统一由 MainWindow NavRail 管

```
漫画                              视频
├ 发现                            ├ 在线搜索
│  ├ 搜索        (SearchView)     │  ├ 搜索     (VideoSearchView)
│  ├ 排行        (RankView)        │  ├ 推荐     (VideoRecommendView)
│  ├ 分类        (CategoryView)    │  └ 兜底源   (复用 VideoSearchView)
│  └ 每周必看    (WeeklyView)      ├ 本地        (VideoLocalView)
├ 我的                            ├ 刮削工具    (ScrapeToolsView)
│  ├ 本地        (LocalView)       ├ 任务看板    (VideoTaskDashboard)
│  └ 收藏        (FavoriteView)    └ 演员
└ 工具                               ├ 列表     (ActorListView)
   └ 抓取工具    (ScrapeToolsView)    └ 档案     (ActorProfileView)
```

每个子页是独立 View，NavRail 切换时 `PageHost.Content` 替换整个视图——和漫画侧完全一致。**删除 VideoView 内部那个隐藏的 196px 导航栏**和所有 `Visibility` 子页切换。

### 2.6 在线搜索的交互流（视频 & 漫画统一）

```
搜索关键词
  → StateHostControl 显示 loading
  → 结果回来：VirtualizedCardGrid 渲染 MediaCard 列表
  → 单击卡片：右侧 DetailDrawerShell 滑入（快速预览，不离开结果列表）
      ├ 有流地址：抽屉内 16:9 播放器自动起播
      └ 无流：显示海报 + 「▶ 预览」按钮（hover 才点）
  → 双击卡片 / 抽屉内「完整详情 →」：DetailPageShell 整页覆盖
      └ 整页大播放器 + 双列信息 + 磁力 + 同系列推荐
  → 「⛶ 放大」：PopoutPlayerWindow 独立窗口（三处播放器接力保持不变）
  → 返回：回到结果列表，抽屉态恢复
```

漫画侧的「单击开侧栏、双击进整页」交互也已存在于 `VideoPosterCard`（`VideoPosterCard.xaml.cs:30-80` 的双击判定窗口），这套逻辑直接上移到 `MediaCard` 即可，漫画侧也获得同样的双击进整页详情能力。

---

## 三、迁移路径（按依赖顺序）

### 阶段 A · 抽公共控件（不破坏现有）

1. **`BrowserToolbar`**：从 `SearchView.xaml:13-47` 抽出「搜索框 + chips + 排序 + 卡片尺寸」组合，参数化各段可选。先让漫画搜索页用它，验证不回归。
2. **`MediaDetailHost`**：从 `VideoView.xaml:151-418` 的抽屉内容区抽成独立控件，入参 `(mediaItem, host配置)`。先在视频在线详情处引用它，行为不变。
3. **`DetailDrawerShell` / `DetailPageShell`**：两个外壳控件，内部包 `MediaDetailHost`。

### 阶段 B · 完成 MediaCard 统一

4. 补全 `MediaCardAdapters` 的四个适配器（`ComicAlbum`/`LocalComic`/`OnlineVideoSummary`/`VideoItem`），覆盖现有 `AlbumCard`/`LocalComicCard`/`VideoPosterCard`/`VideoFileCard` 全部能力。
5. `VideoView.xaml:595` 的 `VideoFileCard` 换成 `MediaCard` + `VideoCardAdapter`；`ActorProfileView:54` 同样替换。
6. 删除 `VideoFileCard`、`VideoPosterCard`、`AlbumCard`、`LocalComicCard`（或保留为薄封装转发到 MediaCard）。

### 阶段 C · 拆 VideoView

7. 新建 `VideoSearchView`（在线搜索），用 `BrowserToolbar` + `StateHostControl` + `VirtualizedCardGrid` + `MediaCard`，对齐 `SearchView` 结构。把 `VideoView` 在线搜索逻辑搬过来，结果改用虚拟化网格。
8. 新建 `VideoLocalView`（本地库），对齐 `LocalView` 结构。把 `VideoView.xaml:555-637` 的本地页搬出来，详情侧栏换成 `DetailDrawerShell` + `MediaDetailHost`。
9. `VideoRecommendView` 已是独立 View，接入 NavRail。
10. 删除 `VideoView` 内部隐藏导航栏（`:21-74`）和 `OnlineVideoView` 孤儿代码。
11. `VideoView` 缩成薄协调层（或直接删除，NavRail 直接切子页）。

### 阶段 D · 详情统一

12. 漫画章节详情 `ChapterView` 换用 `DetailPageShell` + `MediaDetailHost`（漫画段：章节列表）。
13. 漫画本地详情 `LocalComicDetailPanel` 换用 `DetailDrawerShell` + `MediaDetailHost`。
14. 视频本地详情 `DetailScroll`（552 行内联）删除，用 `DetailDrawerShell` + `MediaDetailHost`。
15. 至此全项目只有一种详情内容模板 + 两种外壳。

### 阶段 E · NavRail 接线

16. `VideoNavSectionProvider` 补全「在线搜索 / 推荐 / 本地 / 刮削工具 / 任务看板 / 演员」全部子页路由。
17. `MainWindow.OnNavItemClicked` 的视频分支改为像漫画分支一样 `PageHost.Content = new VideoSearchView()` 等直接替换，不再进 VideoView 内部切换。

---

## 四、收益对比

| 指标 | 现状 | 重构后 |
| --- | --- | --- |
| `VideoView.xaml` 行数 | 665（含 6 子页） | 0（拆分删除）或 < 50（薄协调） |
| 在线搜索结果网格 | 裸 `WrapPanel`，不虚拟化 | `VirtualizedCardGrid`，千条流畅 |
| 详情 UI 种类 | 4 种（`DetailShell`/`LocalComicDetailPanel`/抽屉+整页/`DetailScroll`） | 1 种模板 (`MediaDetailHost`) + 2 种外壳 |
| 卡片控件种数 | 4（`AlbumCard`/`LocalComicCard`/`VideoFileCard`/`VideoPosterCard`） | 1（`MediaCard` + 适配器） |
| 工具栏样式 | 3 种 | 1 种（`BrowserToolbar`） |
| 视频子页可达性 | 部分隐藏（内部 `Visibility` 切换，导航栏死） | 全部经 NavRail 可达 |
| 漫画/视频体验一致性 | 低（两套工程化水平） | 高（同一套浏览骨架） |
| `OnlineVideoView` 孤儿代码 | 存在 | 删除 |

---

## 五、风险与取舍

1. **`MediaCard` 适配器要覆盖四种卡片全部能力**（角标、hover 按钮、双击判定、收藏心、刮削状态徽章），是最容易遗漏的点。建议每完成一个适配器就做一次截图对比，确保视觉和行为与原卡片一致。
2. **视频在线搜索的 `VideoPosterCard` 双击判定窗口**（280ms 单击延迟）是刻意设计，避免侧栏遮罩吞双击——`MediaCard` 要保留这套逻辑，不能退化成「单击立即开侧栏」。
3. **阶段 C 拆 VideoView 时**，三播放器接力（侧栏/整页/小窗，`VideoView.xaml.cs` 的 `TransferPlayback`）要原样搬到 `VideoSearchView`，不能丢。
4. **不要一次性大重构**。阶段 A 抽控件时不改调用方行为，阶段 B/C/D 每步保持可编译可运行，每拆一个子页就验证一次搜索→详情→播放全链路。
5. **`DetailShell`/`ReaderShell` 已是好设计**，沉浸式阅读/播放保持独立，不并入这套浏览层——本方案只统一「列表 + 详情」型页面。