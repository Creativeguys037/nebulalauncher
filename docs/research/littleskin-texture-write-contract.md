# 皮肤站材质写入的真实契约（littleskin / Blessing Skin）

> 一句话结论：littleskin 的材质写入走 Blessing Skin 的 OAuth JSON API，`PUT /api/players/{uuid}/textures` 接收的是**已存在材质的 tid**（`skin` / `cape` 字段），不是 PNG 文件；OAuth API 没有新建材质的端点，上传 PNG 只存在于 web 路由 `/skinlib`。

- 依据：一手来源——Blessing Skin Server `dev` 分支源码（`routes/api.php`、`app/Http/Controllers/PlayerController.php`、`ClosetController.php`、`routes/web.php`、`app/helpers.php`）、LittleSkin 官方手册与 OIDC 发现文档、对 littleskin.cn 与 api.minecraftservices.com 的只读实测。2026-11-01 采样。
- 范围：OAuth JSON API 的材质读写端点、请求体与响应形态、鉴权 scope、PNG 上传是否可达。
- 未做：没有真 token，成功响应、429、皮肤是否立刻可见均未实测。
- 与既有笔记的关系：`docs/research/littleskin-and-skin-api.md` 的 A2.1 把 Yggdrasil `PUT /api/yggdrasil/user/profile/{uuid}/{textureType}` 当作既成事实，那是规范推断；票 #53 的实测显示 littleskin 上该路径一律 404。本笔记补「真实存在的那条链路」。
- 标签：描述「源码明写」与「实测」；推断单独标出。

---

## 1. 路由与 scope（源码明写）

`routes/api.php`（dev 分支，2026-11-01 读取）：

```php
Route::prefix('players')->middleware('auth:oauth')->group(function () {
    Route::get('', 'PlayerController@list')->middleware(['scope:Player.Read,Player.ReadWrite']);

    Route::middleware(['scope:Player.ReadWrite'])->group(function () {
        Route::post('', 'PlayerController@add');
        Route::delete('{player}', 'PlayerController@delete');
        Route::put('{player}/name', 'PlayerController@rename');
        Route::put('{player}/textures', 'PlayerController@setTexture');
        Route::delete('{player}/textures', 'PlayerController@clearTexture');
    });
});
```

- 四组前缀只有 `user`、`players`、`closet`、`admin`，**没有 `skinlib` / `texture` 组**：OAuth JSON API 不提供材质上传。
- 写材质要求 `Player.ReadWrite`；`auth:oauth` 未通过时返回 `401 application/json {"message":"Unauthenticated."}`（实测，见 §4）。

## 2. setTexture 的请求与响应（源码明写）

`PlayerController@setTexture`：

- 只读两个输入：`$request->input('skin')` 与 `$request->input('cape')`，值是**材质 id（tid）**，不是文件。
- 逐个校验：`Texture::find($tid)` 必须存在；该 tid 必须已在当前用户的衣柜里（`$user->closet()->where('texture_tid', $tid)`），否则拒绝。
- 成功：`return json(trans('user.player.set.success', ['name' => $player->name]), 0, $player->toArray());`
- `json()` helper（`app/helpers.php`）对三参数形态返回 `{"code":<status>,"message":<text>,"data":<array>}`，成功时 `code` 为 `0`。
- 清除：`DELETE {player}/textures`，`clearTexture` 读 `type`（数组）与同名键。

即请求形如 `PUT /api/players/{uuid}/textures` + `{"skin": 123}`（无披风时省略 `cape`）。

## 3. PNG 上传在哪（源码明写）

- `routes/web.php`：`Route::prefix('skinlib')` 下 `Route::post('', 'SkinlibController@handleUpload')` —— **web 路由，session 鉴权**，不是 `/api`。
- `ClosetController@add`（`POST /api/closet`）的入参是 `{tid: integer, name: string}`，只能把**已存在**的材质加进衣柜，同样不能新建材质。

结论：一个只拿 OAuth token 的第三方启动器，能「把站内已有材质套到自己角色上」，不能「上传一张新 PNG 到站内」。

## 4. 实测（2026-11-01，无凭据）

| 请求 | 结果 |
| --- | --- |
| `PUT https://littleskin.cn/api/players/aa6fe8bcd2884cca948737e5ee2bbf70/textures`（无 token） | `401` `application/json` `{"message":"Unauthenticated."}` |
| 同上（`Authorization: Bearer invalidtoken`） | 同上 |
| `DELETE` 同路径（假 Bearer） | 同上 |
| `POST https://api.minecraftservices.com/minecraft/profile/skins`（完整 multipart：`variant` + `file`，无 token） | `401` `application/json` `{ "path" : "/minecraft/profile/skins" }` |
| 同上（空 body 或非 multipart，无 token / 假 Bearer） | `403` `text/html`（XHTML 错误页） |

最后两行说明：Mojang 上传端点的 403 只在请求体不是 multipart 时出现，带正常 multipart 时未授权是 401。`MojangSkinClient` 的 401 分支与真实形态一致。

## 5. 未解的矛盾：`Player.ReadWrite` 能不能拿到

- `https://open.littleskin.cn/.well-known/openid-configuration` 的 `scopes_supported` = `openid`、`offline_access`、`Yggdrasil.PlayerProfiles.Select`、`Yggdrasil.PlayerProfiles.Read`、`Yggdrasil.MinecraftToken.Create`、`Yggdrasil.Server.Join`，**没有** `Player.ReadWrite`（2026-11-01 实测）。
- `https://manual.littleskin.cn/advanced/api` 仍把 `Player.ReadWrite` 列为「请求部分 API 时可能需要请求用户授予的权限」（2026-11-01 实测页面原文）。
- 未做：拿真 token 试一次 `PUT /api/players/{uuid}/textures`。这是唯一能判定的方法，需要的正是票 #53/#58 缺的凭据。

## 6. 对实现的含义

1. 皮肤站换肤是两步：`POST /api/closet {tid, name}` → `PUT /api/players/{uuid}/textures {skin: tid}`；响应是 `{"code","message","data"}`，不是 204。
2. 需要 OAuth access token 与 `Player.ReadWrite`；现有 `Account` 模型只有 Yggdrasil 的 `AccessToken` / `RefreshToken`，接这条链路要另存 OAuth token。
3. 「上传自己的 PNG」在当前 OAuth API 下无解；若产品要求上传，只能走 web session 或另找站方接口。

## 来源

- Blessing Skin Server 源码：<https://github.com/bs-community/blessing-skin-server/blob/dev/routes/api.php>、<https://github.com/bs-community/blessing-skin-server/blob/dev/app/Http/Controllers/PlayerController.php>、<https://github.com/bs-community/blessing-skin-server/blob/dev/app/Http/Controllers/ClosetController.php>、<https://github.com/bs-community/blessing-skin-server/blob/dev/routes/web.php>、<https://github.com/bs-community/blessing-skin-server/blob/dev/app/helpers.php>
- LittleSkin 手册·API：<https://manual.littleskin.cn/advanced/api>
- LittleSkin OIDC 发现：<https://open.littleskin.cn/.well-known/openid-configuration>
- Minecraft Services 皮肤端点：<https://api.minecraftservices.com/minecraft/profile/skins>
- 既有笔记：`docs/research/littleskin-and-skin-api.md`（A2.1 为规范推断）
