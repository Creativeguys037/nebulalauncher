using System;
using System.Text.Json;
using NebulaLauncher.Core.Models;

namespace NebulaLauncher.Skinning;

/// <summary>一次 profile 查询拿到的皮肤资源地址与体型。</summary>
public sealed record SkinTextures(string SkinUrl, string? CapeUrl, string Model);

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
