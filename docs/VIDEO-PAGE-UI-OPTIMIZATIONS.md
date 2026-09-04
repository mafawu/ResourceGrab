# 视频页 UI 优化清单

> 基于 2026-09-04 对 `VideoView.xaml(.cs)`、`PosterCard`、在线搜索/详情/播放交互的实地核对。
> 聚焦视频页内部可落地的体验级优化，每条附证据位置与动作。不含在线搜索的分页/无限滚动与追加 loading（已另行处理）。

---

## A · 在线搜索

### A-1　卡片后台回填详情时没有骨架占位，看起来像信息缺失
- **证据**：`VideoView.xaml.cs:909` `StartOnlineDetailEnrichment` 逐卡片后台拉详情补发行日期/演员/标签，回填前卡片副信息区为空。
- **影响**：用户以为卡片坏了或信息就是缺失的，实际只是还没回填完。
- **动作**：回填前在副信息区显示灰色骨架条（shimmer placeholder）或「加载中…」占位，回填后淡入真实值。可加 `DataTrigger` 按 `IsEnriched` 标志切换。

### A-2　换源时整个结果区清空重建，切回不缓存
- **证据**：`OnlineSourceTab_Click`（`VideoView.xaml.cs:176`）换源时 `ResultCardsPanel.Children.Clear()` + 回到空状态，之前结果全丢。
- **影响**：在 MissAV / JavDB 之间来回切换要重复请求、反复看空状态闪烁。
- **动作**：每个源的结果缓存到内存（带 TTL，复用 `OnlineVideoDetailCache` 单例扩展到搜索结果级），切回时秒回显已缓存结果，后台静默刷新。

### A-3　空结果状态没有可操作的动作
- **证据**：`VideoView.xaml.cs:905` 无结果时 `OnlineEmptyHint.Text = "没有找到匹配的视频"`，只有一段文本。
- **影响**：用户不知道还能做什么（换源？按番号兜底？）。
- **动作**：空状态加两个按钮——「换源重试」（切到其他 `IVideoSource`）和「按番号兜底搜索」（主动触发 `TryOnlineFallbackSearchAsync`，现在只在 MissAV 0 结果时自动跑一次）。按钮根据当前源和关键词是否可解析为番号动态启用。

### A-4　`BuildVideoSourceTabs()` 每次进页面都全量重建
- **证据**：`VideoView.xaml.cs:139` `OnShown` → `BuildVideoSourceTabs()`，每次都 `SourceTabs.Children.Clear()` 重新 new 一批 RadioButton。
- **影响**：源集合在运行期不变，重复创建只增开销和闪烁。
- **动作**：缓存上次构建用的源 Id 列表，进页面时与当前 `GetServices<IVideoSource>()` 做 diff，集合未变则跳过重建。或加 `if (SourceTabs.Children.Count == sources.Count) return` 守卫。

---

## B · 本地视频库

### B-1　本地详情侧栏是一根 552 行的超长滚动条
- **证据**：`VideoView.xaml:436-552` `DetailScroll` 把海报、操作按钮、演员、标签、文件信息表、剧照、同系列、同演员、相关推荐、刮削报告、短评**全部堆进一个垂直滚动**。
- **影响**：找一个信息要滚很久；在线详情有 Tab 分段（`:222` 详情/磁力），本地详情反而没有，不一致。
- **动作**：加 Tab 分段（`视频信息` / `剧照` / `推荐` / `刮削报告`），或用 `Expander` 折叠区段默认只展开「视频信息」。复用在线详情的 Tab 控件样式（`:222-234`）。

### B-2　排序选项缺「最近观看」「最常观看」
- **证据**：`VideoView.xaml:570-578` 排序有最近添加/最早添加/标题/发行日期/评分/我的评分，但 `WatchStatsText`（`:521` 观看次数）和 `OpenStatsText`（`:524` 打开次数）数据已存在却无对应排序项。
- **影响**：本地媒体库最常用的「继续看」「最常看」场景缺失，这是视频库的核心排序诉求。
- **动作**：补「最近观看 ↓」和「观看次数 ↓」两个 `ComboBoxItem`（Tag=`WatchedDesc` / `WatchCountDesc`），`VideoLibraryService` 的排序分支对应补上。数据已在 `VideoItem` 里，改动集中在 VideoView + LibraryService 两处。

### B-3　本地工具栏与在线工具栏视觉不统一
- **证据**：在线搜索头是 `CardPanelStyle` 边框包裹（`VideoView.xaml:87`），本地工具栏（`:562`）是无边框平铺的标题+排序+按钮。
- **影响**：同样是「标题+操作+卡片尺寸」组合，两种外观，视觉割裂。
- **动作**：统一用同一个工具栏外壳——都加 `CardPanelStyle` 边框，或都去掉。建议都加边框（与漫画搜索页 `SearchView.xaml:13` 一致）。

### B-4　`VideoFileCard`（已 Obsolete）仍在本地网格使用
- **证据**：`VideoView.xaml:595` 本地网格 `ItemTemplate` 还是 `VideoFileCard`，替代品 `MediaCard` + `MediaCardAdapters` 未接上。
- **影响**：死代码标了 Obsolete 还在用，迁移未完成，维护负担。
- **动作**：替换为 `MediaCard` + `VideoCardAdapter`，覆盖 `VideoFileCard` 全部角标/状态能力后删除 `VideoFileCard`。

---

## C · 播放器与详情

### C-1　抽屉里的播放器高度写死 349px
- **证据**：`VideoView.xaml:182` `Height="349"` 固定，不随抽屉宽度（620 固定）或窗口缩放变化。
- **影响**：16:9 下 620 宽凑巧约 349，但窗口缩放或抽屉宽度调整后比例失调，出现黑边或裁切。
- **动作**：用 `SizeChanged` 按实际宽度算高度（完整详情页 `OnlineFullPlayerSlot` 已经这么做了，`:328`），抽屉对齐同一套逻辑。

### C-2　剧照点击在外部浏览器打开，没有应用内图库
- **证据**：`VideoView.xaml.cs` `RenderOnlinePreviewImages` 里点击剧照是 `Process.Start(captured)` 在浏览器打开原图。
- **影响**：离开应用、没有上一张/下一张导航、没有缩放，体验割裂。
- **动作**：加一个轻量应用内 lightbox/画廊控件（左右键翻页、滚轮缩放、Esc 关闭），剧照浏览不离开应用。漫画阅读器（`ReaderView`）已有图片浏览与缩放逻辑，可抽公共控件复用。

### C-3　磁力列表无排序、无批量操作
- **证据**：`VideoView.xaml:267` 磁力列表按源返回顺序平铺，无排序，无批量复制。
- **影响**：结果多时找不到体积最大/最新的，逐条复制低效。
- **动作**：加排序（体积↓/日期↓），每行加 checkbox 支持批量复制或「打开下载工具」。

### C-4　关闭抽屉没有滑出动画
- **证据**：`SetOnlineDetailVisible(true)` 有 200ms 滑入动画（`AnimateDrawerIn`），但 `SetOnlineDetailVisible(false)` 直接 `Visibility=Collapsed`，瞬间消失。
- **影响**：开合不对称，关闭瞬间消失显得生硬。
- **动作**：关闭时反向播一个 200ms 滑出动画（`X: 0 → 620`），`Completed` 后再 `Collapsed`。复用 `AnimateDrawerIn` 的 `DoubleAnimation` 反向参数。

---

## D · 视觉一致性

### D-1　评分胶囊颜色硬编码，不跟随主题
- **证据**：`VideoView.xaml:210`/`:212`/`:304`/`:306` 评分背景 `#26FFB347`、文字 `#E8890C` 写死，浅色/深色主题下都一样。
- **影响**：深色模式下橙色与背景对比度可能不足，且违反「颜色走 DynamicResource」的整体约定。
- **动作**：提到主题资源字典（已有 `WarningSubtleBrush`/`WarningBrush`），引用 `DynamicResource` 让深色模式自动适配。

### D-2　两处空状态视觉权重不一致
- **证据**：本地空状态（`:603` `EmptyPanel`）有大图标+标题+副标题+按钮；在线空状态（`:129` `OnlineEmptyState`）只有小图标+一行文本。
- **影响**：同为「空状态」但视觉权重差一倍，用户感觉是两套界面。
- **动作**：统一空状态模板（图标尺寸、字号、是否带按钮），建议都引用 `StateHostControl` 的空状态样式（漫画侧已用）。

---

## 优先级建议

| 优先级 | 项 | 理由 |
| --- | --- | --- |
| **高** | B-2（最近观看排序）、B-1（本地详情 Tab） | 视频库日常使用核心场景，改动集中 |
| **高** | A-1（骨架占位） | 直接影响「卡片是否像坏的」的第一印象 |
| **中** | C-1（播放器高度自适应）、C-4（滑出动画） | 体验细节，改动小 |
| **中** | A-2（换源缓存）、A-3（空状态动作） | 减少重复请求与等待 |
| **低** | C-2（应用内图库）、D-1/D-2（视觉统一） | 锦上添花，可穿插进行 |
| **清理** | B-4（删 VideoFileCard）、B-3（工具栏统一） | 死代码与一致性收尾 |