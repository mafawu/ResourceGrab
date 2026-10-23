# DSO UI 设计系统（ResourceGrab 重构版）

> 一句话：**中性冷灰画布 + 单一靛蓝强调色 + 发丝级描边**。颜色只用来表达层级，不用来装饰。
> 旧版（粉色渐变 + 整块实心高亮 + 发光阴影）已全部移除。

## 1. 设计原则

| 原则 | 落地方式 |
| --- | --- |
| 一个强调色 | 全局只用 `PrimaryBrush` (#4D6BFE) 表达「选中 / 主操作」，其余用中性灰阶表达层级 |
| 发丝描边优先 | 1px `CardBorderBrush` 分层，阴影只保留极轻的一层（Blur 22 / Depth 2 / 16% 不透明） |
| 状态用填充，不用描边 | 筛选药丸：开启=中性填充+主文字；关闭=透明+弱化文字。选中态不再整排描边高亮 |
| 沉默的选中 | 导航选中=8% 强调底 + 3px 左侧指示条；不再有整块实心渐变和光晕 |
| 深浅同构 | `Colors.xaml` / `DarkColors.xaml` 键名完全一致，切换只换字典，不改 XAML |

## 2. 颜色

### 浅色（Daylight）
| Token | 值 | 用途 |
| --- | --- | --- |
| AppBackgroundBrush | #F5F7FB | 画布 |
| SurfaceBrush | #FFFFFF | 卡片 / 面板 / 顶栏 |
| CardBorderBrush | #E6EAF2 | 1px 描边 |
| DividerBrush | #EDF0F7 | 分隔线 / 药丸底 |
| TextPrimaryBrush | #10141F | 主文字 |
| TextSecondaryBrush | #5C667A | 次级文字 |
| TextDisabledBrush | #9AA3B4 | 关闭态 / 占位 |
| PrimaryBrush | #4D6BFE | 强调 / 主按钮 |
| PrimarySubtleBrush | #EEF1FF | 强调底 |
| Success / Warning / Danger | #12B981 / #F5A524 / #E5484D | 语义色 |

### 深色（Midnight）
画布 #0B0E14、表面 #12161F、描边 #232A38、主文字 #E9EDF5；
强调色提亮到 #6E86FF（保证在 #12161F 上 ≥ 4.5:1），主按钮前景改为深色 #0B0E14。

## 3. 尺寸与形状

- 4pt 基准网格；正文 13px、次级 12px、卡片标题 13.5px、页标题 19px。
- 圆角：控件 10、卡片 16、面板 16、药丸 15（半高）、图标按钮 9–10。
- 控件高度：常规 30、工具栏 34、窗口按钮 32。
- 间距：4 / 8 / 12 / 16 / 20。

## 4. 组件约定

| 组件 | 规则 |
| --- | --- |
| 主按钮 | 实心 `PrimaryBrush`，无渐变；hover 变暗一档，pressed 再暗一档 |
| 次级按钮 | 透明底 + 1px 描边，hover 用 `HoverBgBrush` |
| 图标按钮 | 32×32、圆角 10、无边框，hover 出现 5% 墨色底 |
| 分段控件（漫画/视频/小说/写真） | 灰轨道 + 白色浮起药丸，选中不再填满强调色 |
| 筛选药丸 | 见「设计原则」第 3 条；多选开关与单选筛选语义分开 |
| 导航项 | 高 42、圆角 10、选中=强调淡底 + 左侧 3px 指示条 + 强调色文字 |
| 卡片 | 圆角 16、1px 描边、无重阴影；hover 时描边转 `CardHoverBorderBrush` |
| 滚动条 | 7–8px 细条，圆角胶囊，仅在 hover 加深 |
| 输入框 | 圆角 10、聚焦时描边变强调色（不再发光） |

## 5. 图标（app.ico）

- 造型：**取景角标 + 向下箭头** —— 外框代表「抓取范围」，箭头代表「落到本地」，与产品名「抓资源」同构。
- 底板：1024 圆角方（R=236），对角渐变 #6382FF → #3649D6，顶部有一层 30%→0 的纵向高光，让图标有体积而不出现硬分界带。
- 前景：白色角标（线宽 62/1024，小尺寸自动加粗到 74）+ 白色箭头（60/1024）。
- 多尺寸单独渲染（16/20/24/32/40/48/64/96/128/256），≤24px 时自动加粗线宽，避免缩小后糊成灰块。
- 源文件：`resources/icon/gen.py`（Pillow 生成 ICO，不依赖设计软件），产物同名目录下的 `app.ico` / `icon-*.png`。

顶栏品牌块与图标同构：30×30 圆角 9 渐变底 + 15×15 角标与箭头，保证「任务栏图标 = 应用内标识」。

## 6. 如何再换肤

1. 改 `Themes/Colors.xaml` 与 `Themes/DarkColors.xaml`（键名不要动，XAML 里有 214 处 DynamicResource 引用）。
2. 改 `Themes/Tokens.xaml` 调整圆角 / 间距 / 字号。
3. 形状与状态规则在 `Themes/Core.Controls.xaml`、`Themes/Search.xaml`、`Controls/NavRail.xaml` 的模板里。
4. 重新生成图标：`python resources/icon/gen.py`，产物覆盖 `src/ResourceGrab.App/Assets/app.ico`。
