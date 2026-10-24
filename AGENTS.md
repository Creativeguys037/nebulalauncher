# AGENTS.md

本仓库的协作、环境与代码规范见 [CONVENTIONS.md](CONVENTIONS.md)；域文档布局见 [docs/agents/domain.md](docs/agents/domain.md)。

## Agent skills

### Issue tracker

GitHub Issues（`Creativeguys037/nebulalauncher`，用 `gh` CLI 操作），面板任务板用 deck 工具；PRs as a request surface 关闭。See `docs/agents/issue-tracker.md`.

### Triage labels

triage 五个状态角色沿用默认标签字符串（`needs-triage` / `needs-info` / `ready-for-agent` / `ready-for-human` / `wontfix`），类别角色用 `bug` / `enhancement`。See `docs/agents/triage-labels.md`.

### Domain docs

single-context：全仓库共用一份根目录 `GLOSSARY.md`，架构决策放 `docs/adr/`。See `docs/agents/domain.md`.
