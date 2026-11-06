# AGENTS.md

本仓库给 agent 的入口：只有指针与禁令，正文见所指文件。

## 指针

- 协作、环境、技术栈、代码与 XAML 规范、界面怎么画、改完怎么验：[CONVENTIONS.md](CONVENTIONS.md)
- 界面结构与布局（五层模型、固定栅格、滚动边界、控件关系、禁止清单）：[docs/UI-STRUCTURE.md](docs/UI-STRUCTURE.md)
- 知识文档：[docs/PROJECT.md](docs/PROJECT.md) · [docs/CODE.md](docs/CODE.md) · [docs/FEATURES.md](docs/FEATURES.md) · [docs/PITFALLS.md](docs/PITFALLS.md) · [GLOSSARY.md](GLOSSARY.md)
- 议题与分诊：[docs/agents/issue-tracker.md](docs/agents/issue-tracker.md) · [docs/agents/triage-labels.md](docs/agents/triage-labels.md) · 域文档布局：[docs/agents/domain.md](docs/agents/domain.md)
- 界面素材（背景图、空态插图、图标、场景图）：用 `tools/gen-image.ps1` 现场生成，不占位、不贴外部素材站。密钥从 `-ApiKey` / `ARK_API_KEY` / `.local/ark.key` 读，**绝不写进仓库**（本仓库是 public）。

## 禁令

- 不用 `dotnet test`（本机不可用），用 `tools/TestRunner/TestRunner.csproj`。
- 不引新 NuGet 包，不改冻结接口签名，不直接改 `App.axaml.cs` 的 `ConfigureServices`。
- 页面里不写颜色字面量。
- 不 `Stop-Process` 别人的 `dotnet` / `NebulaLauncher` 进程。
- 没跑过 `dotnet build` 与 TestRunner 不得声称完成。
- 页面根不用 `ScrollViewer`（一级界面不整页滚动），滚动只在 `GlassPanel` 内容框内；见 [CONVENTIONS.md](CONVENTIONS.md) §7.7。
- 不把 API key 写进代码、文档、票面或提交历史。
