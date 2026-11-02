# 皮肤站网页会话的材质写入契约（littleskin / Blessing Skin 6.0.2）

> **勘误（2026-11-02 复测）**：`client_id 1674` 的设备代码流白名单已生效（`POST /oauth/device_code` 200），本篇 §4 关于「设备码流仍被白名单阻塞」的结论作废；最新实测与 scope 校验行为见 `docs/research/littleskin-oauth-device-flow-20261102.md`。网页会话链路本身的结论不变。

> 一句话结论：littleskin 上唯一能完成「上传本地 PNG → 应用到角色 → 清除 → 删除材质」的通道是网页会话（密码登录 + CSRF + 站点 JSON 路由）；OAuth JSON API 只能应用衣柜里已有的 tid，不能上传 PNG，且设备码流仍被应用白名单阻塞。

- 依据：2026-11-01 对 littleskin.cn 的真实账号实测（登录、上传、应用、清除、删除全链路），结合站点前端 chunk `app.6d836bf.js` / `627.2ca8103.js` 的路由与上传表单字段、Blessing Skin Server 6.0.2 源码。`GET /user/closet/ids`、`POST /user/closet` 由 6.0.2 源码 `routes/web.php:80-87`、`ClosetController@allIds/@add` 核实，本次未发真实请求实测。
- 范围：网页会话登录、CSRF、`POST /texture` 上传、`PUT/DELETE /user/player/{pid}/textures` 应用/清除、`DELETE /texture/{tid}` 删除；以及它与 OAuth JSON API 的边界。
- 未做：长期会话有效期、验证码/二次验证变体、其他 Blessing Skin 站点复现、积分余额变化的精确核对（扣费公式已从 6.0.2 源码抄录，实际余额未逐次核对）。
- 与既有笔记的关系：`docs/research/littleskin-texture-write-contract.md` 只覆盖 OAuth JSON API，且把路由参数写成 `{uuid}`（实测为 `{pid}`）、把上传字段写成 `name/file/type/public`（实测还需 `source`/`link`/`ignoreSimCheck`）；本篇补网页会话链路并给出勘误。官方（Mojang）一侧的换肤请求与响应契约见 `docs/research/mojang-skin-api.md`；两套端点部署在不同域名、没有交集，排查时先确认走的是哪一侧。
- 标签：实测、源码明写、推断分别标注。

---

## 1. 登录与会话（实测）

1. `GET https://littleskin.cn/auth/login` → 200，返回 SPA 壳（`<main></main>`），页面里有 `<meta name="csrf-token" content="…">`；`window.blessing.version` 为 `6.0.2`，`window.blessing.route` 为 `auth/login`，`window.blessing.user` 为 `null`。
2. `POST https://littleskin.cn/auth/login`，JSON：
   ```json
   { "identification": "<邮箱或用户名>", "password": "<密码>", "remember": false }
   ```
   请求头：`Content-Type: application/json`、`Accept: application/json`、`X-CSRF-TOKEN: <meta csrf-token>`。
3. 成功响应：

   ```json
   { "code": 0, "message": "登录成功，欢迎回来", "data": { "redirectTo": "https://littleskin.cn/user" } }
   ```

   同时下发 `XSRF-TOKEN`、`BS_SESSION`、`locale` 三个 cookie。`BS_SESSION` 是后续所有网页 JSON 路由的会话凭据。
4. 本次账号/环境没有验证码、没有二次验证：登录页的 `blessing-extra` 为 `{"tooManyFails":false,"recaptcha":"","invisible":false}`。这是站点风控状态，不是永久契约；实现时要按 419/302/验证码页面分支处理。
5. `remember: true` 是否换发长期 cookie 未实测；不要把「记住我」当作免登录保证。

**CSRF**：所有改状态的网页请求都要带 `X-CSRF-TOKEN`（从当前页面 `meta` 或 `XSRF-TOKEN` cookie 取）。不带时 `POST /texture` 返回 `419 Page Expired`；实现里遇到 419 应刷新页面取新 token，仍失败再重新登录。

## 2. 网页 JSON 路由（实测 + 源码明写）

| 方法 | 路径 | 作用 | 关键请求/响应 |
| --- | --- | --- | --- |
| GET | `/user/player/list` | 当前账号的角色列表 | 200，数组；每项含 `pid`、`uid`、`name`、`uuid`、`tid_skin`、`tid_cape`、`last_modified` |
| PUT | `/user/player/{pid}/textures` | 应用材质 | JSON `{"skin": <tid>}` 或 `{"cape": <tid>}`；200 `{"code":0,"message":"材质已成功应用至角色 …","data":{…,"tid_skin":…}}` |
| DELETE | `/user/player/{pid}/textures` | 清除材质 | JSON `{"type":["skin"]}`（披风用 `["cape"]`，推断）；200 `{"code":0,"message":"角色 … 的材质已被成功重置"}` |
| GET | `/user/closet/list` | 当前账号衣柜 | 200，分页 JSON；每项含材质 `tid`、名称、类型、公开状态等 |
| GET | `/user/closet/ids` | 当前账号衣柜里的全部 tid | 200，纯 JSON 数组（`ClosetController@allIds` 返回 `pluck('texture_tid')`）；源码明写（`routes/web.php:80-87`），本次未实测 |
| POST | `/user/closet` | 把皮肤库已有材质收藏进衣柜 | JSON `{"tid": <tid>, "name": "<衣柜内名称>"}`；200 `{"code":0,"message":"…"}`；与 `POST /api/closet` 共用 `ClosetController@add`；源码明写（`routes/web.php:80-87`），本次未实测 |
| POST | `/texture` | 上传 PNG | multipart，字段见 §3；200 `{"code":0,"message":"材质 … 上传成功","data":{"data":{"tid":…}}}` |
| DELETE | `/texture/{tid}` | 删除材质 | 200 `{"code":0,"message":"材质已被成功删除"}`；再 GET 同 tid → 404 `{"message":"材质不存在"}` |
| GET | `/texture/{tid}` | 读取材质 | 200；已删除则 404 |

补充：

- `POST /user/player/{pid}/textures` 不存在，返回 405，`Allow: PUT, DELETE`。
- 网页路由的 `{pid}` 是角色列表里的 `pid`（整数），不是 `uuid`。把 uuid 塞进 `/api/players/{player}/textures` 会在参数绑定阶段报 500（见 §4）。
- 网页 `PUT /user/player/{pid}/textures` 与 OAuth 的 `PUT /api/players/{pid}/textures` 共用 `PlayerController@setTexture`（`routes/web.php:74`、`routes/api.php`），所以同样要求材质在衣柜里（`PlayerController.php:193-230`）；网页 `POST /texture` 上传成功会在同一次请求里 `$user->closet()->attach(...)`（`SkinlibController.php:307`），上传后即可应用。
- `/user/closet/list` 是分页的；不要把它当成一次性全量列表。判断某件材质是否已在衣柜时用 `/user/closet/ids`，不要翻页拼全量。
- `POST /texture` 返回 `code:2` 只说明 PNG 已在站点皮肤库，不代表它在本账号衣柜里。此时直接 `PUT /user/player/{pid}/textures` 会被 `setTexture` 拒绝（`user.closet.remove.non-existent`）。正确顺序是：`GET /user/closet/ids` 查归属 → 不在则 `POST /user/closet` 收藏 → 再 `PUT`。`ClosetController@add` 会校验积分（`score_per_closet_item`）并扣费，失败时返回 `code:1` 和站点原文，客户端不能假装成功。

## 3. 上传 PNG：`POST /texture`（实测 + 前端源码）

请求是 `multipart/form-data`，字段如下（名称大小写敏感）。6.0.2 核心 `handleUpload` 的 validate 只覆盖 `name`/`file`/`type`/`public`；标「站点扩展」的字段由 littleskin 的皮肤站扩展/前端要求，缺了会在扩展处失败：

| 字段 | 必填 | 取值 | 说明 |
| --- | --- | --- | --- |
| `name` | 是 | 字符串 | 材质名，站点内展示用 |
| `type` | 是 | `steve` / `alex` / `cape` | 皮肤体型或披风；不是官方那套 `classic`/`slim` |
| `file` | 是 | PNG 文件 | 皮肤/披风图片 |
| `source` | 是（站点扩展） | `0` / `1` / `2` | 0=Original，1=Adapted，2=Repost；6.0.2 核心 `handleUpload` 的 validate 不含此字段，但站点装了写 `bs_texture_source` 的扩展，缺它会在扩展处 500 |
| `link` | 是（站点扩展，可为空串） | 字符串 | 原作链接；原创传 `""` |
| `public` | 是 | `"1"` / `"0"` | 字符串，不是布尔；公开/私有 |
| `ignoreSimCheck` | 是（站点扩展） | `"1"` / `"0"` | 跳过相似度检查；前端默认值随页面状态 |
| `setUploader` | 否 | 角色标识 | 上传页可选的「同时设为该角色材质」参数 |

实测结果：

- 只带 `name/file/type/public`（旧笔记写的四字段）→ `500 {"message":"SQLSTATE[23502]: Not null violation: 7 ERROR: null value in column \"source\" of relation \"bs_texture_source\" violates not-null constraint …"}`，**但服务端已经创建了 texture 行**（本次产生 tid 921823，随后人工删除）。也就是说失败响应不等于没有副作用；客户端必须先补齐字段，失败后按名称/列表核对并清理孤儿材质。
- 带齐 `name/type/file/source/link/public/ignoreSimCheck` → 200，响应 tid 在 `data.data.tid`（不是 `data.tid`）。本次上传 `steve.png`（64×64，228 字节）得到 tid 921824。
- 网页上传页会提示积分消耗；6.0.2 的扣费公式（`SkinlibController.php:273-283`）是 `ceil(fileSize/1024) * (public ? score_per_storage : private_score_per_storage) + score_per_closet_item - score_award_per_texture`，余额不足直接拒绝（284-286）。本次未核对实际余额变化；实现仍应在上传前提示用户。
- 重复 PNG（同 hash 且公开或同上传者）返回 `{"code":2,"message":"…重复…","data":{"tid":<已有 tid>}}`（262-270 行）；客户端应按已有 tid 处理，不要把它当失败。
- 上传成功返回 `{"code":0,"message":"…","data":{"data":{"tid":…}}}`（312-314 行 + 站点包装），且会自动加入衣柜（307 行）。

## 4. 与 OAuth JSON API 的边界（实测 + 源码）

- OAuth 路由是 `PUT /api/players/{player}/textures`，`{player}` 按 `players.pid`（integer）绑定，**不是 `{uuid}`**。用 uuid 请求会得到 `500 SQLSTATE[22P02] invalid input syntax for type integer`；用数字 pid + 非 OAuth 凭据会得到 `401 {"message":"Unauthenticated."}`。
- `routes/api.php`（Blessing Skin 6.0.2）的 `players` 组挂在 `auth:oauth` 下；`PUT {player}/textures` 在 `scope:Player.ReadWrite` 组内。web session 与 Yggdrasil access token 都不被 `auth:oauth` 接受（实测 `/api/user`、`/api/players`、`/api/closet` 均 401）。
- `PlayerController@setTexture` 只读 `skin` / `cape` 的 tid；`Texture::find($tid)` 后要求该 tid 在当前用户衣柜里，否则返回 `user.closet.remove.non-existent`。它不接收 PNG 文件。
- OAuth JSON API 没有 `POST /texture` 或 `skinlib` 路由；`routes/api.php` 只有 `user`、`players`、`closet`、`admin` 四组前缀。**OAuth 只能“应用已有材质”，不能“上传本地 PNG”。**
- OIDC 设备码流仍不可用：`POST https://open.littleskin.cn/oauth/device_code`（client_id=1674）→ 400 `{"error":"invalid_client","error_description":"Client was not found or not whitelisted"}`；`GET /oauth/authorize` → 404；discovery 的 `scopes_supported` 没有 `Player.ReadWrite`，也没有 `authorization_endpoint`。
- 因此：**OAuth 不是本票本地 PNG 需求的解法**；它最多是未来的「无密码应用衣柜已有材质」增强，且要先解决应用白名单与 scope 声明。

## 5. 对客户端的含义

1. **唯一完整链路**：网页会话登录 → `POST /texture` 上传 → `PUT /user/player/{pid}/textures` 应用 → `DELETE` 清除 → `DELETE /texture/{tid}` 删除。2026-11-01 已用真实账号跑通，最终角色状态恢复原样，测试材质已删除。
2. **产品边界**：网页会话需要站点登录，且上传会自动把材质放进用户衣柜（`SkinlibController.php:307`），并扣积分（273-307）；这撞上 `docs/PROJECT.md` 的「皮肤站注册/登录」「衣柜」两条明确不做。是否开这个例外，是产品决策，不是客户端能自行决定的。
3. **凭据**：会话 cookie 是 bearer 凭据。推荐 WebView 登录换取 cookie，只把 cookie 交给 DPAPI/现有安全存储，不保存密码；cookie 过期时重新打开 WebView。不要把密码写进账户模型或日志。
4. **CSRF**：每次改状态前从页面/旧 cookie 取 `X-CSRF-TOKEN`；419 先刷新再重试，不要静默重试到风控。
5. **副作用**：`POST /texture` 可能 500 但已创建材质；实现要避免「用户看到失败、衣柜里却多了一件」的孤儿。上传前做本地 PNG 校验和字段补全，失败后按返回/列表核对并清理。
6. **删除归属**：`DELETE /texture/{tid}` 是破坏性操作。只删除启动器自己创建并记录的 tid；用户原有衣柜材质一律不碰，无法确认归属时宁可不删。
7. **体型映射**：`LocalSkinModel.Classic` → `steve`，`LocalSkinModel.Slim` → `alex`；披风 → `cape`。不要传官方的 `classic`/`slim`。

## 来源

- littleskin 网页端：`https://littleskin.cn/auth/login`、`https://littleskin.cn/user/player/list`、`https://littleskin.cn/user/closet/list`、`https://littleskin.cn/texture`（2026-11-01 实测）。
- 前端 chunk：`https://littleskin.cn/app/app.6d836bf.js`（路由表）、`https://littleskin.cn/app/627.2ca8103.js`（上传表单字段）。
- Blessing Skin Server 6.0.2 源码：`routes/api.php`、`routes/web.php`、`app/Http/Controllers/PlayerController.php`、`app/Http/Controllers/SkinlibController.php`、`app/Models/Player.php`。
- OIDC 发现：`https://open.littleskin.cn/.well-known/openid-configuration`。
- 既有笔记：`docs/research/littleskin-texture-write-contract.md`（OAuth JSON API；本篇修正其 `{uuid}` 与上传字段）。
