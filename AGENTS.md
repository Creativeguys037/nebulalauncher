# AGENTS.md

本仓库给 agent 的入口：只有指针与禁令，正文见所指文件。

## 指针

- 协作、环境、技术栈、代码与 XAML 规范、界面怎么画、改完怎么验：[CONVENTIONS.md](CONVENTIONS.md)
- 知识文档：[docs/PROJECT.md](docs/PROJECT.md) · [docs/CODE.md](docs/CODE.md) · [docs/FEATURES.md](docs/FEATURES.md) · [docs/PITFALLS.md](docs/PITFALLS.md) · [GLOSSARY.md](GLOSSARY.md)
- 议题与分诊：[docs/agents/issue-tracker.md](docs/agents/issue-tracker.md) · [docs/agents/triage-labels.md](docs/agents/triage-labels.md) · 域文档布局：[docs/agents/domain.md](docs/agents/domain.md)

## 禁令

- 不用 `dotnet test`（本机不可用），用 `tools/TestRunner/TestRunner.csproj`。
- 不引新 NuGet 包，不改冻结接口签名，不直接改 `App.axaml.cs` 的 `ConfigureServices`。
- 页面里不写颜色字面量。
- 不 `Stop-Process` 别人的 `dotnet` / `NebulaLauncher` 进程。
- 没跑过 `dotnet build` 与 TestRunner 不得声称完成。
