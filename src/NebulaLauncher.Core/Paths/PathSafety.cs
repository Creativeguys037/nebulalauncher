using System;
using System.Collections.Generic;
using System.Linq;

namespace NebulaLauncher.Core.Paths;

/// <summary>
/// 把用户输入或远程元数据里的名字清洗成可以在磁盘上落地的名字。
///
/// 存在的理由：仓库里原本有 5 处各写一套（<c>VersionsViewModel.MakeSafeFileName</c>、
/// <c>LibraryViewModel.SanitizeModPackFileName</c>、<c>WorldsView.SuggestFileName</c>、
/// <c>BackupService.SanitizeFileName</c>、<c>ModpackService.SanitizeDirectoryName</c>），
/// 细节全不一致：只有一处去掉了结尾的点号，没有一处处理 Windows 保留名与长度上限。
/// 结果是玩家输入 <c>World.</c> 会得到一个叫 <c>World</c> 的目录（Windows 会静默吃掉尾部点号），
/// 输入 <c>CON</c> 会直接创建失败，超长名字会让后续路径超过 260 字符。
///
/// PCL2 的对应实现在 <c>Modules/Base/ModValidate.vb</c> 的 <c>ValidateFolderName</c> /
/// <c>ValidateFileName</c>：空值、首尾空格、长度 1–100、结尾小数点、<c>Path.GetInvalidFileNameChars()</c>、
/// 重名检测。这里取它的规则内核，但**返回清洗后的值而不是错误文本**——
/// 调用点要的是「能用的名字」，报错由各自的上层负责。
/// </summary>
public static class PathSafety
{
    /// <summary>单个路径段的上限。取 PCL2 的 100，留足父目录 + 扩展名的余量。</summary>
    public const int MaxSegmentLength = 100;

    /// <summary>
    /// Windows 设备名。带扩展名时同样非法（<c>CON.txt</c> 也打不开），所以按第一段判。
    ///
    /// 三项看起来「不该在表里」，但都有实际理由（PCL2 <c>ModValidate.vb:291</c> 同样列了它们）：
    /// <list type="bullet">
    /// <item><c>CLOCK$</c>：早期 DOS 的时钟设备，Windows 仍保留这个设备名。
    /// 名字里带 <c>$</c>——它是**合法文件名字符**（不在 <c>GetInvalidFileNameChars()</c> 里），
    /// 所以只能靠这张表拦，字符白名单起不到作用。</item>
    /// <item><c>COM0</c> / <c>LPT0</c>：传统串口是 <c>COM1</c>-<c>COM9</c>，但部分驱动会把
    /// 第一个口映射成 0；PCL2 把 0 也当保留名，代价只是给一个几乎不存在的名字加个前缀。</item>
    /// </list>
    /// </summary>
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CLOCK$",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// NTFS 8.3 短名形态：<c>PROGRA~1</c>、<c>ab~1</c>——至少两个字符 + <c>~</c> + 一位数字。
    ///
    /// 为什么要拦（PCL2 <c>ModValidate.vb:294</c>，issue #4505）：Windows 为长文件名自动生成
    /// 8.3 短名时会占用 <c>名字~序号</c> 这个形态。用户/整合包真的创建 <c>ab~1</c> 之后，
    /// 同目录下另一个以 <c>ab</c> 开头、需要短名的老程序（或盘上已有的短名）会拿到不同的短名，
    /// 于是「按短名硬编码的引用」全部指向错误目录。这类问题在中文路径盘上尤其难查。
    ///
    /// 只匹配「整段第一截」：<c>ab~1.jar</c> 的 <c>stem</c> 是 <c>ab~1</c>，同样拦。
    /// <c>ab~</c>（没有序号）不是合法短名形态，放行。
    ///
    /// **与 PCL2 的差异**：PCL2 <c>ModValidate.vb:294</c> 写的是 <c>.{2,}~\d</c>（只认一位序号），
    /// 这里放宽到 <c>\d+</c>。8.3 短名的序号实际可以是多位（同名前缀的第 10 个起就是 <c>~10</c>），
    /// 只拦一位等于把最常见的第二种形态漏掉；放宽之后 <c>ab~</c> 仍然合法，用例照旧成立。
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex ShortNamePattern =
        new(@"^.{2,}~\d+$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// 清洗成合法的文件名（不含路径分隔符）。空输入返回 <paramref name="fallback"/>。
    /// </summary>
    /// <param name="name">用户输入或远程元数据里的名字。</param>
    /// <param name="fallback">清洗后为空时用的兜底名，必须本身合法。</param>
    public static string SanitizeFileName(string? name, string fallback = "unnamed")
        => Sanitize(name, fallback);

    /// <summary>
    /// 清洗成合法的目录名。Windows 上目录名与文件名规则相同，保留这个入口只是为了调用点自解释。
    /// </summary>
    /// <param name="name">用户输入或远程元数据里的名字。</param>
    /// <param name="fallback">清洗后为空时用的兜底名，必须本身合法。</param>
    public static string SanitizeDirectoryName(string? name, string fallback = "unnamed")
        => Sanitize(name, fallback);

    /// <summary>
    /// 名字是否原本就合法（清洗后与原值相同）。用于「要不要提示用户被改过」。
    /// </summary>
    public static bool IsAlreadySafe(string? name, string fallback = "unnamed")
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        return string.Equals(name, Sanitize(name, fallback), StringComparison.Ordinal);
    }

    private static string Sanitize(string? name, string fallback)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return fallback;
        }

        // 首尾空白先去掉：Windows 会静默吃掉结尾空格，留着只会让「看起来一样、实际不一样」。
        var trimmed = name.Trim();
        var invalid = System.IO.Path.GetInvalidFileNameChars();

        var chars = trimmed
            .Select(ch => ch is '/' or '\\' || Array.IndexOf(invalid, ch) >= 0 ? '_' : ch)
            .ToArray();

        var cleaned = new string(chars);

        // 结尾的点号：Windows 上 "World." 与 "World" 指向同一个目录，留着会和后续重名检测打架。
        cleaned = cleaned.TrimEnd('.', ' ');

        if (cleaned.Length > MaxSegmentLength)
        {
            cleaned = cleaned[..MaxSegmentLength].TrimEnd('.', ' ');
        }

        if (cleaned.Length == 0)
        {
            return fallback;
        }

        // 保留名加前缀而不是直接拒绝：玩家的输入仍然要保留可读部分。
        var stem = cleaned.Split('.')[0];

        // 8.3 短名与保留名同一处理：加前缀而不是拒绝，理由同上。
        if (ReservedNames.Contains(stem) || ShortNamePattern.IsMatch(stem))
        {
            cleaned = "_" + cleaned;

            if (cleaned.Length > MaxSegmentLength)
            {
                cleaned = cleaned[..MaxSegmentLength];
            }
        }

        return cleaned;
    }
}