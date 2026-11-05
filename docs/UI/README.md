# ResourceGrab UI 复刻交付清单

> 适用工程：`F:\ProcessProject\C#\ResourceGrab`
> 目标框架：`.NET 10 / WPF`
> 设计基线：OpenDesign 高保真原型（1440×900，最小 1200×750）
> 文档定位：交付清单与验收索引，不替代视觉稿和页面规格。

## 0. 结论先行

1. 工程已有完整壳层、主题字典、五类内容与下载页的路由，以及主要页面。复刻不是从零开发，而是“对齐视觉、补齐规格、补全状态、建立验收”。
2. 原型与工程存在 7 处硬差异（见 §2）。不先裁定就动 XAML，必然返工。
3. 建议按“小说分区 → 全局壳层 → 短剧/下载/搜索 → 漫画/视频/写真”的顺序推进。小说规格最全，适合做样板。
4. 当前最缺的不是页面数量，而是：页面级规格、状态矩阵、1200 宽截图基线、可签字的验收记录。

## 1. 范围与边界

### 1.1 要做

- 壳层：左导航、顶栏、右侧栏、返回栈、下载角标、主题切换。
- 内容：漫画、视频、小说、短剧、写真五类媒体，以及下载页。
- 非路由页面：详情、章节、阅读器、设置、登录、目录选择、编辑弹窗。
- 适配：浅色/暗色；1440×900 与 1200×750；100% 与 200% DPI 抽检。
- 状态：加载、空、错误、重试、离线、禁用、悬停、选中、键盘焦点。

### 1.2 不做

- 不改 `ResourceGrab.Core` 的抓取、解析、反爬与下载逻辑。
- 不新增内容源或业务功能。
- 不把原型以 WebView 形式嵌入 WPF。
- 不在未裁定的情况下整体替换现有 DSO 设计系统。

## 2. 必须先裁定的 7 项差异

差异草案已落到 `docs/UI/DECISIONS.md`：D-01、D-03 待确认，其余默认执行。未确认前不批量修改 `Themes/*`。

| # | 项目 | 原型（目标） | 现有 WPF | 建议 | 影响面 |
|---|---|---|---|---|---|
| 1 | 全局主强调色 | `#445CF0` / 暗色 `#7C92FF` | `#4D6BFE` / 暗色 `#6E86FF` | 全量复刻则统一为原型值；只做小说分区则保留 DSO 主色 | 所有按钮、选中态、焦点环 |
| 2 | 小说强调色 | `#5B43D6`，soft `#EFEBFF`；暗色 `#9480FF`，soft `#1B1732` | `NovelAccentBrush` 为 `#7C5CFF` / 暗色 `#A08CFF`，无 soft 键 | 采用原型值，并新增 `NovelSubtleBrush` | 小说进度、角标、当前章节、标签 |
| 3 | 左栏宽度 | `224` | `MainWindow.xaml` 为 `196` + 左外边距 `14`（合计 `210`）；`Tokens.xaml` 的 `216` 未被使用 | 去掉左外边距，`LeftNavHost` 设 `224`，`LeftRailWidth` 同步 `224`；不要叠加成 `238` | 所有页面横向栅格与卡片列数 |
| 4 | 页面标题字号 | 列表页 `23`，详情页 `25` | `FontSizePageTitle = 19` 仅 `ScrapeToolsView` 使用；页面另有 `19/20/22` 硬编码 | 统一两套字体令牌，并按页面替换硬编码；阅读器章节标题 `27` 单列 | 所有页头视觉重心 |
| 5 | 封面圆角 | 小说/漫画 `12`；视频横图 `10`；写真 `10` | 小说 `12`；漫画 `14`；视频横图 `12`；写真 `12` | 漫画改 `12`；视频横图改 `10`；写真保留 `12` 并记为已知差异；面板 `16` 不动 | 书卡、视频卡、写真卡 |
| 6 | 小说封面比例 | `2:3`（高 = 宽 × 1.5） | `States.xaml` 的 `NovelCoverRatio` 参数为 `1.3333`，即 `3:4` | 采用 `2:3`；同步修改 `States.xaml` 与 `design/novel-online/SPEC.md` | 书卡高度、每行数量、详情封面 |
| 7 | 导航项高度 | `36` | `NavRail.xaml` 显式 `Height="42"` | 改为 `36`，保留 1px 上下间距；按钮高度 `32` 不变 | 导航密度与一屏可容纳项数 |

裁定原则：

- 先确定“全量复刻”还是“小说先行”。前者一次性统一全局值；后者只新增小说局部键，避免影响漫画/视频。
- 同一语义只保留一个令牌。禁止在页面里写死颜色、圆角、字号来绕过差异。
- 每项裁定都要记录影响页面与回归范围，落到 `docs/UI/DECISIONS.md`。

## 3. 需要输出的内容

### D01 差异裁定记录

- 必须包含：§2 七项的最终结论、影响页面、回归范围、确认人、日期。
- 落点：`docs/UI/DECISIONS.md`。
- 现状：`DECISIONS.md` 已给出默认口径，D-01、D-03 待确认。
- 完成判定：七项全部有结论，没有“待定”。

### D02 令牌与主题映射

- 必须包含：原型变量 → WPF 资源键 → 浅色值 → 暗色值 → 使用场景；字号、间距、圆角、控件高度、左栏宽度。
- 必须补齐：`NovelSubtleBrush`；封面圆角键（书/视频分开）；列表页与详情页标题键。
- 落点：`docs/UI/TOKENS.md`，并同步 `Themes/Colors.xaml`、`Themes/DarkColors.xaml`、`Themes/Tokens.xaml`。
- 现状：三个字典已存在；缺映射文档与小说 soft 色。
- 完成判定：每个原型令牌都有唯一 WPF 键；页面无新增硬编码色值；深浅切换正常。

### D03 页面规格

- 必须包含：目标与入口、布局结构、组件与尺寸、数据与命令、五种状态、文案、主题/DPI/键盘、截图、验收项。
- 落点：`docs/UI/pages/<media>-<page>.md`，模板见 §5。
- 现状：`design/novel-online`、`design/short-drama`、`design/download-page`、`design/dso-search-redesign` 已有静态稿；漫画、视频、写真、设置缺页面规格。
- 完成判定：所有 P0 页面有规格；每份规格至少覆盖加载、空、错误、离线、禁用五种状态。

### D04 路由与壳层映射

- 必须包含：路由 ID → View → 导航项 → `SidebarPolicy` → 返回目标；非路由页面的进入与退出路径。
- 落点：`docs/UI/ROUTES.md`，以 `MainWindow.xaml.cs` 的 `RegisterRoutes()` 为准。
- 现状：已有 30 余条路由；`novel.book`、短剧 `history/favorite` 未注册；详情/阅读器多为页面内打开。
- 完成判定：映射表无未知项；每个入口都有明确返回目标；左栏高亮与当前媒体一致。

### D05 组件规格

- 必须包含：`NavRail`、顶栏、`FilterSidebar`、`SidebarHost`、`MediaResultHost`、`VirtualizedCardGrid`、`MediaCard`/`AlbumCard`/`PosterCard`、`DetailShell`、`ReaderShell`、`BrowserToolbar`、`SearchBoxControl`、`StateHostControl`、`SnackbarHost`、对话框。
- 每个组件写清：结构、尺寸、全部状态、键盘行为、深浅主题、对应原型类名与 WPF Style 键。
- 落点：`docs/UI/COMPONENTS.md`。
- 现状：控件均已存在；缺原型类名到 WPF Style 的对照。
- 完成判定：每个组件都有对照表与状态截图；页面不再复制一次性局部样式。

### D06 状态与交互矩阵

- 必须包含：加载、空、错误、重试、离线、禁用、悬停、选中、键盘焦点；`Esc`/`Enter`/`Tab` 行为；分页与滚动；下载中/完成/失败。
- 落点：`docs/UI/COMPONENTS.md` 的状态章节与 `docs/UI/ACCEPTANCE.md`。
- 现状：已有 `LoadingTemplate`、`EmptyTemplate`、`ErrorTemplate`、`UnifiedStateHostStyle`；但未逐页覆盖。
- 完成判定：每页五种状态可复现；失败可重试；空态有明确下一步。

### D07 资源与内容规范

- 必须包含：`Icons.xaml` 图标清单、`Assets/app.ico`、封面占位与比例、字体、空态文案、数字/时间格式、演示数据与真实数据边界。
- 落点：`docs/UI/ASSETS.md`。
- 现状：图标与资源已有；缺封面、空态文案、格式化的统一清单。
- 完成判定：UI 必需资源不依赖外链；断网不破版；同类文案措辞一致。

### D08 截图基线

- 必须包含：每个 P0 页面在 1440×900 与 1200×750 下各一张；浅色/暗色至少覆盖首页、搜索、列表、详情、阅读器、设置。
- 命名：`shots/<media>-<page>-<light|dark>-<width>.png`。
- 落点：`docs/UI/shots/`；原型截图与关键变量同时冻结到 `docs/UI/baseline/`。
- 现状：`design/*/shots` 与 `docs/images` 有零散截图；缺 1200 宽与暗色全覆盖。
- 完成判定：P0 页面双宽截图齐全；暗色抽检无漏白、无低对比文本。

### D09 验收记录

- 必须包含：构建、测试、GUI 冒烟、视觉核对、已知差异、签字。
- 落点：`docs/UI/ACCEPTANCE.md`。
- 现状：`docs/ui-unification-checklist.md` 有框架，但状态与当前代码不同步。
- 完成判定：每次合并更新；未通过项有负责人和日期。

## 4. 页面交付矩阵

状态说明：`有` 表示已有可用资料；`部分` 表示有实现但规格/截图不全；`缺` 表示需要从现有界面反推。

| 页面组 | 路由 / 入口 | WPF 视图 | 规格 | 截图 | 主要缺口 |
|---|---|---|---|---|---|
| 壳层 | 全局 | `MainWindow`、`NavRail`、顶栏、右栏 | 部分 | 部分 | 左栏 224 裁定；1200 基线；右栏状态 |
| 漫画 | `manga.search`、`manga.author`、`manga.rank`、`manga.category`、`manga.weekly`、`manga.favorites`、`manga.local`、`manga.recommend`、`manga.scrape` | `SearchView`、`AuthorSearchView`、`RankBrowseView`/`RankView`、`CategoryBrowseView`/`CategoryView`、`WeeklyView`、`FavoriteView`、`LocalView`、`MangaRecommendView`、`ScrapeToolsView` | 缺 | 缺 | 9 页规格与截图；本地详情面板 |
| 视频 | `video.online`、`video.online-recommend`、`video.local`、`video.recommend`、`video.tasks`、`video.actors` | 统一 `VideoView` + 内部 `SwitchNav` | 缺 | 缺 | 子页路由化；6 页规格；详情栏 |
| 小说 | `novel.online`、`novel.category`、`novel.rank`、`novel.shelf`、`novel.index`、`novel.history`、`novel.reader`；详情走 `OpenNovelBook` | `NovelOnlineView`、`NovelBrowseView`、`NovelShelfView`、`NovelLocalView`、`NovelHistoryView`、`NovelBookView`、`NovelReaderView` | 部分 | 部分 | 分类/排行/书架/本地/历史规格；2:3 比例裁定 |
| 短剧 | `drama.recommend`、`drama.newest`、`drama.rank`、`drama.channel.*`、`drama.local`；详情走 `ShortDramaDetailView` | `ShortDramaBrowseView`、`ShortDramaLocalView`、`ShortDramaDetailView` | 有 | 有 | `history/favorite` 未进导航与路由；详情非路由 |
| 写真 | `photobook.home`、`photobook.random`、`photobook.{dimension}` | `PhotobookGalleryView`、`PhotobookBrowseView` | 缺 | 缺 | 7 条路由的规格与截图 |
| 下载 | `download.page` | `DownloadPage` | 有 | 有 | 与统一壳层合并验收 |
| 搜索 | 顶栏及各媒体结果区 | `SearchView`、`SearchBoxControl`、`NovelSearchPanel`、`VideoSearchPanel`、`LocalSearchPanel` | 部分 | 部分 | 跨媒体结果态；空/错/离线一致性 |

非路由界面必须单独列入 `ROUTES.md`：`ChapterView`、`ReaderView`、`OnlineReaderView`、`NovelBookView`、`ShortDramaDetailView`、`SettingsDialog`、`LoginDialog`、`LocalDirsDialog`、`VideoRootsDialog`、`VideoEditDialog`。

## 5. 单页规格模板

每份页面规格按以下结构写，不增不减：

```markdown
# <页面名>

## 1. 目标与入口
- 用户目标：
- 进入方式（路由 / 按钮 / 详情跳转）：
- 返回目标：

## 2. 布局
- 区域划分（左栏 / 主区 / 右栏 / 底栏）：
- 断点行为（1440 / 1200）：

## 3. 组件与尺寸
- 组件清单：
- 关键尺寸 / 间距 / 圆角：
- 对应 WPF Style 键：

## 4. 数据与命令
- 数据来源：
- 命令与可用条件：
- 空数据兜底：

## 5. 状态
- 加载：
- 空：
- 错误 / 重试：
- 离线：
- 禁用：

## 6. 文案
- 标题 / 副标题：
- 按钮：
- 空态 / 错误：

## 7. 主题、DPI 与键盘
- 浅色 / 暗色差异：
- 200% DPI：
- Tab / Enter / Esc：

## 8. 截图
- 1440×900 浅色：
- 1440×900 暗色：
- 1200×750：

## 9. 验收
- [ ] 无横向滚动
- [ ] 无文字截断
- [ ] 五种状态可复现
- [ ] 返回目标正确
- [ ] 深浅主题无漏白
```

## 6. 实施顺序

| 阶段 | 内容 | 产出 |
|---|---|---|
| M0 | 冻结基线：记录当前版本、截图、原型变量 | `baseline/`、截图清单 |
| M1 | 裁定 §2 七项差异 | `DECISIONS.md` |
| M2 | 落令牌与主题键 | `TOKENS.md`、`Themes/*` |
| M3 | 对齐壳层与路由 | `ROUTES.md`、`MainWindow`、`NavRail` |
| M4 | 小说样板：8 个页面 + 设置 | `pages/novel-*.md`、截图 |
| M5 | 铺开：短剧、下载、搜索 → 漫画、视频、写真 | `pages/*.md` |
| M6 | 状态、资源、验收 | `ACCEPTANCE.md`、全量截图 |

优先级依据资料完整度：小说与短剧/下载/搜索已有静态稿，先做可快速形成样板；漫画、视频、写真需要从现有 WPF 界面反推规格。

优先级定义：P0 为壳层、小说分区与设置；P1 为短剧、下载、搜索；P2 为漫画、视频、写真。

## 7. 建议目录

```text
docs/UI/
├─ README.md          # 本文：总清单与索引
├─ DECISIONS.md       # 差异裁定
├─ TOKENS.md          # 令牌与主题映射
├─ ROUTES.md          # 路由、导航、返回目标
├─ COMPONENTS.md      # 组件与状态
├─ ASSETS.md          # 图标、封面、文案
├─ ACCEPTANCE.md      # 验收记录
├─ pages/
│  ├─ _TEMPLATE.md
│  ├─ novel-online.md
│  ├─ novel-book.md
│  └─ ...
├─ baseline/          # 原型截图与关键变量快照
└─ shots/             # 工程实际截图基线
```

本次先落 `README.md`。其余文件按 M1–M6 逐步补齐，不一次性铺开空文档。

## 8. 现有资料索引

| 资料 | 用途 |
|---|---|
| `docs/DSO-UI.md` | 现行设计系统：原则、颜色、尺寸、组件 |
| `docs/UI统一重构合并执行计划.md` | 既有阶段划分与迁移范围 |
| `docs/ui-unification-checklist.md` | 验收框架（状态需更新） |
| `docs/ARCHITECTURE.md` | 分层、源接口、项目结构 |
| `docs/在线小说源-界面设计.md` | 小说界面既有设计依据 |
| `design/novel-online/SPEC.md` + `shots/` | 小说搜索/详情/阅读器静态稿与 WPF 截图 |
| `design/short-drama/SPEC.md` + `shots/` | 短剧列表/详情/本地/排行 |
| `design/download-page/SPEC.md` + `shots/` | 下载页 |
| `design/dso-search-redesign/SPEC.md` + `shots/` | 搜索改版 |
| `src/ResourceGrab.App/MainWindow.xaml.cs` | 路由注册与返回栈的唯一实现依据 |
| `src/ResourceGrab.App/Shell/*` | 导航模型、路由模型、各媒体 Provider |
| `src/ResourceGrab.App/Themes/*` | 实际颜色、令牌、控件样式 |
| `README.md` | 已接内容源与能力表 |

原型包位于 OpenDesign 命名空间：

```text
C:\Users\Mafa\AppData\Roaming\Open Design\namespaces\release-stable-win\data\projects\e11ee07e-5a2e-4ca5-a307-eecdbd762d39\ResourceGrab-UI-Package\
```

该路径不属于本仓库，后续可能失效。实施前应把原型截图与关键 CSS 变量冻结到 `docs/UI/baseline/`。

## 9. 交接给实现者的最小输入包

按以下顺序提供，缺一项都不要开始改 XAML：

1. 本文件与 `DECISIONS.md`（六项差异的最终结论）。
2. `TOKENS.md`、`ROUTES.md`、`COMPONENTS.md`。
3. `baseline/` 中的原型截图与关键变量快照。
4. `shots/` 中当前工程的截图基线。
5. 目标页面的 `pages/*.md` 规格。
6. 代码入口：`MainWindow.xaml.cs`、`Themes/*`、`Controls/*`、`Views/*`。
7. 验收方式：`dotnet build ResourceGrab.slnx`；测试项目单独执行 `dotnet test tests/ResourceGrab.Core.Tests`；GUI 冒烟按 §5 模板逐页核对。

## 10. 完成定义

- [ ] §2 七项差异全部有结论，且已记录影响面。
- [ ] P0 页面均有规格，且包含五种状态。
- [ ] 页面无新增硬编码颜色、圆角、字号。
- [ ] 1440×900 与 1200×750 截图齐全，浅色/暗色抽检通过。
- [ ] 每个入口可进入、可返回，左栏高亮正确。
- [ ] 键盘 `Tab`/`Enter`/`Esc` 可用，焦点可见。
- [ ] `dotnet build` 无错误，相关测试通过。
- [ ] `docs/UI` 内文档与实际代码一致，验收记录已签字。
