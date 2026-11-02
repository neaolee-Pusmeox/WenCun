# 文存开发约定

- 当前入口为 WenCun.slnx；项目在 src/WenCun.Core、src/WenCun.Capture、src/WenCun.Desktop、tests/WenCun.Tests。不要按旧 WeChatExport 路径恢复代码或文档。
- WxArticleSaver/、wechatDownload/ 是只读参考产品，不参与文存修改、编译或发布。.tools/、publish/、bin/、obj/ 为工具和生成产物。
- 先查看 Git 状态，保留用户已有改动。重写工作区使用当前文件快照隔离验证，不把 HEAD 当成当前可构建版本。
- 验证：dotnet build WenCun.slnx；dotnet test tests/WenCun.Tests/WenCun.Tests.csproj。本地 SDK 可用 .tools/dotnet/dotnet.exe。缺陷测试先在未修实现跑红，再确认修复通过。
- 嗅探只写 pending 元数据，不能偷偷入库正文。名称补全同步清单头和文章行；资料库、采集页、公众号页都要验证。
- Session 只在内存使用。日志、错误、测试产物和文档不能包含真实 Cookie、uin/key、pass_ticket 或完整凭据 URL。
- HTTP 重定向逐跳验证域名，响应在读取过程中限量。测试使用模拟响应，不使用真实微信凭据。
- 保存、嗅探、导出、删除和切库遵守同一任务互斥。传入的 appRoot 不得被静态默认用户目录替代。
- 资源准备完成后再提交正文状态；取消不能留下“已保存但无正文”。损坏 pending JSON 不能当作空清单覆盖。
- 导出目录同时使用稳定身份与可读名称。覆盖先写临时文件再替换；复制和删除检查归属与目录联接，不能仅凭公众号名称递归删除。
- 停止采集需等待启动结束并读取清理警告；失败保留 journal。正常退出、超时、取消、异常都要覆盖。
- 修改能力或数据结构同步 README、DEVELOPMENT；竞品结论注明本地提交与证据强度，不推测闭源机制。
- 不用真实用户数据做测试。原生 UI smoke 使用临时文库及不执行生产 OnStartup 的宿主；CNG 测试只创建并清理独立测试密钥。
