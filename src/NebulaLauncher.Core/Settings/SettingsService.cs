using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using NebulaLauncher.Core.Abstractions;
using NebulaLauncher.Core.Models;
using NebulaLauncher.Core.Net;
using NebulaLauncher.Core.Serialization;

namespace NebulaLauncher.Core.Settings;

/// <summary>
/// JSON 持久化的启动器设置。写入采用「先写 .tmp 再 move 覆盖」，
/// 避免进程在写一半时被杀导致配置文件变成半截 JSON。
/// </summary>
/// <remarks>
/// 不实现 <see cref="IDisposable"/>：<c>_saveGate</c> 是进程级单例的字段，
/// 随进程退出一起回收；主动 Dispose 反而可能在关闭时的最后一次保存上抛 ObjectDisposedException。
/// </remarks>
#pragma warning disable CA1001
public sealed class SettingsService : ISettingsService
#pragma warning restore CA1001
{
    /// <summary>
    /// 设置文件的 JSON 读写配置。由 <see cref="SettingsJsonContext"/> 持有，
    /// 这里留一个引用只是为了兼容包内其它地方的只读复用。
    /// </summary>
    internal static JsonSerializerOptions JsonOptions => SettingsJsonContext.DefaultOptions;

    private readonly ILauncherPaths _paths;
    private readonly IAppLog _log;
    private readonly SemaphoreSlim _saveGate = new(1, 1);

    private AppSettings _current = new();

    public SettingsService(ILauncherPaths paths, IAppLog log)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <inheritdoc />
    public AppSettings Current => _current;

    /// <inheritdoc />
    public event EventHandler<AppSettings>? Changed;

    /// <summary>是否已经加载过一次。见 <see cref="LoadAsync"/> 的幂等语义。</summary>
    private bool _loaded;

    /// <inheritdoc />
    public Task LoadAsync(CancellationToken cancellationToken = default)
    {
        // 六个调用方各自加载一次，旧实现每次都重读 JSON 并重发 Changed，
        // 结果主题与游戏目录在每次进页面时被重新应用一遍。这里只做一次。
        return _loaded ? Task.CompletedTask : ReloadAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        var file = _paths.SettingsFile;

        try
        {
            if (!File.Exists(file))
            {
                _log.Info($"设置文件不存在，使用默认设置：{file}");
                _current = new AppSettings();
                ApplyNetworkProxy();
                _loaded = true;
                return;
            }

            var json = await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
            var loaded = JsonSerializer.Deserialize(json, JsonOptions.Of<AppSettings>());

            if (loaded is null)
            {
                throw new JsonException("反序列化结果为 null");
            }

            Normalize(loaded);
            var migrated = LoadProxyCredential(loaded);
            _current = loaded;
            ApplyNetworkProxy();
            _loaded = true;

            if (migrated)
            {
                // 迁移当场落盘：明文留在磁盘上的每一秒都是风险，而多数用户装完之后根本不会再点「保存设置」。
                // 保存失败只记日志（文件只读、磁盘满），内存里仍有可用密码，下次启动会再试。
                try
                {
                    await SaveAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _log.Warn("代理密码迁移写盘失败，下次启动会重试", ex);
                }
            }
            _log.Info($"已加载设置：{file}");
            Changed?.Invoke(this, _current);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 损坏的文件先留证据，再退回默认值，绝不让启动器因为配置坏掉而无法启动。
            _log.Warn($"设置文件损坏，回退默认值：{file}", ex);
            BackupBrokenFile(file);
            _current = new AppSettings();
            ApplyNetworkProxy();
            _loaded = true;
        }
    }

    /// <inheritdoc />
    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        var file = _paths.SettingsFile;
        var temp = file + ".tmp";

        await _saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directory = Path.GetDirectoryName(file);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // 落盘用副本：明文密码只在内存里，写出去的是密文（见 PersistableCopy）。
            var persisted = PersistableCopy(_current, _log);
            var json = JsonSerializer.Serialize(persisted, JsonOptions.Of<AppSettings>());
            await File.WriteAllTextAsync(temp, json, cancellationToken).ConfigureAwait(false);
            File.Move(temp, file, overwrite: true);

            // 写成功后把内存里的「已加密」标记同步过来：否则下一次保存会被当成「还没加密过」再加密一遍
            // （多加密一层不影响功能，但会让「已加密保存」的判定与磁盘事实不一致）。
            _current.ProxyPasswordProtected = persisted.ProxyPasswordProtected;

            // 保存后立刻应用：设置页点「保存设置」就应当对后续请求生效，不必重启启动器。
            ApplyNetworkProxy();
            _log.Debug($"设置已保存：{file}");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Error($"保存设置失败：{file}", ex);
            throw;
        }
        finally
        {
            _saveGate.Release();
        }

        Changed?.Invoke(this, _current);
    }

    private void BackupBrokenFile(string file)
    {
        try
        {
            var backup = file + ".bak";
            File.Copy(file, backup, overwrite: true);
            _log.Info($"损坏的设置文件已备份：{backup}");
        }
        catch (Exception ex)
        {
            _log.Warn("备份损坏的设置文件失败", ex);
        }
    }

    /// <summary>
    /// 把磁盘上的密文解到内存里的明文字段。解不开（换机器 / 换 Windows 账户 / 密文坏了）时按未设置处理，
    /// 只清掉内存值，不动磁盘——用户重新填一次密码就会覆盖，没必要把别人的密文删掉。
    /// </summary>
    private bool LoadProxyCredential(AppSettings settings)
    {
        // 旧版本存的是明文：明文字段有值、密文字段为空。这里就地加密一次并把位置交给密文字段，
        // 免得「迁移」这件事拖到用户下次点保存——很多人装完之后根本不进设置页。
        // 明文本身继续留在内存里（本次会话要发请求），写盘时由 PersistableCopy 抹掉。
        if (!string.IsNullOrEmpty(settings.ProxyPassword)
            && !ProxyCredentialStore.IsStored(settings.ProxyPasswordProtected))
        {
            settings.ProxyPasswordProtected = ProxyCredentialStore.Protect(settings.ProxyPassword);
            _log.Info("代理密码已从明文迁移到系统密钥保护");
            return true;
        }

        if (!ProxyCredentialStore.IsStored(settings.ProxyPasswordProtected))
        {
            return false;
        }

        if (!string.IsNullOrEmpty(settings.ProxyPassword))
        {
            // 明文与密文同时存在（手改过配置）：以明文为准，下次保存会改写密文。
            return false;
        }

        var plain = ProxyCredentialStore.Unprotect(settings.ProxyPasswordProtected);

        if (plain is null)
        {
            _log.Warn("代理密码无法解密（可能换了 Windows 账户或机器），按未设置处理");
            return false;
        }

        settings.ProxyPassword = plain;
        return false;
    }

    /// <summary>
    /// 生成「可以写进磁盘」的副本：明文密码换成密文，明文本身抹掉。
    ///
    /// 这个副本只用于序列化，不进入 <see cref="Current"/>——界面与代理配置器读到的仍然是内存里的明文，
    /// 否则每次保存都要解密一次，且解密失败会让当前会话直接断掉代理。
    /// </summary>
    internal static AppSettings PersistableCopy(AppSettings source, IAppLog log)
    {
        var copy = source.Clone();

        if (!string.IsNullOrEmpty(copy.ProxyPassword))
        {
            // 已有密文且解出来就是当前明文：保持原样。否则每次保存都会换一段新密文
            // （DPAPI 带随机盐），配置文件会无缘无故变动，用户拿 diff 对比会以为密码被改了。
            var unchanged = ProxyCredentialStore.IsStored(copy.ProxyPasswordProtected)
                && string.Equals(
                    ProxyCredentialStore.Unprotect(copy.ProxyPasswordProtected),
                    copy.ProxyPassword,
                    StringComparison.Ordinal);

            if (!unchanged)
            {
                copy.ProxyPasswordProtected = ProxyCredentialStore.Protect(copy.ProxyPassword);
            }

            if (!ProxyCredentialStore.IsProtected(copy.ProxyPasswordProtected))
            {
                // 非 Windows 或 DPAPI 不可用：退化编码也要如实说一句，别让用户以为已经加密。
                log.Warn("当前系统不支持 DPAPI，代理密码以未加密形式保存");
            }
        }

        copy.ProxyPassword = null;
        return copy;
    }

    /// <summary>把越界或自相矛盾的值拉回可用范围，坏配置不该阻断启动。</summary>
    private static void Normalize(AppSettings settings)
    {
        settings.DownloadThreads = Math.Clamp(settings.DownloadThreads, 8, 256);

        if (settings.MinMemoryMb < 256)
        {
            settings.MinMemoryMb = 256;
        }

        if (settings.MaxMemoryMb < settings.MinMemoryMb)
        {
            settings.MaxMemoryMb = settings.MinMemoryMb;
        }

        if (settings.WindowWidth < 320)
        {
            settings.WindowWidth = 320;
        }

        if (settings.WindowHeight < 240)
        {
            settings.WindowHeight = 240;
        }

        // 启动器窗口尺寸：与游戏窗口的 WindowWidth/Height 无关，下限取窄屏验收尺寸，
        // 低于它布局本身就不成立，回落默认值而不是夹到下限（夹下限会让 0 / 负数也变成可开窗口）。
        // 上限 8000：拔掉外接显示器后窗口可能被记录成超大尺寸，夹住才能保证下次一定开得出来。
        settings.LauncherWindowWidth = NormalizeLauncherWindowSize(
            settings.LauncherWindowWidth, AppSettings.MinimumLauncherWindowWidth, AppSettings.DefaultLauncherWindowWidth);
        settings.LauncherWindowHeight = NormalizeLauncherWindowSize(
            settings.LauncherWindowHeight, AppSettings.MinimumLauncherWindowHeight, AppSettings.DefaultLauncherWindowHeight);

        settings.ExtraJvmArguments ??= string.Empty;
        settings.ExtraGameArguments ??= string.Empty;

        // 三档下载源：历史文件里的 "BmclApi" 由 DownloadSourceJsonConverter 读成 Mirror；
        // 这里再把越界的枚举值（手改配置、旧数值）拉回可用状态。
        settings.DownloadSource = settings.DownloadSource switch
        {
            DownloadSource.Auto => DownloadSource.Auto,
            DownloadSource.Official => DownloadSource.Official,
            _ => DownloadSource.Mirror,
        };

        // 0 表示不限速；上限 1 GB/s 只是挡住「手滑输入 999999999」导致的整数溢出与 UI 抖动。
        settings.DownloadSpeedLimitKb = Math.Clamp(settings.DownloadSpeedLimitKb, 0, 1024 * 1024);

        // 自动内存模式下 MaxMemoryMb 仍要有合法值：用户在设置页关掉自动后立刻要用它。
        settings.MaxMemoryMb = Math.Clamp(settings.MaxMemoryMb, settings.MinMemoryMb, 1024 * 1024);

        settings.GameWindowTitle = string.IsNullOrWhiteSpace(settings.GameWindowTitle)
            ? null
            : settings.GameWindowTitle.Trim();

        // 主题只认三个值；拼错/旧配置/空串一律回落 dark（深色是主视觉，回落浅色会整界面发白）。
        settings.Theme = settings.Theme?.Trim().ToLowerInvariant() switch
        {
            "light" => "light",
            "system" => "system",
            _ => "dark",
        };
        settings.WindowMaterial = string.IsNullOrWhiteSpace(settings.WindowMaterial) ? "solid" : settings.WindowMaterial;

        // 空字符串与 null 都表示「跟随平台默认目录」；保留原样会让设置页出现空白路径框。
        settings.MinecraftDirectory = string.IsNullOrWhiteSpace(settings.MinecraftDirectory)
            ? null
            : settings.MinecraftDirectory.Trim();

        settings.LastInstanceId = string.IsNullOrWhiteSpace(settings.LastInstanceId)
            ? null
            : settings.LastInstanceId.Trim();

        settings.CurseForgeBaseUrl = NormalizeBaseUrl(settings.CurseForgeBaseUrl);

        settings.ContentSource = string.Equals(settings.ContentSource, "curseforge", StringComparison.OrdinalIgnoreCase)
            ? "curseforge"
            : "modrinth";

        settings.IsolationPolicy = settings.IsolationPolicy?.Trim().ToLowerInvariant() switch
        {
            "never" => "never",
            "always" => "always",
            _ => "modded",
        };

        settings.LauncherVisibilityOnLaunch = settings.LauncherVisibilityOnLaunch?.Trim().ToLowerInvariant() switch
        {
            "minimize" => "minimize",
            "close" => "close",
            _ => "keep",
        };

        // 老配置只有 CloseLauncherAfterLaunch：迁移成新的三态，否则那个开关一直是「静默无效」。
        if (settings.CloseLauncherAfterLaunch && settings.LauncherVisibilityOnLaunch == "keep")
        {
            settings.LauncherVisibilityOnLaunch = "close";
        }

        settings.GameProcessPriority = settings.GameProcessPriority?.Trim().ToLowerInvariant() switch
        {
            "high" => "high",
            "low" => "low",
            _ => "normal",
        };

        // 上限 200 只是挡住「手滑输入 999999」把配置写坏；不限下限 0 是因为 0 会被 PruneAsync 当成不轮转。
        settings.WorldBackupRetention = Math.Clamp(settings.WorldBackupRetention, 1, 200);

        // RegisterGpuPreference 是布尔开关，没有可夹取的区间；旧配置里缺这个字段时反序列化会
        // 保留属性初始值 true，与历史行为一致，因此 Normalize 不需要动它。
        // 把它列在这里只为让「新增设置项必须过 Normalize」这条约定在 diff 里可见。

        // UseModrinthMirror 同样是布尔开关，没有区间可夹；旧配置缺字段时反序列化保留属性初始值 false，
        // 正是「默认关」这个语义。这里不做赋值，只为让「新增设置项必须过 Normalize」在 diff 里可见。

        NormalizeProxy(settings);

        NormalizeJvmPresets(settings);
    }

    /// <summary>
    /// 把代理配置应用到进程级 <see cref="HttpClient.DefaultProxy"/>。
    ///
    /// 调用点只有 <see cref="ReloadAsync"/> 的三条出口（无文件 / 加载成功 / 损坏回退）
    /// 与 <see cref="SaveAsync"/> 的成功分支：设置是代理的唯一来源，别处没有第二个写入者。
    /// 这里吞掉异常的原因是启动流程不该因为代理配置而失败（例如极端平台上
    /// <c>HttpClient.DefaultProxy</c> 的 setter 不可用），失败只留日志。
    /// </summary>
    private void ApplyNetworkProxy()
    {
        try
        {
            ProxyConfigurator.Apply(_current);
        }
        catch (Exception ex)
        {
            _log.Warn("应用网络代理设置失败，继续使用原有代理", ex);
        }
    }

    /// <summary>
    /// 代理设置的夹取。
    ///
    /// 两条关键回落：
    /// 1) 未知模式（拼错、旧版本手改）回落 <c>system</c>——那是历史行为，最不容易让用户连不上网；
    /// 2) 「自定义」但地址解析不出来时同样回落 <c>system</c>，而不是留一个「选了自定义、地址空白」
    ///    的组合：那种状态在界面上看起来像在走代理，实际全直连，排查起来毫无线索。
    /// </summary>
    private static void NormalizeProxy(AppSettings settings)
    {
        settings.ProxyMode = settings.ProxyMode?.Trim().ToLowerInvariant() switch
        {
            ProxyModes.None => ProxyModes.None,
            ProxyModes.Custom => ProxyModes.Custom,
            _ => ProxyModes.System,
        };

        settings.ProxyAddress = ProxyAddressParser.Normalize(settings.ProxyAddress);

        if (settings.ProxyMode == ProxyModes.Custom && settings.ProxyAddress is null)
        {
            settings.ProxyMode = ProxyModes.System;
        }

        settings.ProxyUserName = string.IsNullOrWhiteSpace(settings.ProxyUserName)
            ? null
            : settings.ProxyUserName.Trim();

        // 密码不做 Trim：首尾空格可能是密码本身的一部分。
        settings.ProxyPassword = string.IsNullOrEmpty(settings.ProxyPassword) ? null : settings.ProxyPassword;

        if (settings.ProxyUserName is null)
        {
            // 没有用户名时密码没有意义，留着只会在设置页出现「只有一个密码框有内容」的怪状态。
            settings.ProxyPassword = null;
            settings.ProxyPasswordProtected = null;
        }
    }

    /// <summary>
    /// 清理 JVM 预设：去空名、去重 Id、限制条数、把悬空的 ActiveJvmPresetId 归零。
    ///
    /// 坏配置的处理原则与其它设置一致——拉回可用状态，不让启动器因为配置脏而打不开。
    /// </summary>
    private static void NormalizeJvmPresets(AppSettings settings)
    {
        settings.JvmPresets ??= new List<JvmPreset>();

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var cleaned = new List<JvmPreset>(settings.JvmPresets.Count);

        foreach (var preset in settings.JvmPresets)
        {
            if (preset is null)
            {
                continue;
            }

            preset.Id = string.IsNullOrWhiteSpace(preset.Id)
                ? Guid.NewGuid().ToString("N")[..8]
                : preset.Id.Trim();

            if (!seen.Add(preset.Id))
            {
                preset.Id = Guid.NewGuid().ToString("N")[..8];
                seen.Add(preset.Id);
            }

            preset.Name = string.IsNullOrWhiteSpace(preset.Name) ? "未命名预设" : preset.Name.Trim();
            preset.Arguments ??= string.Empty;
            cleaned.Add(preset);

            // 20 组已经远超实际需求，再多只会让设置页难用。
            if (cleaned.Count >= 20)
            {
                break;
            }
        }

        settings.JvmPresets = cleaned;

        settings.ActiveJvmPresetId = string.IsNullOrWhiteSpace(settings.ActiveJvmPresetId)
            ? null
            : settings.ActiveJvmPresetId!.Trim();

        if (settings.ActiveJvmPresetId is not null &&
            !cleaned.Any(preset => string.Equals(preset.Id, settings.ActiveJvmPresetId, StringComparison.Ordinal)))
        {
            // 预设被删掉了但指针还在：退回原文参数，别让启动拿到空参数集。
            settings.ActiveJvmPresetId = null;
        }
    }

    /// <summary>
    /// 启动器窗口尺寸校验。
    ///
    /// 低于下限一律回落默认值：0 / 负数 / 旧配置里的 320×240 都不是「用户想要的窄窗口」，
    /// 而是坏数据；夹到下限会让「窗口小到看不见」这种状态被当成合法值保存回去。
    /// 超过上限则夹到 8000，保证显示器变动后窗口仍然开得出来。
    /// </summary>
    private static int NormalizeLauncherWindowSize(int value, int minimum, int fallback)
    {
        if (value < minimum)
        {
            return fallback;
        }

        return value > AppSettings.MaximumLauncherWindowSize
            ? AppSettings.MaximumLauncherWindowSize
            : value;
    }

    /// <summary>剥掉首尾空白与末尾斜杠；非法 URL 退回 null（等于用官方地址）。</summary>
    private static string? NormalizeBaseUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim().TrimEnd('/');

        return Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) &&
               (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? trimmed
            : null;
    }

    /// <summary>
    /// <see cref="DownloadSource"/> 的 JSON 读写。
    ///
    /// 为什么不能直接用 <see cref="JsonStringEnumConverter"/>：
    /// 1) 历史 settings.json 里写的是 <c>"BmclApi"</c>，三态化之后这个名字不再是主名，
    ///    必须显式映射到 <see cref="DownloadSource.Mirror"/>（枚举里两个成员同值，靠 ToString 不可靠）；
    /// 2) 写出时固定用新名字，避免「同一份配置每次保存名字都在两个别名之间跳」；
    /// 3) 读不懂的值一律回落镜像优先——那是历史默认值，最接近旧行为。
    ///
    /// 放在 <see cref="SettingsJsonContext"/> 里共用：Source Generator 生成的元数据在引用自定义转换器时
    /// 必须能拿到同一份实例，两处各建一个会让「读用一套、写用另一套」。
    /// </summary>
    internal sealed class DownloadSourceJsonConverter : JsonConverter<DownloadSource>
    {
        public override DownloadSource Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null)
            {
                return DownloadSource.Mirror;
            }

            if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var number))
            {
                // 旧枚举里 0=Official、1=BmclApi；三态化后数值整体变了，只能按旧表解释。
                return number switch
                {
                    0 => DownloadSource.Official,
                    _ => DownloadSource.Mirror,
                };
            }

            if (reader.TokenType != JsonTokenType.String)
            {
                throw new JsonException($"下载源字段不是字符串：{reader.TokenType}");
            }

            return ParseName(reader.GetString());
        }

        public override void Write(Utf8JsonWriter writer, DownloadSource value, JsonSerializerOptions options) =>
            writer.WriteStringValue(NameOf(value));

        private static DownloadSource ParseName(string? raw) => raw?.Trim().ToLowerInvariant() switch
        {
            "auto" => DownloadSource.Auto,
            "official" => DownloadSource.Official,
            "bmclapi" or "mirror" => DownloadSource.Mirror,
            _ => DownloadSource.Mirror,
        };

        private static string NameOf(DownloadSource value) => value switch
        {
            DownloadSource.Auto => "Auto",
            DownloadSource.Official => "Official",
            _ => "Mirror",
        };
    }
}

/// <summary>
/// 设置文件的 Source Generator 上下文。
///
/// 为什么需要它：portable 发布链路带 <c>PublishTrimmed=true</c>，裁剪器无法看到
/// <c>JsonSerializer.Serialize&lt;T&gt;</c> 里 T 的反射访问，会把 <see cref="AppSettings"/>
/// 的属性元数据全部剪掉，运行到保存设置时抛 <see cref="InvalidOperationException"/>
/// （"Reflection-based serialization has been disabled"）。改成编译期生成的元数据后，
/// 序列化通路完全不经过反射，裁剪器也就无从剪错。
///
/// <see cref="JsonSerializerOptions.TypeInfoResolver"/> 必须显式挂上：options 一旦被构造，
/// 序列化时走的就是这里登记的元数据，而不是运行时按类型反射构建的那一份。
/// </summary>
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext
{
    /// <summary>与迁移前完全一致的读写口径：Web 命名、缩进、容忍注释与尾逗号、两个枚举转换器。</summary>
    internal static readonly JsonSerializerOptions DefaultOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        // 下载源转换器必须排在通用枚举转换器前面，否则 "BmclApi" 这类历史名会被当成未知值。
        // 通用枚举转换器用泛型版本：非泛型的 JsonStringEnumConverter 靠运行时反射枚举字段，
        // AOT / 裁剪下不可用（IL3050）。
        Converters = { new SettingsService.DownloadSourceJsonConverter(), new JsonStringEnumConverter<DownloadSource>() },
        TypeInfoResolver = new SettingsJsonContext(),
    };
}
