# LittleSkin 与皮肤 API 调研笔记

> 一句话结论：想要在第三方启动器里做全站皮肤库浏览，Blessing Skin / LittleSkin **没有公布任何「公开皮肤库浏览」的 JSON API**；能稳定实现的是「按已知 UUID 拉任意玩家皮肤」（Yggdrasil sessionserver 读接口，无需鉴权）与「用 OAuth2/玩家令牌对自己角色做皮肤/披风读写」。

- 依据：一手来源优先——LittleSkin 官方手册（manual.littlesk.in）、Blessing Skin 官方 API 手册（blessing.netlify.app / bs-community/blessing-skin-manual）、authlib-injector 的《Yggdrasil 服务端技术规范》（yushijinhun.github.io）、Blessing Skin Server 源码（bs-community/blessing-skin-server）、api-evangelist/mojang 整理的 Mojang/Minecraft Services OpenAPI、Minecraft Wiki。
- 范围：Yggdrasil 认证四端点、皮肤/披风所有读写端点的请求/响应结构、材质 URL 与尺寸要求、「站内公开皮肤库浏览」是否存在、Mojang/Microsoft 官方换肤现状、通用 UUID 皮肤拉取端点。
- 未做：未对任何在线站点发起实测请求（沙箱只允许读文档/源码/搜索），端点可运行性标注为「文档明写」或「我的推断」。2026-10-25 采样。
- 标签：`##` 下每条事实附来源 URL，区分「文档明写」与「我的推断」。

---

## A. LittleSkin / Blessing Skin 的 Yggdrasil API

LittleSkin 使用 Blessing Skin 最新开发版本，其 Blessing Skin API 与原版 Blessing Skin API 完全一致，可直参 Blessing Skin 的手册；另有少量 LittleSkin 特有的定制 API。来源：<https://manual.littlesk.in/advanced/api>（文档明写）。

LittleSkin 的 Yggdrasil API 基础地址为 `https://littleskin.cn`，Yggdrasil 端点前缀为 `/api/yggdrasil/`（如 `/api/yggdrasil/authserver/authenticate`、`/api/yggdrasil/sessionserver/session/minecraft/profile/{uuid}`）。来源：LittleSkin 手册 /authserver 与 /sessionserver 引用（<https://manual.littlesk.in/advanced/api>），端点形态依 authlib-injector 规范路径推断；启动器可用 API Location Indication（ALI 头 `X-Authlib-Injector-API-Location`）自动发现。来源：《Yggdrasil 服务端技术规范》「API 地址指示」节（<https://yushijinhun.github.io/authlib-injector/zh/Yggdrasil-%E6%9C%8D%E5%8A%A1%E7%AB%AF%E6%8A%80%E6%9C%AF%E8%A7%84%E8%8C%83.html>，文档明写）。

### A1. 认证四端点（authlib-injector 标准路径，与 Mojang Yggdrasil 差异点）

规范：<https://yushijinhun.github.io/authlib-injector/zh/Yggdrasil-%E6%9C%8D%E5%8A%A1%E7%AB%AF%E6%8A%80%E6%9C%AF%E8%A7%84%E8%8C%83.html>（下称「规范」）。所有端点 JSON body，`Content-Type: application/json; charset=utf-8`；错误用 `{error, errorMessage, cause}` 结构（文档明写）。

| 端点 | 方法 | 请求体 | 响应 | 备注 |
| --- | --- | --- | --- | --- |
| `/api/yggdrasil/authserver/authenticate` | POST | `{username, password, clientToken?, requestUser?, agent?}` | `{accessToken, clientToken, availableProfiles[], selectedProfile?, user?}` | `username` 可为邮箱或（若服务端开 `feature.non_email_login`）角色名 |
| `/api/yggdrasil/authserver/refresh` | POST | `{accessToken, clientToken?, requestUser?, selectedProfile?}` | 新令牌 `{accessToken, clientToken, selectedProfile?, user?}` | 吊销旧令牌、颁发新令牌；可顺带选角色 |
| `/api/yggdrasil/authserver/validate` | POST | `{accessToken, clientToken?}` | `204 No Content` | 无效令牌走错误信息 |
| `/api/yggdrasil/authserver/invalidate` | POST | `{accessToken, clientToken?}` | `204 No Content` | 吊销给定令牌 |
| `/api/yggdrasil/authserver/signout` | POST | `{username, password}` | `204 No Content` | 吊销用户所有令牌 |

- 与 authlib-injector 标准 / Mojang 正版 Yggdrasil 的差异注意点：LittleSkin 的 `/api/yggdrasil/authserver` 走的是完整 Yggdrasil；此外 LittleSkin 还提供「**获取 Minecraft 令牌**」这一 OAuth-for-Yggdrasil 定制端点：`POST /api/yggdrasil/authserver/oauth`，请求 `{uuid}`，返回 `{accessToken, clientToken, availableProfiles[], selectedProfile}`；该令牌等价于 Yggdrasil 签发的 access token，可刷新/验证/吊销/加入服务器，且创建时不支持指定 clientToken（服务端随机生成）。来源：<https://manual.littlesk.in/advanced/api>（LittleSkin 特有 API 节，文档明写）、OpenAPI 文档 <https://manual.littlesk.in/littleskin-api.openapi3_1.yaml>。
- LittleSkin 手册明示：上述 LittleSkin 特有 API 大多需 OAuth2 鉴权（Bearer），常用 scope 含 `User.Read`、`Player.Read/Player.ReadWrite`、`Closet.Read/Closet.ReadWrite`、`Notification.Read`、`PremiumVerification.Read`、`Yggdrasil.PlayerProfiles.Read/Select`、`Yggdrasil.MinecraftToken.Create`。来源：<https://manual.littlesk.in/advanced/api>。

### A2. 皮肤 / 披风读写端点（两条不同链路）

Blessing Skin 存在 **两条互相独立的「改皮肤」链路**，务必区分：

1. **网页材质库链路（`/skinlib` + `/texture`，web 路由，需登录会话 Cookie，非 `/api` JSON API）**——上传材质文件到材质库。
2. **Yggdrasil 材质上传链路（`PUT /api/yggdrasil/user/profile/{uuid}/{textureType}`）**——用 Minecraft access token 给自己的角色直传材质，这是第三方启动器应用的标准路径。

下面分别列出。

#### A2.1 Yggdrasil 材质上传/清除（规范「材质上传」节）

来源：规范「材质上传」节（<https://yushijinhun.github.io/authlib-injector/zh/Yggdrasil-%E6%9C%8D%E5%8A%A1%E7%AB%AF%E6%8A%80%E6%9C%AF%E8%A7%84%E8%8C%83.html>，文档明写）。

| 操作 | 端点 | 方法 | 鉴权 | 请求体 |
| --- | --- | --- | --- | --- |
| 上传皮肤/披风 | `PUT /api/yggdrasil/user/profile/{uuid}/{textureType}` | PUT | `Authorization: Bearer {accessToken}` | `multipart/form-data` |
| 清除皮肤/披风 | `DELETE /api/yggdrasil/user/profile/{uuid}/{textureType}` | DELETE | 同上 | 无 body |

- `{uuid}` = 角色 UUID（无符号，去横线）；`{textureType}` = `skin` 或 `cape`。
- PUT 的 multipart 字段：`model`（**仅皮肤用**，可 `slim`＝细胳膊 Alex 或空串＝普通 Steve）；`file`（PNG 图像，`Content-Type` 必须 `image/png`；建议 `Content-Disposition` 的 `filename` 填文件名作为备注）。
- 成功返回 `204 No Content`；未鉴权/令牌无效返回 `401`。
- 是否允许上传由角色属性 `uploadableTextures` 决定（逗号分隔 `skin`/`cape`）；缺该属性则完全不可上传。**Mojang 官方只允许传皮肤，不许传披风**，`uploadableTextures` 是 authlib-injector 扩展，Mojang 返回不含此项。文档明写。
- **`model`（classic/slim，即 Steve/Alex）如何对应 `textures` 属性**：材质元数据里的 `model` 取值 `default` 或 `slim`。规范「角色」模型：`default`=正常手臂(4px) Steve，`slim`=细手臂(3px) Alex。此两者即响应用户常说的 classic/slim。文档明写。

#### A2.2 网页材质库链路（Blessing Skin `/skinlib` 与 `/texture`）

来源：源码 `routes/web.php`（<https://github.com/bs-community/blessing-skin-server/blob/dev/routes/web.php>）与 `app/Http/Controllers/SkinlibController.php`（<https://github.com/bs-community/blessing-skin-server/blob/dev/app/Http/Controllers/SkinlibController.php>）。这些路由**返回 HTML 视图或用登录会话、再渲染页面**，不是给程序调用的 JSON API。

| 用途 | 端点 | 方法 | 说明 |
| --- | --- | --- | --- |
| 材质库列表页 | `GET /skinlib/list` | GET（web） | `SkinlibController@library`：分页 20/页，query 项 `filter`(skin/steve/alex/cape)、`keyword`(name LIKE)、`uploader`(uid)、`sort`(time/其他)，返回字段 `[tid,name,type,uploader,public,likes,nickname]`；匿名只见 `public=true`，登录用户加自己上传的，管理员全见。供页面渲染 |
| 材质详情页 | `GET /skinlib/show/{tid}` / `GET /skinlib/info/{tid}` | GET（web） | 私有材质需登录且仅上传者/管理员可见（默认私有 404 或 403） |
| 上传材质 | `POST /texture` | POST（web 登录会话） | `SkinlibController@handleUpload`：multipart `file`(PNG)、字段 `name`、`type`(steve/alex/cape)、`public`；校验 mimetype=png、尺寸与积分；返回 JSON 提示（含 `tid`） |

- **材质上传尺寸校验（源码明写，我的归纳）**：`width % 64 == 0 && height % 32 == 0`；皮肤（steve/alex）要求 `width/height ∈ {1, 2}` **且** alex 时 `ratio != 2`（即 alex 必须 1:1 的 64×64，steve 可 2:1 的 64×32 或 1:1 的 64×64）；cape 要求 `ratio == 2`（如 64×32、128×64）。默认 `max_texture_width=8192`。此与实际 Minecraft 皮肤尺寸（64×64 新版 / 64×32 旧版、Alex 64×64）一致。（我的推断：上游约束取 64/32 的整数倍是为了兼容旧版 32×32 皮肤；规范亦要求材质 URL 的 `Content-Type` 必须是 `image/png`。）

- **材质纹理 URL 形态**：Minecraft 按材质 hash 做标识与缓存，hash 取自 URL 文件名段（最后一个 `/` 之后）。规范示例：`https://yggdrasil.example.com/textures/e051c27e...97ca99`。Mojang 官方计算 hash 用图像文件 SHA-256。来源：规范「材质 URL 规范」节（文档明写）。LittleSkin 对应前缀为 `https://littleskin.cn/textures/{hash}`（我的推断，按同类 Blessing Skin 站；hash 即材质文件 SHA-256）。

### A3. 是否存在「站内公开皮肤库」浏览端点

**该 API 没有公开皮肤库浏览接口。**

- Blessing Skin 的 `/api/` 路由（`routes/api.php`，OAuth2 JSON API）**只有**：`user`、`players`（自己的角色）、`closet`（自己的衣柜）、`admin`（管理员）四组。**不存在任何浏览全站其他用户材质的 JSON API 端点**（无 `/api/skinlib`、无 `/api/textures` 列表）。源码：<https://github.com/bs-community/blessing-skin-server/blob/dev/routes/api.php>（`Route::prefix('user'|'players'|'closet'|'admin')`）。
- `/api/closet` 返回的衣柜物品含 `type`/`hash`/`uploader`/`public`/`likes` 等字段，但那是**当前用户自己的衣柜**（`category=skin|cape`、可 `page` 分页），不是全站浏览。来源：<https://blessing.netlify.app/api/closet>。
- 全站材质浏览仅在 **网页 `/skinlib`** 上实现（上一节），且路由是 `web.php` 而非 `api.php`，未经认证用户用它也只拿到分页数据到页面渲染，**没有输出无鉴权 JSON 浏览端点**。（我的推断：即便直接 GET `/skinlib/list` 拿到的是页面或分页对象，也非官方稳定 API，不建议作为程序接口依赖。）
- LittleSkin 本身的 OpenAPI（<https://manual.littlesk.in/littleskin-api.openapi3_1.yaml>）也只列了 `announcements`、`premium-verification`、`yggdrasil/sessionserver/session/minecraft/profiles`、`yggdrasil/authserver/oauth`，**无皮肤库浏览**（文档明写/源码可证）。

> 结论（关键）：Blessing Skin / LittleSkin **没有**「公开皮肤库浏览」JSON API。要做全站皮肤库浏览，只能抓 `/skinlib` 网页（无官方接口），建议放弃或仅做 Top 人气抓取。

### A4. 皮肤 PNG 尺寸/格式与 64×64 布局

- 格式：PNG；材质 URL 的 `Content-Type` 必须 `image/png`（防 MIME sniffing）。来源：规范「材质 URL 规范」「用户上传材质的安全性」节（文档明写）。
- 尺寸：皮肤为 64×64（现代，含 Alex）或 64×32（旧版）；Yggdrasil 规范宽松接受「64 的整数倍宽、32 的整数倍高」并要求服务端重写剔除无关数据；22×17 整数倍的披风为非标，需服务端补透明像素至 64×32 倍数。源自规范「用户上传材质的安全性」节。
- 64×64 与 64×32 布局本身（头/身/手/脚的 UV 划分）在规范中未逐像素描绘，属于 Minecraft 客户端模型约定；笔记只给尺寸约束。（我的推断：经典 Steve 布局 64×32 为旧版，64×64 为 1.8+ 新版，Alex 限 64×64 slim。）

---

## B. 官方换肤（Microsoft / Mojang 账户）

结论先行：

- **能读**：第三方启动器可凭「Minecraft access token」通过 `sessionserver.mojang.com` 读任意玩家的角色属性（textures）；官方皮肤读取对任何持有合法 token 的启动器开放。
- **能写**：官方提供公开的 HTTP「换肤写操作」接口，但**不是**旧的 `api.mojang.com/user/profile/{uuid}/skin`，而是 `https://api.minecraftservices.com/minecraft/profile/skins`（POST）与 `.../skins/active`（DELETE 重置）。**必须携带 Minecraft access token（Bearer）**，该 token 正是第三方启动器经「Microsoft → Xbox Live → XSTS → Minecraft 登录」链路持有并用于启动的 access token。
- **必须走这条链路的条件**：写皮肤用的 token = Minecraft access token，第三方启动器完全具备；因此官方换肤在第三方启动器里**可以写**（只要持有有效 token），不是只能网页。仅当未能完成微软登录（纯离线账户）时才只能网页手动换。

### B1. 旧 Mojang 皮肤写接口的现状

- 旧接口 `PUT/POST https://api.mojang.com/user/profile/{uuid}/skin`（含 part/url 变体）随 Mojang 账户访问方式迁移而**不再被维护/不被新 Microsoft 账户使用**；Minecraft Wiki 与 api-evangelist 均把当前官方换肤定位为 `minecraftservices.com`。来源：Minecraft Wiki《Mojang API》`Change skin` 一节（<https://minecraft.wiki/w/Mojang_API>）；api-evangelist/mojang（<https://github.com/api-evangelist/mojang>）。
- 明确结论（我的推断，依据迁移事实与多来源一致）：`api.mojang.com/user/profile/{uuid}/skin` 对迁移后的 Microsoft 账户**已不可用**（该域名主体 API 已退役到 sessionserver / minecraftservices），不要依赖它。

### B2. 当前官方换肤写接口（中文要点）

来源：api-evangelist/mojang `mojang-skins-api-openapi.yml`（<https://github.com/api-evangelist/mojang/blob/main/openapi/mojang-skins-api-openapi.yml>，x-last-validated 2026-05-30）与 Minecraft Wiki《Mojang API》（<https://minecraft.wiki/w/Mojang_API>）。

| 操作 | 端点 | 方法 | 鉴权 | 请求 |
| --- | --- | --- | --- | --- |
| 换肤（上传/指定 URL） | `https://api.minecraftservices.com/minecraft/profile/skins` | POST | `Authorization: Bearer {accessToken}` | JSON `{variant, url}` **或** `multipart/form-data`（`variant` + `file`(PNG)） |
| 重置为默认皮肤 | `https://api.minecraftservices.com/minecraft/profile/skins/active` | DELETE | 同上 | 无 body |

- `variant`：`classic`（Steve）或 `slim`（Alex）。
- 返回 `AuthenticatedProfile`（含 `id`/`name`/`skins[]`/`capes[]`/`profileActions`）。400 无效载荷、401 缺失/无效 access token。
- `accessToken` 来源：第三方启动器的 Xbox 登录链——`/authentication/xboxlive` → XSTS → Minecraft 登录 `api.minecraftservices.com/authentication/login_with_xbox`，之后的 access token 即 Bearer。api-evangelist 明写「player-account endpoints require a Bearer Minecraft access token obtained via the Xbox Live / XSTS authentication chain」。来源：<https://github.com/api-evangelist/mojang>。

### B3. 官方账户 token 能否用于皮肤接口

- **能**：启动器在游戏内就已持有 Minecraft access token，官方所有写接口（换肤、删 skin、重置）都用它做 Bearer；第三方启动器用它调用 `minecraftservices.com/minecraft/profile/skins` 完全可行（文档明写：认证即该 token）。
- 注意：Minecraft access token 不同于 raw Xbox/XSTS token；需完成 Minecraft 登录以换取 access token。（我的推断/常识，文档侧面佐证。）

---

## C. 通用性

### C1. 使用同一套 Blessing Skin JSON API 的皮肤站

- Blessing Skin 是自托管开源服务（bs-community/blessing-skin-server），凡部署/使用它的皮肤站 API 结构一致。**确定采用 Blessing Skin / authlib-injector 体系的代表性站是 LittleSkin**（littleskin.cn，官方自述）。其它站是否同套框架需逐站验证（我的推断：仅能确定「同套框架」的是所有自建 Blessing Skin 站；本笔记未逐一核验第三方皮肤站名单）。
- authlib-injector 体系（Yggdrasil）被广泛皮肤站实现（Blessing、OpenBMCLAPI、Yggdrasil 各类 mock），其 `GET /` 元数据、`/sessionserver`、`/authserver` 结构在各实现间兼容（文档明写于规范「扩展 API」「参见 参考实现」）。
- Minecraft Wiki《Mojang API》：`api.minecraftservices.com` 是当前微软侧唯一主接口面；`sessionserver.mojang.com` 旧 Yggdrasil 读接口多数公开。（文档明写于 api-evangelist 总览。）

### C2. 按 UUID 拉任意玩家皮肤的通用端点

- **端点**：`GET /sessionserver/session/minecraft/profile/{uuid}?unsigned={true|false}`（Yggdrasil 经 authlib-injector 的标准路径；Mojang 官方对应 `https://sessionserver.mojang.com/session/minecraft/profile/{uuid}?unsigned=false`）。来源：规范「角色部分·查询角色属性」（<https://yushijinhun.github.io/authlib-injector/zh/Yggdrasil-%E6%9C%8D%E5%8A%A1%E7%AB%AF%E6%8A%80%E6%9C%AF%E8%A7%84%E8%8C%83.html>，文档明写）。
- `{uuid}`=无符号 UUID；`unsigned=false` 会带签名（signature，SHA1withRSA）；`unsigned` 缺省 true（不带签名）。角色不存在返回 `204 No Content`。
- **textures 属性是 base64 包裹的 JSON**：响应的 `properties[]` 里 `name=textures` 的 `value` 是一段 Base64 编码的 JSON，解码后为：
  `{timestamp, profileId, profileName, textures: {SKIN: {url, metadata:{model}}, CAPE: {url}}}`（规范文档明写）。`metadata.model` 为 `default`/`slim`。客户端下载 `textures.SKIN.url` 的 PNG 即可，文件名即材质 hash。
- **批量按名查**：`POST /api/profiles/minecraft`，body 为角色名数组，返回不含属性的角色列表（文档明写）。这对「由名字找 UUID 再拉皮肤」很有用。

---

## 给启动器端可实现的端点表

下表为可直接实现的最小端点集（路径以 LittleSkin 为例；自建站把 `littleskin.cn` 换成自己域名）。

| 用途 | 方法+路径 | 鉴权 | 备注 |
| --- | --- | --- | --- |
| 发现 Yggdrasil API | `GET {站点根}` → ALI 头 `X-Authlib-Injector-API-Location` | 无 | 自动定位 `/api/yggdrasil` |
| 登录 | `POST /api/yggdrasil/authserver/authenticate` | 无 | 得 accessToken |
| 刷新令牌 | `POST /api/yggdrasil/authserver/refresh` | 无 | 需旧 accessToken |
| 校验令牌 | `POST /api/yggdrasil/authserver/validate` | 无 | 204=有效 |
| 登出/吊销 | `POST /api/yggdrasil/authserver/invalidate`（单个）/ `signout`（全） | 无 | |
| 读自己角色皮肤 | `GET /api/yggdrasil/sessionserver/session/minecraft/profile/{uuid}` | 无（或带 token） | 解 base64 textures |
| 改自己皮肤 | `PUT /api/yggdrasil/user/profile/{uuid}/skin`（multipart `model`+`file`） | Bearer accessToken | 204 |
| 改自己披风 | `PUT /api/yggdrasil/user/profile/{uuid}/cape` | Bearer accessToken | 204 |
| 删除皮肤/披风 | `DELETE /api/yggdrasil/user/profile/{uuid}/skin\|cape` | Bearer accessToken | 204 |
| （OAuth）登录用户 | Blessing Skin `POST /api/auth/login`（JWT）/ OAuth2 `code/device` 流 | — | 用于非 Yggdrasil 的站内 API；见下 |
| 站内 API 根/信息 | `GET /api` | 无 | 返回版本/site_name |
| 站内衣柜 | `GET/POST/PUT/DELETE /api/closet[...]` | Bearer（站内 JWT） | 自己衣柜 |
| 站内角色 | `GET/POST/PUT/DELETE /api/players[...]` | Bearer（站内 JWT） | 自己角色 |
| 按名批量取 UUID | `POST /api/yggdrasil/api/profiles/minecraft` | 无 | 名字数组→角色 |

**不能实现**：全站公开皮肤库浏览（无 API，见 A3）。

---

## 附录：来源清单

- LittleSkin 用户手册·LittleSkin API：<https://manual.littlesk.in/advanced/api>
- LittleSkin OpenAPI：<https://manual.littlesk.in/littleskin-api.openapi3_1.yaml>
- Blessing Skin 用户手册·Web API：<https://blessing.netlify.app/api/>（basis / auth / players / closet / avatars-and-previews / oauth）
- authlib-injector《Yggdrasil 服务端技术规范》：<https://yushijinhun.github.io/authlib-injector/zh/Yggdrasil-%E6%9C%8D%E5%8A%A1%E7%AB%AF%E6%8A%80%E6%9C%AF%E8%A7%84%E8%8C%83.html>（原 wiki 页已转移到此）
- Blessing Skin Server 源码：<https://github.com/bs-community/blessing-skin-server>（routes/api.php、routes/web.php、app/Http/Controllers/SkinlibController.php）
- Minecraft Wiki·Mojang API：<https://minecraft.wiki/w/Mojang_API>
- api-evangelist/mojang（Mojang/Minecraft Services OpenAPI 汇编）：<https://github.com/api-evangelist/mojang>

---

## D. 实测：皮肤库浏览的数据端点

> **实测时间**：2026-10-25 22:00–22:10（Asia/Shanghai）。**方法**：对 `littleskin.cn` 发起真实 HTTP 请求（`requests` 2.34.2 / Python 3.12），记录实际状态码、`Content-Type` 与响应体形态；辅以 `ghproxy.net` 镜像读取 Blessing Skin 源码全文。**本节所有结论均为实测所得，凡实测不到的写「实测不到」。**
>
> **本节推翻了 A3 的结论。** A3 写于「未对任何在线站点发起实测请求」的前提下，其「没有输出无鉴权 JSON 浏览端点」一句是**基于源码路由的推断，且推断错误**。实测证明 `/skinlib/list` 就是可直接程序消费的公开 JSON 端点。按本次任务要求，**A 节正文一字未改**，请以本节为准，读 A3 时请连同本节一起读。

### D0. 结论速览

| 问题 | 实测答案 |
| --- | --- |
| `/skinlib/list` 返回 HTML 还是 JSON？ | **JSON**。`200` + `Content-Type: application/json`，Laravel 分页对象，`per_page=20` |
| 返回 HTML 吗？ | **完全不返回 HTML**。无论带 `Accept: text/html`、`X-Requested-With: XMLHttpRequest` 还是裸请求，一律 JSON |
| 有可程序消费的「浏览全站皮肤」端点吗？ | **有**，`GET /skinlib/list`（列表）+ `GET /skinlib/info/{tid}`（详情） |
| 需要鉴权吗？ | **不需要**。匿名即可，且匿名只看 `public=true` 的材质 |
| 能分页吗？能搜索吗？能排序吗？ | **都能**：`page` / `keyword` / `uploader` / `filter` / `sort` |
| HTML 里有可提取的 JSON 块吗？ | **没有**。页面是 SPA 空壳，`blessing-extra` 里只有 `{"currentUid":null}`，零条皮肤数据 |
| 是 LittleSkin 私有魔改还是 Blessing Skin 通用能力？ | **通用**。标准 Blessing Skin 6.0.2 就有，已在另一独立站点复验 |

### D1. `GET /skinlib/list` —— 实测为公开 JSON 端点

**这是本次调研最重要的发现：它不是一个「网页」，而是一个未文档化的 JSON 数据端点。**

实测请求与响应：

```
GET https://littleskin.cn/skinlib/list
→ 200
→ Content-Type: application/json
→ Server: nginx
→ len: 3075 bytes / 20 条
```

对同一 URL 分别改 `Accept` 与 XHR 头，**四种组合结果完全一致（均为 `200 application/json`，字节数同为 3075）**，说明服务端无条件返回 JSON、与内容协商无关：

| 请求头组合 | 状态码 | `Content-Type` | 长度 |
| --- | --- | --- | --- |
| 裸请求（无 `Accept`） | 200 | `application/json` | 3075 |
| `Accept: text/html,application/xhtml+xml,...` | 200 | `application/json` | 3075 |
| `Accept: application/json` | 200 | `application/json` | 3075 |
| `X-Requested-With: XMLHttpRequest` | 200 | `application/json` | 3075 |
| 上两者同时 | 200 | `application/json` | 3075 |

> 这排除了「Laravel 常见做法：同一路由在 XHR/JSON Accept 下返回 JSON」这一猜测——它不是条件性返回，而是**恒定**返回 JSON。浏览器打开 `/skinlib/list` 直接看到的就是裸 JSON，网页 `/skinlib` 是另一个路由（SPA 壳）。

响应体结构（Laravel `paginate(20)` 原始序列化，**未做任何包装**）：

```json
{
  "current_page": 1,
  "data": [
    {"tid": 917062, "name": "皮肤1", "type": "alex", "uploader": 5590573,
     "public": true, "hd": false, "likes": 1, "nickname": "KAORO332"},
    {"tid": 917056, "name": "Carrot243", "type": "alex", "uploader": 1864910,
     "public": true, "hd": false, "likes": 1, "nickname": "Carrot243"}
  ],
  "first_page_url": "https://littleskin.cn/skinlib/list?page=1",
  "from": 1,
  "next_page_url": "https://littleskin.cn/skinlib/list?page=2",
  "path": "https://littleskin.cn/skinlib/list",
  "per_page": 20,
  "prev_page_url": null,
  "to": 20
}
```

**顶层键实测恰为 9 个**（`sorted(keys)` 实测输出）：`current_page`、`data`、`first_page_url`、`from`、`next_page_url`、`path`、`per_page`、`prev_page_url`、`to`。

- **没有 `total`，没有 `last_page`**（实测确认）。这是关键约束：**无法获知总条数**，只能靠 `next_page_url` 是否为 `null` 判断是否到底。
- 每条记录恰好 8 个字段（源码 `select` 明写，实测一致）：`tid`、`name`、`type`、`uploader`、`public`、`likes`、`nickname`、`hd`。
- **列表里没有 `hash`**——拿不到图片直链，见 D3 的解法。

### D2. `GET /skinlib/list` 的查询参数（全部实测）

参数语义来自源码 `SkinlibController::library`（经 `ghproxy.net` 镜像取得 master 分支全文），并对每个参数做了实测验证：

```php
public function library(Request $request)
{
    $user = Auth::user();
    // Available filters: skin, steve, alex, cape
    $type     = $request->input('filter', 'skin');
    $uploader = $request->input('uploader');
    $keyword  = $request->input('keyword');
    $sort     = $request->input('sort', 'time');
    $sortBy   = $sort == 'time' ? 'upload_at' : $sort;   // ← 注意：sort 值被直接当列名用

    return Texture::orderBy($sortBy, 'desc')
        ->when($type === 'skin',
            fn (Builder $q) => $q->whereIn('type', ['steve', 'alex']),
            fn (Builder $q) => $q->where('type', $type))
        ->when($keyword, fn (Builder $q, $k) => $q->like('name', $k))
        ->when($uploader, fn (Builder $q, $u) => $q->where('uploader', $u))
        ->when($user, function (Builder $q, User $user) {
            if (!$user->isAdmin()) {
                return $q->where(function (Builder $q) use ($user) {
                    $q->where('public', true)->orWhere('uploader', $user->uid);
                });
            }
        }, fn (Builder $q) => $q->where('public', true))
        ->join('users', 'uid', 'uploader')
        ->select(['tid','name','type','uploader','public','likes','nickname'])
        ->paginate(20);
}
```

实测结果表：

| 参数 | 取值 | 实测行为 | 判定 |
| --- | --- | --- | --- |
| `page` | `1`,`2`,`5`,`100`,`1000`,`10000` | 均 `200 JSON`，`from` 依次为 1/21/81/1981/19981/199981，`next_page_url` 正确递增 | ✅ **生效** |
| `page` | `999999` | `200`，`data: []`，`from: null`，`next_page_url: null` | ✅ 越界返回空数组，不报错 |
| `page` | `0`,`-1`,`abc` | 均回落为 `current_page: 1`、20 条 | ✅ 无效值安全兜底 |
| `filter` | `skin` | 20 条，`type` 分布 `{alex:15, steve:5}`，与不传时**完全相同** | ✅ 生效（=steve+alex） |
| `filter` | `alex` | 20 条，全为 `alex` | ✅ 生效 |
| `filter` | `steve` | 20 条，全为 `steve` | ✅ 生效 |
| `filter` | `cape` | 20 条，全为 `cape`（如 `China Railway (双轨铁路)`） | ✅ 生效 |
| `keyword` | `可爱` | 20 条，命中 `可爱捏`、`超级可爱捏`、`The_Blue_Mc可爱捏` | ✅ 生效（name LIKE） |
| `keyword` | `zzzzzzzzzzzz` | 1 条（`zzzzzzzzzzzzzzz`） | ✅ 生效 |
| `uploader` | `5581561` / `"5581561"` | 2 条（该 uid 的公开材质） | ✅ 生效 |
| `uploader` | `5590573`、`999999999` | 0 条 | ✅ 生效（无匹配） |
| `sort` | `time`（默认） | 按 `upload_at` 倒序，`tid` 从 917063 起 | ✅ 生效 |
| `sort` | `likes` | 按 `likes` 倒序，首条 `tid=15433`、`likes=118836`（全站人气第一「彩虹人」） | ✅ 生效 |
| `sort` | `tid` | 与 `time` 结果相同（tid 与上传时间同序） | ✅ 生效 |
| `sort` | `new`、`id`、`like`、`like_count`、`created_at`、`newest`、`oldest`、`asc`、`desc`、`publish_time`、`likes_count` | **全部 `500`**，返回 `text/html` 错误页 | ⚠️ **500 而非忽略** |
| `per_page` | `100` | 仍为 20 条，`per_page: 20` | ❌ **无效**（硬编码 `paginate(20)`） |
| `q` / `search` | `test` | 与不传时完全相同（20 条原始列表） | ❌ 无效（参数名是 `keyword`） |

**关于 `sort` 的 500（实测并解释）**：源码 `$sortBy = $sort == 'time' ? 'upload_at' : $sort;` 把 `sort` 的值**直接当数据库列名**传给 `orderBy`。合法取值实测只有 **`time` / `likes` / `tid`** 三个（恰好对应 `upload_at`、`likes`、`tid` 三列）；其余值会让 SQL 报「列不存在」→ `500`。这是**未做白名单校验的实现瑕疵**，不是设计上的枚举接口。

- ✅ `per_page` 不可调 → **每页恒为 20 条**，翻完 10 万页才能取 200 万条，抓全站成本高。
- ✅ `sort=likes` 可稳定拿「全站人气榜」，这是最实用的一个用法。

**实测性能**：同一列表端点连续 3 次请求耗时 669 ms / 522 ms / 982 ms（含 TLS 握手，未复用连接时更高）。规模上属于「可接受但需要缓存」。

### D3. `GET /skinlib/info/{tid}` —— 实测也是 JSON，补上列表缺失的 `hash`

这是第二个惊喜：详情端点同样返回 JSON，而 `A2.2` 把它记作「详情页（HTML）」。

```
GET https://littleskin.cn/skinlib/info/15433
→ 200
→ Content-Type: application/json
→ 669 bytes
```

完整响应体（实测抓取，一字未改）：

```json
{
  "tid": 15433,
  "name": "彩虹人【外国搬运】",
  "type": "alex",
  "hash": "77021656b8a6e52c6624fcd91d284310d0ba18824fb6593b4609d9f9eb8b2035",
  "size": 4,
  "uploader": 14674,
  "public": true,
  "upload_at": "2019-03-10 17:56:04",
  "likes": 118838,
  "phash": "1101110000001100011011001011011000100011010111100011011011001110100100111011001111001001001100010111000110011001001001001100100100000111001000101000111111100110111001110111000011101111101100101111011011001100111110010010001110011000110000010001110000010001",
  "protected": false,
  "colorhash": "00000000000100010000000100000000000000010000000000000000000000000000000000000000000000000000000000000000",
  "hd": false
}
```

字段意义（实测 + 源码归纳）：

| 字段 | 含义 | 备注 |
| --- | --- | --- |
| `hash` | **材质 SHA-256**，即图片直链文件名 | 列表里没有，详情里才有 → 见下方「两次请求」问题 |
| `size` | 材质体积（KB 量级） | |
| `upload_at` | 上传时间，`Y-m-d H:i:s` 字符串 | 列表里没有 |
| `phash` | 感知哈希（perceptual hash）位串 | 可用于**相似皮肤检索/去重**，实测存在 |
| `colorhash` | 颜色哈希位串 | 同上 |
| `protected` | 是否受保护 | |
| `public` | 是否公开 | 与列表一致 |

**403 是「私密」而非「无权访问」**：实测 `tid=917062` 的 `info` 返回 `403`，页面正文为

> `详细信息：请求的材质已经设为私密，仅上传者和管理员可查看`

即该材质是**新上传且默认私有**的（呼应 `A2.2` 的「默认私有」），不是端点需要鉴权。换用列表里 `public:true` 的材质立刻 `200 JSON`。

**图片直链（实测可用）**：

```
GET https://littleskin.cn/textures/{hash}
→ 200, Content-Type: image/png, 4459 bytes
```

- 加 `.png` 后缀反而 **404**：`/textures/{hash}.png` → `404 text/html`。**直链必须无扩展名**，这与 `A2.2` 的推断「`https://littleskin.cn/textures/{hash}`」完全吻合，本次已实测确认。
- **无需鉴权**，匿名可下。

**列表 → 图片的两条路径**：

| 路径 | 请求数 | 稳定性 |
| --- | --- | --- |
| 列表拿 `tid` → 调 `info/{tid}` 取 `hash` → 下 `/textures/{hash}` | **每条 2 次请求**（20 条 = 1+20 = 21 次） | 走 JSON，最稳 |
| 列表拿 `tid` → 直接下 `/preview/{tid}` | **每条 1 次请求** | 拿到的是**预览图**，非原始材质 |

实测 `/preview/{tid}`：

```
GET https://littleskin.cn/preview/15433
→ 200, Content-Type: image/webp, 8698 bytes
```

- 是 `webp` 预览（含渲染后的立体展示），**不是**原始 PNG 材质文件，无法直接当皮肤上传。
- **但 `/preview/{tid}` 不需要 `hash`、单次请求、匿名可下** —— 对「启动器里做皮肤库网格缩略图」这条需求，**它是最省事的一跳**；只有在「用户点了下载、要真材质」时才需要补 `info` → `/textures/{hash}`。
- 附带：`/avatar/{uid}` 实测 `200 image/webp`（上传者头像），`/avatar/player/{name}` 同样可用（如 `/avatar/player/wacks` → `200 image/webp`）。

### D4. HTML 里有没有可稳定提取的 JSON 块？—— **没有**

这是任务里「重点」问的一项。实测 `/skinlib` 首页 HTML（`200 text/html`，**仅 5150 字节**）：

| 检查项 | 实测结果 |
| --- | --- |
| `window.__INITIAL_STATE__` | ❌ 不存在 |
| `window.blessing` | ✅ 存在，但内容为 `{"version":"6.0.2","locale":"zh_CN","base_url":"https://littleskin.cn","site_name":"LittleSkin","route":"skinlib","user":null}` —— **只有站点元信息，零条皮肤数据** |
| `<script id="blessing-extra" type="application/json">` | ✅ 存在，内容实测为 `{"currentUid":null}` —— **仅此而已** |
| `data-*` 携带数据的属性 | ❌ 只有 `data-toggle="dropdown"`（Bootstrap 用），无 `data-tid` / `data-name` |
| HTML 中出现 `tid` | **0 次** |
| HTML 中出现 `skinlib/list` | **0 次** |
| HTML 中出现 `/preview/` | **0 次** |
| `card-img-top` | 1 次（只是 CSS 选择器，用于 `filter: drop-shadow`） |

**判定：网页是个 SPA 空壳，一条皮肤数据都不内嵌。** 想要数据只能去调 `/skinlib/list` 这个 JSON 端点；「解析 HTML 拿结构化数据」这条路**实测不存在**（也就不存在「脆弱但可行」的 HTML 刮取方案）。

**佐证（关键证据）**：实测拉取前端 bundle `/app/app.6d836bf.js`（397929 字节），其中路由表原文为：

```js
skinlib:{
  home:()=>"/skinlib",
  info:e=>`/skinlib/info/${e}`,
  list:()=>"/skinlib/list",
  show:e=>`/skinlib/show/${e}`
}
```

前端自己把 `/skinlib/list` 与 `/skinlib/info/{tid}` 当作**数据端点**调用（而不是页面 URL），`/skinlib` 与 `/skinlib/show/{tid}` 才是页面路由。这从客户端一侧证实：`list`/`info` 是 Blessing Skin 的**既定数据接口**，不是我们钻的空子。

### D5. `/api/closet` 的鉴权与错误结构（实测）

```
GET https://littleskin.cn/api/closet          → 401 application/json  {"message":"Unauthenticated."}
GET https://littleskin.cn/api/user            → 401 application/json  {"message":"Unauthenticated."}
GET https://littleskin.cn/api/players         → 401 application/json  {"message":"Unauthenticated."}
```

- **错误结构**：Laravel 标准 `{"message":"Unauthenticated."}`（30 字节），**不是** Yggdrasil 的 `{error, errorMessage, cause}`，也**不是** OAuth2 的 `{error, error_description}`。
- **响应头无 `WWW-Authenticate`**（实测为 `None`）—— 即不会告知该用哪种 scheme，客户端得自己知道要带 `Authorization: Bearer`。
- **带假 token 的行为会骗人**：实测带 `Authorization: Bearer faketoken123` 请求 `/api/closet`，返回的是 **`200 text/html` 的登录页 HTML**，而不是 401 JSON。这是 `auth:oauth` 守卫在 token 无效时的重定向行为。**启动器不能靠「200」判断成功**，必须校验 `Content-Type` 是否 `application/json`。

鉴权要求与返回结构（源码 `routes/api.php` 全文实测取得，`ClosetController@getClosetData`）：

```php
Route::prefix('closet')->middleware('auth:oauth')->group(function () {
    Route::get('', 'ClosetController@getClosetData')
        ->middleware(['scope:Closet.Read,Closet.ReadWrite']);
    Route::middleware(['scope:Closet.ReadWrite'])->group(function () {
        Route::post('', 'ClosetController@add');
        Route::put('{tid}', 'ClosetController@rename');
        Route::delete('{tid}', 'ClosetController@remove');
    });
});
```

| 项 | 实测/源码结论 |
| --- | --- |
| 鉴权 | `auth:oauth` → **OAuth2 Bearer token**，非 JWT 网页会话、非 Yggdrasil accessToken |
| scope | 读：`Closet.Read` **或** `Closet.ReadWrite`；写：必须 `Closet.ReadWrite` |
| 分页 | 控制器为 `getClosetData`，与 `library` 同为分页返回，**支持 `page`**（同一分页器形态，顶层键同上表，无 `total`） |
| 范围 | **只返回当前 token 所属用户自己的衣柜**，与全站浏览无关 |

> 注：`/api/closet` 的**成功**响应体因无有效 token，**实测不到**。此处字段结构沿用原 A3 引用的官方手册（`type`/`hash`/`uploader`/`public`/`likes`）与源码 `getClosetData` 的 select 列表，**未经本次实测验证**，请按「文档明写 + 源码可证」采信，不要当作实测结果。

`/api/players` 同理：`auth:oauth` + `scope:Player.Read,Player.ReadWrite`，`GET` 取自己名下角色列表（`PlayerController@list`），可用来枚举自己的多角色；成功响应体同样**实测不到**（401）。

### D6. `/api/` 四组路由与 LittleSkin OpenAPI：确认无皮肤库浏览

- **OpenAPI 全文实测读取**（`https://manual.littlesk.in/littleskin-api.openapi3_1.yaml`，`200`，OpenAPI `3.1.0`，`info.version` `0.1.5`，`servers.url` `https://littleskin.cn/api`）。**path 总数 4 个，全部列出**：

| # | path | 方法 | 鉴权 / scope |
| --- | --- | --- | --- |
| 1 | `/announcements/` | GET | 无 |
| 2 | `/premium-verification/` | GET | `PremiumVerification.Read` |
| 3 | `/yggdrasil/sessionserver/session/minecraft/profiles` | GET | `Yggdrasil.PlayerProfiles.Read` |
| 4 | `/yggdrasil/authserver/oauth` | POST | `Yggdrasil.MinecraftToken.Create` |

  `components.securitySchemes` 仅一个 `LittleSkinOAuth2`（`oauth2`，`authorizationCode` 流，`authorizationUrl` = `https://littleskin.cn/oauth/authorize`，`tokenUrl` = `https://littleskin.cn/oauth/access_token`，`refreshUrl` = `https://littleskin.cn/api/auth/refresh`），scopes 5 个：`PremiumVerification.Read`、`Yggdrasil.PlayerProfiles.Read`、`Yggdrasil.MinecraftToken.Create`、`Yggdrasil.PlayerProfiles.Select`、`Yggdrasil.MinecraftToken.Create`。**无任何皮肤库浏览相关端点，确认。**

- **`routes/web.php` 全文实测读取**（9626 字节，`use App\Http\Middleware;` 起头）。`skinlib` 组原文：

```php
Route::prefix('skinlib')->name('skinlib.')->group(function () {
    Route::view('', 'skinlib.index')->name('home');          // ← 唯一的 view
    Route::get('info/{texture}', 'SkinlibController@info')->name('info');
    Route::get('show/{texture}', 'SkinlibController@show')->name('show');
    Route::get('list', 'SkinlibController@library')->name('list');

    Route::middleware(['authorize', 'verified'])->group(function () {
        Route::get('upload', 'SkinlibController@upload');
        Route::post('report', 'ReportController@submit');
    });
});
```

  **关键点（源码可证）**：
  - `Route::get('list', 'SkinlibController@library')` **不在任何 `middleware(['authorize', ...])` 组内** → 匿名可达，与实测一致。
  - 该组内**只有 `/skinlib` 用了 `Route::view`**（渲染 HTML 视图）；`info` / `show` / `list` 三个都是控制器方法 → 由控制器自行决定响应形态，实测前两个返 JSON、第三个返 HTML。
  - **不存在** `Route::get('skinlib/data'...)`、不存在 `list.json` 变体。实测 `/skinlib/data` 与 `/skinlib/list.json` 均 `404`，正文 `详细信息：The route skinlib/data could not be found.` / `The route skinlib/list.json could not be found.` —— 与源码一致（Laravel 路由不存在时的标准 404）。
  - `Route::any('', 'HomeController@apiRoot')` 在 `api.php` → 实测 `GET /api` 返回 `{"blessing_skin":"6.0.2","spec":0,"copyright":"由 Blessing Skin Server 强力驱动。","site_name":"LittleSkin"}`，**可用作站点能力探测**（判断是否 Blessing Skin 及其版本）。

- **`routes/api.php` 全文实测读取**（4062 字节）。前缀实测为 `user` / `players` / `closet` / `admin` 四组，**与 A3 描述一致，无皮肤库浏览**。`admin` 组需 `auth:oauth` + `role:admin`。

### D7. 是否是 Blessing Skin 通用能力（跨站复验）

为排除「LittleSkin 自己加的插件」，对另一独立站点复验（同一次实测）：

| 站点 | `GET /skinlib/list` | 结论 |
| --- | --- | --- |
| `littleskin.cn` | `200 application/json`，20 条 | 基准 |
| `mcskin.littleservice.cn` | **`200 application/json`**，20 条 | ✅ 同构 |
| `skin.prinzeugen.net` | `404`（该站路由不同/未启用） | ✗ |
| `skin.carson-14.dev` | 连接失败 | 实测不到 |
| `skin.endernon.cn` | 连接失败 | 实测不到 |
| `mcku.cn` | SSL 错误 | 实测不到 |

`mcskin.littleservice.cn` 的复验细节：

- `GET /api` → `{"blessing_skin":"6.0.2","spec":0,"copyright":"由 Blessing Skin Server 强力驱动。","site_name":"LittleSkin"}` → **确认是标准 Blessing Skin 6.0.2**。
- `GET /skinlib/list?sort=likes` → 顶层键与 LittleSkin **完全一致**（`current_page/data/first_page_url/from/next_page_url/path/per_page/prev_page_url/to`）。
- 有意思的是它首条也是 `tid=15433`「彩虹人【外国搬运】」、`uploader=14674` —— 说明该站与 LittleSkin 共享/镜像了同一份数据（**注意：这不是独立的第二实现，不能当作「多站互证」的强证据，只能证明「非 LittleSkin 单点魔改」**）。
- `GET /skinlib/info/15433` → `200 application/json`，回 `hash` 同为 `77021656...e8b2035`。

**判定**：`/skinlib/list` + `/skinlib/info/{tid}` 返 JSON 是 **Blessing Skin 上游行为**（源码 `library` / `info` 方法即如此，非 LittleSkin 私有插件），理论上**任何**运行标准 Blessing Skin 的站点都有。但**不是所有站都 404 之外还能用**——`skin.prinzeugen.net` 实测 404，说明仍有站未暴露或路径不同；接入任意自建站前必须逐一探测。

### D8. 第三方/其他皮肤库 API（实测）

| 服务 | 端点 | 实测结果 | 判定 |
| --- | --- | --- | --- |
| MineSkin | `GET https://api.mineskin.org/get/list` | `200 application/json`，但有 `"_deprecated":"use list by reference instead. see https://api.mineskin.org/openapi for details"`，返回 `skins[]` 含 `id`/`skinUuid`/`uuid`/`url` | ⚠️ **可用但已废弃**，不建议新接入 |
| MineSkin | `GET .../get/list?size=3` | `200`，条数**未变**（返回 876 字节，仍多条）→ `size` 参数**实测不生效** | ❌ 参数无效 |
| MineSkin | `GET .../v2/mineskin/list` | `404 {"success":false,"errors":[{"code":"not_found",...}]}` | ❌ 不存在 |
| NameMC | `GET https://namemc.com/api/skins` | `403`，正文 `<title>Just a moment...</title>` | ❌ **Cloudflare 人机验证拦截，程序不可消费** |
| NameMC | `GET https://api.namemc.com/profile/Notch` | `404 application/json` | ❌ 该路径不存在（未找到可用公开 API） |

- MineSkin 的 OpenAPI 文档页 `https://api.mineskin.org/openapi` 实测被重定向到 `https://rest.wiki/?https://api.mineskin.org/openapi.yml`（渲染出的是 OpenAPI Viewer 空壳，**正文实测不到**）。
- **NameMC 无可用公开 API**：其网页接口被 Cloudflare 挡死，本次**实测不到**任何可程序消费的 NameMC 端点。
- 结论：**外部没有比 Blessing Skin 自己的 `/skinlib/list` 更好的「中文皮肤库浏览」数据源**。MineSkin 是「上传渲染服务」而非「皮肤库」，且旧列表接口已弃用。

### D9. 最终判断：在启动器里浏览网络皮肤库，可行的实现路径

**共 3 条路径，按推荐度排序。**

#### 路径 1（推荐）：`/skinlib/list` + `/skinlib/info/{tid}` + `/textures/{hash}` —— 直连 JSON

```
列表：GET {site}/skinlib/list?filter=skin&sort=likes&page=1
详情：GET {site}/skinlib/info/{tid}          → 拿 hash
材质：GET {site}/textures/{hash}             → image/png
缩略：GET {site}/preview/{tid}               → image/webp（可跳过 info，省一跳）
头像：GET {site}/avatar/{uploader}           → image/webp
```

| 维度 | 评价 |
| --- | --- |
| 可行性 | **实测完全可用**，匿名、无鉴权、无 Cookie、无需 token |
| 数据完整度 | 列表 8 字段 + 详情 14 字段（含 `hash`/`phash`/`upload_at`），足够做浏览/搜索/排序/下载/相似检索 |
| 功能覆盖 | 分页 ✅ / 关键字搜索 ✅ / 按类型筛选 ✅ / 按上传者 ✅ / 按人气排序 ✅ / 原图下载 ✅ |
| **脆弱程度** | **中等偏低**。这是**未文档化**的端点（不在 OpenAPI、不在官方手册），升级可能变；但它同时是 Blessing Skin 前端自己调用的接口（`app.js` 路由表实锤），上游要改就得连自己前端一起改，**短期内不会无声消失**。风险点是 `sort` 无白名单（传错值 500）、`per_page` 硬编码 20、无 `total` |
| 主要缺点 | ① 每条要 2 次请求才能拿原图（除非接受 `preview` 缩略图）② 每页恒 20 条、无总数，抓大量数据成本高 ③ `sort` 只认 `time`/`likes`/`tid` |

**落地建议**：把 `/skinlib/list` 当「Blessing Skin 兼容层」实现，而非当官方 API：
1. 站点能力探测：先 `GET /api` 看 `blessing_skin` 字段，再试 `GET /skinlib/list?page=1` 且校验 `Content-Type: application/json` —— **两道都过才启用该站**（`skin.prinzeugen.net` 就是靠这道拦掉的）。
2. 全部参数与响应做**防御式解析**：顶层键只取实测存在的 9 个，**不要读 `total`/`last_page`**（不存在，读了会 NRE）；翻页用 `next_page_url == null` 终止。
3. `sort` 只允许 `time`/`likes`/`tid` 三值白名单，避免服务端 500。
4. UI 网格首屏用 `/preview/{tid}`（1 请求/条）；用户点「下载」时才 `info` → `textures`。
5. 结果本地缓存（实测单次 0.5–1.0 s，不缓存会很慢）+ 限速。

#### 路径 2：只做 `/skinlib/list` 元数据浏览（不下载皮肤）

同上但**不取原图**，只用列表字段做「皮肤名 + 上传者 + 点赞数 + 类型」的信息流/榜单展示，缩略图用 `/preview/{tid}`。

| 维度 | 评价 |
| --- | --- |
| 可行性 | **实测可用**，最省请求 |
| 脆弱程度 | 与路径 1 同级（同一端点），但**不碰 `hash`**，因此连「详情端点可能变」的风险都不承担 |
| 局限 | 拿不到原始 PNG，**不能真把皮肤装到角色上**，只能看 |

这是**最稳的最小可行方案**：如果产品目标只是「有个皮肤库可以逛」，路径 2 就够了；要「下载并应用」必须升到路径 1。

#### 路径 3：不依赖公开库，走「已知 UUID / 名字」直取

用 Yggdrasil `GET /api/yggdrasil/sessionserver/session/minecraft/profile/{uuid}`（匿名可读，见 A 节）解 `textures` 属性拿皮肤 URL；或 `POST /api/yggdrasil/api/profiles/minecraft` 按名批量换 UUID。
只能取**你已知的**角色皮肤，**不是浏览**，无法发现新皮肤。

| 维度 | 评价 |
| --- | --- |
| 可行性 | 实测本任务未复测（A 节已记为「文档明写」），但这是官方规范接口 |
| 脆弱程度 | **最低** —— 有 Yggdrasil 规范背书，跨站通用 |
| 局限 | 解决不了「浏览发现」这个需求 |

#### 已被证伪 / 不可行的路径（省得再试）

| 曾设想的路 | 实测结论 |
| --- | --- |
| 解析 `/skinlib` HTML 里的 `window.__INITIAL_STATE__` / `data-*` | ❌ **HTML 里零条数据**，SPA 空壳，`blessing-extra` 只有 `{"currentUid":null}` |
| `/skinlib/data`、`/skinlib/list.json`、`/skinlib/api` | ❌ 全部 `404`（`The route ... could not be found.`） |
| 靠 `Accept: text/html` 或 XHR 头切换出 JSON | ❌ 不需要——它**恒为 JSON**，无需内容协商 |
| `/api/` 下找皮肤库 | ❌ 确认只有 `user`/`players`/`closet`/`admin`，且要 OAuth2 Bearer |
| 靠 `/api/closet` 浏览全站 | ❌ 只返回**自己**衣柜，且 401 |
| NameMC API | ❌ Cloudflare 拦截（403 `Just a moment...`） |
| MineSkin 列表 | ⚠️ 仍可用但已标记 `_deprecated` |

#### 一句话总结

> **「在启动器里浏览网络皮肤库」是可行的，且不需要任何账号。** 核心是 `GET /skinlib/list`（公开 JSON、20/页、可搜索可筛选可排序）+ `GET /skinlib/info/{tid}`（取 `hash`）+ `GET /textures/{hash}`（原图）。这两跳都是 Blessing Skin 上游行为、是它自己前端在调的接口，**不是钻 HTML 的脆弱刮取**；代价是它未写进任何官方文档，属于「能用但要防着变」的兼容层，接入时必须做能力探测与防御式解析。