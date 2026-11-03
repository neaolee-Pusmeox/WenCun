# 更新记录

## 0.1.1 - 2026-11-03

### 界面

- 完成“纸与墨的私人档案馆”界面重构：三页导航、浅色/深色主题、列表、阅读页和导出 HTML 使用同一套语义色与排版。
- 内置 HarmonyOS Sans SC，补充应用图标与矢量标识；原生标题栏和阅读窗口跟随明暗主题。

### 质量与维护

- 新增 `scripts/verify.ps1` 与 `docs/RELEASE.md`，把版本校验、构建、测试和发布步骤收敛到一条路径。
- 资源与配置文件改为临时文件发布，避免中断留下半写文件；阅读窗口使用注入的 `appRoot`，关闭后清理临时预览目录。
- WebView2 用户数据目录按 `appRoot` 隔离；清理未使用的样式、属性和测试专用存储接口。

### 文档

- 对齐 README、DEVELOPMENT、UI-DESIGN、PRODUCT-COMPARISON 与 THIRD-PARTY-NOTICES 的版本、字体和证据表述。
- 新增本更新记录，并明确 0.1.x 发版规则。

## 0.1.0 - 2026-11-02

- 首个可构建版本：微信会话捕获、公众号目录嗅探、待采集清单、SQLite 文库和 HTML/Markdown/TXT/Word/PDF 导出。
