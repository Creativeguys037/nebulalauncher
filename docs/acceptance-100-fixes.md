# #100 验收缺陷修复记录

视觉复核发现 6 个问题，逐条修复。

## 1. 【严重】12/48 张截图重复，4 个界面零覆盖

**根因**：`library/worlds/logs/help` 不是独立页面，是子视图。`NavigationRoutes.cs` 里：
- `Library` → 父页 `Content`（模组页）
- `Worlds` → 父页 `Versions`（版本页）
- `Logs` → 父页 `Settings`（设置·关于）
- `Help` → 父页 `Settings`（设置·关于）

`--page library` 只导航到 Content 父页（显示 ModsView），LibraryView 没被切到。

**修法**：用 `--section` 参数切子视图：
- 资源库：`--page content --section library`
- 世界：`--page versions --section worlds`
- 日志：`--page settings --section logs`
- 帮助：`--page settings --section help`

## 2. 【中】长文本硬裁黑块

首页 1024 实例卡、版本页右列、皮肤名、模组页下拉——缺 `TextTrimming="CharacterEllipsis"`。

## 3. 【中】下载页「下载参数」孤悬元素

疑似残留/错位元素，需定位清理。

## 4. 【中】皮肤页 1024 预览卡裁切

D7 缩高度后内容未跟着重排，底部操作行丢失。

## 5. 【低】暗色主题对比度

「启动游戏」主按钮文字对比度弱、Hero「CONTINUE」几乎不可读。

## 6. 【低】双主题截图状态不一致

皮肤页选中态不同，截图前应统一重置。
