using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace NebulaLauncher.Core.Serialization;

/// <summary>
/// 从服务自己那份 <see cref="JsonSerializerOptions"/> 里取编译期元数据的入口。
///
/// <para>
/// 为什么不用 <c>context.Default.Type</c>：context 的 <c>Default</c> 用的是生成器口径，
/// 而各服务手上那份 options 带着自己的命名策略（camelCase）、大小写不敏感、枚举字符串转换器、
/// 注释跳过等开关。<c>context.Default.Type</c> 会把这些口径全部丢掉，落盘字段名从
/// <c>camelCase</c> 变成 <c>PascalCase</c>，旧文件也读不回来（默认大小写敏感）。
/// </para>
///
/// <para>
/// <c>options.GetTypeInfo(typeof(T))</c> 返回的是「按这份 options 固化好的元数据」：
/// 命名策略与转换器都与原来完全一致，而且不经过反射。元数据按 (options, 类型) 缓存，
/// 反复调用不会重复解析。
/// </para>
///
/// <para>
/// 用扩展方法而不是在每个类里写一行强转，是为了让调用点保持
/// <c>JsonSerializer.Serialize(x, JsonOptions.Of&lt;AppSettings&gt;())</c> 这种一眼能对回原参的形状。
/// </para>
/// </summary>
internal static class JsonMeta
{
    /// <summary>取 <typeparamref name="T"/> 在这份 options 下的元数据。</summary>
    internal static JsonTypeInfo<T> Of<T>(this JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return (JsonTypeInfo<T>)options.GetTypeInfo(typeof(T));
    }
}
