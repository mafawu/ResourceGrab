# 漫画 / 视频 / 小说 UI 统一执行计划

> **⚠️ 本文档已被取代**（2026-09-05）：合并后的最新版见 [`UI统一重构合并执行计划.md`](UI统一重构合并执行计划.md)。
> 本文中「新增 MediaCard / MediaCardViewModel / NavRail / SidebarHost / ShellController」等表述与代码现状不符——这些组件均已存在，剩余工作见合并版 §2 现状盘点。

## 1. 目标

本计划以当前源代码为基准，统一三类内容的 UI 骨架，而不是把三种媒体强行做成同一种内容形态。

目标归纳为八件事：

1. 统一左侧边栏的视觉、宽度、交互和数据驱动方式。
2. 统一右侧边栏的显示策略、标题栏、滚动区、分隔线和内容替换方式。
3. 媒体类型切换时，由同一个壳控制器决定左栏、中栏、右栏的状态。
4. 页面内部操作时，只替换中间面板路由；避免视频页内部再造一层完整导航。
5. 统一全局圆角、间距、字体层级、图标语言和颜色语义。
6. 统一漫画、小说、视频的卡片模型和卡片模板。
7. 统一 loading / empty / error / retry / result 状态处理。
8. 统一结果网格和筛选面板契约。

本计划不改变下载、刮削、索引扫描、阅读进度等业务规则。重构过程中只允许为适配 UI 抽取接口或映射模型，不重写数据层。

---

## 2. 当前问题摘要

### 2.1 导航层级不统一

当前主窗口有顶部类型切换、主窗口左侧导航和右侧面板：

- 主窗口类型入口：`src/ResourceGrab.App/MainWindow.xaml`
- 主窗口左侧导航：`MainWindow.LeftNavHost`
- 主窗口右侧面板：`MainWindow.RightPanelHost`

但实际行为分裂：

- 漫画使用主窗口左侧导航。
- 视频进入后隐藏主窗口左侧导航，在 `VideoView` 内部再实现一层“搜索 / 推荐 / 本地”导航。
- 小说进入后隐藏主窗口左侧导航，页面自身承担列表页职责。
- 本地漫画详情放在右侧面板；视频详情在页面中部直接替换列表。

### 2.2 中间内容路由不统一

`MainWindow.xaml.cs` 中存在大量直接给 `PageHost.Content` 赋值的分支：

- `RestoreMangaContent()`
- `OpenNovelLocal()`
- `OpenVideoView()`
- `ShowLocalList()`
- `OpenReader()`
- `OpenOnlineReader()`

这些逻辑分散在事件处理器里，缺少统一的 route descriptor 和返回栈协议。

### 2.3 结果网格不统一

漫画在线搜索、排行、分类使用：

```text
CardGridViewBase
+ StateHostControl
+ VirtualizedCardGrid
+ AlbumCard
```

小说本地库使用：

```text
CardGridViewBase
+ 手动状态 StackPanel
+ VirtualizedCardGrid
+ 内联 DataTemplate
```

视频本地库使用：

```text
手动 Visibility 控制
+ ScrollViewer
+ WrapPanel.Children.Clear()
+ new VideoFileCard { CardWidth = 180 }
```

因此视频没有真正虚拟化，也没有统一分页和空态处理。

### 2.4 卡片模型不统一

当前存在四套卡片表达：

| 内容 | 当前实现 | 问题 |
| --- | --- | --- |
| 在线漫画 | `AlbumCard` + `AlbumCardViewModel` | 较接近目标，但仍绑定部分控件事件 |
| 本地漫画 | `LocalComicCard` + `LocalComicViewModel` | 与在线漫画字段和行为重复 |
| 小说 | `NovelLocalView.xaml` 内联模板 + `NovelCardViewModel` | 无法复用，样式硬编码在页面内 |
| 视频 | `VideoFileCard` + DependencyProperty + 事件 | 不走统一 VM 绑定，另有旧 `VideoCard` 残留 |

### 2.5 筛选面板重复

当前有三个侧栏筛选实现：

- `LocalSearchPanel`
- `NovelSearchPanel`
- `VideoSearchPanel`

它们都有关键字、标签、计数、包含 / 排除逻辑，但事件签名、构建方式和状态结构不同。

### 2.6 状态展示不统一

漫画在线浏览已经有 `StateHostControl` 的统一状态模板。其他页面仍大量使用独立面板和手工 `Visibility` 切换，导致空态、错误态、加载态和重试行为不一致。

---

## 3. 目标信息架构

### 3.1 三层壳结构

最终应用应稳定为三层：

```text
AppShell
├── TopBar：品牌、媒体类型切换、全局操作、窗口控制
├── LeftNavRail：当前媒体类型的导航项
├── CenterRouteHost：搜索、列表、详情、阅读器等中间路由
└── RightSidebarHost：筛选、上下文信息、任务、下载等辅助面板
```

规则如下：

1. 顶部始终负责“漫画 / 视频 / 小说”这一级切换。
2. 左侧边栏由当前媒体类型的 `NavSectionProvider` 提供。
3. 中间面板永远是唯一的主内容路由宿主。
4. 右侧边栏只是上下文附属区域，不再承载主导航。
5. 阅读、播放等沉浸式页面可以临时隐藏左右侧栏，但仍由壳状态机控制。

### 3.2 左侧边栏策略

左侧边栏默认对所有媒体类型保持同一视觉规格：

```text
Width = 220
Padding = 12
ItemHeight = 44
IconSize = 18
GroupHeaderHeight = 28
```

具体数值进入 token，不以魔法数字散落在页面中。

按媒体类型提供导航项：

#### 漫画

```text
发现
- 搜索
- 排行
- 分类
- 每周必看

我的
- 本地
- 收藏
```

#### 视频

```text
发现
- 在线搜索
- 推荐

我的
- 视频库
- 刮削任务
```

如果推荐或任务暂未完成，可以保留禁用态或空态页，但不应该再让视频页自己绘制第二层左侧栏。

#### 小说

```text
我的
- 本地索引
- 阅读历史

工具
- 索引设置
```

其中“阅读历史”和“索引设置”可以先作为占位路由，后续逐步填充功能。

### 3.3 右侧边栏策略

右侧边栏统一为一个 `SidebarHost`，内部只接受标准化内容：

```text
SidebarHost
├── Header：图标 + 标题 + 主操作 / 关闭按钮
├── Body：ScrollViewer
├── Footer：可选操作区
└── EmptyState：无内容时的标准提示
```

右侧边栏支持四种模式：

| 模式 | 用途 |
| --- | --- |
| `Hidden` | 沉浸式阅读、播放或不需要辅助信息的页面 |
| `Filter` | 当前列表的关键字、标签、属性过滤 |
| `Context` | 当前选中对象的轻量上下文信息 |
| `Task` | 下载队列、刮削进度、批量操作等长任务 |

切换规则：

1. 切换媒体类型时，右栏恢复该类型最近一次的非沉浸式模式。
2. 从列表进入详情时，右栏可切换为 `Context` 或按路由声明隐藏。
3. 进入阅读器时，右栏强制 `Hidden`。
4. 用户手动收起时记录偏好；切类型时不意外展开。
5. 右栏宽度和 splitter 行为由壳统一管理，不再由各页面自行修改列宽。

### 3.4 中间面板路由策略

所有主内容变化都必须经过统一导航服务：

```csharp
public enum MediaKind
{
    Manga,
    Video,
    Novel
}

public sealed record ShellRoute(
    string RouteId,
    MediaKind Kind,
    string Title,
    string? NavItemId,
    Func<UserControl> CreateView,
    SidebarPolicy SidebarPolicy,
    bool IsImmersive = false,
    bool ReplaceCurrent = false);
```

`SidebarPolicy` 初期可以简化为：

```csharp
public enum SidebarMode
{
    Hidden,
    Filter,
    Context,
    Task
}

public sealed record SidebarPolicy(
    SidebarMode Mode,
    string? PanelId = null);
```

导航服务职责：

1. 维护每个 `MediaKind` 的独立返回栈。
2. 切换媒体类型时恢复对应类型的最近路由。
3. 根据路由声明更新左右侧栏。
4. 处理返回按钮、快捷键和浏览器式后退语义。
5. 对重复路由做去重或刷新策略。

---

## 4. 目标组件设计

### 4.1 `ShellController`

新增 `src/ResourceGrab.App/Shell/ShellController.cs`。

它不绘制 UI，只维护全局状态：

```csharp
public sealed class ShellController : ObservableObject
{
    public MediaKind CurrentKind { get; }
    public ShellRoute? CurrentRoute { get; }
    public SidebarMode SidebarMode { get; }
    public bool LeftRailVisible { get; }
    public bool RightSidebarVisible { get; }

    public void SetKind(MediaKind kind);
    public void Navigate(ShellRoute route);
    public bool Back();
    public void SetSidebarMode(SidebarMode mode);
    public void ToggleLeftRail();
    public void ToggleRightSidebar();
}
```

`MainWindow` 只保留两类代码：

1. 把 XAML 控件事件转发给 `ShellController`。
2. 订阅控制器状态变化并更新 `LeftNavHost`、`CenterRouteHost`、`RightSidebarHost`。

不允许在页面内部反向修改主窗口的列宽、导航可见性或右栏内容。

### 4.2 `ShellNavigator`

新增 `src/ResourceGrab.App/Navigation/ShellNavigator.cs`。

职责：

```text
RegisterRouteFactory(kind, routeId, factory)
GoTo(routeId, parameters)
Back()
ReplaceCurrent(routeId, parameters)
ResetStack(kind)
```

迁移期允许保留旧 `Navigation` 静态类作为兼容层，但新代码必须优先使用 `ShellNavigator`。

### 4.3 `NavRail`

新增 `src/ResourceGrab.App/Controls/NavRail.xaml`。

输入模型：

```csharp
public sealed record NavItem(
    string Id,
    string Title,
    object Icon,
    IReadOnlyList<NavItem>? Children = null);

public sealed record NavSection(
    string Title,
    IReadOnlyList<NavItem> Items);
```

`NavRail` 只负责渲染，不理解漫画、视频或小说业务。

它支持：

- 分组标题。
- 图标 + 文本项。
- 选中态。
- 禁用态。
- 徽标数量。
- 收起为 icon-only 模式的后续扩展。

当前 `MainWindow.xaml` 和 `VideoView.xaml` 中重复绘制的导航项全部迁移到这个控件。

### 4.4 `SidebarHost`

新增 `src/ResourceGrab.App/Controls/SidebarHost.xaml`。

它替代目前直接塞进 `RightPanelHost.Content` 的方式。

输入：

```csharp
public interface ISidebarPanel
{
    string PanelId { get; }
    string Title { get; }
    object? Icon { get; }
    FrameworkElement Content { get; }
}
```

第一版可以继续复用现有三个筛选 UserControl，但它们必须实现 `ISidebarPanel`。后续再把内部筛选区替换成通用筛选控件。

### 4.5 `MediaResultHost`

新增 `src/ResourceGrab.App/Controls/MediaResultHost.xaml`。

它是列表页的标准结果区域，组合以下能力：

```text
MediaResultHost
├── ResultState
├── EmptyState
├── LoadingSkeleton
├── ErrorRetry
├── VirtualizedCardGrid
└── Pagination
```

统一状态模型：

```csharp
public enum ResultStatus
{
    Idle,
    Loading,
    Empty,
    Error,
    Ready
}

public sealed record ResultState<T>(
    ResultStatus Status,
    IReadOnlyList<T>? Items = null,
    string? EmptyTitle = null,
    string? EmptyMessage = null,
    string? ErrorMessage = null,
    ICommand? RetryCommand = null);
```

所有列表页禁止再手写一组 `LoadingPanel`、`EmptyPanel`、`ErrorPanel` 并逐个改 `Visibility`。

### 4.6 卡片模型

新增统一基础 ViewModel：

```csharp
public abstract class MediaCardViewModel : ObservableObject
{
    public abstract string CardId { get; }
    public abstract string Title { get; }
    public abstract MediaVisualKind VisualKind { get; }

    public string? Subtitle { get; init; }
    public string? SecondaryTitle { get; init; }
    public object? Cover { get; init; }
    public string? CoverFallbackText { get; init; }

    public IReadOnlyList<string> Tags { get; init; } = [];
    public IReadOnlyList<MediaBadge> Badges { get; init; } = [];
    public CardProgress? Progress { get; init; }
    public IReadOnlyList<CardStat> Stats { get; init; } = [];

    public bool IsSelectable { get; set; }
    public bool IsSelected { get; set; }
    public bool IsFavorite { get; set; }

    public ICommand? OpenCommand { get; init; }
    public ICommand? PrimaryCommand { get; init; }
    public ICommand? SecondaryCommand { get; init; }
    public ICommand? FavoriteCommand { get; init; }
}

public enum MediaVisualKind
{
    PortraitCover,
    WideThumb,
    TextCover
}
```

三种内容分别适配：

| 业务对象 | 目标 ViewModel |
| --- | --- |
| 在线漫画 | `MangaOnlineCardViewModel : MediaCardViewModel` |
| 本地漫画 | `MangaLocalCardViewModel : MediaCardViewModel` |
| 小说资源 | `NovelCardViewModel : MediaCardViewModel` |
| 视频 | `VideoCardViewModel : MediaCardViewModel` |

卡片视觉分为三类：

1. `PortraitCover`：在线漫画、本地漫画、视频海报。
2. `WideThumb`：视频缩略图、推荐卡。
3. `TextCover`：无封面小说，用首字母、类型色和标签组成封面。

### 4.7 `MediaCard`

新增 `src/ResourceGrab.App/Controls/MediaCard.xaml`。

建议做成轻量 templated control 或 UserControl，内部根据 `MediaVisualKind` 选择模板。

统一结构：

```text
MediaCard
├── CoverHost
│   ├── Image / TextCover
│   ├── KindBadge
│   ├── StatusBadges
│   ├── SelectionBadge
│   ├── ProgressTrack
│   └── HoverActions
├── TitleBlock
├── SubtitleBlock
├── TagRow
├── StatRow
└── CommandRow
```

约束：

1. 封面高度比例固定，不允许内容变化撑开卡片。
2. 标题最多两行，副标题最多一行。
3. 标签最多展示三项，超出用 tooltip 或溢出徽标表达。
4. hover 动作必须使用矢量图标按钮，不使用 emoji 或裸文本符号。
5. 多选框、收藏、播放、打开目录等动作通过 command 注入。
6. 不同媒体只允许换 accent、badge、cover ratio，不允许复制整张卡片 XAML。

旧卡片迁移完成后删除：

```text
AlbumCard.xaml / .cs
LocalComicCard.xaml / .cs
VideoFileCard.xaml / .cs
VideoCard.xaml / .cs
```

如果短期仍有调用点，先保留文件并在类头标注 `[Obsolete]`，最后一个阶段统一切除。

### 4.8 筛选面板契约

新增 `src/ResourceGrab.App/Filtering` 目录。

核心模型：

```csharp
public enum FilterTriState
{
    None,
    Included,
    Excluded
}

public sealed record FilterOption(
    string Id,
    string Label,
    int Count,
    FilterTriState State);

public sealed record FilterSection(
    string Id,
    string Title,
    FilterSectionLayout Layout,
    IReadOnlyList<FilterOption> Options);

public enum FilterSectionLayout
{
    Wrap,
    Tree,
    Radio,
    Range
}
```

统一面板接口：

```csharp
public interface IFilterPanel
{
    event EventHandler<FilterStateChangedEventArgs>? FilterChanged;

    void SetSections(IReadOnlyList<FilterSection> sections);
    FilterSelection GetSelection();
    void ApplySelection(FilterSelection selection);
    void Clear();
}
```

第一阶段不要求一次性重写三个筛选面板的业务逻辑，只要求：

1. 三个面板都实现 `IFilterPanel`。
2. 外层都挂到 `SidebarHost`。
3. 标题、搜索框、分组头、chip、清除按钮使用同一样式。
4. 事件签名收敛到 `FilterSelection`。

第二阶段再逐步把内部 UI 替换成共享控件。

---

## 5. 设计令牌与视觉规范

### 5.1 圆角令牌

在 `Themes/Tokens.xaml` 增加完整圆角体系：

```xml
<CornerRadius x:Key="RadiusXs">6</CornerRadius>
<CornerRadius x:Key="RadiusSm">8</CornerRadius>
<CornerRadius x:Key="RadiusMd">10</CornerRadius>
<CornerRadius x:Key="RadiusLg">12</CornerRadius>
<CornerRadius x:Key="RadiusXl">16</CornerRadius>
<CornerRadius x:Key="RadiusPill">999</CornerRadius>
```

语义映射：

| 场景 | Token |
| --- | --- |
| 小 chip、小 badge、输入内元素 | `RadiusXs` |
| 按钮、输入框、普通 tile | `RadiusSm` 或 `RadiusMd` |
| 侧栏区块、详情子面板 | `RadiusLg` |
| 大面板、阅读器容器 | `RadiusXl` |
| 数量徽标、状态 pill | `RadiusPill` |

迁移完成后，视图 XAML 不应出现新的硬编码 `CornerRadius="8"` 这类值。只有主题资源文件允许定义基础值。

### 5.2 间距与尺寸令牌

补充：

```xml
<Thickness x:Key="SpacingXs">4</Thickness>
<Thickness x:Key="SpacingSm">8</Thickness>
<Thickness x:Key="SpacingMd">12</Thickness>
<Thickness x:Key="SpacingLg">16</Thickness>
<Thickness x:Key="SpacingXl">20</Thickness>

<sys:Double x:Key="LeftRailWidth">220</sys:Double>
<sys:Double x:Key="RightSidebarMinWidth">300</sys:Double>
<sys:Double x:Key="RightSidebarDefaultWidth">348</sys:Double>
<sys:Double x:Key="RightSidebarMaxWidth">440</sys:Double>
```

当前主窗口右栏的 300 / 348 / 440、视频页内部导航的 196、页面 margin 20 等数字都需要收敛。

### 5.3 字体层级

建立语义字号，而不是页面里反复写 `FontSize="11.5"`：

```xml
<sys:Double x:Key="FontCaption">11</sys:Double>
<sys:Double x:Key="FontBody">12.5</sys:Double>
<sys:Double x:Key="FontEmphasis">13</sys:Double>
<sys:Double x:Key="FontCardTitle">13</sys:Double>
<sys:Double x:Key="FontSectionTitle">15</sys:Double>
<sys:Double x:Key="FontPageTitle">18</sys:Double>
```

已有部分 token 可继续沿用，但必须清理同名不同值的重复定义。

### 5.4 图标语言

规则：

1. 所有可点击图标使用 `Themes/Icons.xaml` 中的矢量 Path。
2. 禁止在新 UI 中使用 emoji 表达功能图标。
3. 箭头、关闭、清除、播放、收藏、选择、设置、文件夹、筛选都必须有命名图标。
4. 图标颜色跟随父按钮 foreground 或语义 brush，不在视图中写死白色、灰色、金色。
5. 星级评分抽成 `RatingControl`，漫画、视频、对话框共用。

需要优先补齐的图标：

```text
Play, Pause, Stop, HeartFilled, HeartOutline,
StarFilled, StarOutline, Check, Exclude,
ChevronUp, ChevronDown, FontIncrease, FontDecrease,
BackgroundTheme, History, TaskProgress
```

### 5.5 媒体强调色

保留当前语义色：

```text
Manga = PrimaryBrush
Novel = NovelAccentBrush
Video = VideoAccentBrush
```

但使用规则要统一：

1. 类型 badge 使用媒体强调色。
2. 进度条默认使用当前媒体强调色。
3. 选中态仍使用全局 primary，保证交互一致性。
4. 错误、成功、警告只使用全局语义色，不按媒体重新配色。

---

## 6. 状态处理规范

所有异步列表和详情加载统一为：

```text
Idle
Loading
Ready
Empty
Error
NoConfiguration
PartialError
```

最小必须支持：

```text
Idle / Loading / Ready / Empty / Error
```

标准行为：

| 状态 | 展示 | 操作 |
| --- | --- | --- |
| `Idle` | 显示引导提示 | 提供主动搜索 / 加载动作 |
| `Loading` | skeleton 或紧凑 progress | 可取消则显示取消 |
| `Ready` | 结果网格 | 正常交互 |
| `Empty` | 图标 + 标题 + 描述 | 提供下一步动作 |
| `Error` | 错误说明 + 重试 | 重试命令 |
| `NoConfiguration` | 配置引导 | 打开配置 / 选择路径 |
| `PartialError` | 已加载结果 + 局部错误提示 | 不阻断浏览 |

禁止事项：

1. 禁止在业务页面里同时维护五个 `StackPanel.Visibility`。
2. 禁止 catch 后静默吞掉并停留在旧状态。
3. 禁止 loading 时仍允许触发同一查询。
4. 空态文案必须区分“没有数据”和“筛选后没有匹配”。

---

## 7. 结果网格规范

### 7.1 分页与虚拟化

所有超过一屏的结果必须使用虚拟化。

目标结构：

```text
PageRequest
├── Keyword
├── FilterSelection
├── Sort
├── PageIndex
└── PageSize

PageResult<T>
├── Items
├── TotalCount
├── PageIndex
├── PageSize
└── HasMore
```

对已经能一次拿到全量数据的本地库，可以先在内存中分页，但仍要走同一个请求模型。

### 7.2 列数控制

统一由 `MediaResultHost` 管理：

```csharp
public int DesiredColumns { get; set; }
public double CardWidth { get; private set; }
public double SlotWidth { get; private set; }
```

`GridCellSizer` 可以保留，但应成为 `MediaResultHost` 的内部实现细节，页面不应再各自 override 列宽计算。

小说页当前的 `UpdateCellSize()` override 属于迁移对象。

### 7.3 卡片尺寸

建议基准：

| 类型 | 宽度范围 | 封面比例 |
| --- | --- | --- |
| 在线漫画 / 本地漫画 | 120-320 | 3:4 |
| 视频海报 / 缩略图 | 140-320 | 16:9 或 2:3 |
| 小说文本封面 | 120-280 | 3:2 或 3:4 |

具体比例由 `MediaVisualKind` 决定，不由页面单独设置。

---

## 8. 迁移路线

整个工作拆成九个阶段。每个阶段都应能独立编译、运行和合并。

---

### Phase 0：基线冻结与安全网

**目标**

先建立重构前后的对照基线，避免大范围 UI 改造无法回归。

**任务**

1. 记录当前 build 命令和测试命令是否可用。
2. 补充或确认核心服务测试仍然通过：
   - 本地漫画扫描
   - 视频库查询
   - 小说索引读取
   - 阅读历史保存
3. 建立手工 smoke 清单：
   - 浅色 / 深色主题
   - 最小窗口 1200x750
   - 漫画搜索、排行、分类、本地、阅读
   - 视频搜索、本地、详情、刮削
   - 小说列表、筛选、翻页、滚动、阅读
4. 新增 `docs/ui-unification-checklist.md`，后续每个阶段勾选。

**验收**

- 项目能编译。
- 现有测试通过。
- 手工 smoke 清单有明确记录。

---

### Phase 1：令牌、圆角和图标语言收敛

**目标**

先做低风险视觉基础，不改变页面结构。

**任务**

1. 扩展 `Themes/Tokens.xaml`：
   - radius scale
   - spacing scale
   - sidebar width tokens
   - font size aliases
2. 清理 `Cards.xaml` 中覆盖圆角的特殊值。
3. 将按钮、输入框、chip、panel、reader container 改为语义 radius token。
4. 盘点视图中的硬编码颜色：
   - `VideoView.xaml.cs`
   - `VideoFileCard.xaml`
   - `LocalComicDetailPanel.xaml.cs`
   - `NovelReaderView.xaml.cs`
5. 将星级、心形、箭头、播放等文字符号替换成矢量图标资源。
6. 新增缺失 icon key。

**涉及重点**

```text
src/ResourceGrab.App/Themes/Tokens.xaml
src/ResourceGrab.App/Themes/Cards.xaml
src/ResourceGrab.App/Themes/Core.Controls.xaml
src/ResourceGrab.App/Themes/Search.xaml
src/ResourceGrab.App/Views/*.xaml
src/ResourceGrab.App/Controls/*.xaml
```

**验收**

- 三种主题下无明显对比度回退。
- 同类控件圆角一致。
- 主流程没有 emoji 功能图标。
- 编译和无回归 smoke 通过。

---

### Phase 2：引入 ShellController 与左侧 NavRail

**目标**

先把导航状态从 `MainWindow` 事件处理器中拆出来。

**任务**

1. 新增 `MediaKind`、`NavItem`、`NavSection`、`ShellRoute`、`SidebarPolicy`。
2. 实现 `ShellController`。
3. 实现 `INavSectionProvider`：
   - `MangaNavSectionProvider`
   - `VideoNavSectionProvider`
   - `NovelNavSectionProvider`
4. 实现 `NavRail` 控件。
5. `MainWindow.LeftNavHost` 改为承载 `NavRail`。
6. 保留旧 `NavSearch`、`NavRank`、`NavCategory` 等控件的兼容逻辑，直到所有事件迁移完成。
7. 视频页内部的 `NavSearch_Click`、`NavRecommend_Click`、`NavLocal_Click` 先不改行为，只标记为待迁移。

**验收**

- 漫画导航行为不变。
- 视频和小说暂时仍走旧页面，但主壳已具备注入新导航的能力。
- 左侧导航视觉完全一致。
- 类型切换不会丢失当前类型独立的返回栈。

---

### Phase 3：统一右侧 SidebarHost

**目标**

让右侧面板成为受控插槽，而不是各页面直接改主窗口布局。

**任务**

1. 新增 `SidebarHost`、`ISidebarPanel`、`SidebarMode`。
2. 将 `RightPanelHost` 改为承载 `SidebarHost`。
3. 把当前三种内容迁入：
   - `DownloadPanel` => task panel
   - `LocalSearchPanel` => manga filter panel
   - `NovelSearchPanel` => novel filter panel
   - `LocalComicDetailPanel` => manga context panel
   - `VideoSearchPanel` => video filter panel
4. 将右栏宽度和 splitter 控制移入 `ShellController`。
5. 页面不得再访问 `MainWindow.RightPanelHost` 或 `PanelColumn`。
6. 为每个媒体类型记录最近使用的 sidebar 状态。

**验收**

- 切换漫画 / 视频 / 小说时，右栏内容按预期替换或隐藏。
- 右栏宽度、splitter、收起展开行为一致。
- 漫画、小说、视频筛选功能不回退。
- 阅读器进入时右栏自动隐藏。

---

### Phase 4：引入 ShellNavigator 和中心路由

**目标**

所有主内容切换都变成 route，而不是散落的 `PageHost.Content = ...`。

**任务**

1. 新增 `ShellNavigator`。
2. 注册初始路由：
   ```text
   manga.search
   manga.rank
   manga.category
   manga.weekly
   manga.favorites
   manga.local
   manga.chapter
   manga.reader.local
   manga.reader.online

   video.search
   video.recommend
   video.library
   video.detail
   video.tasks

   novel.index
   novel.reader
   ```
3. 将 `MainWindow` 中直接切换内容的分支改为 navigator 调用。
4. 每个 route 声明自己的 sidebar policy。
5. 实现每类型独立 back stack。
6. 保持旧的 `Navigation.BackHandler` 作为兼容层。

**验收**

- 搜索、排行、分类、本地、阅读之间返回顺序正确。
- 切到视频或小说再切回漫画，漫画停留原页面。
- 详情和阅读器的返回不污染列表筛选状态。
- 不再有页面直接引用主窗口的 `PageHost`。

---

### Phase 5：统一结果状态和结果网格

**目标**

所有列表页使用同一个状态容器和虚拟化网格。

**任务**

1. 新增 `ResultState<T>`、`ResultStatus`、`MediaResultHost`。
2. 让 `MediaResultHost` 内部复用 `StateHostControl` 和 `VirtualizedCardGrid`。
3. 迁移漫画在线搜索、排行、分类、每周、收藏。
4. 迁移本地漫画列表。
5. 迁移小说列表。
6. 最后迁移视频本地列表，删除 `CardsPanel.Children.Clear()` 方案。
7. 统一分页控件和 page size 行为。

**验收**

- 所有列表页 loading / empty / error 样式一致。
- 本地漫画和小说大数据量仍保持虚拟化。
- 视频列表从 `WrapPanel` 迁移后可流畅滚动。
- 分页文案、按钮高度、间距一致。

---

### Phase 6：统一卡片模型和卡片模板

**目标**

四套卡片合并成一个可配置的媒体卡系统。

**任务**

1. 新增 `MediaCardViewModel`、`MediaBadge`、`CardStat`、`CardProgress`。
2. 实现四个适配器：
   - 在线漫画
   - 本地漫画
   - 小说
   - 视频
3. 实现 `MediaCard`。
4. 支持 `PortraitCover`、`WideThumb`、`TextCover`。
5. 迁移顺序建议：
   1. 视频列表
   2. 小说列表
   3. 本地漫画
   4. 在线漫画
6. hover action、多选、收藏、打开目录、播放全部改为 command。
7. 删除旧卡片前先跑全量 smoke。

**验收**

- 卡片标题、副标题、tag、badge、progress、hover action 视觉一致。
- 无封面小说使用统一 text cover。
- 视频卡片支持时长、分辨率、观看状态、刮削状态、多选。
- 漫画卡片支持已下载、来源、作者、热度信息。
- 旧卡片文件删除后项目仍编译通过。

---

### Phase 7：统一筛选面板

**目标**

三个筛选侧栏共用一套交互语言和数据契约。

**任务**

1. 定义 `FilterOption`、`FilterSection`、`FilterSelection`。
2. 实现 `FilterSidebar` 主体：
   - header
   - keyword field
   - section list
   - tri-state chips
   - tree group
   - clear all
3. 实现三个 provider：
   - `MangaLocalFilterProvider`
   - `NovelFilterProvider`
   - `VideoFilterProvider`
4. 第一批迁移 UI 样式，不急于重写查询逻辑。
5. 第二批将查询参数转换收敛到 provider。
6. 保证左键包含、右键排除、清除、计数展示一致。

**验收**

- 三个筛选面板的搜索框、chip、分组头、分隔线、清空按钮一致。
- 筛选状态切换媒体类型后正确恢复。
- 空结果文案能区分“库为空”和“筛选后为空”。
- 视频复杂筛选项仍完整可用。

---

### Phase 8：统一详情与阅读器外壳

**目标**

详情和阅读器不再各自发明页面骨架。

**详情**

1. 建立 `DetailShell`：
   ```text
   DetailShell
   ├── Header：返回、标题、主操作
   ├── MediaBlock：封面 / 海报 / 缩略图
   ├── MetaBlock：状态、统计、来源
   ├── ActionBlock
   └── Sections
   ```
2. 漫画章节页、本地漫画详情、视频详情逐步迁入。
3. 视频详情从“列表内替换”改成独立 `video.detail` route。
4. 本地漫画详情可以从右侧 context panel 迁移为中心详情页；如需保留快速预览，可在列表页增加轻量 context sidebar。

**阅读器**

1. 建立 `ReaderShell`：
   ```text
   ReaderShell
   ├── TopToolbar
   ├── ContentView
   ├── BottomBar
   └── ImmersiveMode
   ```
2. 漫画本地阅读器、漫画在线阅读器共用 toolbar 和 fit-mode 控件。
3. 小说阅读器复用同一个 shell，但内容区替换为文本视图。
4. 视频播放如果未来内置，也复用 shell 的沉浸模式和底部控制区。

**验收**

- 详情页返回、标题截断、主按钮位置一致。
- 阅读器隐藏 / 恢复工具栏行为一致。
- 快捷键提示样式一致。
- 漫画和小说的阅读历史不回退。

---

### Phase 9：清理旧实现和固化规范

**目标**

删除兼容层，防止再次发散。

**任务**

1. 删除不再被引用的旧卡片。
2. 删除视频页内部导航。
3. 删除页面内手写 loading / empty / error 面板。
4. 合并重复样式，移除 `[Obsolete]` 兼容 key。
5. 更新 `docs/UI_COMPONENT_UNIFICATION.md`，标注其被本计划接替的部分。
6. 在 README 或贡献文档中加入 UI 规则摘要：
   - 新页面必须走 route。
   - 新列表必须用 `MediaResultHost`。
   - 新卡片必须继承 `MediaCardViewModel`。
   - 新筛选必须实现 `IFilterPanel`。
   - 新图标必须使用矢量资源。

**验收**

- 无未引用的旧 UI 文件。
- 全项目无新增硬编码圆角、颜色、emoji 图标。
- 三种媒体类型全流程 smoke 通过。
- 测试通过。

---

## 9. 推荐目录结构

```text
src/ResourceGrab.App/
├── Shell/
│   ├── ShellController.cs
│   ├── ShellState.cs
│   ├── MediaKind.cs
│   ├── Providers/
│   │   ├── MangaNavSectionProvider.cs
│   │   ├── VideoNavSectionProvider.cs
│   │   └── NovelNavSectionProvider.cs
│   └── Routes/
│       ├── MangaRoutes.cs
│       ├── VideoRoutes.cs
│       └── NovelRoutes.cs
├── Navigation/
│   ├── ShellNavigator.cs
│   ├── ShellRoute.cs
│   └── NavigationParameters.cs
├── Controls/
│   ├── NavRail.xaml
│   ├── SidebarHost.xaml
│   ├── MediaResultHost.xaml
│   ├── MediaCard.xaml
│   ├── RatingControl.xaml
│   └── IconControl.xaml
├── Filtering/
│   ├── IFilterPanel.cs
│   ├── FilterSidebar.xaml
│   ├── FilterSection.cs
│   ├── FilterSelection.cs
│   └── Providers/
│       ├── MangaLocalFilterProvider.cs
│       ├── NovelFilterProvider.cs
│       └── VideoFilterProvider.cs
└── Themes/
    ├── Tokens.xaml
    ├── Colors.xaml
    ├── DarkColors.xaml
    ├── Core.Controls.xaml
    ├── Cards.xaml
    ├── States.xaml
    ├── Search.xaml
    └── Readers.xaml
```

---

## 10. 每阶段提交策略

每个 phase 至少拆成以下 commit：

1. `feat(shell): add contracts and models`
2. `refactor(ui): migrate one module`
3. `style(theme): replace legacy visual values`
4. `chore(cleanup): remove obsolete controls`

不建议一次性提交“新增框架 + 三类内容全量迁移”。推荐每次只迁移一个页面或一个媒体类型，保持可回滚。

---

## 11. 回归测试矩阵

每阶段至少检查以下组合。

### 媒体类型

```text
漫画在线
漫画本地
漫画阅读
视频在线搜索
视频本地库
视频详情
视频刮削
小说列表
小说筛选
小说阅读
```

### 布局状态

```text
默认 1440x900
最小 1200x750
左栏收起 / 展开
右栏收起 / 展开
右栏拖动到最小和最大
浅色主题
深色主题
Windows 100% / 125% / 150% DPI
```

### 数据状态

```text
无配置
加载中
空库
筛选后为空
网络错误
局部资源失败
超过一页数据
超过 1000 条数据
```

---

## 12. 明确不做的事

1. 不在本轮统一下载器、登录、设置弹窗的全部视觉细节。
2. 不重写视频刮削器和视频元数据模型。
3. 不把小说文本阅读改成图片阅读逻辑。
4. 不为了统一而删除视频独有的番号、演员、系列、分辨率等信息。
5. 不在 UI 重构期间顺手修改外部站点解析逻辑。

---

## 13. 完成定义

项目达到以下状态时视为统一完成：

1. 顶部类型切换是唯一的第一级媒体入口。
2. 三种媒体共享同一个左栏控件和右栏控件。
3. 中间内容全部由 route 驱动。
4. 所有结果列表使用同一个状态容器和虚拟化网格。
5. 四种卡片来自同一模型和同一控件族。
6. 三个筛选面板遵循同一 contract 和视觉规格。
7. 圆角、间距、字号、颜色、图标均有语义 token。
8. 旧卡片、旧页面导航、旧状态面板清理完毕。
9. 三种媒体在全量 smoke matrix 下无功能性回退。
