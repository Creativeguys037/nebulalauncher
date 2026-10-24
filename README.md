# Nebula Launcher

以 Windows 为当前验证平台、跨平台架构的 Minecraft 启动器：Avalonia 11 + .NET 8，原生渲染，不内嵌 WebView。

文档：[PROJECT.md](docs/PROJECT.md) 项目与边界 · [CODE.md](docs/CODE.md) 代码与调用链 · [FEATURES.md](docs/FEATURES.md) 功能三态 · [PITFALLS.md](docs/PITFALLS.md) 坑与裁决 · [GLOSSARY.md](GLOSSARY.md) 术语 · [CONVENTIONS.md](CONVENTIONS.md) 规范、界面、验证

构建：

```powershell
. .\env.ps1
dotnet build -v q --nologo
dotnet run --project tools/TestRunner/TestRunner.csproj -v q --nologo -m:1 -p:BuildInParallel=false
```
