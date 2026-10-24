using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace NebulaLauncher.Core.Serialization;

/// <summary>
/// <see cref="JsonNode"/> 落盘用的统一口径。
///
/// 为什么不能就地 <c>new JsonSerializerOptions { WriteIndented = true }</c>：
/// <c>JsonNode.ToJsonString(options)</c> 内部对 <see cref="JsonValue"/> 走的是
/// <c>JsonSerializer.Serialize(writer, value, options)</c>，那一步要 <see cref="JsonSerializerOptions.TypeInfoResolver"/>
/// 给出元数据。裁剪发布的默认口径没有反射解析器，于是 <c>ToJsonString</c> 抛
/// <c>NotSupportedException</c>——表现为「加载器 profile 写不进磁盘」或「标记文件写不出来」，
/// 而且异常被上层 catch 吞掉，只在日志里留一行（见 #26）。
/// </summary>
internal static class JsonNodeWrite
{
    /// <summary>带缩进的写盘口径。所有需要人眼可读的 version json 都用它。</summary>
    internal static JsonSerializerOptions Indented { get; } = new()
    {
        WriteIndented = true,
        TypeInfoResolver = JsonNodeJsonContext.Default,
    };
}

/// <summary>
/// <see cref="JsonNode"/> 家族的 Source Generator 上下文。
///
/// 登记抽象基类与两个容器即可：<see cref="JsonValue"/> 的具体值类型由 STJ 内置转换器处理，
/// 不走反射元数据。
/// </summary>
[JsonSerializable(typeof(JsonNode))]
[JsonSerializable(typeof(JsonObject))]
[JsonSerializable(typeof(JsonArray))]
internal sealed partial class JsonNodeJsonContext : JsonSerializerContext
{
}
