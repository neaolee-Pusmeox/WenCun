# 文存 WenCun

当前预发布版本：`0.1.0`。

文存是 Windows 上的微信公众号文章归档工具：从已登录的微信捕获阅读会话，嗅探公众号历史目录，勾选文章后保存到本地文库，并导出 HTML、Markdown、TXT、Word 或 PDF。

目录可见范围、正文和媒体是否能取得，取决于微信实际返回的内容。“目录已完整”不代表所有正文或资源都已下载。

## 开始使用

1. 运行本地构建的 `WenCun.exe`，或在开发环境双击 `run.bat`。
2. 打开“采集”页，阅读并勾选证书和代理说明，点击“开始嗅探”。首次使用需要信任本机生成的证书。
3. 在已登录的微信客户端中打开目标公众号的一篇尚未缓存的文章。工具最多等待三分钟；旧页面可能命中微信缓存，需要换一篇文章。
4. 取得会话后，文存恢复代理并读取历史目录。待采集列表逐页更新；名称缺失时从示例文章或目录首篇文章读取，不在这个阶段保存正文。
5. 嗅探结束后勾选文章，点击“保存选中”。正文入库后从待采集清单移除；导出失败单独记录，可以重新导出。
6. 在“资料库”阅读和搜索文章，在“公众号”查看待采集、已保存和导出状态。

名称探测失败时暂时显示公众号标识，重新嗅探或成功保存文章后可补全。

## 三个页面

| 页面 | 用途 |
| --- | --- |
| 资料库 | 按公众号浏览，搜索标题/公众号/作者，阅读、重试、重新导出；“待采集”筛选读取 JSON 清单 |
| 采集 | 嗅探目录、选择待采集文章、选择文库与输出格式、设置图片保存方式 |
| 公众号 | 汇总已保存文章及只嗅探尚未保存的公众号，查看目录进度，进入对应列表 |

任务期间不能切换文库、删除任务数据或启动另一个写入任务。关闭窗口会先停止嗅探并等待代理清理；保存或导出期间需等待任务结束。

## 保存与导出

- 默认生成 HTML 并下载图片。可选择多个格式；不选格式时只入库。
- 图片可下载、保留远程链接或不保存。下载失败不丢弃正文，但导出文件可能仍依赖网络，“已保存”不等于资源全部离线可用。
- 支持部分微信语音资源。视频、评论、评论回复、阅读量和点赞数目前不采集。
- HTML 保留净化后的正文结构。Markdown 表格使用 HTML。Word 支持基础段落、列表、表格及 PNG/JPEG/GIF；复杂合并单元格和其他图片格式不保证还原。
- PDF 和内置阅读器依赖 Microsoft Edge WebView2 Runtime；自包含发布包含 .NET 运行时，不包含 WebView2 Runtime。
- 付费或受限文章只保存实际返回的可见内容，并标记“部分正文”。

导出目录使用文章标识摘要，避免同标题、同日期文章覆盖：

```text
文库/
├─ library.db
├─ assets/文章标识/资源文件
└─ articles/公众号/日期 标题 [标识摘要]/
   ├─ article.html
   ├─ article.md
   ├─ article.txt
   ├─ article.docx
   ├─ article.pdf
   └─ assets/
```

只有选择的格式才生成。文章改名后重新导出可能产生新目录，旧目录不会自动清理。

## 数据与恢复

| 位置 | 内容 |
| --- | --- |
| `%LOCALAPPDATA%\WenCun\config.json` | 文库路径、主题、格式和图片选项 |
| `%LOCALAPPDATA%\WenCun\library` | 默认文库，可在采集页更换 |
| `%LOCALAPPDATA%\WenCun\pending\*.json` | 公众号待采集清单、名称和目录游标；不随文库移动 |
| `%LOCALAPPDATA%\WenCun\capture-journal.json` | 采集期间的代理恢复记录；正常恢复后删除 |
| `%LOCALAPPDATA%\WenCun\webview` | 阅读器及 PDF 的 WebView2 数据 |

选择空文件夹会复制文库，选择已有文库会打开它。旧文库保留；新位置不能在当前文库内部，也不能通过目录联接或符号链接复制。

停止嗅探保留已完成页面，下次从未完成游标继续；完整目录从第一页检查新文章。当前文库已有正文的文章不会再次加入待采集。

损坏的待采集文件会保留：列表跳过该文件，继续写入对应公众号时会报错，避免覆盖旧清单。处理前先备份原文件。

删除文章只删除数据库记录和正文，导出文件保留。删除公众号会尝试清理可确定属于它的资源和导出目录；共享旧目录、锁定文件或无法安全处理的路径可能保留。

## 开发与构建

需要 Windows 和 `global.json` 指定的 .NET SDK（当前为 `10.0.401`，允许同功能带补丁升级）。脚本优先使用项目内 `.tools\dotnet\dotnet.exe`，否则从 PATH 查找 `dotnet`。

```powershell
dotnet build WenCun.slnx
dotnet test tests\WenCun.Tests\WenCun.Tests.csproj --no-build
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\run.ps1
```

使用项目内 SDK 时，把 `dotnet` 替换成 `.\.tools\dotnet\dotnet.exe`。生成 Windows x64 自包含发布文件：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\publish.ps1
# 独立输出目录可保留之前的包
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\publish.ps1 -Output publish\review
```

`publish` 是本地构建产物，修改源码不会自动更新其中的程序。

## 连接边界

嗅探临时修改当前用户的 WinINET HTTPS 代理，仅解密 `mp.weixin.qq.com`。其他 HTTPS 请求可能通过本地代理转发；当前实现不是按域名配置的 PAC，依赖企业代理的网络需要单独验证。

会话 Cookie、签名参数只在内存中使用，不写入待采集清单。证书保存在 Windows 当前用户证书存储，私钥由 Windows CNG 管理；证书过期或被移除后需重新信任。

异常退出后，下次启动会尝试恢复本工具留下的代理设置。恢复失败时保留记录并显示原因。微信接口变化、会话失效或频控可能中断采集；重新捕获会话后继续，不保证全历史始终可访问。

## 开发文档

- [开发指南](docs/DEVELOPMENT.md)：架构、数据契约、调试、验证与修改入口。
- [技术与功能对比](docs/PRODUCT-COMPARISON.md)：文存、WxArticleSaver、wechatDownload 的路径和可借鉴事项。
- [维护约定](AGENTS.md)：开发时必须同步的跨模块约束。

本地参考目录 `WxArticleSaver` 和 `wechatDownload` 不参与文存构建。
