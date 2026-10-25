# Nebula Launcher

以 Windows 为当前验证平台、跨平台架构的 Minecraft 启动器：Avalonia 11 + .NET 8，原生渲染，不内嵌 WebView。

文档：[PROJECT.md](docs/PROJECT.md) 项目与边界 · [CODE.md](docs/CODE.md) 代码与调用链 · [FEATURES.md](docs/FEATURES.md) 功能三态 · [PITFALLS.md](docs/PITFALLS.md) 坑与裁决 · [GLOSSARY.md](GLOSSARY.md) 术语 · [CONVENTIONS.md](CONVENTIONS.md) 规范、界面、验证

构建：

```powershell
. .\env.ps1
dotnet build -v q --nologo
dotnet run --project tools/TestRunner/TestRunner.csproj -v q --nologo -m:1 -p:BuildInParallel=false
```

## 许可

本项目以 [MIT 许可证](LICENSE) 发布：可自由使用、修改、分发与再授权，需保留版权与许可声明，且不提供任何担保。

**第三方内容不在本项目许可范围内**，按各自来源的条款使用与署名：模组中文名数据取自 [PCL2](https://github.com/Meloong-Git/PCL)，作者龙腾猫跃；译名整理者为 MC 百科（mcmod.cn）。「本仓库不是 PCL 的官方衍生版本，也未获 PCL 作者背书」这一边界、逐项署名与移除路径写在 [CONVENTIONS.md](CONVENTIONS.md) §9。
