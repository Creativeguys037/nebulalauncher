# 官方换肤 API 的请求与响应契约（Mojang / api.minecraftservices.com）

> 一句话结论：官方换肤三个端点成功时返回的是 **profile JSON**（不是 204 No Content）；请求形态是 `variant` + `file`（上传本地 PNG）或 `variant` + `url`（按 URL 换）；未授权时**完整 multipart 回 `401 application/json`**，空 body / 非 multipart 才回 `403 text/html`。

- 依据：Minecraft Wiki《Mojang API》原文（`https://minecraft.wiki/index.php?title=Mojang_API&action=raw`，2026-11-01 抓取，72571 字节）、2026-11-01 对 `api.minecraftservices.com` 的三条无凭据实测、`modrinth/code`、`MultiMC/Launcher`、`PolyMC/PolyMC` 三份开源实现源码。
- 范围：`POST /minecraft/profile/skins`（multipart 上传与 JSON 按 URL 换）、`DELETE /minecraft/profile/skins/active`（重置）、`variant` 取值与大小写、成功响应体结构、未授权响应形态、皮肤对客户端的可见时机。
- 未做：**没有真 token，成功响应的真实字节未抓**——形态由一方文档明写 + 开源实现依赖行为交叉确认；429 未实测；披风端点只顺带记了失败形态。
- 与既有笔记的关系：`docs/research/littleskin-and-skin-api.md`、`littleskin-texture-write-contract.md`、`littleskin-web-session-texture-write-contract.md` 讲的是**皮肤站（Blessing Skin）一侧**的材质写入，与部署在 `littleskin.cn` 上的路由、pid、网页会话相关；本篇只讲**官方 Mojang 一侧**（`api.minecraftservices.com`）。两套端点没有交集，不要互相套用。
- 标签：`一方文档明写` / `实测` / `开源实现` / `推断` 分别标注。

---

## 1. 三个端点

| 用途 | 方法与 URL | 载荷 | 成功响应 |
| --- | --- | --- | --- |
| 上传本地 PNG 换肤 | `POST https://api.minecraftservices.com/minecraft/profile/skins` | multipart：`variant` + `file` | profile JSON |
| 按 URL 换肤 | `POST` 同上 | JSON：`variant` + `url` | profile JSON |
| 重置为默认皮肤 | `DELETE https://api.minecraftservices.com/minecraft/profile/skins/active` | 无 body | profile JSON |

Wiki 原文（`Mojang_API` 第 592-634 行，`一方文档明写`）：

- `=== Change skin ===`：Payload 是 `variant`（`classic` = Steve，`slim` = Alex）+ `url`；Request 是 `POST /minecraft/profile/skins`；Response 为「Returns [[#Query player profile|the profile]] if operation succeeds.」
- `=== Upload skin ===`：Payload 两段，`variant` 与 `file`（图像数据）；Request 同为 `POST /minecraft/profile/skins`；示例为
  `curl -X POST -H "Authorization: Bearer <access token>" -F variant=classic -F file="@steeevee.png;type=image/png" https://api.minecraftservices.com/minecraft/profile/skins`；Response 同上。
- `=== Reset skin ===`：Request 是 `DELETE /minecraft/profile/skins/active`；Response 同上。
- `=== Hide cape ===` / `=== Show cape ===`：`DELETE` / `PUT /minecraft/profile/capes/active`，Response 同样是「Returns the player profile if operation succeeds.」

## 2. `variant` 请求小写、响应大写

请求侧只认小写 `classic` / `slim`（Wiki 明写，示例里也是 `-F variant=classic`）。

响应侧的 `skins[].variant` 是**大写** `CLASSIC` / `SLIM`（Wiki 第 402 行起 `Query player profile` 的结构定义，`一方文档明写`）：

```
* id: Player's UUID
* name: Player name
* skins[]:
  * id: Skin's UUID
  * state: ACTIVE / INACTIVE
  * url: URL to the skin
  * variant: CLASSIC（Steve）/ SLIM（Alex）
* capes[]:
  * id / state / url / alias
```

即同一个概念在请求与响应里大小写不同；解析 profile 时不能用请求侧的取值去比。`modrinth/code` 的反序列化给这个枚举标了 `#[serde(rename_all = "UPPERCASE")]`（`packages/app-lib/src/state/minecraft_auth.rs:1264-1275`），说明「响应侧大写」是客户端必须按它解析的既成事实，不是 Wiki 笔误。

## 3. 成功响应是 profile JSON，不是 204

（`一方文档明写` + `开源实现`）三条 Response 都写「Returns the player profile」；`modrinth/code` 的 `packages/app-lib/src/state/minecraft_skins/mojang_api.rs:134` 在 equip 之后直接 `response.json::<MinecraftProfile>().await.ok()`，把成功体当 profile 解析，是真实现依赖的行为。

对本仓的意义：`MojangSkinClient.SendAsync`（`src/NebulaLauncher.App/Skinning/MojangSkinClient.cs:102`）只用 `response.IsSuccessStatusCode` 判定成功、从不读成功体。功能上不错（MultiMC、PolyMC 同样不读，见 §5），但拿不到最新 profile 的 `skins[]`/`state`，3D 预览只能等下一次查询——这是「增强」而非「缺陷」。

## 4. 未授权响应：401 还是 403 取决于 body（实测）

2026-11-01 17:1x，无 token，`实测`：

| 请求 | 状态码 | Content-Type | 响应体 |
| --- | --- | --- | --- |
| `POST .../profile/skins`，完整 multipart（`variant` + `file`） | **401** | `application/json` | `{"path" : "/minecraft/profile/skins"}`（41 字节） |
| `POST .../profile/skins`，空 body | **403** | `text/html` | `<!DOCTYPE html PUBLIC '-//W3C//DTD XHTML 1.0 Transitional//EN' ...`（XHTML 错误页，1484 字节） |
| `POST .../profile/skins`，`application/json` + `{"variant":"classic","url":"…"}` | **403** | `text/html` | 与上一行完全相同（同一份 1484 字节 HTML） |
| `POST .../profile/skins`，`application/x-www-form-urlencoded` | **403** | `text/html` | 同上 |
| `DELETE .../profile/skins/active`，无 body | **401** | `application/json` | `{"path" : "/minecraft/profile/skins/active"}`（48 字节） |

结论：**403 不代表「没权限」，而是「请求根本不像这个端点的合法请求」**。决定走哪条分支的变量是 **`Content-Type` 是不是 `multipart/form-data`**，不是 `User-Agent`：加 `User-Agent: NebulaLauncher/1.0` 的空 body 仍 403，multipart 用 curl 默认 UA 仍 401。一旦 multipart 结构正确，鉴权失败统一回 401 JSON；带上假 Bearer 也是同一个 401 与同一 body——**鉴权校验先于 payload 校验**，所以无 token 时无法用探测确认 `variant` 大小写或成功体。401 的 body 只有 `path`，没有 `error` / `errorMessage` / `cause`。所以客户端不能用「403 = 登录过期」这类映射，`MojangSkinClient.cs:109` 把 401 映射成登录过期是与真实形态一致的。

顺带一个容易踩的坑：Wiki 页面顶部的通用规则（`Mojang_API` 第 7-15 行）写「请求必须带 `Content-Type: application/json`，否则返回 415」，那说的是 JSON 类端点；同一页的 `Upload skin` 小节自己给的是 `-F` 的 multipart 示例。拿 JSON 去打上传端点，实测就是上表第三行的 403。

同一形态在皮肤站一侧也有类比：`PUT /api/players/{pid}/textures` 不带 `Accept: application/json` 时 Laravel 会 302 到登录页，带上才回 401 JSON（见 `docs/research/littleskin-texture-write-contract.md`）。**两个站点的共同教训是：状态码的含义跟着请求形态走，不能只看数字。**

## 5. 开源实现对照

| 维度 | Minecraft Wiki（一方） | modrinth/code | MultiMC/Launcher | PolyMC/PolyMC | 本仓 `MojangSkinClient` |
| --- | --- | --- | --- | --- | --- |
| 上传 URL | `POST /minecraft/profile/skins` | 同（`mojang_api.rs:99`） | 同（`SetSkinStep.cpp:48`，`%1/minecraft/profile/skins`） | 同（`SkinUpload.cpp:62`） | 同（`MojangSkinClient.cs:30` 基址 + `UploadSkinAsync`） |
| multipart 字段 | `variant` + `file` | `.text("variant", …)` + `.part("file", …)`（`:80`、`:89`） | `name="file"; filename="skin.png"` + `name="variant"`（`:64`、`:68`） | 同（`SkinUpload.cpp:68`、`:72`） | `form.Add(..., "variant")` + `file`（`:73`） |
| `variant` 取值 | 小写 `classic`/`slim` | 小写（`:82-83`） | **大写** `CLASSIC`/`SLIM`（`:24`、`:26`） | **大写**（`SkinUpload.cpp:46`、`:48`） | 小写（`:53` `VariantOf`） |
| 重置皮肤 | `DELETE /minecraft/profile/skins/active` | 同（`mojang_api.rs:117`） | **`DELETE /minecraft/profile/skins`（不带 `/active`）**（`SetSkinStep.cpp:56`） | 同 Wiki（`SkinDelete.cpp:50`） | 同 Wiki（`ResetSkinAsync`） |
| 成功判定 | —（返回 profile） | `error_for_status()` 后解析 JSON（`:107`、`:134`） | `error == QNetworkReply::NoError`，**不读 body**（`:89`） | `m_reply->error() != NoError` 即失败，**不读 body**（`SkinUpload.cpp:103`） | `IsSuccessStatusCode`，**不读 body**（`:102`） |

三条结论：

1. **请求形态没有分歧**：四份实现与 Wiki 完全一致（URL、两个字段、`image/png`、`skin.png`），本仓不需要改。
2. **不读成功体是主流做法**：MultiMC、PolyMC 与本仓都不读；只有 modrinth 读来刷新自己缓存的 profile。要不要读是产品选择，不是正确性问题。
3. **`variant` 大写只在两家 Qt 启动器里出现**，与 Wiki 明写的小写相反；`reset` 用不带 `/active` 的路径同样只有 MultiMC 这么做。这两处更可能是旧实现的偏差，本仓保持 Wiki 的写法（小写 + `/active`）是对的。

## 6. 皮肤何时对客户端可见

（`推断` + `实测`，无一方文档明写）

- Wiki 的 `Skin` 页与 `Mojang_API` 页都**没有**关于生效时机的表述。对 `rejoin|relog|reconnect|restart|cache|immediate|next join` 逐页 grep：`Skin` 页 **0 命中**；`Mojang_API` 页 9 处命中（1041、1122、1148、1152、1309、1422、1469、1471、1508）**全部落在 `== Peer-to-peer multiplayer networking ==` 一节**，讲的是 Bedrock 联机信令与凭据缓存，与皮肤无关。皮肤相关章节（`Mojang_API` 592-635）内 0 命中。
- 协议上，皮肤是 GameProfile 的 `textures` 属性（base64 JSON + 签名），由服务端在玩家加入时随 Player Info Update 下发给客户端；客户端拿到的是「加入那一刻」的快照。
- 仓内实测观察：`docs/PITFALLS.md:266` 记「把某套皮肤应用到当前账户，服务端确实换了（重进游戏能看到），但侧边栏 3D 卡片继续显示旧皮肤」。
- 2026-11-01 维护者实机复验：官方账户上传本地皮肤成功、游戏内皮肤生效（`#53`）。

合起来的可用结论：**换肤成功后需要重新加入世界/服务器（或重启客户端）才能看到新皮肤；已经在游戏里的会话不会热更新**。这条是推导 + 观察，不是官方承诺，所以客户端提示语写成「重进游戏生效」是安全的，别承诺「立刻生效」。

## 7. 对本仓的含义

1. `MojangSkinClient` 的请求构造与 Wiki 一致，**无需改动**（`variant` 小写、multipart 两字段、`/skins/active` 重置）。
2. 集成测试的成功桩用 `HttpStatusCode.NoContent`（`tests/NebulaLauncher.Tests/AppSkin_MojangSkinClientTests.cs:31`、`:51`、`:63`）与真实响应（200 + profile JSON）不符；实现只看 `IsSuccessStatusCode` 所以测试仍成立，但**它没有覆盖「真实成功体是 JSON」这件事**。若要加读 body 的能力，先把这个桩改成 200 + profile JSON。
3. 401 → 「登录过期」的映射正确；403 的语义是「请求形态不对」，不该映射成权限问题。
4. 想要「换肤后 3D 预览立刻更新」，可读成功响应体里的 `skins[]`（`state: ACTIVE`）刷新本地缓存，或复用现有的 `SkinChangeNotifier` 广播。

## 8. 来源清单

- Minecraft Wiki《Mojang API》，`https://minecraft.wiki/index.php?title=Mojang_API&action=raw`，72571 字节，2026-11-01 抓取，revid 3782642（2026-09-18T04:07:23Z），固定链接 `https://minecraft.wiki/w/Mojang_API?oldid=3782642`。关键行：`7-15`（JSON 端点通用规则）、`402`（Query player profile）、`417`/`560`（响应侧 `variant` 大写）、`592`（Change skin）、`607`（Upload skin）、`620`（curl 示例）、`626`（Reset skin）、`637`/`645`（Hide/Show cape）。
- Minecraft Wiki《Skin》，`https://minecraft.wiki/index.php?title=Skin&action=raw`，42323 字节，2026-11-01 抓取，revid 3855602（2026-10-21T23:47:14Z），固定链接 `https://minecraft.wiki/w/Skin?oldid=3855602`。仅用于「无生效时机表述」这一否定结论，以及 `109` 行的 Player Info Update 显示机制说明。
- `modrinth/code`，`packages/app-lib/src/state/minecraft_skins/mojang_api.rs`（行号见 §5）。
- `MultiMC/Launcher`，`launcher/minecraft/auth/steps/SetSkinStep.cpp`（行号见 §5）。
- `PolyMC/PolyMC`，`launcher/minecraft/services/SkinUpload.cpp`、`launcher/minecraft/services/SkinDelete.cpp`。
- 本机实测：2026-11-01 对 `https://api.minecraftservices.com/minecraft/profile/skins`（multipart / 空 body）与 `.../skins/active`（DELETE）的无凭据请求。
- 仓内：`src/NebulaLauncher.App/Skinning/MojangSkinClient.cs`、`tests/NebulaLauncher.Tests/AppSkin_MojangSkinClientTests.cs`、`docs/PITFALLS.md:266`、票 `#53`。
