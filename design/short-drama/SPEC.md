# 短剧页 · 界面设计说明

> 目标视口 **1440×900（1x）**，Windows WPF。
> 设计稿：`list.html`（发现）/ `detail.html`（播放+详情）/ `rank.html`（榜单），深浅两套主题由 `?theme=light` 切换。
> 截图：`shots/*.png`。渲染脚本：`render-shots.ps1`。
> 上游文档：`docs/短剧页接入方案.md`（数据源可行性已验证）。本文只管**长什么样、怎么落到 XAML**。

---

## 一、北极星

**一屏里只有两件事：剧名和封面。** 短剧是「点开就看」的内容，用户 90% 的时间在列表和播放之间往返，所以：

- 不做详情中间页 —— 点卡片直接进「播放 + 选集」页，海报未播放时就是详情页；
- 列表页不设右侧筛选栏 —— 筛选只剩一行标签 chips，把宽度全留给封面；
- 颜色只出现在三个地方：选中态、评分星、排行前 3 名。

---

## 二、信息架构

```
顶栏：漫画 / 视频 / 本地小说 / 写真 / 【短剧】/ 下载          ← 新增一级胶囊（第 5 个）
左栏 216px
├ 线路卡（替代其它页的「源切换」）：● xiguaai.net  ⟳
├ 发现：推荐 · 最近上新 · 榜单
├ 频道：成人短剧(465) · 成人漫剧 · AI 魔改 · 西瓜原创
└ 我的：追剧(12) · 观看历史

主区
├── 短剧 · 共 465 部 · 每日更新                        ← 页头
├── [搜索框 Ctrl K]                        [4列|5列|6列]  ← 工具行
├── 全部 · 母子 · 乱伦 · 熟女 · 都市 · 古风 · … 更多标签 46  ← 筛选 chips
├── 继续观看（3 张宽卡，无记录时整块隐藏）
├── 最近上新  20/580                            查看全部 →  ← 6 列封面网格
└ 榜单页：热播榜 / 推荐榜 / 潜力榜（pill tabs）+ TOP20 行式列表
```

**为什么「继续观看」放在 chips 之下、网格之上**：它是回访钩子，位置必须固定且永远可见；但它不属于列表内容，所以用宽卡（76×48 缩略图 + 进度条）与下方 6 列封面形成明确区隔。

---

## 三、与 DSO UI 的对应

颜色 **全部**取自现成语义键，没有新增色值（除评分星与进度用 `WarningBrush` / `PrimaryBrush`）：

| 设计稿变量 | XAML 资源 | 深色 | 浅色 |
| --- | --- | --- | --- |
| `--bg` | `AppBackgroundBrush` | #0B0E14 | #F5F7FB |
| `--surface` | `SurfaceBrush` | #12161F | #FFFFFF |
| `--surface-2` | `CardHoverBrush` | #1A2130 | #F0F3F9 |
| `--border` | `CardBorderBrush` | #232A38 | #E6EAF2 |
| `--border-soft` | `DividerBrush` | #1B212D | #EDF0F7 |
| `--border-strong` | `DividerStrongBrush` | #2B3345 | #DCE2EE |
| `--text / -2 / -3` | `TextPrimary/Secondary/DisabledBrush` | #E9EDF5 / #98A2B5 / #6B7488 | #10141F / #5C667A / #9AA3B4 |
| `--accent` | `PrimaryBrush` | #6E86FF | #4D6BFE |
| `--accent-text` | `PrimaryLightBrush` | #9AAEFF | #3D57E8 |
| `--accent-soft` | `PrimarySubtleBrush` | #1A2340 | #EEF1FF |
| `--scrim`（角标底） | 视图内 `#99000000` | — | — |
| `--hover` | `HoverBgBrush` | #14E9EDF5 | #0A10141F |

尺寸令牌：左栏 `LeftRailWidth=216`、圆角 `RadiusLg=12`（卡片）/`RadiusMd=10`（控件）/`RadiusSm=8`（选集格）、字号 `FontPageTitle=19`、`FontSectionTitle=15`、`FontCardTitle=13.5`、`FontBody=13`、`FontCaption=11`。右侧选集栏宽 **348**（= `RightSidebarDefaultWidth`）。

### 3.1 一处刻意的破例：封面不随主题变化

浅色主题下画布是白的，但**封面是内容不是控件**——真实封面永远是照片。所以设计稿里封面底色在两套主题中相同（近黑的中性色 + 色相轮转的柔光），否则浅色主题会把整屏封面色块漂白成灰白，看起来像图片加载失败。

> 落地时对应：`MediaCard` 的封面区不放 `SurfaceBrush` 背景，占位用 `HoverBgBrush` + 首字（现 `PortraitCoverTemplate` 已是这个做法）。

---

## 四、卡片规格（需要新增一个 `MediaVisualKind`）

现有 `PortraitCover` 是 **2:3 + 标题压在封面底部**，正好和短剧的信息结构冲突：短剧要同时显示「评分、更新到第几集、角标、类型」，压标题会挤成一团。因此新增 `MediaVisualKind.DramaCover`：

| 元素 | 规格 |
| --- | --- |
| 封面 | 4:5（源站封面 374×420≈0.89，4:5 中心裁切约 10% 宽），圆角 12，1px `CardBorderBrush` |
| 角标（左上） | 高 20 / 圆角 6 / 字号 10.5，两态：`新上·高分·原创` = `PrimarySubtleBrush` 底 + `PrimaryLightBrush` 字；`热播` = `#99000000` 底 + 白字 |
| 评分（右上） | 高 20 / 圆角 6 / `#99000000` 底 / 白字 11 加粗，前置 ★ 用 `WarningBrush` |
| 集数（右下） | 高 20 / 圆角 6 / `#99000000` 底 / 白字 10.5，文本直接取源站「更新至6集 / 全7集」 |
| 悬停 | 封面描边转 `PrimaryBrush`，中央浮出 44px 圆形播放键（`rgba(0,0,0,.46)` + 1px 白 24%），**点按直接播放**（有进度则续播） |
| 标题 | 封面下 8px，13.5 半粗，单行省略 |
| 副信息 | 标题下 3px，11.5 `TextDisabledBrush`：`AI成人短剧 · 3 小时前` |
| 卡片外框 | 不加 `SurfaceBrush` 底、不加阴影——封面本身就是卡片（与现有 `MediaCard` 的"整体卡片"不同，故用新模板而非改旧模板） |

> 6 列时单卡宽 ≈182，封面高 ≈227，两行卡片在 900 高度下「第二行被裁一半」是预期的——网格滚动，不是布局错误。

---

## 五、播放页（`detail.html`）

单页承载「详情 + 播放」，避免多一次跳转：

```
面包屑：短剧 / AI成人短剧 / 剧名
┌─────────────────────────────────┬──────────────┐
│ 播放器 16:9（未播时显示海报）      │  选集 · 共 6 集   ← 348px
│  [中央播放键] [水印]              │  01 02 [03] 04 05 06
│  ▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬ 进度        │  自动连播下一集  ●
│  ▮ 1:32/4:21   ‹上集 下集› 选集 🔊 1.0× ⛶ │  已看 2/6 ▬▬▬
├─────────────────────────────────┤  评分/状态/时长/上线
│ 剧名（19px 粗）                   │  播放/线路
│ ★8.4 · 更新至6集 · 3.7万播放 · 上线日期 │ ──────────────
│ [从第3集继续] [追剧] [收藏] [分享]  [刷新地址] │ 签名链接 2 小时有效提示
│ 剧情简介（3 行 + 展开全部）        │
│ 标签 chips                        │
└─────────────────────────────────┴──────────────┘
猜你喜欢（同 6 列网格，7 部）
```

**关键交互**

| 交互 | 行为 |
| --- | --- |
| 主按钮文案 | 有进度时「从第 3 集继续」，无进度时「立即播放」（截图里是前者） |
| 选集 | 点即切，不刷新页面、不重置滚动位置；当前集 `PrimaryBrush` 实底 + `PrimaryFgBrush` 字 |
| 上一集 / 下集 | 位于播放器控制条内；首集/末集时置灰（`opacity .4`、不可点） |
| 自动连播 | 开关默认开，状态持久化到 `config.videoPlayback.autoPlayNext`（复用现有设置项） |
| **刷新地址** | 单独按钮：签名过期（约 2 小时）后 403 时手动换；也可由播放器检测到 403 自动重取一次 |
| 追剧 / 收藏 | 站点侧需登录，**本工具只做本地记录**（`short-drama-favorites.json`），不弹登录框 |
| 返回 | 回到列表并恢复滚动位置与当前页码 |

**为什么选集栏放右侧而不是播放器下方**：单集只有 2–4 分钟，用户切集频率远高于其它视频类型；右侧常驻 6×N 的方格比"点开再选"少一次点击，且 348px 恰好等于现有 `RightSidebarDefaultWidth`，不用新开一套尺寸。

---

## 六、榜单页（`rank.html`）

行式列表而非封面网格——榜单的可读性来自**名次 + 热度**，封面只是辅助：

| 元素 | 规格 |
| --- | --- |
| 名次 | 宽 26，`tabular-nums`；前 3 名 17px + `PrimaryBrush`，其余 15px + `TextDisabledBrush` |
| 缩略图 | 72×90，圆角 8 |
| 标题行 | 13.5 半粗 + 紧跟一个集数小胶囊（`PrimarySubtleBrush` 底 / `PrimaryLightBrush` 字） |
| meta 行 | 11.5 `TextDisabledBrush`，`类型 · 标签 · 标签 · 评分` |
| 描述 | 12 `TextSecondaryBrush`，单行省略 |
| 热度 | 右对齐，13 加粗数值 + 11「热度」，下方涨跌（`SuccessBrush` ▲ / `TextDisabledBrush` —） |
| 行悬停 | 行底 `SurfaceBrush` + 1px `CardBorderBrush`，圆角 12 |

涨跌数据源站**没有**（只有热度值），落地时只显示热度，`▲ n` 需删掉或在本地按上一次抓取快照算差值（P3）。

---

## 七、状态设计（复用 `StateHostControl`）

| 状态 | 表现 |
| --- | --- |
| 加载中 | 6 张骨架卡（`SkeletonBrush` 圆角 12），**不用**居中转圈——列表页转圈会让 900px 高度突然空一片 |
| 空结果 | 图标 + 「没有找到相关短剧」+ 副文案「换个关键词，或点上面的标签试试」，`StateKind.Empty` |
| 线路失败 | `StateKind.Error`，标题「线路暂时不可用」，描述列出试过的域名，**主按钮 = 换线路**（触发 `/go-home/` 自发现），次按钮 = 打开配置目录 |
| 播放地址过期 | 播放器内联浮层「地址已过期，正在重新获取…」，自动重试 1 次；仍失败则显示「刷新地址」主按钮 |
| 单集取流失败 | 选集格该集标红点（`DangerBrush`）+ toast，不整页报错 |
| 下载校验失败 | 该集卡片角标转 `WarningBrush`「残缺」，详情页内联提示「第 4 集文件不完整，点此重下」，本地库不把它算作"已下载" |

---

## 八、落地清单（控件 → 文件）

| 设计元素 | 实现方式 | 文件 |
| --- | --- | --- |
| 顶栏第 5 个胶囊 | 新增 `KindDramaPill` | `MainWindow.xaml` |
| 左栏三个分组 + 线路卡 | 新 `ShortDramaNavSectionProvider`；线路卡放在 `NavRail` 上方的独立 `Border`（不进 NavItem 列表，避免被 Rebuild 丢失选中态） | `Shell/Providers/ShortDramaNavSectionProvider.cs`、**新增** `Controls/LineStatusCard.xaml` |
| 6 列封面网格 | 复用 `VirtualizedCardGrid`（`DesiredColumns=6`、`MinCardWidth=160`） | 不改 |
| 卡片 | **新增** `MediaVisualKind.DramaCover` + 对应 `DataTemplate`；新增 `ShortDramaCardAdapter` | `Controls/MediaCard.xaml`、`ViewModels/ShortDramaCardAdapter.cs` |
| 筛选 chips 行 | 新增 `TagChipRow`（单选语义；选中=中性填充+描边，与 DSO "状态用填充" 一致） | **新增** `Controls/TagChipRow.xaml` |
| 继续观看 3 卡 | **新增** `ContinueWatchStrip`，数据来自 `ShortDramaHistoryService` | **新增** `Controls/ContinueWatchStrip.xaml` |
| 播放器 | 直接复用 `OnlineVideoPreviewPlayer`（`StreamUrl` + `PosterUrl`，`Referer` 传空） | 不改 |
| 选集格 | **新增** `EpisodeGrid`（`ItemsSource` + `SelectedIndex`，5/6 列自适应；带 `EpisodeLocalState` 三态点） | **新增** `Controls/EpisodeGrid.xaml` |
| 下载菜单 | `Button` + `ContextMenu`（或 `Popup`），菜单项右侧带预估体积 | `Views/ShortDramaDetailView.xaml` |
| 本地页 | **新增** `ShortDramaLocalView`：下载中区块 + 已下载网格 + 占用统计 | **新增** `Views/ShortDramaLocalView.xaml` |
| 下载中行 | **新增** `DownloadTaskRow`（或 `ItemsControl` + `DataTemplate`） | `Views/ShortDramaLocalView.xaml` |
| 下载页按剧聚合 | `TransferKind.ShortDrama` + 聚合行（子进度「第 3/6 集」） | `ViewModels/DownloadPageViewModel.cs` |
| 榜单行 | **新增** `RankRow`（或用 `ItemsControl` + `DataTemplate`，无需自绘控件） | `Views/ShortDramaRankView.xaml` |
| 密度切换（4/5/6 列） | 复用 `GridCellSizer` + 现成分段控件样式 | 不改 |
| 加载 / 空 / 错误态 | 复用 `StateHostControl` | 不改 |

**预计新增 8 个控件、0 处重构既有控件**——现有 `MediaCard` / `VirtualizedCardGrid` / `StateHostControl` / `OnlineVideoPreviewPlayer` 全部原样复用。

---

## 九、下载与本地库（`local.html`）

本地功能 = **下载 → 落盘 → 离线可播**。下载链路的 ffmpeg 可行性已在 `docs/短剧页接入方案.md` 第六节实测通过（46.7 MB / 261.5 秒 / 720×1280），这里只讲界面。

### 9.1 三个入口，一件事

| 入口 | 位置 | 语义 |
| --- | --- | --- |
| 「下载 ▾」按钮 | 播放页操作行，紧随主播放按钮 | 本集 / 未看集 / 全剧 / 打开文件夹 |
| 选集格状态点 | 播放页右侧选集 | 每集右上角 5px 圆点：灰=云端、橙=`WarningBrush`=下载中、绿=`SuccessBrush`=已下载；已下载的格子加 1px 绿色内描边 |
| 「本地」导航项 | 左栏 我的 → 本地（带数量） | 离线库列表 |

**「下载未看集」为什么不叫「下载剩余集」**：用户真正想要的是"把我还没看的那几集存下来"，而不是"补全缺的集"。这个语义差异决定了它是按观看进度算，而不是按文件存在性算。

### 9.2 本地页结构

```
页头：本地短剧          6 部 · 28 集 · 占用 2.6 GB  [打开目录]
工具行：[搜索本地剧名]                        [全部|未看完|未下载全]
下载中（有任务时才出现）
  ├ 妈妈和姐姐…  第 4/6 集   ▬▬▬▬▬▬▬ 62% · 2.4 MB/s · 剩 1:20 · 44/71 MB   ⏸ ✕
  └ 苗疆少女     排队中      等待前一个任务完成 · 并发上限 2                 ✕
已下载 6 部                                  按下载时间 ▾
  └ 6 列网格：卡片徽章=绿色「本地」，角标=「6 / 6 集」，副信息=「1.2 GB · 3 天前下载」
页脚一行：下载目录说明（config\短剧下载，删文件夹即从库中移除）
```

| 元素 | 规格 |
| --- | --- |
| 「下载中」行 | 44×56 竖版缩略图 + 剧名 + `第 n/N 集` 胶囊（`PrimarySubtleBrush`）+ 3px 进度条（`PrimaryBrush`）+ 一行 `tabular-nums` 明细；右下 28×28 图标按钮（暂停 / 取消）；列表宽 **840**（不让进度条拉满 1160，否则像进度条故障） |
| 排队行 | 进度条 0%、明细换成「等待前一个任务完成 · 并发上限 2」，只保留取消按钮 |
| 「本地」徽章 | `rgba(61,213,152,.16)` 底 + `SuccessBrush` 字（**状态用填充**，不描边） |
| 占用统计 | 页头右对齐，数字用 `tabular-nums` 加粗 |
| 空态 | `StateHostControl`：「还没有本地短剧」+「去发现页点『下载』即可离线观看」+ 主按钮「去发现」 |
| 删除 | 卡片右键 → 删除本集 / 删除本剧，**走回收站**（保留反悔余地），不硬删 |

### 9.3 下载菜单（播放页）

```
[从第 3 集继续] [下载 ▾] [追剧] [收藏] [分享]           [刷新地址]
                  └─ 下载本集    第 3 集 · 47 MB
                     下载未看集  第 4–6 集
                     下载全剧    6 集 · 约 280 MB
                     ─────────────
                     打开下载文件夹（尚未下载时置灰）
```

- 每个菜单项右侧给出**预估体积**——短剧单集 30–70 MB，用户需要在下之前知道要花多少磁盘；
- 体积算法：首集实际大小 × 集数（首集已下过则直接用真实值）；未知时显示「约 N 集 × 50 MB」；
- 「下载全剧」在已全下时变成「已全部下载」并置灰。

### 9.4 竖屏适配（设计稿与实现的一处已知差异）

源站视频是 **720×1280 竖屏**。设计稿为了版面稳定把播放器画成 16:9，**实现时不要锁死 16:9**：

- stage 用 `MaxHeight`（建议 460）+ 视频比例自适应，`Stretch=Uniform`，两侧留黑；
- 海报（未播放态）用封面竖图居中 + 两侧模糊填充（比整块黑边好看，也不泄露裁切）；
- 本地卡片缩略图按 4:5 裁切，抽帧取第 3 秒（避开首帧黑场）。

---

## 十、可访问性与细节

- 所有可点区域 ≥ 32px 高（选集格 34×34、按钮 34）。
- 文字对比：正文 `TextPrimary` 在两种画布下 ≥ 7:1；`TextDisabledBrush` 只用于辅助信息（≥ 4.5:1）。
- 键盘：`Ctrl K` 聚焦搜索；`.` / `,` 切上一集/下一集；`Esc` 从播放页返回列表（沿用 `MainWindow` 现有返回栈）。
- 角标文本不用 emoji，图标全部走 `Themes/Icons.xaml` 的矢量 Path（设计稿里的 SVG 已按同一套 stroke 1.6 / 线性风格绘制）。
- 深浅主题只换字典：设计稿里除「封面占位」外没有任何硬编码色值，落地后 `ThemeManager` 切换不需要改视图。

---

## 十一、未做 / 待定

1. **动效未做**：卡片悬停 160ms、播放键淡入、选集切换的 120ms 高亮过渡。结构落地后按 `Motion Spec` 补。
2. **封面为占位**：接入真实封面后需复核 4:5 裁切身位（尤其人物脸部），必要时改 `Stretch=Uniform` + 上下留边。
3. **榜单涨跌**：源站无数据，见第六节。
4. **搜索页**：设计稿里搜索复用同一网格（输入后原地替换 `最近上新` 区块），未单独出图。
5. **沉浸式连播**：站点支持 `data-hg-next-url` 无限流式连播（像短视频上下滑），本轮不做，属于 P3 可选玩法。
