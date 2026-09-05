# 漫画 / 视频 / 小说 UI 统一合并执行计划

> 本文档合并并取代以下两份文档：
>
> - `MEDIA_UI_UNIFICATION_EXECUTION_PLAN.md`（执行计划，提供阶段划分、验收标准、令牌体系）
> - `UI-REDESIGN-PROPOSAL.md`（重构方案，提供 `文件:行` 级现状诊断、BrowserToolbar、双壳详情、播放器细节）
>
> 合并时已对代码实地核对（2026-09-05），两份原文档中与代码现状不符的部分已修正。
> 原「新建组件」的表述一律改为按实际状态标注：已存在 / 待接线 / 待补全 / 缺失。

---

## 1. 目标

统一三类内容的 UI 骨架，而不是把三种媒体强行做成同一种内容形态。归纳为八件事：

1. 统一左侧边栏的视觉、宽度、交互和数据驱动方式（`NavRail` 已存在，收尾接线）。
2. 统一右侧边栏为受控插槽 `SidebarHost`（已存在，未接管主窗口右栏）。
3. 媒体类型切换由 `ShellController` 统一决定左栏、中栏、右栏状态（已存在，接线未完成）。
4. 页面内部操作只替换中间面板路由，删除 `VideoView` 内部第二层导航。
5. 统一全局圆角、间距、字体层级、图标语言和颜色语义（令牌体系）。
6. 统一漫画、小说、视频的卡片模型（`MediaCard` + 适配器已完成大半，收尾迁移）。
7. 统一 loading / empty / error / retry / result 状态处理（`MediaResultHost` 已存在，无页面使用）。
8. 统一结果网格、分页和筛选面板契约（`Filtering` 契约已存在，三面板接入未完成）。

本计划不改变下载、刮削、索引扫描、阅读进度等业务规则。重构过程中只允许为适配 UI 抽取接口或映射模型，不重写数据层。

---

## 2. 代码现状盘点（2026-09-05 实地核对）

### 2.1 已存在且已接线

| 组件 | 位置 | 状态 |
| --- | --- | --- |
| `NavRail` | `Controls/NavRail.xaml` | 已存在，已嵌入 `MainWindow.xaml:160` 的 `LeftNavHost` |
| `ShellController` | `Shell/ShellController.cs` | 已存在 |
| `ShellNavigator` | `Navigation/ShellNavigator.cs` | 已存在 |
| `MediaKind` / `ShellRoute` / `SidebarPolicy` / `NavModels` | `Shell/` | 已存在 |
| `MangaNavSectionProvider` / `VideoNavSectionProvider` / `NovelNavSectionProvider` | `Shell/Providers/` | 已存在 |
| `Filtering` 模型 | `Filtering/FilterModels.cs` | 已存在 |

### 2.2 已存在但未接线 / 未完成迁移

| 组件 | 位置 | 缺口 |
| --- | --- | --- |
| `SidebarHost` | `Controls/SidebarHost.xaml` | 主窗口右栏仍是裸 `RightPanelHost`（`MainWindow.xaml:289`），未由 `SidebarHost` 承载 |
| `MediaResultHost` | `Controls/MediaResultHost.xaml` | **没有任何 Views 引用**，列表页未接入 |
| `MediaCard` + `MediaCardAdapters` | `Controls/MediaCard.xaml`、`ViewModels/` | 适配器迁移未完成，旧卡片仍被大量 View 引用 |
| `FilterSidebar` | `Controls/FilterSidebar.xaml` | 三个筛选面板（`LocalSearchPanel` / `NovelSearchPanel` / `VideoSearchPanel`）未接入契约 |
| `ShellNavigator` 路由注册 | — | `Shell/Routes/` 目录不存在，路由注册表未建立 |

### 2.3 仍缺失（需新增）

| 组件 | 用途 | 来源 |
| --- | --- | --- |
| `BrowserToolbar` | 统一「搜索框 + 筛选 chips + 排序 + 卡片尺寸」工具栏，替换现存 3 种手搓工具栏 | 重构方案 §2.2 |
| `MediaDetailHost` | 统一详情内容模板（海报 + 信息表 + 简介 + 区段 + 操作栏） | 重构方案 §2.4 |
| `DetailDrawerShell` / `DetailPageShell` | 详情双外壳：右侧悬浮抽屉（快速预览）+ 整页覆盖 | 重构方案 §2.4 |

### 2.4 待删除（迁移完成后）

| 文件 | 说明 |
| --- | --- |
| `Controls/AlbumCard.xaml` / `.cs` + `ViewModels/AlbumCardViewModel.cs` | 在线漫画旧卡 |
| `Controls/LocalComicCard.xaml` / `.cs` | 本地漫画旧卡 |
| `Controls/VideoCard.xaml` / `.cs` | 视频旧卡残留 |
| `Controls/VideoPosterCard.xaml.cs` | 双击判定逻辑先上移到 `MediaCard` 再删 |
| `Views/VideoView.xaml` / `.cs` | 拆分完成后删除（或缩成薄协调层） |

仍引用旧卡片的 View（盘点时逐一处理）：`ActorListView`、`ActorProfileView`、`CategoryBrowseView`、`CategoryView`、`FavoriteView`、`LocalView`、`RankBrowseView`、`RankView`、`SearchView`、`VideoRecommendView`、`VideoView`、`WeeklyView`。

### 2.5 必须保留的既有行为（易回退点）

1. **三播放器接力**：`VideoView.xaml.cs` 的 `TransferPlayback`（侧栏播放器 / 整页播放器 / 小窗 `PopoutPlayerWindow` 之间保持进度接力）。拆分 `VideoView` 时原样搬到新的 `VideoSearchView`。
2. **280ms 双击判定窗口**：`VideoPosterCard.xaml.cs:30-80` 是刻意设计（避免侧栏遮罩吞掉双击），上移到 `MediaCard` 时必须保留，不能退化成「单击立即开侧栏」。
3. **`DetailShell` / `ReaderShell`**：已是好设计，沉浸式阅读/播放保持独立，不并入浏览层。
4. 漫画侧 `CardGridViewBase` → `StateHostControl` → `VirtualizedCardGrid` 四层抽象继续作为所有列表页的地基。

---

## 3. 原两文档冲突点的裁定

| 冲突 | 裁定 |
| --- | --- |
| 卡片路线：执行计划「新建 MediaCard 体系」 vs 方案「MediaCard 已存在」 | **以代码为准：已存在**。剩余工作是补齐适配器并迁移调用点，不新建 |
| 详情外壳：执行计划「单一 DetailShell」 vs 方案「双壳（抽屉 + 整页）」 | **采用双壳**。一套 `MediaDetailHost` 内容模板，包 `DetailDrawerShell`（620 悬浮抽屉）或 `DetailPageShell`（整页路由）；整页详情走 `ShellNavigator` 路由，两案不互斥 |
| 导航机制：执行计划「ShellNavigator 路由」 vs 方案「沿用 PageHost.Content 直接赋值」 | **采用 ShellNavigator**。`PageHost.Content = new XxxView()` 是被诊断的问题本身，阶段 E 的直接赋值写法废弃 |
| 封面比例：漫画 3:4 vs 2:3；视频 16:9/2:3 vs 1.5:1 | **统一为**：漫画纵向 2:3；视频横向 1.5:1（本地视频可选 2:3 海报态）；小说文本封面 3:2。数值进 token，由 `MediaVisualKind` 决定 |
| 抽屉宽度 620 vs 右栏 token 上限 440 | **互不冲突**：`DetailDrawerShell` 是悬浮层不属于右栏 splitter，宽度独立为 `DetailDrawerWidth` token（620） |

---

## 4. 目标架构

### 4.1 三层壳结构

```text
AppShell
├── TopBar：品牌、媒体类型切换、全局操作、窗口控制
├── LeftNavRail：NavRail，由当前媒体类型的 NavSectionProvider 提供导航项
├── CenterRouteHost：搜索、列表、详情、阅读器等中间路由（唯一主内容宿主）
└── RightSidebarHost：SidebarHost 承载的筛选 / 上下文 / 任务面板
```

规则：

1. 顶部始终负责「漫画 / 视频 / 小说」一级切换。
2. 左侧边栏由当前媒体类型的 `NavSectionProvider` 提供，`NavRail` 只渲染不理解业务。
3. 中间面板唯一入口是 `ShellNavigator` 路由；禁止散落的 `PageHost.Content` 赋值。
4. 右侧边栏只是上下文附属区域，四种模式：`Hidden` / `Filter` / `Context` / `Task`。
5. 阅读、播放等沉浸式页面可临时隐藏左右侧栏，但由壳状态机控制。

### 4.2 路由与导航

沿用已存在的 `ShellRoute` / `SidebarPolicy` / `ShellNavigator` 契约，补建 `Shell/Routes/` 注册表：

```text
manga.search  manga.rank  manga.category  manga.weekly  manga.favorites
manga.local   manga.chapter  manga.reader.local  manga.reader.online

video.search  video.recommend  video.library  video.detail  video.tasks
video.actors  video.actor-profile

novel.index   novel.reader
```

导航服务职责：每 `MediaKind` 独立返回栈、切类型恢复最近路由、按路由声明更新左右侧栏、后退语义、重复路由去重。

### 4.3 视频导航项（对齐漫画侧）

```text
视频
├ 在线
│  ├ 在线搜索   (VideoSearchView)
│  ├ 推荐       (VideoRecommendView)
│  └ 兜底源     (复用 VideoSearchView)
├ 本地
│  ├ 视频库     (VideoLocalView)
│  └ 刮削工具   (ScrapeToolsView)
├ 任务看板      (VideoTaskDashboard)
└ 演员
   ├ 列表       (ActorListView)
   └ 档案       (ActorProfileView)
```

每个子页是独立 View，`NavRail` 切换经 `ShellNavigator` 替换中间路由——和漫画侧完全一致。删除 `VideoView` 内部导航（`SwitchNav` / `NavSearch_Click` / `NavRecommend_Click` / `NavLocal_Click`）。

---

## 5. 组件规格

### 5.1 `BrowserToolbar`（新增）

统一「搜索框 + 源/筛选 chips + 排序 + 卡片尺寸滑杆」组合，替换现存三种手搓工具栏：

| 页面 | 现状 |
| --- | --- |
| 漫画搜索 | `SearchView.xaml:13-47` `CardPanelStyle` + `SearchBoxControl` + `SourceTabs` + `CardSizeSlider` |
| 视频在线搜索 | 同款样式内联在 `VideoView.xaml:87-119` |
| 视频本地 | 无外框，标题 + 排序 + 滑杆平铺（`VideoView.xaml:562-587`） |

各段可参数化隐藏；基于已有的 `SearchBoxControl` / `CardSizeSlider` 组合实现。

### 5.2 `MediaDetailHost` + 双外壳（新增）

一套详情内容模板：

```text
MediaDetailHost
├── MediaBlock：海报 / 播放器槽位（视频 16:9 / 漫画 2:3）
├── TitleRow：番号 + 评分（RatingControl）
├── InfoTable：发行日 / 演员（可点）/ 类型 / 厂商
├── Synopsis：可选中复制
├── TagChips：点击即搜索
├── GalleryStrip：剧照横滚
├── MediaSections：视频 = 磁力列表 / 同系列 / 同演员；漫画 = 章节列表 / 评论
└── ActionBar：播放 / 下载 / 刮削 / 编辑 / 打开目录 / 收藏
```

两种外壳（内容相同，chrome 不同）：

- `DetailDrawerShell`：右侧悬浮抽屉（宽 620），快速预览，点遮罩关闭；有流地址时 16:9 播放器自动起播。
- `DetailPageShell`：整页覆盖列表区，返回经路由回栈；大播放器 + 双列信息。

迁移对象：`VideoView` 内联详情（约 268 行抽屉 + 117 行本地侧栏）、`LocalComicDetailPanel`、`ChapterView`。最终全项目只有一种详情模板 + 两种外壳。

### 5.3 `MediaCard`（收尾）

`MediaCard` + `MediaCardViewModel` + `MediaCardAdapters` 已存在。剩余工作：

| 媒体对象 | 适配器 | 比例 | 角标/状态 |
| --- | --- | --- | --- |
| `ComicAlbum`（漫画在线） | `ComicAlbumCardAdapter` | 2:3 | 章节数 |
| `LocalComic`（漫画本地） | `LocalComicCardAdapter` | 2:3 | 已读/未读、章节数 |
| `OnlineVideoSummary`（视频在线） | `OnlineVideoCardAdapter` | 1.5:1 | 有流可播 ▶ |
| `VideoItem`（视频本地） | `VideoCardAdapter` | 1.5:1 | 刮削状态、收藏 |

约束：封面比例固定不撑开；标题两行、副标题一行；标签最多三项溢出 tooltip；hover 动作用矢量图标按钮（禁 emoji）；动作一律 command 注入；不同媒体只换 accent / badge / ratio，不复制卡片 XAML。卡片交互统一：**单击开抽屉详情（280ms 双击判定窗口内让位）、双击进整页、hover 露动作按钮**。

### 5.4 `MediaResultHost`（接入）

已存在但零使用。组合：`ResultState` 状态机（`Idle/Loading/Ready/Empty/Error/NoConfiguration/PartialError`）+ `StateHostControl` + `VirtualizedCardGrid` + `Pagination`。所有列表页禁止再手写一组状态 `StackPanel` 逐个切 `Visibility`。列数由 host 统一管理，页面不再各自 override 列宽计算。分页统一 `PageRequest` / `PageResult<T>` 模型，本地全量数据内存分页也走同一模型。

### 5.5 筛选契约（收尾）

`Filtering/FilterModels.cs` 与 `FilterSidebar` 已存在。剩余工作：三个面板（`LocalSearchPanel` / `NovelSearchPanel` / `VideoSearchPanel`）实现 `IFilterPanel`、挂到 `SidebarHost`、事件收敛到 `FilterSelection`、左键包含 / 右键排除 / 清除 / 计数展示一致。第一批只迁样式不重写查询逻辑。

### 5.6 设计令牌

沿用执行计划令牌体系，在 `Themes/Tokens.xaml` 补齐：

- 圆角：`RadiusXs 6 / Sm 8 / Md 10 / Lg 12 / Xl 16 / Pill 999`，视图 XAML 禁止新的硬编码 `CornerRadius`。
- 间距：`SpacingXs 4 / Sm 8 / Md 12 / Lg 16 / Xl 20`。
- 尺寸：`LeftRailWidth 220`（现 196，迁移时收敛）、`RightSidebarMin 300 / Default 348 / Max 440`、`DetailDrawerWidth 620`。
- 字号：`FontCaption 11 / FontBody 12.5 / FontEmphasis 13 / FontCardTitle 13 / FontSectionTitle 15 / FontPageTitle 18`。
- 图标：可点击图标一律 `Themes/Icons.xaml` 矢量 Path，禁 emoji；补齐 `Play / Pause / HeartFilled / StarFilled / Check / Exclude / Chevron* / FontIncrease / History / TaskProgress` 等 key；星级抽 `RatingControl` 共用。
- 媒体强调色：`Manga = Primary`、`Novel = NovelAccent`、`Video = VideoAccent`；类型 badge 与进度条用媒体色，选中态用全局 primary，错误/成功/警告只用全局语义色。

---

## 6. 迁移路线

每阶段独立编译、运行、可合并。**推荐每次只迁移一个页面或一个媒体类型。**

### Phase 0：现状盘点与基线冻结

1. 核对 §2 盘点表，逐项确认「已接线 / 未接线」状态（本文档核对时点为 2026-09-05，后续代码可能再推进）。
2. 记录 build / test 命令可用性；确认本地漫画扫描、视频库查询、小说索引读取、阅读历史保存的测试通过。
3. 建立手工 smoke 清单：浅/深主题、最小 1200x750、漫画搜索/排行/分类/本地/阅读、视频搜索/本地/详情/刮削、小说列表/筛选/翻页/阅读。
4. 新增 `docs/ui-unification-checklist.md`，后续每阶段勾选。

**验收**：项目可编译；现有测试通过；smoke 清单有记录；盘点表每个条目有明确状态。

### Phase 1：令牌、圆角和图标收敛

1. 扩展 `Themes/Tokens.xaml`（radius / spacing / sidebar 宽度 / 字号 / `DetailDrawerWidth`）。
2. 清理 `Cards.xaml` 等主题文件中的特殊圆角覆盖；按钮、输入框、chip、panel、reader container 改语义 token。
3. 盘点硬编码颜色：`VideoView.xaml.cs`、`VideoFileCard`（若仍引用）、`LocalComicDetailPanel.xaml.cs`、`NovelReaderView.xaml.cs`。
4. 文字符号（星级/心形/箭头/播放）替换为矢量图标，新增缺失 icon key。

**验收**：三主题无明显对比度回退；同类控件圆角一致；主流程无 emoji 功能图标；编译 + smoke 通过。

### Phase 2：Shell 接线收尾

1. `MainWindow` 从 `LeftNavHost` / `PageHost` / `RightPanelHost` 直通结构切换为由 `ShellController` 状态驱动；`MainWindow` 只保留事件转发 + 状态订阅。
2. `RightPanelHost` 改由 `SidebarHost` 承载；`DownloadPanel` → Task、三个 SearchPanel → Filter、`LocalComicDetailPanel` → Context。
3. 右栏宽度与 splitter 控制移入 `ShellController`；页面禁止访问 `MainWindow.RightPanelHost` 或改列宽。
4. 建立 `Shell/Routes/` 注册表（§4.2 全部路由），`MainWindow` 内直接切换内容的分支改为 navigator 调用。
5. 每类型独立返回栈；切媒体类型恢复最近路由与最近 sidebar 模式。

**验收**：漫画行为不变；类型切换不丢返回栈；右栏内容按类型正确替换/隐藏；阅读器进入右栏自动隐藏；不再有页面直接引用 `PageHost`。

### Phase 3：BrowserToolbar 统一工具栏

1. 新增 `BrowserToolbar`，从 `SearchView.xaml:13-47` 抽出组合并参数化各段可选。
2. 漫画搜索页先接入验证不回归，再迁视频在线搜索、视频本地、排行/分类/每周/收藏。

**验收**：三类页面工具栏布局、间距、控件一致；卡片尺寸滑杆行为不变。

### Phase 4：拆分 VideoView，NavRail 全量接管视频子页

1. 新建 `VideoSearchView`（在线搜索）：`BrowserToolbar` + `StateHostControl` + `VirtualizedCardGrid` + `MediaCard`，对齐 `SearchView` 结构；`TransferPlayback` 三播放器接力原样搬入。
2. 新建 `VideoLocalView`（本地库）：对齐 `LocalView` 结构，详情侧栏换 `DetailDrawerShell` + `MediaDetailHost`。
3. `VideoRecommendView`、`ScrapeToolsView`、`VideoTaskDashboard`、`ActorListView`、`ActorProfileView` 接入 `VideoNavSectionProvider` 全量路由。
4. 删除 `VideoView` 内部导航（`SwitchNav` 系列）与所有 `Visibility` 子页切换；`VideoView` 缩成薄协调层或直接删除。

**验收**：视频全部子页经 NavRail 可达；搜索 → 详情 → 播放全链路（含小窗接力）无回退；千条结果滚动流畅。

### Phase 5：MediaResultHost 全面接入

1. 漫画在线搜索、排行、分类、每周、收藏迁移。
2. 本地漫画列表、小说列表迁移。
3. 视频本地列表迁移，删除 `WrapPanel` + `Children.Clear()` 方案。
4. 统一分页控件与 page size 行为；`GridCellSizer` 收编为 host 内部细节。

**验收**：所有列表页 loading / empty / error 样式一致；漫画/小说大数据量保持虚拟化；视频列表流畅滚动；空态文案区分「库为空」与「筛选后为空」。

### Phase 6：MediaCard 收尾

1. 补齐四适配器，覆盖旧卡片全部能力（角标、hover 按钮、280ms 双击窗口、收藏心、刮削徽章、多选）。
2. 迁移顺序：视频列表 → 小说列表 → 本地漫画 → 在线漫画。
3. 每完成一个适配器做一次截图对比；删旧卡片前跑全量 smoke。

**验收**：标题/副标题/tag/badge/progress/hover 一致；无封面小说统一 text cover；旧卡片文件删除后编译通过。

### Phase 7：详情统一

1. 新增 `MediaDetailHost`、`DetailDrawerShell`、`DetailPageShell`。
2. 视频在线详情（抽屉 + 整页）、视频本地详情迁入双壳。
3. 漫画章节详情 `ChapterView` 换 `DetailPageShell`；本地漫画详情 `LocalComicDetailPanel` 换 `DetailDrawerShell`（列表页保留轻量 context 快速预览）。
4. 交互统一：单击开抽屉、双击/「完整详情 →」进整页、`⛶ 放大` 进 `PopoutPlayerWindow`。

**验收**：详情返回、标题截断、主按钮位置一致；有流视频抽屉自动起播；返回不污染列表筛选状态；阅读历史不回退。

### Phase 8：筛选契约收尾

1. 三面板实现 `IFilterPanel`、挂 `SidebarHost`、样式统一、事件收敛 `FilterSelection`。
2. 第二批把查询参数转换收敛到 provider（`MangaLocalFilterProvider` / `NovelFilterProvider` / `VideoFilterProvider`）。
3. 左键包含 / 右键排除 / 清除 / 计数一致；筛选状态切类型后正确恢复。

**验收**：三面板视觉与交互一致；视频复杂筛选项完整可用；空结果文案区分两种空。

### Phase 9：清理与固化

1. 删除旧卡片、`VideoView` 内部导航、页面内手写状态面板、`[Obsolete]` 兼容 key。
2. 更新 `docs/UI_COMPONENT_UNIFICATION.md` 标注被本文档接替；在贡献文档加入 UI 规则摘要（新页面走 route、新列表用 `MediaResultHost`、新卡片继承 `MediaCardViewModel`、新筛选实现 `IFilterPanel`、新图标用矢量资源）。

**验收**：无未引用旧 UI 文件；无新增硬编码圆角/颜色/emoji；三媒体全量 smoke 通过；测试通过。

---

## 7. 回归测试矩阵

每阶段至少覆盖：

```text
媒体类型：漫画在线 / 漫画本地 / 漫画阅读 / 视频在线搜索 / 视频本地库 / 视频详情 /
          视频刮削 / 小说列表 / 小说筛选 / 小说阅读

布局状态：默认 1440x900 / 最小 1200x750 / 左右栏收起展开 / 右栏拖到最小最大 /
          浅色主题 / 深色主题 / DPI 100% / 125% / 150%

数据状态：无配置 / 加载中 / 空库 / 筛选后为空 / 网络错误 / 局部资源失败 /
          超过一页 / 超过 1000 条
```

关键链路（每阶段必测）：**视频：搜索 → 单击抽屉 → 双击整页 → 三播放器接力 → 小窗 → 返回列表抽屉态恢复**；**漫画：搜索 → 详情 → 阅读 → 返回 → 筛选状态保留**。

---

## 8. 明确不做的事

1. 不在本轮统一下载器、登录、设置弹窗的全部视觉细节。
2. 不重写视频刮削器和视频元数据模型。
3. 不把小说文本阅读改成图片阅读逻辑。
4. 不为统一而删除视频独有的番号、演员、系列、分辨率等信息。
5. 不在 UI 重构期间顺手修改外部站点解析逻辑。
6. 不并入沉浸式阅读/播放（`DetailShell` / `ReaderShell` 保持独立）。

---

## 9. 完成定义

1. 顶部类型切换是唯一第一级媒体入口。
2. 三种媒体共享同一个 `NavRail` 和 `SidebarHost`。
3. 中间内容全部由 `ShellNavigator` 路由驱动，无散落的 `PageHost.Content` 赋值。
4. 所有结果列表使用 `MediaResultHost`（同一状态容器 + 虚拟化网格 + 统一分页）。
5. 所有卡片来自 `MediaCard` + 适配器，旧卡片文件删除。
6. 详情只有 `MediaDetailHost` 一种模板 + 双外壳，旧内联详情删除。
7. 三个筛选面板遵循同一 `IFilterPanel` 契约与视觉规格。
8. 圆角、间距、字号、颜色、图标均有语义 token。
9. 三种媒体在全量回归矩阵下无功能性回退。
