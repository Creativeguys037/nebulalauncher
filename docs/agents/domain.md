# Domain Docs

本仓库的域文档布局：**single-context**（单上下文）。

结论：本仓库所有子系统共用一套领域用语，根目录一份 `GLOSSARY.md`，架构决策统一放 `docs/adr/`，
不使用多上下文的 `GLOSSARY-MAP.md`，也不在 `src/<context>/` 下再分各自的 `GLOSSARY.md` 与 `docs/adr/`。
等第一次真正写下领域词条或写下第一条 ADR 时再建这些文件，初始化阶段不预建空文件。

新版技能只认 `GLOSSARY.md` 与 `GLOSSARY-MAP.md` 这两个名字；旧名 `CONTEXT.md` / `CONTEXT-MAP.md` 已废弃，
不要再用。

## 探索代码前先读这些

- 仓库根目录的 **`GLOSSARY.md`**；
- **`docs/adr/`**：读与你即将动手的区域相关的 ADR。

如果这些文件还不存在，**静默继续**。不要提它们的缺失，也不要建议预先创建。
`/domain-modeling` 技能（经 `/grill-with-docs` 与 `/improve-codebase-architecture` 到达）会在词条或决策真正落定时按需生成。

## 文件结构

```
/
├── GLOSSARY.md
├── docs/adr/
│   ├── 0001-event-sourced-orders.md
│   └── 0002-postgres-for-write-model.md
└── src/
```

## 用词表里的词汇

当输出里出现领域概念时（议题标题、重构提案、假设、测试名），采用 `GLOSSARY.md` 中的定义，
不要漂移到词表明确回避的同义词。

如果需要的概念还没进词表，这本身是个信号：要么你在发明项目不用的语言（重新考虑），
要么那里真有缺口（记下来交给 `/domain-modeling`）。

## 标出 ADR 冲突

如果输出与既有 ADR 矛盾，明确说出来，不要静默覆盖：

> _Contradicts ADR-0007 (event-sourced orders), but worth reopening because…_
