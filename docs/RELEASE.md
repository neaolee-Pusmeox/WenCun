# 文存发布规约

本文件是文存唯一的开发与发布路径。无论人工、脚本还是任何模型参与，都按本文件执行；不得根据记忆、旧版文档或某个模型的习惯增加旁路。与 [AGENTS.md](../AGENTS.md) 冲突时，以更严格且可验证的条款为准。

## 1. 单一版本源

- 版本号只写在 `Directory.Build.props` 的 `<Version>`。
- `README.md` 的“当前预发布版本”和 `CHANGELOG.md` 的最新条目是发布镜像，必须与版本源一致；`scripts/verify.ps1` 会校验。
- 程序显示版本时读取程序集版本（`App.Version`），不得再新增手写版本常量。
- 版本语义：补丁位用于兼容性修复、文档和界面校正；次版本位用于新增用户可见能力；主版本位用于破坏数据结构、导出契约或用户工作流。0.x 阶段仍按此规则递增，不跳号、不复用已发布版本。

## 2. 提交与分支

- 每次开始前执行 `git status --short --branch -uall`；用户已有改动必须保留。
- 不要改写历史、强推 `main`、移动已推送的标签，或用 `reset --hard` / `clean` 清理工作区。
- 提交信息使用 `类型: 摘要`，类型为 `feat`、`fix`、`docs`、`chore`、`release`。一个提交只做一类事。
- 用户明确不更新版本时，普通修复提交保留 `Directory.Build.props` 的版本源，变更记入 `CHANGELOG.md` 的“未发布”条目；不生成发布包，不创建或移动标签。只有正式发布才递增版本并执行第 4 节的完整发布流程。
- 发布提交只包含版本镜像、更新记录和本次发布所需改动；`publish/`、`.tools/`、`bin/`、`obj/` 不得入库。
- 不把 `WxArticleSaver/`、`wechatDownload/` 的参考代码复制进文存；引用其结论必须写明本地提交和证据强度。

## 3. 唯一验证门禁

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\verify.ps1
```

该脚本依次执行：版本镜像校验、`dotnet build WenCun.slnx`、`dotnet test tests\WenCun.Tests\WenCun.Tests.csproj --no-build`。任何一步失败都不得提交或发布。

- 本地没有 CI 工作流；本地脚本和人工验收是唯一门禁，不能用“模型说已通过”代替。
- 涉及界面、绑定、主题、字体、图标时，另跑原生 UI smoke，并按 `docs/UI-DESIGN.md` 检查浅色/深色与 1000 宽窗口。
- 采集布局还需检查宽、窄、矮、高窗口的独立滚动、固定保存按钮和筛选刷新；窗口尺寸改动需检查最大化 / 恢复后的原生客户区绘制边界，不能只验普通窗口截图。
- 原生 UI smoke 包括主窗口与阅读窗口；无桌面环境的 `-SkipNativeUi` 会排除两者，不能作为界面发布验收。阅读器改动还需实际本地 WebView2 渲染，模拟初始化成功不足以证明正文可读。
- 修改图标或生成脚本时额外运行 `python -B scripts/test_render_icon.py`，并检查 Windows 读取的实际 ICO / 可执行文件图标方向、透明圆角与 16–256 各帧；不可只审查 PNG。
- 涉及采集、代理、证书时，按 `docs/DEVELOPMENT.md` 的人工验收项补真实环境验证；模拟响应不能替代现场结论。
- 缺陷修复先在未修实现上复现失败，再确认修复后通过；只改测试或只改实现都不算完成。

## 4. 发布步骤

1. 确认工作区只含本次要发布的改动，记录 `git rev-parse HEAD`。
2. 更新 `Directory.Build.props` 的 `<Version>`，同步 `README.md` 与 `CHANGELOG.md`；必要时更新 `docs/DEVELOPMENT.md`、`docs/UI-DESIGN.md` 和 `THIRD-PARTY-NOTICES.md`。
3. 运行 `scripts\verify.ps1`。
4. 审阅 `git diff`，确认没有真实 Cookie、uin/key、pass_ticket、完整凭据 URL，也没有把生成物加入暂存；然后提交源码：

```powershell
git add -A
git commit -m "release: 0.1.2"
```

5. 从刚提交的源码生成发布包并检查实际产物：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\publish.ps1 -Output publish\release-0.1.2
(Get-Item publish\release-0.1.2\WenCun.exe).VersionInfo.ProductVersion
```

`publish\` 是本地产物，不入库；版本检查必须读取生成的 exe，而不是只信 csproj。

6. 推送 `main`：

```powershell
git push origin main
```

7. 发布标签与远程发布（仅在明确授权时执行）：

```powershell
git tag -a v0.1.2 -m "文存 0.1.2"
git push origin v0.1.2
```

GitHub Release 的标题、更新说明和资产必须来自同一版本的 `CHANGELOG.md` 与已核验的发布包；不得用预览包冒充稳定包。

## 5. 发布后核验

- 重新读取 `git rev-parse HEAD`、`git status --short --branch -uall`、`git ls-remote --heads origin main` 和标签指向。
- 下载或读取远程发布包，核对文件名、版本资源和校验和；只看发布页文字或工作流成功不算产物证明。
- 记录发布结论：提交 SHA、版本、标签、发布地址、产物路径和未覆盖的人工验收项。
- 如果远程状态不符，停止后续发布，不要用强推或重打同名标签“修复”。

## 6. 回滚与热修

- 不改写已发布历史。需要回滚时发布新的补丁版本，并在 `CHANGELOG.md` 说明影响范围。
- 发布包错误、版本号错误或文档与产物不一致时，递增补丁位重新发布；不得复用版本号。
- 数据库或导出目录变更必须可增量读取旧数据；不能保证兼容时先备份并单独评审迁移方案。

## 7. 文档治理

- 能力、数据结构、验证命令、发布路径变化时，同一提交更新 README、DEVELOPMENT、RELEASE、CHANGELOG。
- 文档只写当前代码能证明的事实；竞品结论注明本地提交与证据强度，不推测闭源机制。
- 发现文档与代码冲突时，先修正文档或代码中的一方，再提交；不允许保留“以后再说”的分叉描述。
- 清理只针对确认属于本次工作的日志、预览、截图、临时文库和构建输出。保留正式源资产、可复现脚本、本地 SDK 与既有发布包；不得用 `git clean` 或递归通配删除代替路径核验。
