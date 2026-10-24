using System.Globalization;

namespace NebulaLauncher.Core.Launching;

/// <summary>
/// 启动参数去重与覆盖（移植 PCL2 <c>Modules/Minecraft/ModLaunch.vb:1617 DeduplicateJavaArguments</c>）。
///
/// 为什么必须有这一步：CmlLib 4.1 只把 <c>MLaunchOption.ExtraGameArguments</c> 原样拼在自建参数之后，
/// 不做任何去重。用户在设置里填 <c>--width 1280</c>、启动器又按窗口设置生成一条 <c>--width 854</c>，
/// 实测拼出来是 <c>--width 854 --width 1280</c>——MC 的解析器遇到重复键会<b>两条都判无效</b>，
/// 用户看到的是「填了没用」；同样的重复出现在 <c>--uuid</c> 这类关键参数上就是启动即崩。
///
/// 两种参数的语义<b>刻意相反</b>，不是笔误：
/// <list type="bullet">
/// <item>
/// <b>游戏参数：后出现者覆盖。</b>MC 的 <c>--key value</c> 与顺序无关，重复即双双失效；
/// 后写的那条才是用户当下的意图，所以用新值替换旧值、整个列表里只留一条。
/// </item>
/// <item>
/// <b>JVM 参数：不覆盖。</b>JVM 对重复的 <c>-XX</c> 取最后一个，但 <c>-Xmx</c> / <c>-javaagent</c> /
/// <c>-cp</c> 这些键的<b>相对位置</b>带语义（谁在谁前面决定谁生效）。若把后出现的值搬到先出现的位置，
/// 顺序就变了，JVM 的最终取值可能反过来——PCL2 注释写的是「覆盖会反噬设置页」。
/// 所以这里只丢弃「键和值都完全相同」的重余项，不同值原样保留，交给 JVM 自己裁决。
/// </item>
/// </list>
///
/// 单一 token 与「键 + 值」对必须分开处理：<c>-Xmx2G</c>、<c>-XX:+UseG1GC</c>、<c>-javaagent:a.jar</c>
/// 都是自成一体的参数，没有配对的值；把它们当键值对会把下一条参数吃成自己的值。
/// </summary>
public static class LaunchArgumentDeduplicator
{
    /// <summary>
    /// 唯一不参与「后出现者覆盖」的游戏参数：OptiFine / Forge 允许同一命令行挂多个 tweakClass，
    /// 覆盖掉其中一个会让另一套补丁整体失效。
    /// </summary>
    private const string TweakedClassOption = "--tweakClass";

    /// <summary>
    /// 按 PCL2 语义去重。纯函数：不读配置、不写日志，同样的输入永远得到同样的输出。
    /// </summary>
    /// <param name="arguments">已切分好的参数列表（切分必须在变量替换之后，否则键比较的是占位符）。</param>
    /// <param name="isJvmArguments">
    /// true 表示这是 JVM 参数（不覆盖、只去完全重复）；false 表示游戏参数（后出现者覆盖）。
    /// </param>
    public static List<string> Deduplicate(IReadOnlyList<string> arguments, bool isJvmArguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        var result = new List<string>(arguments.Count);
        var index = 0;

        while (index < arguments.Count)
        {
            var key = arguments[index];

            if (IsStandaloneArgument(arguments, index))
            {
                index++;

                // 单 token 参数只认「整串相同」：-Xmx2G 与 -Xmx4G 是两个不同的开关值，
                // 谁覆盖谁都没有依据，交给 JVM 按顺序取最后一个。
                if (result.Contains(key))
                {
                    continue;
                }

                result.Add(key);
                continue;
            }

            var value = arguments[index + 1];
            index += 2;

            var handled = false;

            // 向前找相同的键。键值对在 result 里是交错存放的，这里沿 PCL2 的做法逐个位置比对。
            for (var j = 0; j + 1 < result.Count; j++)
            {
                if (result[j] != key)
                {
                    continue;
                }

                if (!isJvmArguments && key != TweakedClassOption)
                {
                    // 游戏参数：用新值覆盖旧值，且不再追加第二份。
                    result[j + 1] = value;
                    handled = true;
                    break;
                }

                // JVM 参数与 --tweakClass：键和值都相同才是真重复；值不同则留着，
                // 后续查找继续（更早的位置可能存着同键同值的那一份）。
                if (result[j + 1] == value)
                {
                    handled = true;
                    break;
                }
            }

            if (handled)
            {
                continue;
            }

            result.Add(key);
            result.Add(value);
        }

        return result;
    }

    /// <summary>
    /// 移除由启动器属性接管的服务器参数。
    ///
    /// CmlLib 会根据 <c>MLaunchOption.ServerIp</c>/<c>ServerPort</c> 生成一组
    /// <c>--server</c>/<c>--port</c> 或 <c>--quickPlayMultiplayer</c>。如果实例或服务器页
    /// 同时提供了 <c>ServerAddress</c>，旧值会让 CmlLib 忽略指定地址或与 Quick Play 冲突。
    /// 显式地址优先，与 PCL 的「自动进服最后追加再去重」保持一致。
    /// 没有显式地址时调用方不应使用此方法，用户自定义的服务器参数要原样保留。
    /// </summary>
    public static List<string> RemoveServerArguments(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        var result = new List<string>(arguments.Count);
        for (var index = 0; index < arguments.Count; index++)
        {
            var key = arguments[index];
            if (key is "--server" or "--port" or "--quickPlayMultiplayer")
            {
                if (!IsStandaloneArgument(arguments, index))
                {
                    index++;
                }

                continue;
            }

            if (key.StartsWith("--server=", StringComparison.Ordinal) ||
                key.StartsWith("--port=", StringComparison.Ordinal) ||
                key.StartsWith("--quickPlayMultiplayer=", StringComparison.Ordinal))
            {
                continue;
            }

            result.Add(key);
        }

        return result;
    }

    /// <summary>
    /// 把 CmlLib 自己也会生成的窗口类参数（<c>--width</c> / <c>--height</c> / <c>--fullscreen</c> /
    /// <c>--demo</c>）从额外参数里摘出来，由调用方写回 <see cref="CmlLib.Core.ProcessBuilder.MLaunchOption"/>
    /// 的对应属性。
    ///
    /// 为什么不能只是「留着」：这四类键 CmlLib 一定会按 <c>MLaunchOption</c> 的属性再生成一遍，
    /// 与额外参数里的同键并存就是上面说的「两条都失效」。摘掉并写回属性，才是「后出现者覆盖」在
    /// CmlLib 结构下的等价实现。
    ///
    /// 不在这里猜 <c>--width</c> 缺值、<c>--width abc</c> 这类坏写法：原样留在返回的列表里，
    /// 让游戏自己报参数错误——启动器替用户猜一个数字只会让问题更难被发现。
    /// </summary>
    /// <param name="arguments">已经过 <see cref="Deduplicate"/> 的游戏参数。</param>
    public static GameArgumentOverride AbsorbKnownOptions(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        var remaining = new List<string>(arguments.Count);
        int? width = null;
        int? height = null;
        bool? fullscreen = null;
        bool? demo = null;

        for (var index = 0; index < arguments.Count; index++)
        {
            var key = arguments[index];
            var standalone = IsStandaloneArgument(arguments, index);

            if ((key == "--width" || key == "--height") && !standalone &&
                int.TryParse(arguments[index + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var numeric))
            {
                if (key == "--width")
                {
                    width = numeric;
                }
                else
                {
                    height = numeric;
                }

                index++;
                continue;
            }

            if (standalone && key == "--fullscreen")
            {
                fullscreen = true;
                continue;
            }

            if (standalone && key == "--demo")
            {
                demo = true;
                continue;
            }

            remaining.Add(key);
        }

        return new GameArgumentOverride(remaining, width, height, fullscreen, demo);
    }

    /// <summary>
    /// 判断当前位置的 token 是否「自成一个参数」（不跟值）。
    ///
    /// 启发式照抄 PCL2：后面那条以 <c>-</c> 开头、且 <c>-</c> 之后不是非零数字时，它就是新参数，
    /// 而不是当前参数的值。这样 <c>-xPos 23</c> 是键值对（<c>23</c> 不以 <c>-</c> 开头），
    /// <c>-xPos -50</c> 也是键值对（<c>-</c> 后是 <c>50</c>），而 <c>--fullscreen --demo</c> 是两个开关。
    ///
    /// 局限与 PCL2 相同：<c>-xPos 0</c> 的 <c>0</c> 会被当成新参数——<c>Val</c> 判 0 即视为非数字。
    /// 这是源码里就有的取舍，改掉会让 <c>-Xmx2G -Xms2G</c> 这类单 token 序列被误读，所以照抄。
    /// </summary>
    private static bool IsStandaloneArgument(IReadOnlyList<string> arguments, int index)
    {
        var token = arguments[index];

        // 不以 - 开头的一定是别人的值（或一个裸文件名），不可能再开一对键值。
        if (!token.StartsWith('-'))
        {
            return true;
        }

        if (index + 1 >= arguments.Count)
        {
            return true;
        }

        var next = arguments[index + 1];
        return next.StartsWith('-') && LeadingNumber(next.AsSpan(1)) == 0;
    }

    /// <summary>
    /// VB <c>Val()</c> 的等价物：解析字符串开头能被识别的数值，识别不到就是 0。
    /// 只需要区分「0」与「非 0」，因为调用方拿它当「这是数字还是新参数」的判据。
    /// </summary>
    private static double LeadingNumber(ReadOnlySpan<char> text)
    {
        var index = 0;

        while (index < text.Length && char.IsWhiteSpace(text[index]))
        {
            index++;
        }

        var start = index;

        if (index < text.Length && (text[index] == '+' || text[index] == '-'))
        {
            index++;
        }

        var digits = 0;

        while (index < text.Length && char.IsAsciiDigit(text[index]))
        {
            index++;
            digits++;
        }

        if (index < text.Length && text[index] == '.')
        {
            index++;

            while (index < text.Length && char.IsAsciiDigit(text[index]))
            {
                index++;
                digits++;
            }
        }

        if (digits == 0)
        {
            return 0;
        }

        // 指数部分允许失败：Val("1e") 也是 1，后面的 e 不属于数字。
        if (index < text.Length && text[index] is 'e' or 'E')
        {
            var exponentStart = index;
            index++;

            if (index < text.Length && (text[index] == '+' || text[index] == '-'))
            {
                index++;
            }

            var exponentDigits = 0;

            while (index < text.Length && char.IsAsciiDigit(text[index]))
            {
                index++;
                exponentDigits++;
            }

            if (exponentDigits == 0)
            {
                index = exponentStart;
            }
        }

        return double.TryParse(text[start..index], NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : 0;
    }
}

/// <summary>
/// 从额外游戏参数里抽取窗口类配置的结果。
/// </summary>
/// <param name="Remaining">未能映射到 <c>MLaunchOption</c> 属性、需要原样追加的参数。</param>
/// <param name="WindowWidth">用户指定且合法的窗口宽度；null 表示用户没指定或写法坏到无法解析。</param>
/// <param name="WindowHeight">用户指定且合法的窗口高度。</param>
/// <param name="Fullscreen">用户写了 <c>--fullscreen</c> 时为 true。</param>
/// <param name="Demo">用户写了 <c>--demo</c> 时为 true。</param>
public sealed record GameArgumentOverride(
    IReadOnlyList<string> Remaining,
    int? WindowWidth,
    int? WindowHeight,
    bool? Fullscreen,
    bool? Demo);
