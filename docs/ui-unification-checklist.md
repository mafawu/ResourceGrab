# UI 统一回归检查清单

## Phase 0：基线
- [x] dotnet build 编译通过（0 错误）

## Phase 1：令牌与图标
- [x] Tokens.xaml 新增 radius scale (Xs/Sm/Md/Lg/Xl/Pill)
- [x] Tokens.xaml 新增 spacing scale + sidebar 尺寸 + 字号别名
- [x] Icons.cs 补充 Local/HeartOutline/ChevronUp/ChevronDown/History/TaskProgress/FolderPlus
- [x] ~45 处视图硬编码 CornerRadius 替换为 DynamicResource token（16 文件）
- [x] VideoView.xaml.cs / NovelReaderView.xaml.cs 硬编码颜色替换为主题资源

## Phase 2：ShellController 与 NavRail
- [x] MediaKind / NavItem / NavSection / ShellRoute / SidebarPolicy 契约
- [x] ShellController 实现
- [x] MangaNavSectionProvider / VideoNavSectionProvider / NovelNavSectionProvider
- [x] NavRail 控件实现并接入 MainWindow
- [x] KindPill_Click 切换时刷新 NavRail 分组

## Phase 3：SidebarHost
- [x] SidebarHost 控件实现
- [x] ISidebarPanel 接口 + 五个现有面板适配完成
- [ ] 页面调用点迁移到 ShellController 控制（兼容层保留）

## Phase 4：ShellNavigator 路由替换
- [x] ShellNavigator 实现
- [x] 12 条路由注册（manga ×7 + video ×1 + novel ×2）
- [x] SetPage() 辅助方法统一赋值 + 返回栈跟踪
- [x] 27 处 PageHost.Content = X 替换为 SetPage(X)
- [x] NavigateTo* 方法使用路由导航

## Phase 5：MediaResultHost
- [x] ResultStatus / ResultState<T> / PageRequest / PageResult<T> 模型
- [x] MediaResultHost 控件实现
- [ ] 各列表页迁移到 MediaResultHost（基础设施就位，逐页替换）

## Phase 6：统一卡片
- [x] MediaCardViewModel 基类
- [x] 四个适配器: MangaOnline/MangaLocal/Video/Novel CardAdapter
- [x] MediaCard 控件（三模板 TemplateSelector）
- [x] 旧四张卡片标记 [Obsolete]
- [ ] 视图切换到新卡片后删除旧文件

## Phase 7：筛选面板契约
- [x] FilterOption / FilterSection / FilterSelection / IFilterPanel
- [x] MangaLocalFilterProvider / NovelFilterProvider / VideoFilterProvider
- [x] FilterSidebar 统一筛选控件

## Phase 8：详情与阅读器外壳
- [x] DetailShell 控件
- [x] ReaderShell 控件（沉浸模式支持）
- [ ] 页面迁入外壳（基础设施就位）

## Phase 9：清理
- [x] VideoView 内部导航隐藏，SwitchNav 暴露给 NavRail
- [x] 视频/小说模式显示 LeftNavHost（NavRail 可用）
- [x] 旧卡片 [Obsolete] 标记
- [ ] 全量 smoke 通过后删除旧文件
