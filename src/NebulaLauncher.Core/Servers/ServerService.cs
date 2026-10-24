using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using NebulaLauncher.Core.Abstractions;
using NebulaLauncher.Core.Models;

namespace NebulaLauncher.Core.Servers;

/// <summary>
/// 收藏服务器列表 + Minecraft Server List Ping（1.7+ 协议）。
/// 服务器不可达不是异常：返回 <see cref="ServerStatus.Online"/> = false 并带中文原因。
/// </summary>
public sealed class ServerService : IServerService, IDisposable
{
    private static readonly TimeSpan PingTimeout = TimeSpan.FromSeconds(5);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private static readonly Regex FormatCodePattern = new("§[0-9a-fk-orA-FK-OR]", RegexOptions.Compiled);

    private readonly ILauncherPaths _paths;
    private readonly IAppLog? _log;
    private readonly List<ServerEntry> _servers = [];
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>是否已经与 <c>servers.json</c> 同步过。导入前必须为 true，否则会把磁盘上已有的收藏写没。</summary>
    private bool _loaded;

    private ServerImportSummary? _lastImport;

    public ServerService(ILauncherPaths paths, IAppLog? log = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _log = log;
    }

    public IReadOnlyList<ServerEntry> Servers
    {
        get
        {
            lock (_servers)
            {
                return _servers.ToList();
            }
        }
    }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var path = _paths.ServersFile;
            if (!File.Exists(path))
            {
                lock (_servers)
                {
                    _servers.Clear();
                }

                Volatile.Write(ref _loaded, true);
                return;
            }

            var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            var loaded = JsonSerializer.Deserialize<List<ServerEntry>>(json, JsonOptions) ?? [];

            lock (_servers)
            {
                _servers.Clear();
                _servers.AddRange(loaded.Where(entry => !string.IsNullOrWhiteSpace(entry.Host)));
            }

            Volatile.Write(ref _loaded, true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log?.Warn("读取服务器列表失败", ex);

            // 读失败也算「已同步」：磁盘文件坏了，反复重试只会让导入按钮每次都失败一次。
            Volatile.Write(ref _loaded, true);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task AddAsync(ServerEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (string.IsNullOrWhiteSpace(entry.Host))
        {
            throw new ArgumentException("服务器地址不能为空", nameof(entry));
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ServerEntry? replaced = null;
            var replacedIndex = -1;

            lock (_servers)
            {
                var existingIndex = _servers.FindIndex(server =>
                    string.Equals(server.Host, entry.Host, StringComparison.OrdinalIgnoreCase)
                    && server.Port == entry.Port);

                if (existingIndex >= 0)
                {
                    replaced = _servers[existingIndex];
                    replacedIndex = existingIndex;
                    _servers.RemoveAt(existingIndex);
                }

                if (string.IsNullOrWhiteSpace(entry.Id))
                {
                    entry.Id = Guid.NewGuid().ToString("N");
                }

                _servers.Add(entry);
            }

            bool saved;
            try
            {
                saved = await SaveAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 取消落在「内存已改、落盘未完成」：回滚再重抛（REVIEW-R34-R36 P3-11）。
                RollbackAdd(entry, replaced, replacedIndex);
                throw;
            }

            if (saved)
            {
                return;
            }

            // 没落盘就把内存改回去：界面与磁盘保持一致，调用方收到异常后显示失败。
            RollbackAdd(entry, replaced, replacedIndex);

            throw SaveFailureException();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RemoveAsync(string id, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ServerEntry? removed = null;
            var removedIndex = -1;

            lock (_servers)
            {
                removedIndex = _servers.FindIndex(entry => string.Equals(entry.Id, id, StringComparison.Ordinal));
                if (removedIndex >= 0)
                {
                    removed = _servers[removedIndex];
                    _servers.RemoveAt(removedIndex);
                }
            }

            if (removed is null)
            {
                return;
            }

            bool removedSaved;
            try
            {
                removedSaved = await SaveAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                RollbackRemove(removed, removedIndex);
                throw;
            }

            if (removedSaved)
            {
                return;
            }

            // 删除没落盘：放回原位置，界面与磁盘一致，调用方收到异常。
            RollbackRemove(removed, removedIndex);

            throw SaveFailureException();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 从 <c>&lt;gameDirectory&gt;/servers.dat</c> 导入 Minecraft 自带的服务器列表。
    ///
    /// 合并规则：按 <see cref="ServerEntry.Address"/> 去重（不区分大小写），已存在的地址整条跳过——
    /// 用户在 Nebula 里改过的名字比 <c>servers.dat</c> 里的旧名字更可信。返回值只含本次新增的条目。
    /// </summary>
    public async Task<IReadOnlyList<ServerEntry>> ImportFromServersDatAsync(
        string gameDirectory,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(gameDirectory))
        {
            _log?.Warn("导入 servers.dat 失败：游戏目录为空");
            PublishImportSummary(new ServerImportSummary());
            return [];
        }

        // 没先与 servers.json 同步就落盘，会把磁盘上已有的收藏覆盖掉。
        if (!Volatile.Read(ref _loaded))
        {
            await LoadAsync(cancellationToken).ConfigureAwait(false);
        }

        var path = Path.Combine(gameDirectory, "servers.dat");
        if (!File.Exists(path))
        {
            _log?.Info($"没有找到 servers.dat：{path}");
            PublishImportSummary(new ServerImportSummary { FilePath = path, FileExists = false });
            return [];
        }

        // 文件解析是同步 IO，丢到线程池上跑，别卡 UI 线程。
        var parsed = await Task.Run(
                () => ServerListDatReader.TryRead(path, _log),
                cancellationToken)
            .ConfigureAwait(false);

        var added = new List<ServerEntry>();

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_servers)
            {
                foreach (var source in parsed)
                {
                    var address = source.Address;
                    if (ContainsAddress(_servers, address) || ContainsAddress(added, address))
                    {
                        continue;
                    }

                    var entry = new ServerEntry
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        Name = string.IsNullOrWhiteSpace(source.Name) ? source.Host : source.Name,
                        Host = source.Host,
                        Port = source.Port,
                    };

                    _servers.Add(entry);
                    added.Add(entry);
                }
            }

            if (added.Count > 0)
            {
                bool importSaved;
                try
                {
                    importSaved = await SaveAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    RollbackImport(added);
                    throw;
                }

                if (!importSaved)
                {
                    // 导入没落盘：撤掉本次内存新增，保持与磁盘一致，向调用方报失败。
                    RollbackImport(added);
                    throw SaveFailureException();
                }
            }
        }
        finally
        {
            _gate.Release();
        }

        PublishImportSummary(new ServerImportSummary
        {
            FilePath = path,
            FileExists = true,
            Total = parsed.Count,
            Added = added.Count,
            Skipped = parsed.Count - added.Count,
        });

        _log?.Info($"servers.dat 导入完成：{path} 共 {parsed.Count} 条，新增 {added.Count} 条");
        return added;
    }

    /// <inheritdoc />
    public ServerImportSummary? LastServersDatImport => Volatile.Read(ref _lastImport);

    public Task<ServerStatus> PingAsync(string host, int port, CancellationToken cancellationToken = default)
        => PingAsync(host, port, PingTimeout, cancellationToken);

    internal async Task<ServerStatus> PingAsync(
        string host,
        int port,
        TimeSpan pingTimeout,
        CancellationToken cancellationToken = default)
    {
        // 允许直接传入 "host:port"（例如 ServerEntry.Address）。
        // 规则与启动参数共用 ServerAddressParser：两边各写一套时 IPv6 的处理会分叉。
        if (!string.IsNullOrWhiteSpace(host))
        {
            var parsed = ServerAddressParser.Parse(host);

            if (parsed.PortSpecified)
            {
                port = parsed.Port;
            }

            // 方括号要去掉：Socket 只接受裸字面量。
            if (!string.Equals(parsed.Host, host, StringComparison.Ordinal))
            {
                host = parsed.Host;
            }
        }

        if (string.IsNullOrWhiteSpace(host))
        {
            return Offline(host ?? string.Empty, port, "服务器地址为空");
        }

        if (port <= 0 || port > 65535)
        {
            port = 25565;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(pingTimeout);
        var stopwatch = Stopwatch.StartNew();

        try
        {
            // 先做名称解析，把「域名写错」和「服务器没开」区分开。
            var addresses = await Dns.GetHostAddressesAsync(host, timeout.Token).ConfigureAwait(false);
            if (addresses.Length == 0)
            {
                return Offline(host, port, "无法解析服务器地址");
            }

            using var client = await ConnectAsync(addresses, port, timeout.Token).ConfigureAwait(false);

            await using var stream = client.GetStream();
            var handshake = MinecraftProtocol.Frame(
                MinecraftProtocol.BuildHandshakeBody(host, port, MinecraftProtocol.StatusProtocolVersion, 1));
            await stream.WriteAsync(handshake, timeout.Token).ConfigureAwait(false);
            await stream.WriteAsync(MinecraftProtocol.BuildStatusRequest(), timeout.Token).ConfigureAwait(false);
            await stream.FlushAsync(timeout.Token).ConfigureAwait(false);

            var length = await MinecraftProtocol.ReadVarIntAsync(stream, timeout.Token).ConfigureAwait(false);
            if (length <= 0 || length > 4 * 1024 * 1024)
            {
                return Offline(host, port, "服务器返回了异常的数据长度");
            }

            var buffer = new byte[length];
            await stream.ReadExactlyAsync(buffer, timeout.Token).ConfigureAwait(false);

            using var payload = new MemoryStream(buffer);
            var packetId = MinecraftProtocol.ReadVarInt(payload);
            if (packetId != 0x00)
            {
                return Offline(host, port, $"服务器返回了未知的数据包（id={packetId}）");
            }

            var json = MinecraftProtocol.ReadString(payload);
            var latency = (int)stopwatch.ElapsedMilliseconds;
            return ParseJson(host, port, json, latency);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Offline(host, port, "连接超时（5 秒）");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SocketException ex)
        {
            return Offline(host, port, MapSocketError(ex));
        }
        catch (Exception ex)
        {
            _log?.Warn($"Ping 服务器失败：{host}:{port}", ex);
            return Offline(host, port, $"查询失败：{ex.Message}");
        }
    }

    public void Dispose() => _gate.Dispose();

    // ---------------- 内部实现 ----------------

    private void PublishImportSummary(ServerImportSummary summary) => Volatile.Write(ref _lastImport, summary);

    private static bool ContainsAddress(IEnumerable<ServerEntry> entries, string address) =>
        entries.Any(entry => string.Equals(entry.Address, address, StringComparison.OrdinalIgnoreCase));

    private static async Task<TcpClient> ConnectAsync(
        IReadOnlyList<IPAddress> addresses,
        int port,
        CancellationToken cancellationToken)
    {
        SocketException? lastError = null;
        foreach (var address in addresses)
        {
            var client = new TcpClient(address.AddressFamily) { NoDelay = true };
            try
            {
                await client.ConnectAsync(address, port, cancellationToken).ConfigureAwait(false);
                return client;
            }
            catch (SocketException exception)
            {
                client.Dispose();
                lastError = exception;
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }

        throw lastError ?? new SocketException((int)SocketError.HostNotFound);
    }

    /// <summary>
    /// 把内存里的服务器列表写回磁盘。返回是否成功。
    ///
    /// 返回值必须被调用方检查：以前这里吞掉异常，界面显示「已添加」，
    /// 重启后收藏消失且没有任何提示（AUDIT-2026-10-23 P1-4）。
    /// </summary>
    private async Task<bool> SaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            List<ServerEntry> snapshot;
            lock (_servers)
            {
                snapshot = _servers.ToList();
            }

            var directory = Path.GetDirectoryName(_paths.ServersFile);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(snapshot, JsonOptions);
            var temporary = _paths.ServersFile + ".tmp";
            await File.WriteAllTextAsync(temporary, json, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, _paths.ServersFile, overwrite: true);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log?.Warn("保存服务器列表失败", ex);
            return false;
        }
    }

    /// <summary>保存失败时给用户的说明。调用方捕获后展示这一段，而不是自己拼文案。</summary>
    /// <summary>把「新增/替换」这一步的内存改动撤回原状（失败与取消两条路径共用）。</summary>
    private void RollbackAdd(ServerEntry entry, ServerEntry? replaced, int replacedIndex)
    {
        lock (_servers)
        {
            _servers.Remove(entry);

            if (replaced is not null)
            {
                _servers.Insert(Math.Clamp(replacedIndex, 0, _servers.Count), replaced);
            }
        }
    }

    /// <summary>把「删除」这一步的内存改动撤回原位置。</summary>
    private void RollbackRemove(ServerEntry removed, int removedIndex)
    {
        lock (_servers)
        {
            _servers.Insert(Math.Clamp(removedIndex, 0, _servers.Count), removed);
        }
    }

    /// <summary>撤掉本次导入新增的条目。</summary>
    private void RollbackImport(List<ServerEntry> added)
    {
        lock (_servers)
        {
            foreach (var entry in added)
            {
                _servers.Remove(entry);
            }
        }

        added.Clear();
    }

    private static IOException SaveFailureException() =>
        new("服务器列表写入磁盘失败：请检查数据目录权限或磁盘空间。");

    private static ServerStatus Offline(string host, int port, string error) => new()
    {
        Host = host,
        Port = port,
        Online = false,
        Error = error,
        CheckedAt = DateTimeOffset.UtcNow,
    };

    private static string MapSocketError(SocketException exception) => exception.SocketErrorCode switch
    {
        SocketError.ConnectionRefused => "服务器拒绝了连接（端口未开放或已关闭）",
        SocketError.HostNotFound or SocketError.NoData => "无法解析服务器地址",
        SocketError.TimedOut => "连接超时（5 秒）",
        SocketError.NetworkUnreachable or SocketError.HostUnreachable => "网络不可达",
        _ => $"网络错误：{exception.SocketErrorCode}",
    };

    private static ServerStatus ParseJson(string host, int port, string json, int latencyMs)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        var versionName = string.Empty;
        var protocolVersion = 0;
        if (root.TryGetProperty("version", out var version))
        {
            versionName = GetString(version, "name") ?? string.Empty;
            if (version.TryGetProperty("protocol", out var protocolNode) && protocolNode.TryGetInt32(out var protocol))
            {
                protocolVersion = protocol;
            }
        }

        var online = 0;
        var max = 0;
        if (root.TryGetProperty("players", out var players))
        {
            if (players.TryGetProperty("online", out var onlineNode) && onlineNode.TryGetInt32(out var parsedOnline))
            {
                online = parsedOnline;
            }

            if (players.TryGetProperty("max", out var maxNode) && maxNode.TryGetInt32(out var parsedMax))
            {
                max = parsedMax;
            }
        }

        var motdRaw = root.TryGetProperty("description", out var description)
            ? FlattenText(description)
            : string.Empty;

        var motd = FormatCodePattern.Replace(motdRaw, string.Empty);

        var favicon = root.TryGetProperty("favicon", out var faviconNode)
                      && faviconNode.ValueKind == JsonValueKind.String
            ? faviconNode.GetString()
            : null;

        return new ServerStatus
        {
            Host = host,
            Port = port,
            Online = true,
            LatencyMs = latencyMs,
            OnlinePlayers = online,
            MaxPlayers = max,
            Motd = motd,
            MotdRaw = string.IsNullOrEmpty(motdRaw) ? null : motdRaw,
            VersionName = string.IsNullOrEmpty(versionName) ? null : versionName,
            ProtocolVersion = protocolVersion,
            FaviconDataUri = string.IsNullOrEmpty(favicon) ? null : favicon,
            CheckedAt = DateTimeOffset.UtcNow,
        };
    }

    private static string? GetString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>把 MOTD 的 chat component 摊平成带 § 格式码的纯文本。</summary>
    private static string FlattenText(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                return element.GetString() ?? string.Empty;

            case JsonValueKind.Array:
                return string.Concat(element.EnumerateArray().Select(FlattenText));

            case JsonValueKind.Object:
                var builder = new System.Text.StringBuilder();
                if (element.TryGetProperty("color", out var color) && color.ValueKind == JsonValueKind.String)
                {
                    var code = ToFormatCode(color.GetString());
                    if (!string.IsNullOrEmpty(code))
                    {
                        builder.Append(code);
                    }
                }

                if (element.TryGetProperty("bold", out var bold) && bold.ValueKind == JsonValueKind.True)
                {
                    builder.Append("§l");
                }

                if (element.TryGetProperty("italic", out var italic) && italic.ValueKind == JsonValueKind.True)
                {
                    builder.Append("§o");
                }

                if (element.TryGetProperty("underlined", out var underlined) && underlined.ValueKind == JsonValueKind.True)
                {
                    builder.Append("§n");
                }

                if (element.TryGetProperty("text", out var text))
                {
                    builder.Append(FlattenText(text));
                }

                if (element.TryGetProperty("extra", out var extra))
                {
                    builder.Append(FlattenText(extra));
                }

                return builder.ToString();

            default:
                return string.Empty;
        }
    }

    private static string? ToFormatCode(string? color) => color switch
    {
        "black" => "§0",
        "dark_blue" => "§1",
        "dark_green" => "§2",
        "dark_aqua" => "§3",
        "dark_red" => "§4",
        "dark_purple" => "§5",
        "gold" => "§6",
        "gray" => "§7",
        "dark_gray" => "§8",
        "blue" => "§9",
        "green" => "§a",
        "aqua" => "§b",
        "red" => "§c",
        "light_purple" => "§d",
        "yellow" => "§e",
        "white" => "§f",
        _ => null,
    };
}
