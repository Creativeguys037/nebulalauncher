# LittleSkin OAuth 设备代码流：白名单状态与 scope 复核（2026-11-02）

> 一句话结论：`client_id 1674` 的设备代码流白名单已于 2026-11-02 生效（`POST /oauth/device_code` 返回 200 与 `user_code`），但 OIDC discovery 仍未声明 `Player.ReadWrite`，且 device_code 端点不校验 scope——`Player.ReadWrite` 是否真正下发，只能靠真账号授权后的 token 验证。

- 依据：2026-11-02 对 `open.littleskin.cn` 的匿名 HTTP 实测；`manual.littleskin.cn` 的设备代码流与 OAuth2 作用域两页原文；2026-11-01 的旧结论见 `docs/research/littleskin-texture-write-contract.md`、`docs/research/littleskin-web-session-texture-write-contract.md`。
- 范围：`POST /oauth/device_code`、`POST /oauth/token` 轮询与刷新、OIDC discovery 元数据、设备代码流白名单与测试模式流程。
- 未做：真账号完成授权后的 token 内容（`scope` 字段/JWT 声明）、带 token 的真实 `PUT /api/players/{pid}/textures`、refresh 实测、测试模式解除后的第三方账号授权。
- 与既有笔记的关系：本篇更正上述两篇中「设备码流仍被应用白名单阻塞」的结论（该阻塞已解除）；两篇关于网页会话链路、OAuth 材质写入端点边界（无新建材质端点、只收衣柜已有 tid）的结论仍成立。
- 标签：实测、手册明写、推断分别标注。

---

## 1. 判据变化

| 判据 | 2026-11-01 | 2026-11-02 |
| --- | --- | --- |
| `POST /oauth/device_code`（client_id 1674） | 400 `invalid_client`（未在白名单） | **200**，返回 `user_code` / `device_code` / `verification_uri` |
| device_code 端点是否校验 scope | 未测 | **不校验**：`scope=Definitely.Not.A.Scope` 同样 200 |
| OIDC discovery `scopes_supported` 含 `Player.ReadWrite` | 否 | 仍否 |
| OIDC discovery `device_authorization_endpoint` | 无 | 有（`https://open.littleskin.cn/oauth/device_code`） |
| 代码实现（`CloudClosetClient` 设备码流） | 无 | 仍无 |

结论：原先「等站方加白名单」的阻塞项已经消失；把「discovery 是否声明 `Player.ReadWrite`」当作放行判据是错的——它是元数据展示，不是授权能力的开关。真正的判据是：设备码端点 200 + 真账号授权后 token 是否带该 scope。

## 2. `POST /oauth/device_code` 实测（2026-11-02）

请求（`application/x-www-form-urlencoded`）：

```
client_id=1674&scope=Player.ReadWrite
```

响应 `200`：

```json
{
  "user_code": "828105",
  "device_code": "d63e332cc3394924b655a4c5d54338af",
  "verification_uri": "https://open.littleskin.cn/oauth/link",
  "verification_uri_complete": "https://open.littleskin.cn/oauth/link?user_code=828105",
  "expires_in": 300,
  "interval": 5
}
```

- 缺 `client_id` → `400 invalid_request`。
- `device_code` 是一次性的、5 分钟有效；本次实测值已作废，不要复用。
- 手册：`scope` 为空时默认 `User.Read`，因此实现必须显式请求 `Player.ReadWrite`；要 refresh token 必须同时请求 `offline_access`；要 `id_token` 必须请求 `openid`。

## 3. scope 校验行为（实测，重要）

`scope=Definitely.Not.A.Scope` 仍返回 200。说明该端点只校验客户端与白名单，不校验 scope 合法性/可授予性。

推论（推断）：不能拿「device_code 200」证明 `Player.ReadWrite` 被批准；它只证明应用进了白名单。scope 的实际授予发生在用户授权页与 token 签发阶段，必须用真 token 验证。

## 4. `POST /oauth/token` 轮询实测

用上面的 `device_code` 立刻轮询：

```
grant_type=urn:ietf:params:oauth:grant-type:device_code&client_id=1674&device_code=...
```

响应 `400`：

```json
{"error":"authorization_pending","error_description":"...","message":"..."}
```

说明 token 端点可达、设备码有效，只差用户在授权页完成授权。手册列出的轮询错误：`authorization_pending`（继续轮询）、`slow_down`（按 `interval` 退避）、`expired_token`（停止并提示）；其余错误应停止轮询。

## 5. OIDC discovery 快照（2026-11-02）

`https://open.littleskin.cn/.well-known/openid-configuration`：

- `issuer`: `https://open.littleskin.cn`
- `device_authorization_endpoint`: `https://open.littleskin.cn/oauth/device_code`
- `token_endpoint`: `https://open.littleskin.cn/oauth/token`
- `userinfo_endpoint`: `https://open.littleskin.cn/oauth/userinfo`
- `scopes_supported`: `openid`、`offline_access`、`Yggdrasil.PlayerProfiles.Select`、`Yggdrasil.PlayerProfiles.Read`、`Yggdrasil.MinecraftToken.Create`、`Yggdrasil.Server.Join`
- 仍无 `authorization_endpoint`（授权代码流未开放），仍无 `Player.ReadWrite`。

解释（推断）：`scopes_supported` 只列了 Yggdrasil Connect 相关 scope，不代表 Blessing Skin 的 `Player.*` / `Closet.*` 不可申请；手册的 scope 页明确列了它们。实现不要以 discovery 做能力探测。

## 6. 手册契约（明写）

- **白名单申请**：应用管理页把回调 URL 设为 `https://open.littleskin.cn/oauth/callback`；用绑定邮箱发邮件工单，标题注明「申请 OAuth 设备代码流白名单」，正文给应用名称与客户端 ID；一周内审核。
- **测试模式**：初次白名单应用默认测试模式，**仅应用创建者可授权**；投入生产需再次发工单申请解除。
- **授权页**：`verification_uri` 为 `https://open.littleskin.cn/oauth/link`，页面上 `user_code` 称为「授权码」；`verification_uri_complete` 会自动代入。
- **token 响应**：`token_type: Bearer`、`expires_in`（实测文档示例 259200 秒 = 3 天）、`access_token`；`refresh_token` 仅申请 `offline_access` 时返回；`id_token` 仅申请 `openid` 时返回。
- **刷新**：`grant_type=refresh_token&refresh_token=...&client_id=...`；刷新后**旧的 access token 与 refresh token 立即失效**，后续请求必须用新值。
- **ID 令牌**：JWT，`iss` 可用于拼 `.well-known/openid-configuration` 取 `jwks_uri` 验签；签名算法除 RS256 还可能是 PS256/ES256/EdDSA。

## 7. 对 #60 的影响

1. 阻塞项「等白名单」可关闭；`#60` 可以进入实现。
2. 实现必须在 device_code 请求里显式带 `scope=Player.ReadWrite offline_access`（如需 `id_token` 再加 `openid`）。
3. 测试模式下只有维护者账号能完成授权——这一步是「外部访问权限」，AI 不能代劳；但 device_code 的申请与 token 轮询是代码可做的。
4. `Player.ReadWrite` 是否真被授予，只能在维护者授权后看 token/调 `PUT /api/players/{pid}/textures` 验证；这是当前唯一的未知项，也是 #60 剩余验收的核心。

## 8. 未验证

- 真账号授权后的 token `scope` 字段与 `id_token` 声明。
- 带 `Player.ReadWrite` 的 token 调 `GET /api/players`、`PUT /api/players/{pid}/textures` 的真实响应。
- refresh 全链路（旧 token 立即失效的实测）。
- 解除测试模式后第三方账号的授权路径。

## 来源

- 手册·设备代码流：https://manual.littleskin.cn/advanced/oauth2/device-authorization-grant
- 手册·OAuth2 作用域：https://manual.littleskin.cn/advanced/oauth2/scopes
- OIDC discovery：https://open.littleskin.cn/.well-known/openid-configuration
- 实测端点：`POST https://open.littleskin.cn/oauth/device_code`、`POST https://open.littleskin.cn/oauth/token`
