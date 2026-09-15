# 开发笔记 / Development notes

> 这是维护者用的实现笔记（踩坑记录、实测数据、验证方法）。
> 面向用户的说明见仓库根目录的 [README.md](../README.md)。

# Jellyfin 增强（自建插件 + 前端脚本）

部署：`<JELLYFIN_DIR>`（Docker / linuxserver 镜像 / 端口 8096 / Jellyfin **12.0.0**）

**没有任何文件被替换或挂载覆盖**：注入在请求时完成，插件的全部内容都在 `config/data/plugins/`（挂载卷）里。

---

## 一、目录结构

```
tools/
├── client-scripts/                 ← 前端脚本的【唯一来源】（改这里）
│   ├── search-results.js           搜索页四个分类方格 + 原生列表页分页桥接
│   ├── grid-hover-preview.js       视频卡片左上角"小眼睛" + 宫格弹层
│   └── navigation-smooth.js        页面切换冻结帧（返回不闪）+ 样式 + 滚动恢复
├── jf-featureenhance/              ← 插件源码（C#）
│   └── Jellyfin.Plugin.FeatureEnhance/
│       ├── Plugin.cs               插件入口（Name = "Feature Enhance"，GUID 保持不变）
│       ├── PluginServiceRegistrator.cs       注册注入中间件
│       ├── PluginUser.cs                     从认证票据取用户 id（身份绝不来自查询参数）
│       ├── ClientInjectionStartupFilter.cs   请求时向 index.html 注入引导脚本 + 3 个 <script>
│       ├── ClientScriptController.cs         /FeatureEnhance/Client/{name}.js（读 DLL 内嵌资源）
│       ├── SearchApiController.cs            Search / Search/Counts / Search/Playable（口径一致 + 原生筛选排序）
│       ├── PathSearchProvider.cs             IInternalSearchProvider（原生搜索支持路径/文件夹名）
│       ├── GridController.cs                 /FeatureEnhance/Grid/{id}（实时生成 9 宫格，10%~90% 取样）
│       └── PathMatcher.cs                    归一化 / 编辑距离 / 近似子串
├── docs/
│   ├── icon.svg                    ← 插件图标的【源文件】（512×512，满幅方形，改这里）
│   ├── icon.png                    导出图：仓库 README / 插件清单 imageUrl / 仪表盘图标都用它
│   └── DEVELOPMENT.md              本文件
└── README.md
```

图标是**满幅方形**（不留圆角、四角不透明）：仪表盘自己会给图标套一层圆角框，
资源里再画圆角就会出现"圆角套圆角 + 四角透明"的观感（2026-09-14 按这个反馈重做过）。
设计语言与 Jellyfin 品牌一致：紫→蓝渐变底 + 白色四角星（"增强/提升"的通用符号）。
用无头 Chromium 渲染 `docs/icon.svg`（1024 再 LANCZOS 缩到 512，等价于 2× 超采样）：

```bash
node tools/scripts/render-icon.mjs      # 见仓库里的渲染脚本；输出 docs/icon.png
```

运行时装在：`config/data/plugins/FeatureEnhance_2.0.0.0/Jellyfin.Plugin.FeatureEnhance.dll`（+ 自动生成的 meta.json）。

插件数据目录用的是**根命名空间**：`config/data/plugins/Jellyfin.Plugin.FeatureEnhance/`。
2026-09-14 改名（PathSearch → FeatureEnhance）时，176 张宫格缓存直接从
`Jellyfin.Plugin.PathSearch/grid/` `mv` 过来，没有重新生成。

---

## 二、改完怎么生效

改 **C#** 或 **JS** 是同一条路（JS 在编译时被内嵌进 DLL，所以改 JS 也要重新 build）：

```bash
export DOTNET_ROOT=$HOME/.dotnet PATH=$HOME/.dotnet:$PATH
cd <JELLYFIN_DIR>/tools/jf-featureenhance
dotnet build Jellyfin.Plugin.FeatureEnhance/Jellyfin.Plugin.FeatureEnhance.csproj -c Release
cp Jellyfin.Plugin.FeatureEnhance/bin/Release/net10.0/Jellyfin.Plugin.FeatureEnhance.dll \
   <JELLYFIN_DIR>/config/data/plugins/FeatureEnhance_2.0.0.0/
docker restart jellyfin
```

浏览器刷 **Ctrl+Shift+R**（注入的 script URL 带版本戳，重启后自动变）。

---

## 三、功能

### 3.1 宫格预览（小眼睛按钮）

- 视频卡片（`data-type` 为 Video/Movie/Episode/MusicVideo）**左上角**出现小眼睛按钮（鼠标端 26px / 触屏 34px）
- 点击 → 全屏弹层显示该视频的 **3×3 = 9 格**宫格图（✕ 或点背景关闭）
- **9 格 = 片长的 10% / 20% / ... / 90%**（每格一帧、各自一次 seek），横向铺满整部片子，
  不是"连续几秒里的几帧"
- **暗场只做单格微调**：某格刚好落在转场/黑帧上时，只把**那一格**往后挪 **1 个百分点**（例如 40% → 41%），
  只挪一次、不循环；**不会为了几格黑画面把整张图重跑一遍**。
  九格里一半以上都暗 ⇒ 这本来就是暗片，原样使用（全黑视频不会因此多跑一轮）。
- **每格都是完整画面，不裁切、无黑边**：格子宽高比跟随视频本身 ——
  竖向视频保持原方向；**横向视频每格旋转 90°**（`transpose=1`）变成竖格，
  于是格子的宽高比恰好等于画面宽高比，整帧刚好放满。
- **整张图永远是竖版**（宽 = 3×格子宽，高 = 1920），客户端**不做任何旋转**，
  只按屏幕大小等比缩放（`max-width:94vw` / `max-height:92vh`）——所以每个视频只需要缓存一份。
- 左下角烧入 **时长 | 文件大小**（如 `1:23:45  |  1.2 GB`）
- **三种状态全部只用图标、不带文字**（Web Animations API 驱动 —— 弹层一律内联样式，不依赖会被清掉的 `<style>`）：
  | 状态 | 表现 |
  |---|---|
  | 生成中 | 正中间三个跳动的点 |
  | 重试中 | 正中间一个旋转的刷新图标（失败后自动重试 2 次，带时间戳绕开浏览器缓存） |
  | 不可用 | 顶部居中的**划掉的小眼睛**徽章，同时显示原生缩略图 |
  —— 没有封面的视频，原生缩略图是 Jellyfin 的**蓝灰色占位图**，所以"不可用"必须有个明确标记，
  否则会被当成"宫格图发蓝"（这个坑真踩过）
- 首次生成（实测，冷缓存）：1080p 116min **1.17s**、4K 3840×2160 116min **1.40s**、
  4K 4096×2304 34min **3.28s**（这个文件冷启动时单格就要 ~1.0s，瓶颈是机械盘随机读）、
  720×1280 竖屏 **0.30s**、480×1068 **0.27s**、230×144 老片 **0.27s**、0.6s 短视频 **0.31s**；
  之后走磁盘缓存（**~8~15ms**）
- **硬件加速完全交给 Jellyfin 自己**（这一版把之前手写的 `-hwaccel/-qsv_device` 全删了）：
  插件按 trickplay 的写法构造 `EncodingJobInfo` + `BaseEncodingJobOptions`，然后直接问
  `EncodingHelper.GetInputArgument()` / `GetVideoProcessingFilterParam()` 要 ffmpeg 参数。
  于是在这台机器上自动得到（实测日志）：
  ```
  -init_hw_device vaapi=va:,vendor_id=0x8086,driver=iHD -init_hw_device qsv=qs@va -filter_hw_device qs
  -hwaccel vaapi -hwaccel_output_format vaapi -noautorotate -i file:"..." -noautoscale
  ```
  设备选择、硬解器选择、色调映射、GPU↔内存搬运（`hwdownload`）全部是原生逻辑；
  Dashboard → 播放 → 转码 里换硬件方案、升级 Jellyfin 都自动跟随。
  配置的那套硬件路径失败时，按 `MediaEncoder` 同样的方式整体回退到软件解码。
  **硬件解码确实在跑**（4K 116min、同样 9 个采样点顺序跑一遍的实测）：
  | | 墙钟 | 整机 CPU 时间 |
  |---|---|---|
  | 硬件（`-hwaccel vaapi`） | **2.95s** | **5.67s** |
  | 软件（`-threads 1`） | 8.44s | 17.14s |
  单帧对比更直观：0.38s vs 0.84s。只有 **JPEG 编码**走 CPU 的 `mjpeg`
  （trickplay 关掉 `EnableHwEncoding` 时走的就是这条路；`tile`/合成需要内存里的帧）。
- **没有**直接调用 `IMediaEncoder.ExtractVideoImagesOnIntervalAccelerated()`：那个方法不接受
  seek，会把整个文件从头读到尾（本机素材是 NTFS 机械盘上的 20GB remux，动辄几分钟），
  只适合后台任务，不适合"点开就看"。
- **故意不用 keyframe-only（`-skip_frame nokey`）**，虽然本机 `TrickplayOptions.EnableKeyFrameOnlyExtraction=true`：
  实测同一台机器、同一个 4K 116min（关键帧间隔 2s）跑 9 格，**3.00s → 2.20s**，只省 ~0.09s/格；
  但代价是两个都致命 ——
  ① 取到的是"附近的关键帧"而非目标时刻：MVSD-603（关键帧间隔 8.34s）实测偏差 **7.27s / 3.04s / 1.10s**，
  而正常模式 `pts` 正好等于请求时间；
  ② **几秒~几十秒的短视频直接 0 帧**：2.1s 与 10.3s 两个素材实测都是 **0/9**（seek 落在唯一的关键帧之后，
  后面全是非关键帧）→ 整张图生成失败，前端回退成原生占位图。
  这个开关是给"后台整片扫描"设计的，不适合"点开就看"的即时取帧。
- 每格抽完会顺带量一次亮度（16×16 灰度、9 格并发，开销可忽略），只有单格偏暗才补取那一格
- **每格一个 ffmpeg 进程**（各一次 seek、各一帧），最多 9 个并发；单格 60s 硬超时后强杀，
  免得坏文件把请求挂住。每格独立 ⇒ 某格取不到帧只丢那一格（合成时用前一格顶上，位置不会错位），整张不会失败。
  （试过"一个进程 9 个输入"复用一次设备初始化：4K 116min 1.75s vs 1.40s、慢 4K 3.78s vs 3.10s，
  反而更慢；而且只要有一格 seek 超范围，多输入滤镜图会**卡死**——单输入进程只会立即报错退出。）

### 3.2 搜索增强

- 搜索结果里多一行 **「文件夹」**（原生搜索把它硬编码排除了，见第四节）
- 文件夹卡片 **1:1 复刻原生卡片**：`.card.overflowPortraitCard.card-withuserdata` + 缩略图
  （取文件夹内第一个有图的子项）+ 名称（可点击）+ 完整路径；**无悬浮按钮、无悬浮放大**
- 点击 → 原生文件夹视图：优先调用 **`Emby.Page.getRouteUrl(item)`**（客户端自己的路由解析，
  自动适配 modern/legacy 显示模式，文件夹得到 `#/list?parentId=<id>&serverId=<sid>`），手写路由链仅兜底
- **模糊匹配**：文件夹名支持编辑距离/子序列（`网凰`→`示例`、`武则天1996…`→`武则天1995…`）
- **单字符可搜**（中文单字如「杂」）
- **结果缓存**（内存 + sessionStorage，TTL 5 分钟）：从详情页返回搜索页时**零请求、立即渲染**
  （实测与原生分区同为 190ms 出现）
- **翻页**：每页 24 条，标题右侧与原生分区共用同一套翻页器

### 3.3 搜索结果分页（服务端分页 + 纵向网格）

**原生是什么样**：搜索页只有一个请求 ——
`/Items?...&searchTerm=…&limit=800&includeItemTypes=Movie&…`，回来之后按类型分组，
每组一次性 `buildCards` 成一条横向滚动条。所谓"前进后退按钮"只是横向滚动箭头，**没有分页**。
本机 14.5 万条目下搜 `20`：**首屏 920 张卡片**（800 视频/照片 + 100 头部视频 + 1 相册 + 20 文件夹），
主线程几秒钟都在建卡片 —— 桌面端卡顿，移动端直接卡死。

**为什么不能直接用原生的 `/Items?searchTerm=` 做分页**（这一版最关键的发现）：

服务端只要带了 `searchTerm` 就走**搜索提供者**（`SearchManager` → `IInternalSearchProvider`）：
`ItemsController` 把 `limit * 3` 当成提供者的上限，拿回的 id 集合再交给数据库查询。
于是 **`TotalRecordCount` 被 `limit*3` 卡死**，还随 `limit` 变 —— 实测（term=20）：

| 请求 | 返回的 TotalRecordCount |
|---|---|
| `includeItemTypes=Photo&limit=1` | **3** |
| `includeItemTypes=Photo&limit=800` | **2400**（= limit×3，真实命中远不止） |

也就是说：想要准确总数就得请求大 limit，而大 limit 会把几百条一次性甩回来 ——
"小页 + 准确总数 + 深翻页"在原生接口上做不到。

**做法**：搜索页只查总数、不渲染卡片；详情直接复用**原生列表页**。

1. （已删除，2026-09-15）早期做法是从 React 容器的 fiber（`__reactContainer$…`）翻出 app 的 QueryClient、
   订阅 queryCache，把原生搜索分区裁到 2 条写回。原生请求被彻底短路之后原生分区永远是空的，这段
   fiber 遍历 + 写 React Query 缓存的代码既没用又脆，已整段删掉；
2. 搜索页只渲染**四个方格**：视频 / 图片 / 相册 / 文件夹，每格是各自的**真实总数**
   （`/FeatureEnhance/Search/Counts` 一次请求返回四组计数）；搜索页**一张卡片都不建**；
3. 点方格 → 把搜索上下文（term + match + 类型）写进 **URL**，然后进**原生列表页**：
   `#/list?type=Video,Movie,Episode,Series&feTerm=…&feMatch=…&serverId=…`
   （早期写 sessionStorage，但那个有 30 分钟 TTL、换标签页就丢，丢了之后列表页会静默变成“整个媒体库”）；
   （`#/list?type=X` 这个用法是从 `list.chunk.js` 里挖出来的：列表页把路由参数 `type`
   直接当 `IncludeItemTypes` 用；**必须带 serverId**，否则页面不会发请求、一张卡片都没有）；
5. 列表页的数据由插件**桥接**：包一层 `ApiClient.getItems`（列表页正是调它，见 `list.chunk.js` 第 199 行
   `o[l](o.getCurrentUserId(), N(e, {...}))`）——
   列表页要第几页，就先用插件接口取那一页的 id 与**真实总数**，再把 id 交回原生接口取完整 DTO
   （卡片/排序/筛选器全用原生的），最后按 id 顺序还原；
6. 原生筛选（已看/未看/收藏/继续观看）也传进插件接口（`filters` 参数，走 UserData 子查询），
   所以"筛选后总数"同样是精确的。

**匹配口径**：搜索页按**名称**匹配（`CleanName`/`Name` LIKE），只有这个类型一条名称都匹配不上时，
才退回插件特有的**路径匹配**（输入文件夹名 → 找到里面的视频）。两种口径都用 COUNT 精确统计，
`match=name|path|auto` 由服务端回传、客户端沿用，保证"总数"和"翻页内容"永远同一口径。

实测（搜 `2026`，本机 14.5 万条目）：

| | 原生 | 现在 |
|---|---|---|
| 搜索页代价 | **920 张卡片**一次性建完 | **0 张卡片**，只查总数（视频 11549 / 图片 2121 / 相册 0 / 文件夹 1） |
| 横向滚动条 | 每个分区一条，能一直滑 | 没有（搜索页只有四个方格） |
| 详情页 | 没有分页，也不可点 | 原生列表页，原生分页/排序/筛选 |
| 详情页总数 | `#/list?type=Video` 只能报 300（limit×3） | **11549**，翻到 101-200 也正常 |

几个必须踩到的坑：

- **包装 `fetch`/`XHR` 必须赶在 app bundle 之前**：`defer` 注入的脚本排在 app bundle 之后，而 jellyfin-apiclient
  启动时就把 `window.fetch` 的引用抓走了 —— 晚注入的包装不生效。所以拦截代码放在 `<head>` 里同步执行
  （见 `ClientInjectionStartupFilter.Bootstrap`），不是放在三个 defer 脚本里；
- **同一条路由只换搜索词时不要重建整块 UI**，否则会重复请求十几个类型；
- **结果容器只能有一个**：模块级引用 + 进场先清残留。踩过 —— `sync()` 建了一个容器却没存引用，
  `build()` 又建了一个，页面上出现两个 `#fe-search-results`（一个是空壳）；
- **包 `ApiClient.getItems` 比 hook `fetch` 靠谱**：前者是原生列表页真正调用的入口，
  而且能拿到结构化参数（`StartIndex`/`Limit`/`SortBy`/`Filters`/`IncludeItemTypes`）；
  内层再调原生接口时要 `delete opts.SearchTerm` 并带上 `Ids`，否则会递归回自己；
- `#/list?type=X` **必须带 `serverId`**，否则列表页不发请求（页面上只有排序/筛选按钮、0 张卡片）；
- 桥接只在"列表页的 `type` 和当前搜索上下文一致"时生效，否则从搜索结果点进某个文件夹
  （`#/list?parentId=…`）会被误接管；
- **原生结果区要"同步"藏掉**：一开始是等四个方格渲染完再 `display:none`，结果每次搜索都会先闪一下原生列表
  （用户看得见缩略图）。现在改成在 `hashchange` 里同步给 `body` 加 `fe-searching` 类，用 CSS
  （`body.fe-searching .searchResults{display:none}`）隐藏 —— CSS 在元素出现的同一帧就生效；
  另外 MutationObserver 的回调里也会**立刻补一次这个类**（微任务，早于下一次绘制），
  覆盖"app 内部用 pushState 导航、根本没触发 hashchange"的情况；
- **不做任何"放出原生结果"的兜底**（自动的不要，手动的也不要）：自动的会"闪一下原生列表再跳回方格"，
  手动的等于把一个已经被短路的页面交回原生 —— 两边都是空白。现在接口取不到数就把四个格子画成 **0**，
  加一句提示 + 「重试」，页面永远留在插件这一侧；
- **文件夹的条数不能走 `kind=folder` 的完整查询**：它名称匹配不到时会跑模糊匹配（扫两万行），
  慢起来几秒，正好会踩到上面那个兜底。现在 `/Search/Counts` 支持 `kind=folder`（只做一次 GROUP BY，实测 30~250ms）；
- **原生搜索"不是隐藏而是不发"**：一开始只是把原生结果区 `display:none`，但请求照打（搜索页一次 6 个：
  `/Artists`/`/Persons`/`/Studios` + 两条 `/Items?limit=100` + 一条 `/Items?limit=800`），
  800 条照传、900 张卡片照建，纯浪费。现在插件在 **app bundle 之前**往 `<head>` 注入一小段引导脚本
  （`ClientInjectionStartupFilter.Bootstrap`），把这些请求在本地直接回一个空的 200：
  实测每次搜索 **6 → 0 个请求、0 字节**。
  两个必须知道的点：① 这个脚本必须同步跑在 head 里 —— jellyfin-apiclient 在模块初始化时就抓走了
  `window.fetch`，晚注入的包装完全不生效（踩过一轮）；② 搜索页实际走的是 **axios 的 XHR**，不是 fetch，
  所以 `XMLHttpRequest.prototype.open/send` 也要一起包（伪造 `readyState/status/responseText/response`
  + `getAllResponseHeaders()`，再补发 readystatechange/load/loadend）。
  这个短路是**永久**的：没有任何自动或手动的放行路径（`window.__feNativeSearch` 只在调试时可从控制台改，
  插件自己永不修改，也没人读它来决定行为）。代价：万一注入脚本本身没加载成功，搜索页会是空白，强刷即可恢复；
- **模糊匹配要有"确定性的结果集"才能分页**：现在每个分组按 **名称 → 路径 → 模糊** 逐级下降，
  模糊用的是 `PathMatcher.ScoreFuzzy`（滑窗编辑距离，阈值 44 分）。两个关键设计：
  ① **不许用子序列**：实测 `bulll` 会把 `user_威猛山人_MS4wLjABAAAA…` 这种随机长串也算命中（字符顺序恰好凑齐），
  结果全是垃圾；改成"名称里任意一段与查询词编辑距离够近"就干净了（`bulll` → "Bull Video …"、`网凰` → 网黄 系）；
  ② **排序必须确定**（分数降序 + 同名按名字），这样计数和每一页拿到的是同一个集合 ——
  老版本"模糊补充只在第一页追加、又不计入 Total"就是计数/列表对不上的根源；
  扫描量用 SQL 粗筛（名称里至少含查询词的一个字符）+ `MaxFuzzyCandidates`（6000 行）卡住，实测单次 0.25s 左右；
- **"方格上的数字"和"列表里的条数"必须同口径**（用户实测：搜 `bull` 方格写 1、点进去 52 条）。原因是老代码里两处不一致：
  ① 文件夹的"模糊补充"（编辑距离/子序列）只在 `startIndex==0` 时才跑，**既不计入 Total 也不能翻页**，
  `Total=1` 却返回 52 条；② 列表口径写死 `nameMatch`，而计数口径是 `auto`（名称没有结果会退到路径匹配）。
  现在：`/Search*` 增加 `fuzzy` 参数（默认 0 = 关闭模糊补充），folder 与 item 走同一套 `match` 口径，
  并且列表页桥接会把计数接口回传的 `match` 原样带过去 —— 校验过 9 组（term × 4 个格子）数字全部相等。
  模糊匹配本身仍然保留在 `PathSearchProvider`（原生搜索框）里；
- **`PhotoAlbum` 之类的类型本身 `IsFolder=1`**：早期在 `kind=item` 上加了个 `!IsFolder`，
  结果"相册"格子永远是 0。现在只按类型过滤，不再叠加 IsFolder 判断；
- **原生筛选/排序要真透传**：列表页把 `SortBy`/`Is4K`/`HasSubtitles`/`Tags`… 放在 `getItems` 的 options 上，
  桥接原样传给插件接口，服务端再用 Jellyfin 自己的 `TranslateQuery`/`ApplyOrder` 翻译 —— 手写 SQL 覆盖不了
  4K/HD/字幕/预告/标签这些维度（早期只透传 4 个用户数据筛选，界面选中了结果却不变）；
- **「文件夹」格子要能播**：原生播放/随机/入队同样走 `getItems`，拿回文件夹就什么都放不了。做法是在 document
  捕获阶段（早于列表页绑在按钮上的处理器）打一个"播放意图"标记，桥接看到标记就改问
  `/FeatureEnhance/Search/Playable`（同一套 match 口径 + 权限 + 原生筛选，取文件夹**内部**的可播放条目），
  再按 id 回原生接口取 DTO 交给原生播放器；
- **一次搜索只花一个请求**：计数结果按搜索词缓存 5 分钟（内存 + sessionStorage），从列表页/详情页返回时零请求；
- **原生加载指示要一起压掉**：原生搜索页的 `Loading.show()` 层由 React Query 的 `isPending` 驱动，
  而 `isPending` 在查询被**禁用**时同样是 true —— 它可以永远转圈。`body.fe-searching` 下统一
  `display:none` 掉 `.docspinner`/`.mdl-spinner`/`CircularProgress`。

### 3.4 页面切换冻结帧（返回主页/上一级不闪）

**原生是什么样**（1280×720、20fps 录屏逐帧统计）：

| 阶段 | 帧 | 画面 |
|---|---|---|
| 点开条目 | 147~149 | **整页内容区全空**（只剩顶栏），3 帧 ≈ 150ms |
| 返回上一级 | 248~252 | 同样全空 2~3 帧，之后卡片逐张把图片画出来（又 3~4 帧） |

也就是"页面会闪烁一下，好像是重新渲染了元素"——其实是 React 把旧页面拆掉、
新页面 buildCards + 图片解码之间的空窗期。

**做法**：路由一变就先给**当前这一页**拍一张"冻结帧"，挂到 fixed 覆盖层上，
让新页面在覆盖层后面慢慢渲染，等新页面真的画出内容了再 200ms 淡出。
用户看到的是"旧页面 → 新页面"的交叉过渡，而不是"旧页面 → 黑屏 → 新页面"。

- `cloneNode(true)` 很便宜：首页 **2211 个节点 / 76 张卡片只要 2.8ms**；
- **必须预拍**：点浏览器返回时 `popstate` 里 React 的监听器（注册得更早）往往已经把 DOM 换掉了，
  这时候现场克隆只能克隆到新页面的空壳（踩过：覆盖层里就是那片空白）。
  现在页面每次安定下来（路由切换完成 / 滚动停住）都会预拍一张，导航时直接用；
- 克隆的 iframe（首页大图区）会**重新发起加载**，所以换成同尺寸底色块；
- `cloneNode` **不复制 scrollTop/scrollLeft**，要按节点下标一一还原（否则横向滚动条会跳回最左）；
- 覆盖层 `z-index:1000` 低于顶栏（MUI AppBar 1100）：顶栏保持可交互，也不会遮住大图区上的悬浮顶栏；
- 用户一旦滚轮/触摸/按键，立刻撤掉覆盖层，绝不抢操作；
- 淡出前还有 190ms"静置"：新页面常常是"卡片先出来、图片后画"，不静置就会在淡出中途露出灰块。

另外把两处**独立会闪的东西**关掉了：`.lazy-image-fadein*`（每张卡片返回时淡入一次）和
`.blurhash-canvas`（先画一块模糊色再被真图盖住）。

> 试过 View Transitions API（`document.startViewTransition`）：Chrome 认这个 API，
> 但在无头录制里整个页面会长时间停在空白（快照层没被画出来），风险太大，弃用。

### 3.5 Spotlight（首页大图区）：已移除

这个功能前后改了三版（iframe 内接管手势 → 父页面转发滚动 → 父页面透明触控层 + WAAPI 渐变），
每一版都在跟"iframe 根本不像页面的一部分"这件事较劲：纵向手势被 iframe 吃掉、横向手势又和首页 tab 的
滑动切换打架，补一处漏一处。2026-09-15 用户要求直接移除，于是：

- 容器环境里设 `ABYSS_SPOTLIGHT=0`（`docker-compose.yaml`）—— 主题自己的钩子 `10-abyss.sh`
  读到 0 会主动清理：删掉 `/usr/share/jellyfin/web/index.html` 里的 loader 标签 +
  `web/ui/` 下的 spotlight.html / spotlight.css / spotlight-loader.js；
- 插件里的 `spotlight-bridge.js` / `spotlight-touch.js` 已删除（csproj、控制器、注入列表一并去掉）；
- 资源缓存在 `config/abyss/spotlight/` 没删，想再打开把 `ABYSS_SPOTLIGHT` 改回 1 重启即可。

移除后实测：首页 spotlight iframe **0 个**、`abyss-spotlight-visible` 类不再出现、
`#homeTab` 里 13 个正常分区、页面滚动正常（0 → 500）。

### 3.6 原生搜索的路径匹配

`PathSearchProvider`（`IInternalSearchProvider`）把 **Path** 纳入匹配 → **输入文件夹名就能搜到里面的视频**；
命中较少时自动补充模糊结果。原生搜索框直接生效，不需要前端改动。

耗时（本机 14.5 万条目）：`/Search/Hints` **0.13~0.33s**、`/FeatureEnhance/Search`（文件夹分区）**0.28~0.39s**，
首次调用 1.6s 是 .NET JIT 冷启动。每次搜索会做一次全表 LIKE 扫描 + 最多 2 万行候选打分，
嫌慢就把 `PathSearchProvider.MaxCandidates` / `MaxFoldersScanned`（都是 20000）调小。

---

## 四、关键实现 / 踩过的坑

1. **服务端硬编码排除文件夹**：`SearchManager.BuildExcludeItemTypes()` 写死
   `excludeItemTypes.Add(BaseItemKind.Folder)` / `CollectionFolder`；插件用 `ISearchProvider`
   返回文件夹也会在第二遍查询被过滤 —— 所以「文件夹」分区只能在客户端补一行。
2. **注入方式**：第三方注入插件（JavaScript Injector）在 12 上不可用（其加载器调用 `ApiClient.fetch()`，
   12 没有这个方法）。改为插件自己的 `IStartupFilter` 中间件：拦 `/web/index.html`、去掉
   `Accept-Encoding` 取未压缩 HTML、在 `</body>` 前插 `<style>`+`<script defer>`。

   ⚠️ **2026-09-15 修正**：原来这里写着"清掉 ETag/Last-Modified/Accept-Ranges"——**清不掉**。
   静态文件中间件是在**响应开始那一刻**才写这几个头的，晚于中间件里的 `Headers.Remove`（实测
   响应里照旧有 `ETag`/`Last-Modified`/`Accept-Ranges`，连 `Content-Type` 都被它覆盖回 `text/html`）。
   于是浏览器带 `If-None-Match` 过来时服务端会回 **304 + 空 body**：我们拿不到 HTML、注入被整个跳过，
   浏览器继续用自己缓存里那份 index.html —— 装插件之前访问过 Web UI 的浏览器缓存里没有注入，
   表现就是"插件时灵时不灵、强刷才好"。更糟的是往 304 响应里写字节会被 Kestrel 直接抛
   `InvalidOperationException`，每个带缓存的浏览器每次加载都在日志里刷一条 ERR。
   真正的修法：把 `If-None-Match`/`If-Modified-Since` **请求头**一起去掉（静态中间件只能回 200 全量），
   并且非 200 响应原样透传、不写 body。
3. **卡片属性是 `data-id` + `data-type`**（不是 `data-itemid`）。
4. **查询参数鉴权区分大小写**：必须 `?ApiKey=`；`api_key=` 会 401。
5. **`ApiClient.getUrl()` 不带 token** → 脚本里统一补 `ApiKey`（图片接口本身不需要 token）。
6. **卡片内还有标题链接**（`<a data-id data-type>`）：按 `[data-id][data-type]` 遍历会给一张卡加两个按钮，
   必须只认 `.card`。
7. **`.cardText` 必须在 `.cardBox` 里**（`.cardScalable` 之外），否则被图片盖住 → "看不到名称和路径"。
8. **移动端 `touchstart` 不能 `preventDefault()`**（会连带吃掉 click → 按钮点不动）。
9. **注入的 `<style>` 会被 web 客户端清掉** → 按钮/弹层一律用内联样式。
10. **返回卡顿与脚本无关**（A/B：有脚本 5 次长任务/588ms，无脚本 7 次/722ms）。真因是卡片 `width`
    与 `scrollbar-color` 过渡（返回瞬间 722 个过渡同时跑）；用注入样式把过渡属性收窄到
    `transform/opacity/box-shadow` → **722 → 4**。
11. **返回时恢复滚动**：记录每个路由的纵向滚动与各横向列表 `scrollLeft`；现在是 `popstate` 后
    **连续 12 帧、最多 900ms** 每帧校正一次（原生会在页面显示之后自己再复位一次），
    用户一开始滚就不再抢（旧版是 200/600/1200/2000ms 四次，最后一次会"跳"一下）。
    ⚠️ **2026-09-15 修正**：当时只改了 `navigation-smooth.js`，`grid-hover-preview.js` 里那份旧实现
    （200/600/1200/2000ms、期间还会屏蔽记录、且不会因用户滚动而停手）一直留着，两套同时在
    `popstate` 上抢 `window.scrollTo` → "返回后 2 秒内自己滚一下会被拽回去"。旧的那份已删除，
    并在 `boot()` 里设了 `history.scrollRestoration = 'manual'`，免得再和浏览器自己的恢复打架。
12. **格子尺寸必须是偶数**：奇数宽（例如 244×432 竖屏素材算出 361）会让 `pad` 在 yuv420p 下非法 →
    ffmpeg 直接失败、接口 500。现在统一 `round(...,2)*2` 取偶。
13. **别照抄 trickplay 的整文件扫描**：`ExtractVideoImagesOnIntervalAccelerated` 没有 seek 参数，
    会线性读完整个文件；`-skip_frame nokey` 只是省解码，磁盘 I/O 照旧（20GB remux ≈ 分钟级）。
    要"点了就有"，就必须自己给每个采样点 `-ss`，硬件参数仍然从 `EncodingHelper` 取。
14. **硬件参数只能整段用**：`GetInputArgument()` 返回的 `-init_hw_device`/`-filter_hw_device` 是**全局**选项、
    硬解器那半截是输入选项；同一进程里重复整段会直接报 `named device already exists`。
    所以改成"一格一个进程"，每个进程原样用原生参数，不手工拆分/拼装。
15. **很多素材在库里还没有媒体流信息**：本机 一条约 8 万条目的库里 **36%** 的视频 没有 `MediaStreamInfos`，
    `video.GetMediaSources()` 拿不到 `VideoStream` → 宫格必然生成失败；**播放一次**之后 Jellyfin 补齐了信息就正常了
    （用户实测："播放后再预览就成功了"就是这个问题）。
    现在缺信息时用 `IMediaEncoder.GetMediaInfo(new MediaInfoRequest { MediaSource = ..., MediaType = DlnaProfileType.Video })`
    现探一次（和播放时同一个 ffprobe，**不写库**，只用于本次生成），结果按 item 缓存 30 分钟；
    探测失败（缺 moov / 被截断的坏文件）也记 10 分钟，避免每次悬停都重探。
    实测随机 12 个"未分析"素材：**12/12 成功**，平均 0.55s。
16. **多输入滤镜图怕 seek 超范围**：某一路 `-ss` 超过文件末尾时，`hstack`/`vstack` 收不到帧，
    ffmpeg 既不报错也不退出（实测一路挂到超时被杀）。单输入进程则会立刻非 0 退出 —— 这也是"一格一进程"的原因。
17. **（已删除）React Query 的实例只能从 fiber 上找**：曾经的兜底是从 `#reactRoot` 的 `__reactContainer$…`
    往下走几步拿到 `QueryClientProvider` 的 `memoizedProps.client`，订阅 queryCache 再 `setQueryData` 写回。
    原生搜索被彻底短路之后它没有任何用处，已整段删除；**别再往回加** —— 遍历私有 fiber 结构 + 写别人的
    缓存，脆而且和"不依赖原生渲染"的方向相反。
18. **（同 17，已删除）`setQueryData` 会同步触发自己的订阅**：当时必须用标志位挡住递归、还得用状态指纹
    而不是对象引用比较（`replaceEqualDeep` 会重建数组），否则就是死循环 —— 又一个"这条路不该走"的证据。
19. **`var` 闭包**：在循环里给元素挂监听器时，闭包引用循环变量 `var` 会全部指向最后一个元素
    （表现："点视频的下一页，翻的是照片"）。要么用参数固化，要么用 IIFE。
20. **点返回（popstate）时 DOM 可能已经被换掉了**：React 的 history 监听器注册得比注入脚本早，
    它可能先重渲染。冻结帧必须**提前预拍**，不能等到 popstate 再克隆。
21. **View Transitions API 不可用**：Chrome 认 `document.startViewTransition`，但在无头录制里
    整个页面会长时间停在空白（快照层没画出来），无法验证也无法保证 —— 用自建覆盖层代替。
22. **iframe 会吃掉纵向手势**（历史记录，对应已移除的 spotlight）：`touch-action:pan-y` 让浏览器拿走手势、
    而 iframe 内部又没东西可滚 → 表现为"滑不动 / 和下滑冲突"。当时的结论是"在 iframe 内部用 capture +
    `stopPropagation` 全覆盖、再把纵向位移转发给父页面"，或者干脆用父页面透明触控层接管 ——
    但两者的横滑都会和首页 tab 的滑动切换打架，最后这个功能被整体移除了（见 3.5）。
23. **截图/录屏验证**：Playwright 录 webm → `ffmpeg -vf fps=20` 抽帧 → numpy 算每帧内容区的
    均值/标准差，"整页空白帧"就是标准差 <12 的那些帧。返回闪烁的 A/B 就是这么量出来的
    （优化前 2~3 帧空白，优化后 0 帧）。
24. **插件图标怎么才会显示**（踩了一轮才搞清楚）：
    - 仪表盘的图标来自 `GET /Plugins/{id}/{version}/Image`，它**先看 `Manifest.ImagePath`，再看 `ImageResourceName`**；
      `HasImage` 就是这两个之一非空（`LocalPlugin.cs`）。
    - `PluginManifest.ImageResourceName` 带 `[JsonIgnore]`，**写进 meta.json 会被丢掉**；
      服务器只在"插件没有目录记录"（镜像内置插件）那条分支里才会填它。
      所以我们虽然实现了 `IHasEmbeddedImage` 并把 `docs/icon.png` 内嵌进 DLL，**目录安装并不走这条路**。
    - 目录安装（我们把 DLL 丢进 `plugins/`）要在 `meta.json` 里写 `imagePath`，而且**写相对路径**
      （`"icon.png"`）最好：控制器用 `Path.GetFullPath(imagePath, pluginPath)` 解析，
      换版本目录、改插件名都不会失效；写容器绝对路径在换目录后就会 404。
    - 图标资产要**满幅方形**：仪表盘自己套圆角，资源里画圆角会变成"圆角套圆角"，四角透明还会透出底色。
      512×512、不透明、无圆角 —— 和其他插件的观感一致。
    - 改完 meta.json 要重启 Jellyfin（扫描阶段读一次）。

---

## 五、升级影响

- 插件与全部前端脚本都在 `config/data/plugins/`（挂载卷）→ **镜像更新、容器重建都不受影响**
- 容器内 `index.html` 与 web 目录**保持原版**（`PathSearch`/`FeatureEnhance` 都出现 0 次），
  升级不需要"重新打补丁"（主题那套 Spotlight 已随 `ABYSS_SPOTLIGHT=0` 移除，与本插件无关）
- 2026-09-14 改名：插件名 `Path Search` → `Feature Enhance`，命名空间
  `Jellyfin.Plugin.PathSearch` → `Jellyfin.Plugin.FeatureEnhance`，路由 `/PathSearch/*` → `/FeatureEnhance/*`，
  部署目录 `PathSearch_1.0.0.0/` → `FeatureEnhance_2.0.0.0/`。**GUID 保持不变**
  （`5b3f0c47-…`），所以仪表盘里是同"一个插件"在升级，不会出现两个残留项
- 唯一注意：Jellyfin **大版本**升级（12 → 13）时插件需按新 ABI 重新编译（所有插件的通例）
- 宫格缓存在 `config/data/plugins/Jellyfin.Plugin.FeatureEnhance/grid/`（每个视频一张，约 100~400KB，不自动淘汰；
  删掉目录内容即全部重来，下次悬停/点击时按需重新生成）
- 接口缓存：`private, max-age=300` + `ETag`/`Last-Modified`，控制器自己处理 `If-None-Match` 回 **304**
  （以前是 `public, max-age=86400` 且 URL 不带版本戳 —— 旧图/失败回退图会在浏览器里粘一整天）
- 生成失败返回 500 → 前端重试 2 次后回退成原生缩略图并**明确标注**（`Items/{id}/Images/Primary`），不会出现空白弹层

---

## 六、怎么验证（可选）

**接口层**（最快）：登录拿 token，直接打网格接口看尺寸/耗时，缓存落在
`config/data/plugins/Jellyfin.Plugin.FeatureEnhance/grid/<itemId>.jpg`：

```bash
TOK=$(curl -s -X POST http://127.0.0.1:8096/Users/AuthenticateByName \
  -H 'Content-Type: application/json' \
  -H 'Authorization: MediaBrowser Client="x", Device="x", DeviceId="x", Version="1"' \
  -d '{"Username":"admin","Pw":"<密码>"}' | python3 -c 'import json,sys;print(json.load(sys.stdin)["AccessToken"])')
curl -s -o /tmp/g.jpg -w '%{http_code} %{time_total}\n' \
  "http://127.0.0.1:8096/FeatureEnhance/Grid/<itemId>?refresh=true" \
  -H "Authorization: MediaBrowser Token=\"$TOK\""
```

**浏览器层**：本机 `~/.cache/ms-playwright` 已有 Chromium，`playwright-core` 可以直接借
DSH Playwright MCP 自带的那份（`node_modules/playwright-core` 软链即可，不用装）：
`~/.nvm/versions/node/v22.23.2/lib/node_modules/@playwright/mcp/node_modules/playwright-core`，
浏览器要显式给 `executablePath: ~/.cache/ms-playwright/chromium-1234/chrome-linux64/chrome`
（默认的 1237 没装）。

**登录**：不要去改管理员密码，也不要新建用户。直接从 `config/data/data/jellyfin.db`
（**只读**）里取一条现成的 `Devices.AccessToken`，用 `addInitScript` 写进
`localStorage.jellyfin_credentials` 即可。两个必须注意的点：

- `LastConnectionMode` 的枚举是 **Local=0, Remote=1, Manual=2** —— 写成 1 会去解析空的
  `RemoteAddress`，客户端直接 `Must supply a serverAddress` 挂掉；
- 直接给 `ManualAddress` + `LastConnectionMode: 2` 才起得来。

验证点（都在 `/tmp/mine/test-all2.mjs` 里跑过）：

| 项 | 怎么量 | 期望 |
|---|---|---|
| 搜索分页 | `document.querySelectorAll('.searchResults .card').length` | 搜 `20` 时 **85** 张（原生 900+） |
| 翻页归属 | 点某个分区的 `[data-fe="next"]`，比较各分区首张卡片的 `data-id` | 只有被点的分区变 |
| 冻结帧 | 每帧记录 `#feature-enhance-freeze` 是否在 DOM | 导航后 ~30ms 出现、新页面就绪后消失 |
| 空白帧 | 录屏抽帧算内容区 std | **0 帧** std<12 |
| 搜索页不闪 | 进搜索后每 30ms 采样 `.searchResults` 的 `offsetParent`/高度 | 采样期间**原生结果区可见次数 = 0**，四个方格 ~400ms 出现 |
| 数量一致性 | 方格上的数字 vs 列表页分页器的总数 | 9 组（term × 4 格）全部相等 |
| 主题入场动画 | 切页后采样 `getAnimations()` 与分区 `opacity` | 原生：10 个 `abyss-section-fade-up`、45 万个采样里 40/60 分区透明；插件：0 个、0/60 |

> **量 DOM 的时候只看"可见的那一页"**：jellyfin-web 会把旧页面留在 DOM 里（只是 `display:none`/移出视图），
> `document.querySelectorAll('.card')` 会把旧页面的卡片一起数进来（踩过：以为列表累加了 200 张，
> 实际可见页只有 100 张）。用 `[data-role="page"]` + `getBoundingClientRect().height > 20` 过滤后再读。

**排错**：日志里搜 `Feature Enhance`，第一次生成会打印实际使用的原生 ffmpeg 参数（含设备/硬解器）；
命令行跑失败时用 `LogDebug` 里的完整命令 + stderr 复现（日志级别调到 Debug）。

**收尾**：如果为了验证临时改过 管理员密码哈希（PBKDF2-SHA512 / iterations=210000，格式
`$PBKDF2-SHA512$iterations=210000$<salt hex>$<hash hex>`）或新建过会话，**验证完必须还原**，
并删掉 `Devices` 表里新增的测试行（本机原有会话是 Id 1~5）。
