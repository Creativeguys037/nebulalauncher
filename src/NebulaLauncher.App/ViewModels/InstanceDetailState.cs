using CommunityToolkit.Mvvm.ComponentModel;
using NebulaLauncher.Core.Instances;
using NebulaLauncher.Core.Models;

namespace NebulaLauncher.ViewModels;

/// <summary>
/// 版本页右栏「实例详情 · 概览」的编辑状态（票 #88）。
///
/// ## 为什么单独一个对象，而不是把字段摊在 <see cref="VersionsViewModel"/> 上
/// <see cref="VersionsViewModel"/> 已经四千九百行、二十多个编辑器字段。再摊八个进去，
/// 「保存时该读哪几个」只能靠字段名前缀去猜，而猜错的后果是**写盘写错字段**。
/// 把它们收在一个对象里，保存路径就是 <c>state.ToMemoryMb()</c> 这种一问一答，
/// 校验、只读、脏检查也各只有一处。
///
/// ## 草稿与服务值分离（这是本对象存在的核心理由）
/// 票面要求「非法值不写盘，不丢输入」。这两个要求放在一个字段上做不到：
/// 把输入框直接绑到 <c>InstanceInfo.MaxMemoryMb</c>，用户打一个 <c>4</c> 就已经改了模型；
/// 而每敲一个键就把非法值夹回合法值，用户永远打不出 <c>4096</c>（打到 <c>4</c> 就被改成 512）。
/// 所以这里存的是**草稿**（<see cref="MemoryText"/> 等），服务值只在
/// <see cref="ApplySaved"/> 时被推进来。校验失败时草稿原样保留，模型一个字节都没动。
///
/// ## 只读的判据为什么是「运行中」而不是「已启动过」
/// 运行中的实例改内存与 Java 没有意义：那一次 JVM 已经按旧参数起来了，
/// 改动只会让用户以为生效了。而「已启动过但当前没跑」必须可改——那是最常见的调参场景。
/// </summary>
public sealed partial class InstanceDetailState : ObservableObject
{
    /// <summary>当前正在展示 / 编辑的实例。null 表示详情面板处于空态（没有选中实例）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasInstance))]
    [NotifyPropertyChangedFor(nameof(IsReadOnly))]
    [NotifyPropertyChangedFor(nameof(ReadOnlyHint))]
    private InstanceRow? _row;

    /// <summary>内存输入框的草稿文本。永远保留用户最后打进去的内容。</summary>
    [ObservableProperty]
    private string _memoryText = string.Empty;

    /// <summary>
    /// 「跟随全局」勾选态。勾上表示本实例不覆盖全局内存（落盘为 null）。
    ///
    /// 三态与 <see cref="InstanceInfo.VersionType"/> 同一套思路，但这里只有两态：
    /// 内存没有「明确设成 0」这种语义，跟随全局与自定义已经够用。
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMemoryEditable))]
    private bool _followGlobalMemory = true;

    /// <summary>Java 路径的草稿文本。空串表示自动选择（落盘为 null）。</summary>
    [ObservableProperty]
    private string _javaPathText = string.Empty;

    /// <summary>
    /// 内存字段下方的红字。null 表示当前没有错误。
    ///
    /// 它只在 <see cref="Validate"/> 之后被写入，**不在每次按键时清空**——
    /// 用户正在修一个错值时，红字要留到他改成合法值为止，
    /// 否则「打第一个字符红字消失、下一帧又出现」会闪得看不清原因。
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMemoryError))]
    private string? _memoryError;

    /// <summary>Java 字段下方的红字。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasJavaError))]
    private string? _javaError;

    /// <summary>保存进行中。按钮转进度环、字段禁用。</summary>

    /// <summary>生效的全局内存值（MB），用于「跟随全局」时的说明与回填。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MemoryHint))]
    private int _globalMemoryMb = 4096;

    /// <summary>生效的全局 Java 路径，用于「自动选择」时的说明。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(JavaHint))]
    private string? _globalJavaPath;

    /// <summary>
    /// 本机可分配内存上限（MB）。0 表示探测不到，由
    /// <see cref="InstanceLaunchTuning.EffectiveUpperBound"/> 回落到兜底值。
    ///
    /// 它是「上限提示」与「超出即非法」两条规则的同一个来源：两处各算一次，
    /// 就会出现提示写 8192、校验按 16384 放行的自相矛盾。
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MemoryHint))]
    [NotifyPropertyChangedFor(nameof(UpperBoundMb))]
    private int _detectedUpperBoundMb;

    /// <summary>实际生效的上限，供提示文案与校验共用。</summary>
    public int UpperBoundMb => InstanceLaunchTuning.EffectiveUpperBound(DetectedUpperBoundMb);

    /// <summary>详情面板是否展示了实例。</summary>
    public bool HasInstance => Row is not null;

    /// <summary>
    /// 字段是否只读：实例运行中一律只读（运行中的实例改内存与 Java 不会生效）。
    ///
    /// 判据只有一份——<see cref="InstanceRow.IsRunningNow"/>，由
    /// <c>VersionsViewModel.SyncDetailRunningState</c> 在刷新时按
    /// 「磁盘运行锁 或 本会话有进程」写一次。这里再自己算一遍
    /// （<c>Row.IsRunning || Row.IsSessionRunning</c>）会让「徽标说运行中、字段却能改」
    /// 这种不一致成为可能，而它在界面上极难发现。
    /// </summary>
    public bool IsReadOnly => Row?.IsRunningNow == true;

    /// <summary>
    /// 内存输入框允许的最大字符数。与校验里的长度上限同源——
    /// 界面写死一个更大的值时，用户能打进去一串必然被判非法的输入，只会得到一句红字。
    /// </summary>
    public int MemoryMaxLength => InstanceLaunchTuning.MemoryTextMaxLength;

    /// <summary>字段是否可交互（未只读、未保存中）。</summary>
    public bool FieldEnabled => HasInstance && !IsReadOnly;

    /// <summary>内存输入框是否可编辑：跟随全局时不需要填值。</summary>
    public bool IsMemoryEditable => FieldEnabled && !FollowGlobalMemory;

    /// <summary>运行中时字段下方那句说明。<see cref="IsReadOnly"/> 为 false 时为空串。</summary>
    public string ReadOnlyHint => IsReadOnly
        ? "实例正在运行，内存与 Java 只有在游戏退出后才能修改。"
        : string.Empty;

    public bool HasMemoryError => !string.IsNullOrWhiteSpace(MemoryError);

    public bool HasJavaError => !string.IsNullOrWhiteSpace(JavaError);

    /// <summary>内存字段下方的常驻说明：推荐值与上限都写在这里，用户不必去设置页找。</summary>
    public string MemoryHint
    {
        get
        {
            var upper = UpperBoundMb;

            if (FollowGlobalMemory)
            {
                return GlobalMemoryMb > 0
                    ? $"跟随全局设置：{GlobalMemoryMb} MB。取消勾选可单独指定。"
                    : "跟随全局设置。取消勾选可单独指定。";
            }

            return $"单位 MB，范围 {InstanceLaunchTuning.MinMemoryMb}–{upper}。";
        }
    }

    /// <summary>Java 字段下方的常驻说明：说清「留空」与「填了全局路径」各是什么行为。</summary>
    public string JavaHint
    {
        get
        {
            var global = GlobalJavaPath;

            if (string.IsNullOrWhiteSpace(JavaPathText))
            {
                return string.IsNullOrWhiteSpace(global)
                    ? "留空表示按游戏版本自动选择 Java。"
                    : $"留空表示自动选择（当前全局：{InstanceLaunchTuning.DescribeJavaPathShort(global)}）。";
            }

            return "填 java.exe 的完整路径，或 JDK 根目录（会展开到 bin 下的 java）。";
        }
    }

    /// <summary>
    /// 把服务值推进草稿。选中实例、切换到另一个实例、保存成功后重新回填都走这里。
    ///
    /// 草稿与服务值分离是本对象存在的核心理由：把输入框直接绑到
    /// <c>InstanceInfo.MaxMemoryMb</c>，用户打一个 <c>4</c> 就已经改了模型；
    /// 而每敲一个键就把非法值夹回合法值，用户永远打不出 <c>4096</c>。
    /// </summary>
    public void LoadFrom(InstanceRow row, int globalMemoryMb, string? globalJavaPath, int detectedUpperBoundMb)
    {
        Row = row;

        GlobalMemoryMb = globalMemoryMb;
        GlobalJavaPath = globalJavaPath;
        DetectedUpperBoundMb = detectedUpperBoundMb;

        var info = row.Info;
        FollowGlobalMemory = info.MaxMemoryMb is null;

        // 回填时夹一次：手改过 instance.json 或换了内存更小的机器时，旧值可能已经越界。
        // 直接显示越界值会让用户「什么都没改，一保存就被判非法」。
        //
        // 跟随全局时也把生效值填进来（而不是留空）：输入框空着会被读成「这个实例没有内存设置」，
        // 而用户真正想知道的是「不设置的时候实际会分配多少」。框禁用 + 这个值，
        // 语义就是「跟随全局，当前等于 4096」。
        var effective = info.MaxMemoryMb
                        ?? (globalMemoryMb > 0 ? globalMemoryMb : InstanceLaunchTuning.FallbackMaxMemoryMb);

        MemoryText = InstanceLaunchTuning.ClampMemory(effective, detectedUpperBoundMb)
            .ToString(System.Globalization.CultureInfo.InvariantCulture);

        JavaPathText = info.JavaPath ?? string.Empty;

        // 换实例时必须清掉上一个实例的错误：留着会让用户以为当前实例就是这个状态。
        MemoryError = null;
        JavaError = null;

        RaiseComputed();
    }

    /// <summary>
    /// 校验草稿。合法返回 true；非法时把原因写进对应的 <c>XxxError</c> 并返回 false。
    ///
    /// **不夹值、不清空草稿**：非法输入原样留在框里，这是票面「不丢输入」的落点。
    /// </summary>
    /// <param name="resolveJava">
    /// Java 路径展开函数（通常是 <c>JavaService.ResolveJavaExecutable</c>）。
    /// 传 null 表示跳过 Java 存在性校验——服务未就绪时不能让整张表单都存不进去。
    /// </param>
    public bool Validate(Func<string?, string?>? resolveJava)
    {
        var ok = true;

        if (!FollowGlobalMemory)
        {
            var parsed = InstanceLaunchTuning.ParseMemory(MemoryText, DetectedUpperBoundMb);
            MemoryError = parsed.IsValid ? null : parsed.Error;
            ok &= parsed.IsValid;
        }
        else
        {
            // 跟随全局时不存在「本实例的值」，上一轮的错误必须清掉，
            // 否则勾上「跟随全局」后仍看到一条红的「请填写内存」。
            MemoryError = null;
        }

        if (resolveJava is null)
        {
            JavaError = null;
        }
        else
        {
            JavaError = InstanceLaunchTuning.ValidateJavaPath(JavaPathText, resolveJava);
            ok &= JavaError is null;
        }

        RaiseComputed();
        return ok;
    }

    /// <summary>
    /// 校验通过后取要落盘的内存值。跟随全局时返回 null。
    /// 调用方**必须先调 <see cref="Validate"/>**：这里不做二次校验，
    /// 那会变成「两处各判一次，两处迟早不一致」。
    /// </summary>
    public int? ToMemoryMb() =>
        FollowGlobalMemory
            ? null
            : InstanceLaunchTuning.ParseMemory(MemoryText, DetectedUpperBoundMb).MemoryMb;

    /// <summary>校验通过后取要落盘的 Java 路径。空白归一成 null（跟随自动选择）。</summary>
    public string? ToJavaPath()
    {
        var trimmed = (JavaPathText ?? string.Empty).Trim();

        return trimmed.Length == 0 ? null : trimmed;
    }

    /// <summary>
    /// <summary>运行状态变了（游戏启动 / 退出）时重算只读与按钮可用性。</summary>
    public void RefreshReadOnlyState() => RaiseComputed();

    partial void OnRowChanged(InstanceRow? value) => RaiseComputed();

    partial void OnFollowGlobalMemoryChanged(bool value) => RaiseComputed();

    partial void OnMemoryTextChanged(string value) => RaiseComputed();

    partial void OnJavaPathTextChanged(string value) => RaiseComputed();

    partial void OnGlobalMemoryMbChanged(int value) => RaiseComputed();

    partial void OnGlobalJavaPathChanged(string? value) => RaiseComputed();

    partial void OnDetectedUpperBoundMbChanged(int value) => RaiseComputed();

    /// <summary>
    /// 把所有派生属性一次性通知出去。
    ///
    /// 手写而不是靠 <c>[NotifyPropertyChangedFor]</c> 串起来：派生关系是网状
    /// （一个字段牵动六七个属性，其中几个又依赖别的字段），
    /// 逐个标注漏一个就是「按钮该亮没亮」，而这类漏标在界面上极难发现。
    /// </summary>
    private void RaiseComputed()
    {
        OnPropertyChanged(nameof(HasInstance));
        OnPropertyChanged(nameof(IsReadOnly));
        OnPropertyChanged(nameof(FieldEnabled));
        OnPropertyChanged(nameof(IsMemoryEditable));
        OnPropertyChanged(nameof(ReadOnlyHint));
        OnPropertyChanged(nameof(MemoryHint));
        OnPropertyChanged(nameof(JavaHint));
        OnPropertyChanged(nameof(UpperBoundMb));
    }

}

