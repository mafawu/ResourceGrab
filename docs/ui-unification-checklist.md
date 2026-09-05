# UI 统一回归检查清单

> 配套文档：`docs/UI统一重构合并执行计划.md`。更新：2026-09-05 自动化迁移轮次。

## Phase 0：基线
- [x] dotnet build 编译通过（0 错误）
- [x] dotnet test 通过（267/267，ResourceGrab.Core.Tests）
- [x] 代码现状盘点完成（合并计划 §2）

## Phase 1：令牌与图标
- [x] Tokens.xaml radius scale (Xs/Sm/Md/Lg/Xl/Pill) + spacing + sidebar 尺寸 + 字号别名
- [x] Icons.cs 补充 Local/HeartOutline/ChevronUp/ChevronDown/History/TaskProgress/FolderPlus
- [x] ~45 处视图硬编码 CornerRadius 替换为 DynamicResource token（16 文件）
- [x] VideoView.xaml.cs / NovelReaderView.xaml.cs 硬编码颜色替换为主题资源

## Phase 2：Shell 接线
- [x] MediaKind / NavItem / NavSection / ShellRoute / SidebarPolicy 契约
- [x] ShellController / ShellNavigator 实现
- [x] 三个 NavSectionProvider + NavRail 接入 MainWindow
- [x] 右栏由 SidebarHost 承载（ContentControl 包裹，DownloadPanel 默认体，关闭按钮接 CollapseRightPanel）
- [x] 五个面板实现 ISidebarPanel 并经 `ShowSidebarPanel()` 注入
- [x] 全量路由注册（manga ×7 + video ×5 + novel ×2），`Register` 支持每路由 SidebarPolicy
- [x] `OnNavItemClicked` 全部走 `_navigator.GoTo`，`OnRouteChanged` 按路由策略更新右栏
- [x] 页面无直接访问 `MainWindow.PageHost` / `RightPanelHost`（仅壳内部控制可见性）

## Phase 3：BrowserToolbar
- [x] `BrowserToolbar` 控件（标题/搜索框/chips/排序/动作/卡片尺寸/搜索按钮，各段可隐藏）
- [x] 漫画搜索页接入（SearchView）
- [x] 视频在线搜索接入（VideoView OnlineToolbar）
- [x] 视频本地页接入（VideoView LocalToolbar，代码后置组装标题/排序/动作）
- [ ] 其余列表页（排行/分类/每周/收藏/本地漫画）仅复用 CardSizeSlider，无三态不一致问题，可选迁移

## Phase 4：视频子页路由化
- [x] video.online / video.local / video.recommend / video.tasks / video.actors 全量路由
- [x] VideoView 内部导航栏保持隐藏，SwitchNav 由路由工厂驱动
- [x] TransferPlayback 三播放器接力保留在 VideoView 内
- [ ] VideoView 拆分为独立子页 View（保留薄协调层形态；需 GUI 回归验证后执行）

## Phase 5：结果状态与网格
- [x] ResultStatus / ResultState<T> / PageRequest / PageResult<T> 模型 + MediaResultHost 控件
- [x] 视频本地列表已用 VirtualizedCardGrid（原诊断已过时）
- [x] 漫画侧 StateHostControl + VirtualizedCardGrid
- [ ] 各列表页逐页迁移到 MediaResultHost（低收益，保持现状）

## Phase 6：统一卡片
- [x] MediaCardViewModel 基类 + 四适配器（MangaOnline/MangaLocal/Video/Novel）
- [x] MediaCard 控件（三模板 TemplateSelector）
- [x] 视频本地列表 + ActorProfileView 已切换 MediaCard + VideoCardAdapter
- [x] 删除无引用的 VideoCard.xaml/.cs
- [ ] AlbumCard / LocalComicCard / VideoPosterCard / PosterCard 调用点切换（需逐卡截图对比，防 hover/双击/角标回退）

## Phase 7：筛选面板契约
- [x] FilterOption / FilterSection / FilterSelection / FilterStateChangedEventArgs / IFilterPanel
- [x] MangaLocalFilterProvider / NovelFilterProvider / VideoFilterProvider
- [x] FilterSidebar 统一筛选控件
- [x] 三面板（Local/Novel/VideoSearchPanel）显式实现 IFilterPanel（GetSelection/ApplySelection/Clear/SetSections + 统一 FilterChanged 事件）
- [x] 三面板经 SidebarHost 挂载

## Phase 8：详情与阅读器外壳
- [x] DetailShell 控件
- [x] ReaderShell 控件（沉浸模式支持）
- [ ] MediaDetailHost + DetailDrawerShell/DetailPageShell 双壳（未开始；需 GUI 验证交互）

## Phase 9：清理与固化
- [x] 旧 VideoCard 删除
- [x] 两份旧方案文档标注被合并版取代
- [x] README 增加 UI 规则摘要
- [ ] AlbumCard / LocalComicCard / VideoPosterCard / PosterCard 删除（依赖 Phase 6 剩余项）
- [ ] 全量 smoke（需人工 GUI 验证：三媒体 × 浅深主题 × 播放接力链路）
