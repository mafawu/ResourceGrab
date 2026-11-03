# 漫画首页 · DSO UI 升级设计说明

**目标视口**：1440×900（1x）。平台 Windows WPF。
**北极星**：界面是暗房，封面才是照片——UI 退后，封面是唯一主角。

---

## 1. 现状诊断（基于 docs/images/ui-dark.png、ui-light.png）

| # | 问题 | 影响 |
| --- | --- | --- |
| 1 | 启动首屏 80% 为空白，只有一个居中空态图标 | 最贵的一屏没有承载任何内容，用户没有回访理由 |
| 2 | 左栏 9 个导航项各带 28px 圆角图标外框 | 9 个方框构成持续视觉噪音，与选中态竖条互相干扰 |
| 3 | 搜索条、排序、密度控制、来源 chips 被裹在同一张卡片里（`BrowserToolbar` 是一个整体容器） | 四类不同层级的信息挤成一个"大搜索框"，密度控制看起来像搜索的一部分 |
| 4 | 顶栏右侧「漫画 · 在线搜索与本地浏览」 | 与顶栏类型胶囊表达同一信息，重复 |
| 5 | 深色下画布 `#0B0E14` 与表面 `#12161F` 仅差约 1.15:1 | 卡片没有边界感，只靠 1px 描边撑住 |
| 6 | 空态图标底板为靛蓝渐变圆角方块 | 与极简方向不符，像模板残留 |

## 2. 结构改动

```
顶栏（保留四胶囊 / 窗口控制）
├ 左栏 216px：源切换 → 发现 / 我的 / 工具 三组，无图标外框
│   └ 选中态：8% 靛蓝底 + 左侧 2.5px 竖条 + 文字转 accent-text
└ 主区
    ├ 页头：页面标题 28px + 副标题（当前源）
    ├ 搜索行：内联搜索条（max 620）+ 结果区工具条（密度分段控制，右对齐）
    ├ 来源 chips 一行
    ├ 继续阅读：3 张宽卡（封面 72×96 + 标题 + 章节 + 3px 进度条 + 时间）
    └ 本周热门：6 列 2:3 海报网格（角标 + 排名 + 标题 + 标签）
```

**空态处理**：不再为「无关键词」单独做一屏空态，而是让同一页在无查询时承载「继续阅读 + 本周热门」。搜索后原地替换主体为结果网格，页头与搜索条保持不动——避免页面跳变。

## 3. 令牌改动（可直接落到 Themes/）

### 3.1 DarkColors.xaml

| 键 | 现值 | 新值 | 理由 |
| --- | --- | --- | --- |
| `SurfaceBrush` | `#12161F` | `#151A25` | 与画布拉开到约 1.3:1，卡片获得边界 |
| `CardBorderBrush` | `#232A38` | `#26303F` | 描边随表面一起抬一档，避免"表面变亮而描边变弱" |
| `TextDisabledBrush` | `#6B7488` | `#7A8498` | 在 `#0B0E14` 上 4.12:1 → 5.12:1，达到正文 4.5:1 |
| 新增 `AccentTextBrush` | — | `#9AAEFF` | 用于"靛蓝底浅色块上的文字"（导航选中、chip 选中），在 `#1A2340` 上 7.2:1 |

### 3.2 Colors.xaml（浅色）

| 键 | 现值 | 新值 | 理由 |
| --- | --- | --- | --- |
| `TextDisabledBrush` | `#8B93A5` | `#646E82` | 在 `#F5F6F9` 上 2.79:1 → 4.63:1 |
| 新增 `AccentTextBrush` | — | `#3D57E8` | 在 `#E8ECFF` 上 3.66:1 → 4.74:1 |
| `PrimaryBrush`（仅作填充时） | `#4F6BFF` | `#4459F0` | 白字在 `#4F6BFF` 上 4.30:1 → 5.30:1 |

> 注意：`PrimaryBrush` 同时承担"描边/焦点环/进度条"这类非文字用途，若不想动它，正确做法是**新增 `PrimarySolidBrush = #4459F0` 专供白字按钮**，而不是整体改深。

### 3.3 Tokens.xaml

| 键 | 现值 | 新值 | 理由 |
| --- | --- | --- | --- |
| `FontPageTitle` | 19 | 28 | 页面标题需要成为页面的锚点，19 与正文 13 拉不开 |
| 新增 `FontPageSub` | — | 13 | 副标题独立于 `FontBody`，避免日后调正文牵动页头 |
| `RadiusSm` | 8 | 8（不变） | 导航项/按钮沿用 |
| 新增 `PageGutter` | — | 32 | 主区左右留白（现为硬编码） |
| 新增 `SectionGap` | — | 22 | 区块间距（现为散落的 Margin） |

## 4. 需要新增/复用的控件

| 控件 | 状态 | 说明 |
| --- | --- | --- |
| `ContinueReadingCard` | 新增 | 72×96 封面 + 标题 + 章节 + 进度条；数据来自下载历史/阅读进度 |
| `PosterCard` | **直接复用，无需改动** | 已具备 `TopLeftContent` / `TopRightContent` / `BottomLeftContent` / `BottomRightContent` 四个角标槽位、`Progress`、`MetaLeft/MetaRight`；本轮设计里的「中文/无修」角标与排名数字直接填进去即可 |
| `DensitySegmented` | 新增 | 结果区右侧 4/5/6 分段控制，绑定现有 `GridCellSizer` |
| `SectionHeader` | 新增 | 标题 + 右侧「查看全部」动作 |
| `StateHostControl` | 复用 | 搜索中/空/错误仍走它，不再自绘空态 |

## 5. 未做的部分

- 搜索结果态、详情抽屉、下载面板未在本轮重设计，沿现有实现。
- 动效未做（hover 提升、区块入场），待结构落地后按 Motion Spec 补。
- 封面为抽象色块占位，接入真实封面后需复核 2:3 裁切与角标可读性。

## 6. 落地路径（改动面）

现有 `SearchView` 是薄壳：`BrowserToolbar`（标题 / 搜索 / 排序 / 密度滑块 `CardSizeSlider` / 来源 chips 全在这一个容器里）+ `StateHostControl`（`StateKind` 驱动，默认 `Hint`）+ 分页条。所以改动集中在三处：

1. **`Theme/*.xaml`**：按 §3 改 6 个令牌值 + 新增 `AccentTextBrush`（深浅各一）。
2. **`Controls/DiscoveryPanel.xaml(.cs)`**（新增）：继续阅读 + 热门两个区块，数据来自 `LocalLibraryService`（最近阅读进度）与排行榜源；无数据时该区块整块隐藏，不留空壳。
3. **`BrowserToolbar` 版式拆分**：现有 `PART_Title` / `PART_SearchBox` / `PART_Sort` / `PART_Actions` / `PART_SizeSlider` / `PART_SearchButton` / `PART_Chips` 七个插槽全部保留，只把外层容器从"一张大卡片"改成"页头 + 搜索行 + chips 行"三段（密度滑块留在搜索行右端，视觉上与搜索框分离）。
4. **`StateHostControl` / `SearchView`**：`StateKind.Hint` 分支改为渲染 `DiscoveryPanel`。

`PosterCard`、`GridCellSizer`、`VirtualizedCardGrid`、`CardSizeSlider` 都不需要改。
