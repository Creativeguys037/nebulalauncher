# Issue tracker: GitHub

本仓库的议题与规格存放处是 GitHub Issues（仓库 `Creativeguys037/nebulalauncher`）。所有操作走 `gh` CLI；
面板任务板（deck 工具）与 GitHub Issues 是同一份数据的两个入口，不要另建一套跟踪表。

## 约定

- **建议题**：`gh issue create --title "..." --body "..."`。多行正文用 heredoc。
- **读议题**：`gh issue view <number> --comments`，评论用 `jq` 过滤，同时取标签。
- **列议题**：`gh issue list --state open --json number,title,body,labels,comments --jq '[.[] | {number, title, body, labels: [.labels[].name], comments: [.comments[].body]}]'`，配 `--label` 与 `--state` 过滤。
- **评论**：`gh issue comment <number> --body "..."`。
- **打标签 / 摘标签**：`gh issue edit <number> --add-label "..."` / `--remove-label "..."`。
- **关闭**：`gh issue close <number> --comment "..."`。

仓库从 `git remote -v` 推断；在克隆内运行 `gh` 时自动解析，无需显式传 `--repo`。

## 拉取请求作为分诊入口

**PRs as a request surface: no.**（若本仓库把外部 PR 当作需求入口，改成 `yes`；`/triage` 读这个标志。）

设为 `yes` 时，PR 走与议题相同的标签与状态，用对应的 `gh pr` 命令：

- **读 PR**：`gh pr view <number> --comments`，diff 用 `gh pr diff <number>`。
- **列待分诊的外部 PR**：`gh pr list --state open --json number,title,body,labels,author,authorAssociation,comments`，只保留 `authorAssociation` 为 `CONTRIBUTOR` / `FIRST_TIME_CONTRIBUTOR` / `NONE` 的条目（丢掉 `OWNER` / `MEMBER` / `COLLABORATOR`）。
- **评论 / 打标签 / 关闭**：`gh pr comment`、`gh pr edit --add-label`/`--remove-label`、`gh pr close`。

GitHub 的议题与 PR 共用一套编号：裸 `#42` 可能是任一者，先用 `gh pr view 42` 解析，失败再回退 `gh issue view 42`。

## 技能说「publish to the issue tracker」时

建一张 GitHub issue。

## 技能说「fetch the relevant ticket」时

运行 `gh issue view <number> --comments`。

## Wayfinding 操作

供 `/wayfinder` 使用。**地图**是一张议题，**工单**是它的子议题。

- **地图**：带 `wayfinder:map` 标签的单张议题，正文承载 Notes / Decisions so far / Fog。`gh issue create --label wayfinder:map`。
- **子工单**：以 GitHub 子议题（sub-issue）挂到地图下，走 `gh api` 的 sub-issues 端点。子议题不可用时，把子工单加进地图正文的任务清单，并在子工单正文第一行写 `Part of #<map>`。标签取 `wayfinder:<type>`（`research` / `prototype` / `grilling` / `task`）。认领后指派给推进的开发。
- **阻塞**：用 GitHub 原生 issue dependencies，这是唯一在 UI 上可见的规范表示。建边：
  `gh api --method POST repos/<owner>/<repo>/issues/<child>/dependencies/blocked_by -F issue_id=<blocker-db-id>`，
  其中 `<blocker-db-id>` 是阻塞者的数字 **database id**（`gh api repos/<owner>/<repo>/issues/<n> --jq .id`），**不是** `#number` 也不是 `node_id`。
  GitHub 用 `issue_dependencies_summary.blocked_by` 报告开放中的阻塞者，这是活的闸门。依赖不可用时，退化为子工单正文顶部的 `Blocked by: #<n>, #<n>` 行。所有阻塞者关闭后该工单才算解除阻塞。
- **Frontier 查询**：列出地图的开放子工单（`gh issue list --state open`，限定地图的子议题 / 任务清单），丢掉有开放阻塞者（`issue_dependencies_summary.blocked_by > 0`，或 `Blocked by` 行里有开放议题）或有指派的，按地图顺序取第一个。
- **认领**：`gh issue edit <n> --add-assignee @me`，这是本会话的第一次写操作。
- **解决**：`gh issue comment <n> --body "<答案>"`，随后 `gh issue close <n>`，再把上下文指针（摘要 + 链接）追加到地图的 Decisions so far。

本仓库另有面板任务板工具（`deck_issue_create` / `deck_map_plan_create` / `deck_map_link` / `deck_map_snapshot` / `deck_issue_patch` / `deck_issue_get` 等）。
建票、挂父子边、建阻塞边优先用这些工具：它们按 GitHub 的方式落原生子议题与依赖边，并逐条回报落点；判「落上了」只看写后读回。
上文裸 `gh api` 命令用于工具不可用或需要手工排查的场合。
