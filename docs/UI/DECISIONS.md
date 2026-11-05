# UI 复刻差异裁定（M1）

> 关联：`docs/UI/README.md` §2
> 状态：默认执行口径
> 日期：2026-11-05
> 说明：本文件只记录裁定与影响，不修改代码；实现从 M2 开始。

## 0. 裁定摘要

默认按“全量复刻、原型优先”执行。若本轮只做小说分区，可只回退 `D-01`，其余裁定仍然成立。

| ID | 事项 | 裁定 | 影响面 | 状态 |
|---|---|---|---|---|
| D-01 | 全局主强调色 | 采用原型 `#445CF0` / 暗色 `#7C92FF`，同步派生键 | 所有页面 | 默认执行，需全页回归 |
| D-02 | 小说强调色 | 采用原型 `#5B43D6` / `#9480FF`，新增 soft 键 | 小说分区 | 默认执行 |
| D-03 | 左栏宽度 | 总宽 `224`，去掉左外边距，接通 `LeftRailWidth` | 壳层与所有页面 | 默认执行，需壳层回归 |
| D-04 | 页面标题字号 | 列表 `23`，详情 `25`，统一两套字体令牌 | 所有页头 | 默认执行，需逐页替换 |
| D-05 | 封面圆角 | 漫画 `12`；视频横图 `10`；小说/写真保持 `12` | 卡片组件 | 默认执行 |
| D-06 | 小说封面比例 | `2:3`，列表与详情同步 | 小说列表与详情 | 默认执行 |
| D-07 | 导航项高度 | `36`，保留 1px 上下间距 | 左导航 | 默认执行 |

## 1. 裁定原则

1. 原型是视觉目标，现有 DSO 是实现基础；能用令牌解决的，不写页面局部样式。
2. 同一语义只保留一个键。发现双源时先合并，再改值。
3. 每项改动都要有改动前/后截图，覆盖 1440×900 与 1200×750。
4. 全局改动优先做，页面改动随后做，避免反复回归。
5. 已知差异必须记录，不允许“看起来差不多”而静默保留。

## 2. 逐项裁定

### D-01 全局主强调色

**结论**

- 浅色：`#4D6BFE` → `#445CF0`，次要文本色 `#3348C6`，soft `#E9EDFE`。
- 暗色：`#6E86FF` → `#7C92FF`，次要文本色 `#A9B7FF`，soft 保持 `#1A2340`。
- `PrimaryFgBrush` 不变：浅色白字、暗色深字。

**现状**

- 浅色 `PrimaryBrush #4D6BFE`、`PrimarySubtleBrush #EEF1FF`。
- 暗色 `PrimaryBrush #6E86FF`、`PrimarySubtleBrush #1A2340`。
- 主色不是单个键，已派生到按钮、焦点、导航选中、阴影等 19 个键。

**必须同步的键**

| 键 | 浅色现值 → 目标 | 暗色现值 → 目标 |
|---|---|---|
| `PrimaryBrush` | `#4D6BFE` → `#445CF0` | `#6E86FF` → `#7C92FF` |
| `PrimaryHoverBrush` | `#3E5BF0` → 由新主色提亮约 6% | `#8A9EFF` → 由新主色提亮约 6% |
| `PrimaryPressedBrush` | `#2F47CC` → 由新主色压暗约 8% | `#5570F0` → 由新主色压暗约 8% |
| `PrimaryLightBrush` | `#7A93FF` → `#7C92FF` | `#9AAEFF` → `#A9B7FF` |
| `PrimarySubtleBrush` | `#EEF1FF` → `#E9EDFE` | `#1A2340` → `#1A2340` |
| `PrimaryGradientStartBrush` | `#5B7BFF` → 由新主色派生 | `#7E93FF` → 由新主色派生 |
| `PrimaryGradientEndBrush` | `#3F53E8` → 由新主色派生 | `#5570F0` → 由新主色派生 |
| `InputFocusBrush` | `#4D6BFE` → `#445CF0` | `#6E86FF` → `#7C92FF` |
| `FocusGlowBrush` | `#334D6BFE` → `#33445CF0` | `#406E86FF` → `#407C92FF` |
| `ActiveNavBgBrush` | `#144D6BFE` → `#14445CF0` | `#1F6E86FF` → `#1F7C92FF` |
| `SelectionBgBrush` | `#1F4D6BFE` → `#1F445CF0` | `#266E86FF` → `#267C92FF` |
| `CardHoverBorderBrush` | `#C7D2FE` → 按新主色重算 | `#3B4A6E` → 按新主色重算 |
| `CardHoverShadowBrush` | `#1A4D6BFE` → `#1A445CF0` | `#1A6E86FF` → `#1A7C92FF` |
| `TextLinkBrush` | `#3E5BF0` → `#3348C6` | `#8A9EFF` → `#A9B7FF` |
| `PrimaryGlowColor` | `#334D6BFE` → `#33445CF0` | `#336E86FF` → `#337C92FF` |
| `PrimaryHoverColor` | `#3E5BF0` → 与 Hover 同步 | `#8A9EFF` → 与 Hover 同步 |
| `PrimaryPressedColor` | `#2F47CC` → 与 Pressed 同步 | `#5570F0` → 与 Pressed 同步 |
| `PrimaryGradientStartColor` | `#5B7BFF` → 与 Start 同步 | `#7E93FF` → 与 Start 同步 |
| `PrimaryGradientEndColor` | `#3F53E8` → 与 End 同步 | `#5570F0` → 与 End 同步 |

**执行顺序**

1. 先改 `Colors.xaml` / `DarkColors.xaml` 的 `PrimaryBrush` 与 soft。
2. 再算 hover/pressed/gradient，写入同一字典。
3. 最后全局搜索仍引用旧色值的硬编码，逐个替换为键。
4. 全页截图回归，重点看白字按钮、选中态、焦点环、链接。

**验收**

- 主按钮白字对比度不低于 4.5:1。
- 浅色/暗色下的选中态与焦点环清晰可见。
- 页面中不再出现 `#4D6BFE`、`#6E86FF`、`#3E5BF0`、`#2F47CC` 的硬编码。

**回退**

- 若只做小说分区，保留现有 DSO 主色，`D-01` 延后；`D-02` 仍按原型执行。

### D-02 小说强调色

**结论**

- `NovelAccentBrush`：浅色 `#7C5CFF` → `#5B43D6`；暗色 `#A08CFF` → `#9480FF`。
- 新增 `NovelSubtleBrush`：浅色 `#EFEBFF`；暗色 `#1B1732`。
- 小说进度、角标、当前章节、标签统一使用这两个键。

**现状**

- 两个字典只有 `NovelAccentBrush`，没有 soft 键。
- 当前使用点：`Cards.xaml` 的进度填充与角标、`NovelLocalView.xaml` 的徽标/进度文本、`NovelSearchPanel.xaml` 的徽标。

**执行顺序**

1. 新增 `NovelSubtleBrush`，浅/暗各一条。
2. 改 `NovelAccentBrush` 值。
3. 把小说徽标、进度条轨道、当前章节背景中直接使用主色透明度的位置改为 soft 键。
4. 检查浅色 soft 上的主色文字对比度。

**验收**

- 小说分区的紫蓝色与原型一致。
- soft 背景上的文字对比度不低于 4.5:1。
- 不再新增小说专用的局部色值。

### D-03 左栏宽度

**结论**

- 左栏总宽 `224`。
- `MainWindow.xaml` 的 `LeftNavHost`：`Width` 改为 `{StaticResource LeftRailWidth}`，`Margin` 改为 `0,14,0,14`。
- `Tokens.xaml` 的 `LeftRailWidth`：`216` → `224`。
- 保留现有浮动卡片结构；内容内边距从 `10,12` 调整为 `12,12`，使内容宽接近原型的 200。

**现状**

- `LeftNavHost` 硬编码 `Width=196`，左外边距 `14`，总占位 `210`。
- `Tokens.xaml` 的 `LeftRailWidth=216` 没有被任何 XAML 或 C# 使用。
- 若直接把 `Width` 改成 `224` 且保留 `Margin=14`，总占宽会变成 `238`，这是错误做法。

**已知差异**

- 原型左栏是通栏、右侧发丝描边；工程是带 18px 圆角和阴影的浮动卡。
- 本轮保留浮动卡，控制改动量；若后续要求严格复刻，再单独立项改为通栏。

**验收**

- 左栏总占宽为 `224`，不是 `238`。
- 1920 与 1200 宽下，主区卡片列数符合页面规格。
- `LeftRailWidth` 有实际引用，不再是死令牌。

### D-04 页面标题字号

**结论**

- 列表页标题 `23`。
- 详情页标题 `25`。
- 阅读器章节标题 `27` 单列，不在本次统一范围内。
- 合并两套字体令牌：`Tokens.xaml` 的 `FontPageTitle` 与 `Core.Controls.xaml` 的 `FontSizePageTitle` 只保留一套语义。

**现状**

- `Tokens.xaml`：`FontPageTitle=19`。
- `Core.Controls.xaml`：`FontSizePageTitle=19`，目前只被 `ScrapeToolsView.xaml` 引用。
- 页面大量硬编码：`DownloadPage` 19、`MangaRecommendView` 20、`VideoRecommendView` 20、`ActorProfileView` 20、`NovelBookView` 20、`ShortDramaDetailView` 22。

**执行顺序**

1. 先决定保留哪一个令牌名，另一个删除或改为别名。
2. 新增列表标题 `23` 与详情标题 `25` 两个明确语义。
3. 按页面替换硬编码，不直接全局搜索替换数字，避免误伤正文。
4. 逐页核对行高、换行与顶部工具条对齐。

**验收**

- 列表页标题统一 `23`，详情页标题统一 `25`。
- 页面中不再出现标题语义的 `19/20/22` 硬编码。
- 1200 宽下长标题不截断、不压住右侧操作。

### D-05 封面圆角

**结论**

- 小说书卡：保持 `12`（`RadiusLg`），不改。
- 漫画 `AlbumCard` / `LocalComicCard` 封面：`14` → `12`。
- 视频 `WideThumbTemplate` 横图：`12` → `10`；竖版与短剧封面保持 `12`。
- 写真 `PosterCard`：保持 `12`。
- 面板与卡片外壳：保持 `16`。

**现状**

- `AlbumCard.xaml`、`LocalComicCard.xaml` 封面 `CornerRadius=14,14,0,0`。
- `MediaCard.xaml` 的 `PortraitCoverTemplate` / `WideThumbTemplate` 使用 `RadiusLg=12`。
- `PosterCard.xaml` 根边框 `12`。
- `CardStyle` 使用 `CardCornerRadius=16`。

**注意**

- 不要直接改 `RadiusLg`。它被大量组件复用，改全局会误伤按钮、面板、徽标。
- 新增 `BookCoverCornerRadius=12` 与 `VideoCoverCornerRadius=10`，只在对应模板引用。

**已知差异**

- 原型写真封面为 `10`、卡片外壳 `16`；工程 `PosterCard` 卡片与封面共用 `12`。本轮保留 `12`，记为已知差异；如严格复刻，再拆分封面与外壳圆角。

**验收**

- 漫画封面与小说封面视觉一致。
- 视频横图更接近原型的 10px 圆角。
- 没有因为改圆角导致图片裁切或阴影溢出。

### D-06 小说封面比例

**结论**

- 小说书卡比例从 `3:4` 改为 `2:3`。
- `States.xaml` 的 `NovelCoverRatio` 参数由 `1.3333` 改为 `1.5`。
- 详情页封面由 `140×186` 改为 `140×210`，保持 2:3。
- 同步修改 `design/novel-online/SPEC.md` 中“3:4 书卡”的描述。

**现状**

- 列表：`Height = CardWidth × 1.3333`。
- 详情：`NovelBookView.xaml` 固定 `Width=140 Height=186`。
- 原型：书卡 2:3，详情封面 2:3。

**影响**

- 书卡高度增加约 12.5%，同一行能容纳的卡片数量可能减少。
- `VirtualizedCardGrid` 的槽位高度、滚动位置、骨架屏高度需要一起核对。
- 详情页封面变高，左侧信息区与右侧章节区需要重新对齐。

**验收**

- 列表与详情封面均为 2:3。
- 1440 与 1200 宽下，卡片不重叠、不裁切。
- 骨架屏与真实卡片高度一致，加载完成不跳动。

### D-07 导航项高度

**结论**

- 导航项高度 `42` → `36`。
- 保留上下各 1px 间距。
- 按钮高度 `32` 不变。

**现状**

- `NavRail.xaml` 的 `NavRailItemContainerStyle` 显式 `Height=42`。
- 原型 `.navitem` 高度为 `36`。

**执行顺序**

1. 改 `NavRail.xaml` 的 `Height`。
2. 检查图标容器、文字基线、选中指示条是否随高度居中。
3. 检查一屏能容纳的导航项数量与滚动条位置。

**验收**

- 导航项高度 36，文字与图标垂直居中。
- 选中指示条不超出圆角边界。
- 左栏底部账号区不被挤压。

## 3. 影响文件清单

| ID | 主要文件 |
|---|---|
| D-01 | `Themes/Colors.xaml`、`Themes/DarkColors.xaml`；全局硬编码色值需搜索 |
| D-02 | `Themes/Colors.xaml`、`Themes/DarkColors.xaml`、`Themes/Cards.xaml`、`Views/NovelLocalView.xaml`、`Views/NovelSearchPanel.xaml` |
| D-03 | `MainWindow.xaml`、`Themes/Tokens.xaml` |
| D-04 | `Themes/Tokens.xaml`、`Themes/Core.Controls.xaml`、`Views/DownloadPage.xaml`、`Views/MangaRecommendView.xaml`、`Views/VideoRecommendView.xaml`、`Views/ActorProfileView.xaml`、`Views/NovelBookView.xaml`、`Views/ShortDramaDetailView.xaml`、`Views/ScrapeToolsView.xaml` |
| D-05 | `Controls/AlbumCard.xaml`、`Controls/LocalComicCard.xaml`、`Controls/MediaCard.xaml`、`Controls/PosterCard.xaml`、`Themes/Tokens.xaml` |
| D-06 | `Themes/States.xaml`、`Views/NovelBookView.xaml`、`design/novel-online/SPEC.md` |
| D-07 | `Controls/NavRail.xaml` |

## 4. 验证与回退

**改动前**

- 保存 1440×900 与 1200×750 的浅色/暗色截图。
- 记录当前 `dotnet build` 与测试结果。

**改动后**

- 同一页面、同一尺寸、同一主题重新截图。
- 检查对比度：白字主按钮、soft 背景文字、暗色焦点环。
- 检查布局：无横向滚动、无文字截断、无卡片重叠。
- 检查交互：导航、返回、右栏、滚动位置。

**回退**

- 每项裁定独立提交，回退只撤销该 ID 涉及的文件。
- `D-01` 与 `D-02` 只改颜色字典，不涉及数据或业务逻辑，回退成本最低。
- `D-06` 改比例会影响布局，回退时同时检查骨架屏与滚动位置。

## 5. 签收

| ID | 结论 | 确认 | 日期 |
|---|---|---|---|
| D-01 | 采用原型主色，全页回归 | 待确认 | |
| D-02 | 采用原型小说色 + soft | 默认执行 | |
| D-03 | 左栏总宽 224，保留浮动卡 | 待确认 | |
| D-04 | 列表 23 / 详情 25 | 默认执行 | |
| D-05 | 漫画 12、视频横图 10、写真保留 12 | 默认执行 | |
| D-06 | 小说 2:3 | 默认执行 | |
| D-07 | 导航项 36 | 默认执行 | |

未确认项不阻塞文档工作；但进入 `Themes/*` 与页面改造前，需要把 D-01、D-03 的确认补齐。
