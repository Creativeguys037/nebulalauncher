using System;
using System.Text.Json;
using NebulaLauncher.Core.Models;

namespace NebulaLauncher.Skinning;

/// <summary>一次 profile 查询拿到的皮肤资源地址与体型。</summary>
public sealed record SkinTextures(string SkinUrl, string? CapeUrl, string Model);

/// <summary>
/// 角色能上传哪些材质，取自角色属性 <c>uploadableTextures</c>（逗号分隔 <c>skin</c>/<c>cape</c>）。
///
/// <para>
/// 语义按 authlib-injector 规范：属性<b>缺失或为空</b>＝完全不可上传；
/// 只有明确列出 <c>skin</c>/<c>cape</c> 才允许上传对应材质。
/// Mojang 官方角色不返回该属性，所以官方账户读出来永远是两项皆 false
/// ——这正是「官方只能换肤、不能传披风」在数据上的体现。
/// </para>
///
/// <para>
/// 单独成一个记录而不是往 <see cref="SkinTextures"/> 里塞字段：
/// 后者是按位置构造的既有类型，加参数会牵动全部既有调用点；
/// 而且「能上传什么」与「当前材质是什么」是两件不同的事，混在一起会让调用点读不出意图。
/// </para>
/// </summary>
public sealed record SkinCapabilities(bool CanUploadSkin, bool CanUploadCape)
{
    /// <summary>属性缺失或为空时的取值：什么都不允许。</summary>
    public static SkinCapabilities None { get; } = new(false, false);
}

/// <summary>
/// 从会话服务器拿皮肤的纯逻辑部分：拼地址、解 base64 包裹的 textures 载荷。
///
/// 网络与 PNG 解码不在这里（那部分要碰 Avalonia 与 HttpClient），
/// 这样地址规则与容错分支能被单测直接覆盖：缺字段、坏 base64、没有 SKIN 都必须回 <c>null</c>
/// 而不是抛异常把侧边栏卡打挂。
/// </summary>
public static class SkinProfileParser
{
    private const string MojangSessionServer = "https://sessionserver.mojang.com";

    private const string YggdrasilSuffix = "/yggdrasil";

    /// <summary>
    /// 会话服务器 profile 地址。离线账户没有皮肤，返回 <c>null</c>。
    /// 第三方验证账户的 API 根常以 <c>/yggdrasil</c> 结尾，会话服务挂在它的兄弟路径 <c>/sessionserver</c> 上。
    /// </summary>
    public static string? BuildProfileUrl(Account? account)
    {
        if (account is null || account.Type == AccountType.Offline || string.IsNullOrWhiteSpace(account.Uuid))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(account.AuthServerUrl))
        {
            return $"{MojangSessionServer}/session/minecraft/profile/{account.Uuid}?unsigned=false";
        }

        var root = account.AuthServerUrl.Trim().TrimEnd('/');
        if (root.EndsWith(YggdrasilSuffix, StringComparison.OrdinalIgnoreCase))
        {
            root = root[..^YggdrasilSuffix.Length];
        }

        return $"{root}/sessionserver/session/minecraft/profile/{account.Uuid}?unsigned=false";
    }

    /// <summary>
    /// 解析会话服务器的 profile 响应：<c>properties[]</c> 里 <c>name=textures</c> 的 value 是 base64 包裹的 JSON。
    /// 任何一层缺失都返回 <c>null</c>，由调用方回退到默认皮肤。
    /// </summary>
    public static SkinTextures? ParseProfile(string? json, string fallbackModel = "classic")
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("properties", out var properties)
                || properties.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var property in properties.EnumerateArray())
            {
                if (!property.TryGetProperty("name", out var name)
                    || name.ValueKind != JsonValueKind.String
                    || !string.Equals(name.GetString(), "textures", StringComparison.Ordinal))
                {
                    continue;
                }

                if (!property.TryGetProperty("value", out var value)
                    || value.ValueKind != JsonValueKind.String
                    || value.GetString() is not { Length: > 0 } payload)
                {
                    return null;
                }

                return ParseTexturesPayload(payload, fallbackModel);
            }

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>解析展开后的 textures 载荷。皮肤站的实现偶尔用 URL-safe base64，两种都试。</summary>
    public static SkinTextures? ParseTexturesPayload(string payload, string fallbackModel = "classic")
    {
        var bytes = DecodeBase64(payload);
        if (bytes is null)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(bytes);
            if (!document.RootElement.TryGetProperty("textures", out var textures)
                || textures.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (!textures.TryGetProperty("SKIN", out var skin)
                || !skin.TryGetProperty("url", out var skinUrl)
                || skinUrl.GetString() is not { Length: > 0 } url)
            {
                // 没有 SKIN 就是没有皮肤，兜底交给默认皮肤，不当成错误。
                return null;
            }

            var model = NormalizeModel(fallbackModel);
            if (skin.TryGetProperty("metadata", out var metadata)
                && metadata.TryGetProperty("model", out var modelElement)
                && modelElement.GetString() is { Length: > 0 } declared)
            {
                model = NormalizeModel(declared);
            }

            string? capeUrl = null;
            if (textures.TryGetProperty("CAPE", out var cape)
                && cape.TryGetProperty("url", out var capeUrlElement))
            {
                var raw = capeUrlElement.GetString();
                capeUrl = string.IsNullOrWhiteSpace(raw) ? null : raw;
            }

            return new SkinTextures(url, capeUrl, model);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>把 profile 里的 <c>default</c>/<c>slim</c> 收敛成账户模型用的 <c>classic</c>/<c>slim</c>。</summary>
    public static string NormalizeModel(string? model) =>
        string.Equals(model, "slim", StringComparison.OrdinalIgnoreCase) ? "slim" : "classic";

    /// <summary>
    /// 解析角色能上传哪些材质：读 <c>properties[]</c> 里 <c>name=uploadableTextures</c> 的 value。
    ///
    /// <para>
    /// 属性缺失、为空、或整份响应根本不是合法 profile，一律返回
    /// <see cref="SkinCapabilities.None"/>（UI 据此把上传按钮藏起来），不抛异常。
    /// 值里出现未知类型（如 <c>elytra</c>）时忽略它，不因此判定整条属性无效。
    /// </para>
    /// </summary>
    public static SkinCapabilities ParseCapabilities(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return SkinCapabilities.None;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("properties", out var properties)
                || properties.ValueKind != JsonValueKind.Array)
            {
                return SkinCapabilities.None;
            }

            foreach (var property in properties.EnumerateArray())
            {
                if (property.ValueKind != JsonValueKind.Object
                    || !property.TryGetProperty("name", out var name)
                    || name.ValueKind != JsonValueKind.String
                    || !string.Equals(name.GetString(), "uploadableTextures", StringComparison.Ordinal))
                {
                    continue;
                }

                if (!property.TryGetProperty("value", out var value)
                    || value.ValueKind != JsonValueKind.String)
                {
                    return SkinCapabilities.None;
                }

                return ParseUploadableTextures(value.GetString());
            }

            return SkinCapabilities.None;
        }
        catch (JsonException)
        {
            return SkinCapabilities.None;
        }
    }

    /// <summary>把 <c>uploadableTextures</c> 的逗号分隔值折成两项布尔。空白项与未知类型都跳过。</summary>
    public static SkinCapabilities ParseUploadableTextures(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return SkinCapabilities.None;
        }

        var skin = false;
        var cape = false;
        foreach (var item in value.Split(','))
        {
            var trimmed = item.Trim();
            if (string.Equals(trimmed, "skin", StringComparison.OrdinalIgnoreCase))
            {
                skin = true;
            }
            else if (string.Equals(trimmed, "cape", StringComparison.OrdinalIgnoreCase))
            {
                cape = true;
            }
        }

        return new SkinCapabilities(skin, cape);
    }

    private static byte[]? DecodeBase64(string payload)
    {
        var normalized = payload.Replace('-', '+').Replace('_', '/');
        var padding = normalized.Length % 4;
        if (padding is 2)
        {
            normalized += "==";
        }
        else if (padding is 3)
        {
            normalized += "=";
        }
        else if (padding is 1)
        {
            return null;
        }

        try
        {
            return Convert.FromBase64String(normalized);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
