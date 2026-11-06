namespace NebulaLauncher.Core.Instances;

/// <summary>
/// 实例「启动调优」两项（内存与 Java）的判定规则：合法区间、推荐值、错误文案。
///
/// ## 为什么这些规则在 Core 而不在界面
/// 同一份规则有三个消费点：设置页（全局默认值）、版本页详情面板（实例覆盖值）、
/// 启动前的预检（<c>PreflightChecker</c>）。三处各写一份数字时，用户会遇到
/// 「详情页接受 512，设置页说不合法」这种自相矛盾的反馈，而它不会抛异常、不会被测试发现，
/// 只能等用户报上来。这里把「什么算合法」「推荐多少」收成一处的纯函数，界面只负责渲染结论。
///
/// ## 为什么上限是「可分配内存」而不是固定 16384
/// 票 #88 要求内存输入「带推荐值与上限提示」。上限写死 16384 会带来一个错的方向：
/// 8 GB 机器上用户按提示的上限填 16 GB，游戏必然起不来，而界面还显示「在合理区间内」。
/// 所以上限取本机可分配内存（探测不到时才回落到 <see cref="FallbackMaxMemoryMb"/>）。
///
/// 这与设置页滑杆的口径不完全一致（那边允许拖到 16384 再靠超额提示拦住），
/// 差异是刻意的：滑杆是连续控件，用户拖过头需要看得见的橙色警告；
/// 详情页是精确输入框，超出可分配内存的值应当直接判为非法、不写盘。
/// </summary>
public static class InstanceLaunchTuning
{
    /// <summary>
    /// 内存下限（MB）。低于它连原版客户端都起不来，属于「填了等于没填」的值。
    /// 与 <c>MemoryAdvisor.MinSaneMaxMemoryMb</c> 取同一个数：推荐值永远不会低于它，
    /// 那么手动值允许比推荐值更小就没有意义——两者必须是同一个下限。
    /// </summary>
    public const int MinMemoryMb = 512;

    /// <summary>
    /// 探测不到可分配内存时的上限兜底（MB）。
    /// 与设置页 <c>MaxMemoryMaximum</c> 同值：探测失败时不能给一个比设置页更松的口径。
    /// </summary>
    public const int FallbackMaxMemoryMb = 16384;

    /// <summary>整数输入的允许长度上限。挡住误粘贴造成的超长串解析成负数或溢出。</summary>
    public const int MemoryTextMaxLength = 9;

    /// <summary>
    /// 一次内存输入的解析结果。
    ///
    /// 为什么用结果对象而不是「解析失败返回 -1」：界面要区分三种失败并给出不同的红字
    /// （空、不是数字、超出区间），压成一个错误码后界面还得再猜一遍是哪一种。
    /// </summary>
    /// <param name="IsValid">是否可以直接写盘。</param>
    /// <param name="MemoryMb">解析出的值；<paramref name="IsValid"/> 为 false 时无意义。</param>
    /// <param name="Error">就地显示在字段下方的中文原因；合法时为 null。</param>
    public readonly record struct MemoryParseResult(bool IsValid, int MemoryMb, string? Error)
    {
        public static MemoryParseResult Ok(int memoryMb) => new(true, memoryMb, null);

        public static MemoryParseResult Fail(string error) => new(false, 0, error);
    }

    /// <summary>
    /// 解析并校验一次内存输入。
    ///
    /// 三条判据，顺序不能换：先判空（空与「不是数字」的引导不同），再判能否解析成整数
    /// （用户可能粘贴了 <c>4G</c> / <c>4,096</c>），最后判区间。反过来先判区间的话，
    /// 空输入会走到「超出上限」那条红字上，把人误导到完全相反的方向。
    /// </summary>
    /// <param name="text">输入框原文，允许首尾空白。</param>
    /// <param name="upperBoundMb">
    /// 上限（MB）。传 0 或负数表示探测不到可分配内存，此时改用
    /// <see cref="FallbackMaxMemoryMb"/>，而不是把任何值都判成超限。
    /// </param>
    public static MemoryParseResult ParseMemory(string? text, int upperBoundMb)
    {
        var trimmed = (text ?? string.Empty).Trim();

        if (trimmed.Length == 0)
        {
            return MemoryParseResult.Fail("请填写分配给游戏的内存。");
        }

        if (trimmed.Length > MemoryTextMaxLength
            || !int.TryParse(trimmed, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var value))
        {
            // 只认纯数字：NumberStyles.None 会挡掉 "4,096" 的千分位与 "+4096" 的正号，
            // 这两种写法都不是用户会在这种输入框里想要的表达。
            return MemoryParseResult.Fail("请只填整数，单位是 MB（例如 4096）。");
        }

        var upper = EffectiveUpperBound(upperBoundMb);

        if (value < MinMemoryMb)
        {
            return MemoryParseResult.Fail($"不能小于 {MinMemoryMb} MB，再低游戏起不来。");
        }

        if (value > upper)
        {
            return MemoryParseResult.Fail($"不能超过 {upper} MB（本机可分配内存）。");
        }

        return MemoryParseResult.Ok(value);
    }

    /// <summary>
    /// 实际生效的上限。<paramref name="detectedUpperBoundMb"/> 探测失败（&lt;= 0）时回落到
    /// <see cref="FallbackMaxMemoryMb"/>，并且永远不低于 <see cref="MinMemoryMb"/>——
    /// 两个越界的探测结果都会让「区间」这个前提失效，退化成「怎么填都非法」是最坏的结果。
    /// </summary>
    public static int EffectiveUpperBound(int detectedUpperBoundMb)
    {
        var upper = detectedUpperBoundMb > 0 ? detectedUpperBoundMb : FallbackMaxMemoryMb;

        return Math.Max(upper, MinMemoryMb);
    }

    /// <summary>
    /// 把一个已存盘的内存值夹回合法区间。用于「打开面板时回填」：
    /// 手改过 <c>instance.json</c> 或换了台内存更小的机器时，旧值可能已经越界，
    /// 直接显示出来会让用户一保存就被判非法，而他并没有改过任何东西。
    /// </summary>
    public static int ClampMemory(int memoryMb, int detectedUpperBoundMb)
    {
        var upper = EffectiveUpperBound(detectedUpperBoundMb);

        return Math.Clamp(memoryMb, MinMemoryMb, upper);
    }

    /// <summary>
    /// Java 路径的就地校验。
    ///
    /// 判据只有一条：这个路径能不能被解成真实存在的 Java 可执行文件。
    /// 展开规则交给 <c>JavaService.ResolveJavaExecutable</c>（它认 <c>java.exe</c>、
    /// <c>javaw.exe</c>、JDK 根目录与 bin 目录四种写法）——详情面板与设置页的「添加 Java」
    /// 必须接受同一批写法，否则用户在设置里能加的路径在详情里会被判非法。
    ///
    /// **不在这里跑 <c>java -version</c>**：那是一次子进程调用，几十到几百毫秒，
    /// 放进「每次按键都校验」的输入框里会把 UI 线程拖住。路径存在性已经能拦住
    /// 绝大多数输错（少打一层 bin、复制到了别的程序），版本是否合适由启动期预检负责。
    /// </summary>
    /// <param name="path">输入框原文。</param>
    /// <param name="resolve">
    /// 路径展开函数，通常是 <c>JavaService.ResolveJavaExecutable</c> 的方法组。
    /// 做成参数是为了让测试不必依赖真实磁盘布局。
    /// </param>
    /// <returns>合法时为 null，否则是就地显示的中文原因。</returns>
    public static string? ValidateJavaPath(string? path, Func<string?, string?> resolve)
    {
        var trimmed = (path ?? string.Empty).Trim();

        if (trimmed.Length == 0)
        {
            // 空表示「交给自动选择」：这不是错误，详情面板会把它存成 null。
            return null;
        }

        ArgumentNullException.ThrowIfNull(resolve);

        return resolve(trimmed) is null
            ? "这个路径下没有可用的 java（需要 java.exe 或 JDK 根目录）。"
            : null;
    }

    /// <summary>
    /// 内存摘要文案，用于列表行的元信息（保存后要能立刻看到变化）。
    /// null 表示跟随全局，此时必须把全局值说出来，否则「跟随全局」看起来等于「没设」。
    /// </summary>
    public static string DescribeMemory(int? instanceMemoryMb, int globalMemoryMb)
    {
        if (instanceMemoryMb is { } own)
        {
            return $"{own} MB";
        }

        // 跟随全局时把生效值一起带上：用户在详情里看到「跟随全局 4096 MB」才知道实际会分配多少。
        return globalMemoryMb > 0
            ? $"跟随全局 {globalMemoryMb} MB"
            : "跟随全局";
    }

    /// <summary>
    /// Java 摘要文案。与 <see cref="DescribeMemory"/> 同一口径：null 要说出会用什么，
    /// 而不是留空。
    /// </summary>
    public static string DescribeJava(string? instanceJavaPath, string? globalJavaPath)
    {
        if (!string.IsNullOrWhiteSpace(instanceJavaPath))
        {
            return ShortenJavaPath(instanceJavaPath!);
        }

        return string.IsNullOrWhiteSpace(globalJavaPath)
            ? "自动选择"
            : $"全局：{ShortenJavaPath(globalJavaPath!)}";
    }

    /// <summary>
    /// 把 Java 全路径压成 <c>jdk-17\bin\java.exe</c> 这种末两段形式，供界面在窄处显示。
    /// 完整路径由详情面板的字段显示；这里只解决「一行放不下全路径」的问题。
    /// </summary>
    public static string DescribeJavaPathShort(string? path)
    {
        // 空值显示成「未设置」而不是空串：界面上留白会被读成渲染失败。
        return string.IsNullOrWhiteSpace(path) ? "未设置" : ShortenJavaPath(path!);
    }

    /// <summary>
    /// 把 Java 全路径压成 <c>jdk-17\bin\java.exe</c> 这种末两段形式。
    /// 列表行的元信息只有一行宽度，全路径会把版本号挤掉；完整路径由详情面板显示。
    /// </summary>
    internal static string ShortenJavaPath(string path)
    {
        try
        {
            var trimmed = path.Trim();
            var parent = Path.GetDirectoryName(trimmed);
            var bin = parent is null ? null : Path.GetFileName(parent);
            var file = Path.GetFileName(trimmed);

            if (string.IsNullOrEmpty(file))
            {
                return trimmed;
            }

            return string.IsNullOrEmpty(bin) ? file : $"{bin}{Path.DirectorySeparatorChar}{file}";
        }
        catch (ArgumentException)
        {
            // 非法字符不算错误：原样显示，让用户自己看出填错了什么。
            return path;
        }
    }
}
