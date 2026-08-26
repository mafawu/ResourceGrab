# 抓资源 ResourceGrab

多源漫画 / 在线视频资源下载器与本地媒体库，基于 .NET 10 / WPF 的 Windows 桌面应用。
支持禁漫天堂、拷贝漫画、包子漫画等多个内容源的搜索与下载，内置下载引擎、本地漫画库、视频库与小说阅读器。

## 内容源

| 源 | 类型 | 说明 |
| --- | --- | --- |
| 禁漫天堂（jm） | 漫画 | 关键词 / JM 号搜索、收藏夹同步、排行、分类、周榜 |
| 拷贝漫画（copymanga） | 漫画 | 搜索、分类浏览 |
| 包子漫画（baozimh） | 漫画 | 搜索、分类浏览 |
| hitomi | 漫画 | 搜索、热门排行 |
| wnacg | 漫画 | 搜索、日 / 周 / 月 / 年排行 |
| MissAV | 视频 | 在线搜索，解析 m3u8 直链，调用系统默认播放器播放 |

界面顶部下拉切换当前源，导航与页面按源能力自动适配；聚合搜索可同时查询多个源。

## 下载引擎

- 多线程下载：最多同时获取 10 个章节图片列表、并行下载 3 个章节、40 张图片
- 断点续传：已存在的图片自动跳过，完整章节直接跳过，中断后无需从头开始
- 图片重组：禁漫分块乱序图片自动还原拼接；jpg / png / webp 输出格式可选
- 实时进度：全局进度条、每章节进度、实时速度

## 本地媒体库

- **漫画库**：管理多个本地目录，封面墙离线浏览（名字 / 标签），一键打开所在文件夹
- **中文名补全**：优先从目录名提取中文片段；纯外文标题可配置 OpenAI 兼容翻译接口（OpenAI / DeepSeek / 通义等）自动补齐并缓存
- **视频库**：本地视频的收藏 / 评分 / 标签 / 演员 / 系列 / 导演等多维管理与筛选；FFmpeg 自动截帧生成缩略图（未安装 FFmpeg 时降级为系统 Shell 缩略图）
- **小说阅读**：内置阅读器，阅读进度、历史与排版偏好独立保存

其他体验：保存账号密码后启动自动登录（DPAPI 加密）、接口域名失效自动轮换冷却、卡片分格大小统一调节并随窗口宽度自适应。

## 技术栈

- .NET 10 / C#，纯 WPF（XAML），无 WebView2 依赖
- `ResourceGrab.Core`：内容源适配（签名 / 解密 / HTML 解析）、下载引擎、图片重组、本地库服务
- `ResourceGrab.App`：WPF 界面层
- 主要依赖：AngleSharp、Polly、SixLabors.ImageSharp、Microsoft.Extensions.DependencyInjection

## 构建与发布

```powershell
# 还原并构建 Release
dotnet build ResourceGrab.slnx -c Release
```

也可以直接双击 `build-release.bat` 一键编译。

发布单文件（win-x64，框架依赖，输出到 `publish\`）：

```powershell
.\publish.ps1
```

框架依赖包约 27MB（输出到 `BIN\release\`），目标电脑需安装 .NET 10 Desktop Runtime：

```powershell
winget install Microsoft.DotNet.DesktopRuntime.10
```

免安装的自包含版（约 160MB）：

```powershell
dotnet publish src/ResourceGrab.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o BIN\release
```

## 快速上手

1. 启动后在顶部选择内容源；需要账号功能（如禁漫收藏夹）的先点击「账号登录」
2. 搜索关键词或 JM 号，点击卡片进入章节详情，勾选章节（拖动框选 / Ctrl 多选 / 右键菜单）后一键下载
3. 下载完成后从导航栏进入「本地」浏览，`管理路径…` 可增删扫描目录
4. 视频库首次使用时添加本地根目录，程序会自动扫描并生成缩略图

## 数据与配置

程序为便携式设计：数据保存在 exe 同级的 `config\` 目录，绿色携带不写注册表。

| 文件 / 目录 | 用途 |
| --- | --- |
| `config.json` | 全局配置（账号、下载参数、接口域名、翻译等） |
| `download-history.json` | 下载历史 |
| `video-library.json` / `video-folders.json` | 视频库数据与根目录 |
| `novel-*.json` | 小说索引、阅读进度与排版设置 |
| `logs\` | 运行日志 |

安全说明：配置文件中的用户名、密码和翻译 API key 在 Windows 上使用 DPAPI（当前用户作用域）加密存储，旧明文配置会在首次启动时自动迁移为加密格式。

**接口域名轮换**：禁漫 API 域名失效时（登录 / 搜索报 404 等），程序自动在域名列表中轮换，失效域名临时冷却跳过。可在 `config.json` 中自定义域名列表，无需重新编译：

```json
"apiDomains": ["www.cdngwc.cc", "www.cdngwc.net"]
```

**标题翻译**：对缺失中文名的本地漫画，可在 `config.json` 中配置 OpenAI 兼容接口自动翻译，结果缓存到 `local-library-cache.json` 并写回 `album.json`：

```json
"titleTranslate": {
  "enabled": true,
  "baseUrl": "https://api.deepseek.com/v1",
  "apiKey": "sk-xxx",
  "model": "deepseek-chat"
}
```

## 免责声明

本工具仅作学习、研究、交流使用，请勿用于任何商业用途；使用本工具产生的 一切风险由用户自行承担，与开发者无关。