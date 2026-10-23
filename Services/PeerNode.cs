using System.Buffers.Binary;
using System.IO;
using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OrtakAlan;

public sealed class PeerNode : IDisposable
{
    const int DiscoveryPort = 47470;
    const long MaxFileBytes = 150L * 1024 * 1024;
    static readonly IPAddress Multicast = IPAddress.Parse("239.255.74.70");

    readonly SettingsModel _settings;
    readonly SettingsService _store;
    readonly Func<string, string, Task<bool?>> _approve;
    readonly string _beaconDir;
    readonly CancellationTokenSource _cts = new();
    readonly object _peerLock = new();
    readonly Dictionary<string, PeerState> _peers = new();
    readonly ConcurrentDictionary<string, Link> _links = new();
    readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new();
    readonly ConcurrentDictionary<string, Lazy<Task<bool?>>> _approvals = new();
    readonly HashSet<string> _denied = new();

    TcpListener? _listener;
    UdpClient? _udp;
    int _tcpPort;

    public event Action<IReadOnlyList<PeerSnapshot>>? PeersUpdated;
    public event Action<IncomingChat>? ChatReceived;
    public event Action<IncomingFile>? FileReceived;

    public PeerNode(SettingsModel settings, SettingsService store, Func<string, string, Task<bool?>> approve)
    {
        _settings = settings;
        _store = store;
        _approve = approve;
        _beaconDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OrtakAlanKesif");
    }

    public void Start()
    {
        Directory.CreateDirectory(_beaconDir);
        StartTcp();
        StartUdp();
        TailscalePeers.Start(_cts.Token);
        _ = Task.Run(BeaconLoop);
        _ = Task.Run(SweepLoop);
    }

    public void Announce()
    {
        try { WriteBeacon(); }
        catch (Exception ex) { Log.Write(ex); }
    }

    public async Task<string?> SendChatAsync(string peerId, string text)
    {
        if (text.Length > 4000) text = text[..4000];
        var (link, error) = await EnsureAsync(peerId);
        if (link == null) return error;
        await link.Send(Obj(("t", "chat"), ("text", text)));
        return null;
    }

    public async Task<string?> SendFileAsync(string peerId, string path)
    {
        if (!File.Exists(path)) return "Dosya bulunamadı";
        var info = new FileInfo(path);
        if (info.Length > MaxFileBytes) return "Dosya 150 MB sınırını aşıyor";
        var (link, error) = await EnsureAsync(peerId);
        if (link == null) return error;

        var id = Guid.NewGuid().ToString("N");
        var name = Path.GetFileName(path);
        await link.Send(Obj(("t", "file-begin"), ("id", id), ("name", name), ("size", info.Length)));
        await using var stream = File.OpenRead(path);
        var buffer = new byte[64 * 1024];
        var index = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer, _cts.Token)) > 0)
        {
            var slice = new byte[read];
            Buffer.BlockCopy(buffer, 0, slice, 0, read);
            await link.Send(Obj(("t", "file-chunk"), ("id", id), ("i", index++), ("data", Convert.ToBase64String(slice))));
        }
        await link.Send(Obj(("t", "file-end"), ("id", id)));
        return null;
    }

    public async Task<string?> ConnectAsync(string peerId)
    {
        var (link, error) = await EnsureAsync(peerId);
        return link == null ? error : null;
    }

    public async Task<(string? Error, List<DirEntry> Entries)> ListAsync(string peerId, string shareId, string rel)
    {
        var (link, error) = await EnsureAsync(peerId);
        if (link == null) return (error, new());
        var req = Guid.NewGuid().ToString("N");
        var waiter = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        link.Pending[req] = waiter;
        await link.Send(Obj(("t", "list"), ("req", req), ("share", shareId), ("rel", rel)));
        var finished = await Task.WhenAny(waiter.Task, Task.Delay(20000));
        if (finished != waiter.Task) return ("Klasör listesi gelmedi", new());
        var message = await waiter.Task;
        var problem = Str(message, "error");
        if (problem.Length > 0) return (problem, new());
        var entries = new List<DirEntry>();
        if (message.TryGetProperty("entries", out var array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in array.EnumerateArray())
            {
                entries.Add(new DirEntry
                {
                    Name = Str(item, "name"),
                    IsDir = item.TryGetProperty("dir", out var dir) && dir.ValueKind == JsonValueKind.True,
                    Size = item.TryGetProperty("size", out var size) && size.TryGetInt64(out var n) ? n : 0,
                    Modified = item.TryGetProperty("mtime", out var mt) && mt.TryGetInt64(out var ticks) ? ticks : 0
                });
            }
        }
        return (null, entries);
    }

    public Task<(string? Error, string? Path)> DownloadAsync(string peerId, string shareId, string rel, string fileName) =>
        DownloadToAsync(peerId, shareId, rel, fileName, null);

    public async Task<(string? Error, string? Path)> DownloadToAsync(string peerId, string shareId, string rel, string fileName, string? destination)
    {
        var (link, error) = await EnsureAsync(peerId);
        if (link == null) return (error, null);
        var id = Guid.NewGuid().ToString("N");
        destination ??= UniqueDownload(fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var waiter = new DownloadWait { FinalPath = destination };
        link.Downloads[id] = waiter;
        await link.Send(Obj(("t", "get-file"), ("id", id), ("share", shareId), ("rel", rel)));
        var finished = await Task.WhenAny(waiter.Done.Task, Task.Delay(TimeSpan.FromMinutes(3)));
        if (finished != waiter.Done.Task) return ("Dosya gelmedi", null);
        try
        {
            return (null, await waiter.Done.Task);
        }
        catch (Exception ex)
        {
            Log.Write(ex);
            return ("Dosya alınamadı", null);
        }
    }

    public Task<string?> MakeDirAsync(string peerId, string shareId, string rel, string name) =>
        FsAsync(peerId, Obj(("t", "mkdir"), ("share", shareId), ("rel", rel), ("name", name)));

    public Task<string?> DeleteAsync(string peerId, string shareId, string rel) =>
        FsAsync(peerId, Obj(("t", "delete"), ("share", shareId), ("rel", rel)));

    public Task<string?> RenameAsync(string peerId, string shareId, string rel, string name) =>
        FsAsync(peerId, Obj(("t", "rename"), ("share", shareId), ("rel", rel), ("name", name)));

    public Task<string?> CopyAsync(string peerId, string shareId, string rel, string to) =>
        FsAsync(peerId, Obj(("t", "copy"), ("share", shareId), ("rel", rel), ("to", to)));

    public Task<string?> MoveAsync(string peerId, string shareId, string rel, string to) =>
        FsAsync(peerId, Obj(("t", "move"), ("share", shareId), ("rel", rel), ("to", to)));

    public async Task<string?> UploadAsync(string peerId, string shareId, string rel, string path)
    {
        if (!File.Exists(path)) return "Dosya bulunamadı";
        var info = new FileInfo(path);
        if (info.Length > MaxFileBytes) return "Dosya 150 MB sınırını aşıyor";
        var (link, error) = await EnsureAsync(peerId);
        if (link == null) return error;

        var id = Guid.NewGuid().ToString("N");
        var waiter = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        link.Pending[id] = waiter;
        await link.Send(Obj(("t", "put-begin"), ("id", id), ("share", shareId), ("rel", rel), ("name", info.Name), ("size", info.Length)));
        if (waiter.Task.IsCompleted) return await ReadFs(waiter, TimeSpan.FromSeconds(5), link, id);

        await using var stream = File.OpenRead(path);
        var buffer = new byte[64 * 1024];
        var index = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer, _cts.Token)) > 0)
        {
            if (waiter.Task.IsCompleted) return await ReadFs(waiter, TimeSpan.FromSeconds(5), link, id);
            var slice = new byte[read];
            Buffer.BlockCopy(buffer, 0, slice, 0, read);
            await link.Send(Obj(("t", "file-chunk"), ("id", id), ("i", index++), ("data", Convert.ToBase64String(slice))));
        }
        await link.Send(Obj(("t", "file-end"), ("id", id)));
        var minutes = Math.Clamp((int)(info.Length / (1024 * 1024)) + 1, 1, 10);
        return await ReadFs(waiter, TimeSpan.FromMinutes(minutes), link, id);
    }

    async Task<string?> FsAsync(string peerId, JsonObject body)
    {
        var (link, error) = await EnsureAsync(peerId);
        if (link == null) return error;
        var req = Guid.NewGuid().ToString("N");
        body["req"] = req;
        var waiter = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        link.Pending[req] = waiter;
        await link.Send(body);
        return await ReadFs(waiter, TimeSpan.FromSeconds(60), link, req);
    }

    static async Task<string?> ReadFs(TaskCompletionSource<JsonElement> waiter, TimeSpan timeout, Link link, string req)
    {
        var finished = await Task.WhenAny(waiter.Task, Task.Delay(timeout));
        if (finished != waiter.Task)
        {
            link.Pending.TryRemove(req, out _);
            return "Yanıt gelmedi";
        }
        try
        {
            var message = await waiter.Task;
            var problem = Str(message, "error");
            return problem.Length == 0 ? null : problem;
        }
        catch (Exception ex)
        {
            Log.Write(ex);
            return "Bağlantı koptu";
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener?.Stop(); } catch { /* kapanış */ }
        try { _udp?.Dispose(); } catch { /* kapanış */ }
        foreach (var link in _links.Values) link.Drop();
        try { File.Delete(BeaconPath()); } catch { /* kapanış */ }
        _cts.Dispose();
    }

    async Task<(Link? Link, string? Error)> EnsureAsync(string peerId)
    {
        if (_denied.Contains(peerId)) return (null, "Bu bilgisayar isteği reddetti.");
        PeerState? peer;
        lock (_peerLock) _peers.TryGetValue(peerId, out peer);
        if (peer == null || !peer.IsOnline || peer.Port <= 0)
            return (null, "Çevrimdışı");

        var gate = _gates.GetOrAdd(peerId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(_cts.Token);
        try
        {
            if (_links.TryGetValue(peerId, out var existing) && existing.Alive)
                return (existing, null);
            return await DialAsync(peer);
        }
        catch (Exception ex)
        {
            Log.Write(ex);
            return (null, "Bağlanılamadı");
        }
        finally
        {
            gate.Release();
        }
    }

    async Task<(Link? Link, string? Error)> DialAsync(PeerState peer)
    {
        var client = new TcpClient { NoDelay = true };
        using var connect = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        connect.CancelAfter(TailscalePeers.IsAddress(peer.Ip) ? TimeSpan.FromSeconds(15) : TimeSpan.FromSeconds(5));
        await client.ConnectAsync(peer.Ip, peer.Port, connect.Token);
        var link = new Link(client, peer.Id, peer.Name);
        await link.Send(Obj(("t", "intro"), ("id", _settings.ComputerId), ("name", _settings.ComputerName)));
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        wait.CancelAfter(TimeSpan.FromSeconds(90));
        var reply = await link.ReadOne(wait.Token);
        if (reply == null)
        {
            link.Drop();
            return (null, "Cevap gelmedi");
        }

        var kind = Str(reply.Value, "t");
        if (kind == "denied")
        {
            _denied.Add(peer.Id);
            link.Drop();
            return (null, "Bu bilgisayar isteği reddetti.");
        }
        if (kind == "later")
        {
            link.Drop();
            return (null, "İzin bekleniyor. Karşı pencerede cevap verin.");
        }
        if (kind != "welcome")
        {
            link.Drop();
            return (null, "Bağlanılamadı");
        }

        ReplaceLink(link);
        _ = Task.Run(() => ReadLoop(link));
        return (link, null);
    }

    void StartTcp()
    {
        _listener = new TcpListener(IPAddress.Any, 0);
        _listener.Start(200);
        _tcpPort = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(_cts.Token); }
                catch { break; }
                client.NoDelay = true;
                _ = Task.Run(() => HandleClient(client));
            }
        });
    }

    async Task HandleClient(TcpClient client)
    {
        var link = new Link(client, "", "");
        try
        {
            using var introWait = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            introWait.CancelAfter(TimeSpan.FromSeconds(10));
            var first = await link.ReadOne(introWait.Token);
            if (first == null || Str(first.Value, "t") != "intro")
            {
                link.Drop();
                return;
            }

            var id = Str(first.Value, "id");
            var name = Str(first.Value, "name");
            if (id.Length == 0 || id == _settings.ComputerId)
            {
                link.Drop();
                return;
            }

            link.PeerId = id;
            link.PeerName = string.IsNullOrWhiteSpace(name) ? "Bilgisayar" : name;
            var decision = await ApproveOnce(id, link.PeerName);
            if (decision == true)
            {
                await link.Send(Obj(("t", "welcome")));
                ReplaceLink(link);
                await ReadLoop(link);
            }
            else if (decision == false)
            {
                await link.Send(Obj(("t", "denied")));
                link.Drop();
            }
            else
            {
                await link.Send(Obj(("t", "later")));
                link.Drop();
            }
        }
        catch (Exception ex)
        {
            Log.Write(ex);
            link.Drop();
        }
    }

    Task<bool?> ApproveOnce(string id, string name)
    {
        var lazy = _approvals.GetOrAdd(id, _ => new Lazy<Task<bool?>>(() => AskCore(id, name)));
        return lazy.Value;
    }

    async Task<bool?> AskCore(string id, string name)
    {
        try
        {
            if (_store.IsApproved(id)) return true;
            if (_store.IsRejected(id)) return false;
            var decision = await _approve(id, name);
            if (decision == true) _store.Decide(id, name, true);
            else if (decision == false) _store.Decide(id, name, false);
            return decision;
        }
        finally
        {
            _approvals.TryRemove(id, out _);
        }
    }

    async Task ReadLoop(Link link)
    {
        try
        {
            while (link.Alive && !_cts.IsCancellationRequested)
            {
                var message = await link.ReadOne(_cts.Token);
                if (message == null) break;
                await Dispatch(link, message.Value);
            }
        }
        catch (Exception ex)
        {
            if (link.Alive && !_cts.IsCancellationRequested) Log.Write(ex);
        }
        finally
        {
            link.Drop();
            if (_links.TryGetValue(link.PeerId, out var current) && current == link)
                _links.TryRemove(link.PeerId, out _);
        }
    }

    async Task Dispatch(Link link, JsonElement message)
    {
        var kind = Str(message, "t");
        var req = Str(message, "req");
        if (req.Length > 0 && (kind is "dir" or "fs") && link.Pending.TryRemove(req, out var waiter))
        {
            waiter.TrySetResult(message);
            return;
        }

        switch (kind)
        {
            case "chat":
                Ui(() => ChatReceived?.Invoke(new IncomingChat(link.PeerId, link.PeerName, Str(message, "text"))));
                break;
            case "list":
                await ReplyList(link, message);
                break;
            case "get-file":
                await ReplyFile(link, message);
                break;
            case "mkdir":
                await ReplyMkdir(link, message);
                break;
            case "delete":
                await ReplyDelete(link, message);
                break;
            case "rename":
                await ReplyRename(link, message);
                break;
            case "copy":
                await ReplyCopy(link, message);
                break;
            case "move":
                await ReplyMove(link, message);
                break;
            case "put-begin":
                await BeginPut(link, message);
                break;
            case "file-begin":
                BeginIncoming(link, message);
                break;
            case "file-chunk":
                WriteChunk(link, message);
                break;
            case "file-end":
                await FinishIncoming(link, Str(message, "id"), null);
                break;
            case "file-error":
                await FinishIncoming(link, Str(message, "id"), Str(message, "error"));
                break;
        }
    }

    async Task ReplyList(Link link, JsonElement message)
    {
        var req = Str(message, "req");
        var entries = new JsonArray();
        string? error = null;
        if (!TryResolve(Str(message, "share"), Str(message, "rel"), false, out var full, out error))
        {
            await link.Send(Obj(("t", "dir"), ("req", req), ("error", error ?? "Paylaşım yok")));
            return;
        }

        if (!Directory.Exists(full))
        {
            await link.Send(Obj(("t", "dir"), ("req", req), ("error", "Klasör bulunamadı")));
            return;
        }

        foreach (var dir in Directory.EnumerateDirectories(full).Take(2000))
        {
            entries.Add(new JsonObject
            {
                ["name"] = Path.GetFileName(dir),
                ["dir"] = true,
                ["size"] = 0,
                ["mtime"] = Directory.GetLastWriteTimeUtc(dir).Ticks
            });
        }
        foreach (var file in Directory.EnumerateFiles(full).Take(2000))
        {
            var name = Path.GetFileName(file);
            if (name.StartsWith('.')) continue;
            entries.Add(new JsonObject
            {
                ["name"] = name,
                ["dir"] = false,
                ["size"] = new FileInfo(file).Length,
                ["mtime"] = File.GetLastWriteTimeUtc(file).Ticks
            });
        }

        await link.Send(new JsonObject
        {
            ["t"] = "dir",
            ["req"] = req,
            ["entries"] = entries
        });
    }

    async Task ReplyFile(Link link, JsonElement message)
    {
        var id = Str(message, "id");
        if (!TryResolve(Str(message, "share"), Str(message, "rel"), true, out var full, out var error))
        {
            await link.Send(Obj(("t", "file-error"), ("id", id), ("error", error ?? "Dosya yok")));
            return;
        }

        var info = new FileInfo(full);
        if (!info.Exists || info.Length > MaxFileBytes)
        {
            await link.Send(Obj(("t", "file-error"), ("id", id), ("error", "Dosya gönderilemedi")));
            return;
        }

        await link.Send(Obj(("t", "file-begin"), ("id", id), ("name", info.Name), ("size", info.Length)));
        await using var stream = File.OpenRead(full);
        var buffer = new byte[64 * 1024];
        var index = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer, _cts.Token)) > 0)
        {
            var slice = new byte[read];
            Buffer.BlockCopy(buffer, 0, slice, 0, read);
            await link.Send(Obj(("t", "file-chunk"), ("id", id), ("i", index++), ("data", Convert.ToBase64String(slice))));
        }
        await link.Send(Obj(("t", "file-end"), ("id", id)));
    }

    void BeginIncoming(Link link, JsonElement message)
    {
        var id = Str(message, "id");
        var name = Path.GetFileName(Str(message, "name"));
        if (!SafeFileName(name))
        {
            _ = FinishIncoming(link, id, "Dosya adı geçersiz");
            return;
        }

        var size = message.TryGetProperty("size", out var sizeEl) && sizeEl.TryGetInt64(out var n) ? n : 0;
        if (size > MaxFileBytes)
        {
            _ = FinishIncoming(link, id, "Dosya 150 MB sınırını aşıyor");
            return;
        }

        link.Downloads.TryRemove(id, out var waiter);
        var temp = Path.Combine(Path.GetTempPath(), "ortakalan-" + id);
        var transfer = new IncomingTransfer
        {
            Name = name,
            Temp = temp,
            Stream = File.Create(temp),
            Waiter = waiter
        };
        link.Incoming[id] = transfer;
    }

    static void WriteChunk(Link link, JsonElement message)
    {
        var id = Str(message, "id");
        if (!link.Incoming.TryGetValue(id, out var transfer) || transfer.Stream == null)
            return;
        var data = Str(message, "data");
        if (data.Length == 0) return;
        try
        {
            var bytes = Convert.FromBase64String(data);
            transfer.Stream.Write(bytes, 0, bytes.Length);
        }
        catch (Exception ex)
        {
            Log.Write(ex);
        }
    }

    async Task FinishIncoming(Link link, string id, string? error)
    {
        if (!link.Incoming.TryRemove(id, out var transfer))
        {
            if (link.Downloads.TryRemove(id, out var pending))
                pending.Done.TrySetException(new IOException(error ?? "Dosya alınamadı"));
            return;
        }

        try { transfer.Stream?.Dispose(); } catch { /* kapanış */ }
        if (!string.IsNullOrEmpty(error))
        {
            try { if (File.Exists(transfer.Temp)) File.Delete(transfer.Temp); } catch { /* kapanış */ }
            if (transfer.DestPath != null)
                await SendFs(link, id, error);
            else
                transfer.Waiter?.Done.TrySetException(new IOException(error));
            return;
        }

        if (transfer.DestPath != null)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(transfer.DestPath)!);
                if (File.Exists(transfer.DestPath)) File.Delete(transfer.DestPath);
                File.Move(transfer.Temp, transfer.DestPath);
                await SendFs(link, id, null);
            }
            catch (Exception ex)
            {
                Log.Write(ex);
                try { if (File.Exists(transfer.Temp)) File.Delete(transfer.Temp); } catch { /* kapanış */ }
                await SendFs(link, id, "Dosya yazılamadı");
            }
            return;
        }

        if (transfer.Waiter != null)
        {
            var finalPath = transfer.Waiter.FinalPath;
            Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
            if (File.Exists(finalPath)) File.Delete(finalPath);
            File.Move(transfer.Temp, finalPath);
            transfer.Waiter.Done.TrySetResult(finalPath);
            return;
        }

        string saved;
        try
        {
            saved = SharedFiles.TakeIncoming(transfer.Temp, link.PeerName, transfer.Name);
        }
        catch (Exception ex)
        {
            Log.Write(ex);
            saved = UniqueDownload(transfer.Name);
            Directory.CreateDirectory(Path.GetDirectoryName(saved)!);
            if (File.Exists(saved)) File.Delete(saved);
            File.Move(transfer.Temp, saved);
        }
        var received = saved;
        Ui(() => FileReceived?.Invoke(new IncomingFile(link.PeerId, link.PeerName, transfer.Name, received)));
    }

    bool TryResolve(string shareId, string rel, bool wantFile, out string full, out string? error)
    {
        full = "";
        error = null;
        var share = _store.CopyShares().FirstOrDefault(s => s.Id == shareId && s.Kind == "folder");
        if (share == null || !Directory.Exists(share.Path))
        {
            error = "Paylaşım yok";
            return false;
        }

        var root = Path.GetFullPath(share.Path);
        var combined = string.IsNullOrEmpty(rel)
            ? root
            : Path.GetFullPath(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)));
        var rootPrefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!combined.Equals(root, StringComparison.OrdinalIgnoreCase) &&
            !combined.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            error = "Geçersiz yol";
            return false;
        }

        if (wantFile && !File.Exists(combined))
        {
            error = "Dosya bulunamadı";
            return false;
        }

        full = combined;
        return true;
    }

    async Task ReplyMkdir(Link link, JsonElement message)
    {
        var req = Str(message, "req");
        var name = Path.GetFileName(Str(message, "name"));
        if (!SafeFileName(name))
        {
            await SendFs(link, req, "Ad geçersiz");
            return;
        }
        if (!TryResolve(Str(message, "share"), ChildRel(Str(message, "rel"), name), false, out var full, out var error))
        {
            await SendFs(link, req, error ?? "Paylaşım yok");
            return;
        }
        try
        {
            if (File.Exists(full))
            {
                await SendFs(link, req, "Bu ad kullanılıyor");
                return;
            }
            Directory.CreateDirectory(full);
            await SendFs(link, req, null);
        }
        catch (Exception ex)
        {
            Log.Write(ex);
            await SendFs(link, req, "Klasör oluşturulamadı");
        }
    }

    async Task ReplyDelete(Link link, JsonElement message)
    {
        var req = Str(message, "req");
        var rel = Str(message, "rel");
        if (string.IsNullOrEmpty(rel))
        {
            await SendFs(link, req, "Kök silinemez");
            return;
        }
        if (!TryResolve(Str(message, "share"), rel, false, out var full, out var error))
        {
            await SendFs(link, req, error ?? "Paylaşım yok");
            return;
        }
        try
        {
            if (Directory.Exists(full)) Directory.Delete(full, true);
            else if (File.Exists(full)) File.Delete(full);
            else
            {
                await SendFs(link, req, "Bulunamadı");
                return;
            }
            await SendFs(link, req, null);
        }
        catch (Exception ex)
        {
            Log.Write(ex);
            await SendFs(link, req, "Silinemedi");
        }
    }

    async Task ReplyRename(Link link, JsonElement message)
    {
        var req = Str(message, "req");
        var rel = Str(message, "rel");
        var name = Path.GetFileName(Str(message, "name"));
        if (string.IsNullOrEmpty(rel) || !SafeFileName(name))
        {
            await SendFs(link, req, "Ad geçersiz");
            return;
        }
        if (!TryResolve(Str(message, "share"), rel, false, out var full, out var error))
        {
            await SendFs(link, req, error ?? "Paylaşım yok");
            return;
        }
        if (!TryResolve(Str(message, "share"), ChildRel(ParentRel(rel), name), false, out var dest, out error))
        {
            await SendFs(link, req, error ?? "Geçersiz yol");
            return;
        }
        await MovePath(link, req, full, dest);
    }

    async Task ReplyCopy(Link link, JsonElement message)
    {
        var req = Str(message, "req");
        if (!TryPair(message, out var full, out var dest, out var error))
        {
            await SendFs(link, req, error ?? "Geçersiz yol");
            return;
        }
        if (IsInside(full, dest))
        {
            await SendFs(link, req, "Klasör kendi içine kopyalanamaz");
            return;
        }
        try
        {
            if (Directory.Exists(full)) CopyTree(full, dest);
            else if (File.Exists(full))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(full, dest, true);
            }
            else
            {
                await SendFs(link, req, "Bulunamadı");
                return;
            }
            await SendFs(link, req, null);
        }
        catch (Exception ex)
        {
            Log.Write(ex);
            await SendFs(link, req, "Kopyalanamadı");
        }
    }

    async Task ReplyMove(Link link, JsonElement message)
    {
        var req = Str(message, "req");
        if (!TryPair(message, out var full, out var dest, out var error))
        {
            await SendFs(link, req, error ?? "Geçersiz yol");
            return;
        }
        if (IsInside(full, dest))
        {
            await SendFs(link, req, "Klasör kendi içine taşınamaz");
            return;
        }
        await MovePath(link, req, full, dest);
    }

    async Task BeginPut(Link link, JsonElement message)
    {
        var id = Str(message, "id");
        var name = Path.GetFileName(Str(message, "name"));
        if (!SafeFileName(name))
        {
            await SendFs(link, id, "Dosya adı geçersiz");
            return;
        }
        var size = message.TryGetProperty("size", out var sizeEl) && sizeEl.TryGetInt64(out var n) ? n : 0;
        if (size > MaxFileBytes)
        {
            await SendFs(link, id, "Dosya 150 MB sınırını aşıyor");
            return;
        }
        if (!TryResolve(Str(message, "share"), ChildRel(Str(message, "rel"), name), false, out var dest, out var error))
        {
            await SendFs(link, id, error ?? "Paylaşım yok");
            return;
        }
        var parent = Path.GetDirectoryName(dest);
        if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent))
        {
            await SendFs(link, id, "Klasör bulunamadı");
            return;
        }
        var temp = Path.Combine(Path.GetTempPath(), "ortakalan-" + id);
        link.Incoming[id] = new IncomingTransfer
        {
            Name = name,
            Temp = temp,
            Stream = File.Create(temp),
            DestPath = dest
        };
    }

    async Task MovePath(Link link, string req, string full, string dest)
    {
        try
        {
            if (string.Equals(full, dest, StringComparison.OrdinalIgnoreCase))
            {
                await SendFs(link, req, null);
                return;
            }
            if (File.Exists(dest) || Directory.Exists(dest))
            {
                await SendFs(link, req, "Bu ad kullanılıyor");
                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            if (Directory.Exists(full)) Directory.Move(full, dest);
            else if (File.Exists(full)) File.Move(full, dest);
            else
            {
                await SendFs(link, req, "Bulunamadı");
                return;
            }
            await SendFs(link, req, null);
        }
        catch (Exception ex)
        {
            Log.Write(ex);
            await SendFs(link, req, "Taşınamadı");
        }
    }

    bool TryPair(JsonElement message, out string full, out string dest, out string? error)
    {
        dest = "";
        var share = Str(message, "share");
        var rel = Str(message, "rel");
        var to = Str(message, "to");
        if (string.IsNullOrEmpty(rel) || string.IsNullOrEmpty(to))
        {
            full = "";
            error = "Geçersiz yol";
            return false;
        }
        if (!TryResolve(share, rel, false, out full, out error)) return false;
        return TryResolve(share, to, false, out dest, out error);
    }

    static bool IsInside(string source, string dest)
    {
        var root = source.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = dest.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(source, dest, StringComparison.OrdinalIgnoreCase);
    }

    static void CopyTree(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), true);
        foreach (var dir in Directory.EnumerateDirectories(source))
            CopyTree(dir, Path.Combine(dest, Path.GetFileName(dir)));
    }

    static string ChildRel(string rel, string name) => string.IsNullOrEmpty(rel) ? name : rel + "/" + name;

    static string ParentRel(string rel)
    {
        var slash = rel.LastIndexOf('/');
        return slash < 0 ? "" : rel[..slash];
    }

    static Task SendFs(Link link, string req, string? error) =>
        link.Send(error == null
            ? Obj(("t", "fs"), ("req", req))
            : Obj(("t", "fs"), ("req", req), ("error", error)));

    void StartUdp()
    {
        try
        {
            _udp = new UdpClient();
            _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _udp.ExclusiveAddressUse = false;
            _udp.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoveryPort));
            _udp.EnableBroadcast = true;
            _udp.JoinMulticastGroup(Multicast);
            _udp.MulticastLoopback = true;
            _ = Task.Run(UdpLoop);
        }
        catch (Exception ex)
        {
            Log.Write(ex);
            try { _udp?.Dispose(); } catch { /* kapanış */ }
            _udp = null;
        }
    }

    async Task UdpLoop()
    {
        while (!_cts.IsCancellationRequested && _udp != null)
        {
            try
            {
                var result = await _udp.ReceiveAsync(_cts.Token);
                var json = Encoding.UTF8.GetString(result.Buffer);
                var lan = !IPAddress.IsLoopback(result.RemoteEndPoint.Address);
                HandlePacket(json, result.RemoteEndPoint.Address, lan);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                if (!_cts.IsCancellationRequested) Log.Write(ex);
                break;
            }
        }
    }

    async Task BeaconLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                WriteBeacon();
                ReadLocalBeacons();
            }
            catch (Exception ex) { Log.Write(ex); }
            try { await Task.Delay(2000, _cts.Token); }
            catch { break; }
        }
    }

    async Task SweepLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            try { await Task.Delay(2000, _cts.Token); }
            catch { break; }
            Publish();
        }
    }

    void WriteBeacon()
    {
        if (_tcpPort <= 0) return;
        var shares = new JsonArray();
        foreach (var share in _store.CopyShares())
        {
            shares.Add(new JsonObject
            {
                ["id"] = share.Id,
                ["k"] = share.Kind,
                ["n"] = share.Name,
                ["w"] = share.WindowsShare
            });
        }

        var obj = new JsonObject
        {
            ["id"] = _settings.ComputerId,
            ["name"] = _settings.ComputerName,
            ["port"] = _tcpPort,
            ["utc"] = DateTime.UtcNow.ToString("o"),
            ["shares"] = shares
        };
        if (TailscalePeers.Self is { } tailscale)
            obj["ts"] = tailscale.ToString();
        var json = obj.ToJsonString();
        var path = BeaconPath();
        var temp = path + ".tmp";
        File.WriteAllText(temp, json);
        File.Move(temp, path, true);
        SendUdp(json);
    }

    void ReadLocalBeacons()
    {
        if (!Directory.Exists(_beaconDir)) return;
        foreach (var file in Directory.EnumerateFiles(_beaconDir, "*.json"))
        {
            try
            {
                var json = File.ReadAllText(file);
                using var doc = JsonDocument.Parse(json);
                var utc = Str(doc.RootElement, "utc");
                if (DateTime.TryParse(utc, null, System.Globalization.DateTimeStyles.RoundtripKind, out var seen) &&
                    DateTime.UtcNow - seen.ToUniversalTime() > TimeSpan.FromSeconds(8))
                {
                    if (DateTime.UtcNow - seen.ToUniversalTime() > TimeSpan.FromSeconds(30) &&
                        !file.EndsWith(_settings.ComputerId + ".json", StringComparison.OrdinalIgnoreCase))
                    {
                        File.Delete(file);
                    }
                    continue;
                }
                HandlePacket(json, IPAddress.Loopback, false);
            }
            catch (Exception ex) { Log.Write(ex); }
        }
    }

    void SendUdp(string json)
    {
        if (_udp == null) return;
        var bytes = Encoding.UTF8.GetBytes(json);
        void Send(IPEndPoint endPoint)
        {
            try { _udp.Send(bytes, bytes.Length, endPoint); }
            catch { /* ağ kapalı olabilir */ }
        }

        Send(new IPEndPoint(Multicast, DiscoveryPort));
        Send(new IPEndPoint(IPAddress.Broadcast, DiscoveryPort));
        Send(new IPEndPoint(IPAddress.Loopback, DiscoveryPort));
        foreach (var address in SubnetBroadcasts())
            Send(new IPEndPoint(address, DiscoveryPort));
        foreach (var address in TailscalePeers.Current)
            Send(new IPEndPoint(address, DiscoveryPort));
    }

    static IEnumerable<IPAddress> SubnetBroadcasts()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork || unicast.IPv4Mask == null)
                    continue;
                var ip = unicast.Address.GetAddressBytes();
                var mask = unicast.IPv4Mask.GetAddressBytes();
                var broadcast = new byte[4];
                for (var i = 0; i < 4; i++) broadcast[i] = (byte)(ip[i] | ~mask[i]);
                yield return new IPAddress(broadcast);
            }
        }
    }

    void HandlePacket(string json, IPAddress ip, bool lan)
    {
        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(json);
            root = doc.RootElement.Clone();
        }
        catch { return; }

        var id = Str(root, "id");
        if (id.Length == 0 || id == _settings.ComputerId) return;
        var name = Str(root, "name");
        var port = root.TryGetProperty("port", out var portEl) && portEl.TryGetInt32(out var portNo) ? portNo : 0;
        IPAddress? announced = null;
        if (IPAddress.TryParse(Str(root, "ts"), out var tailscale) && TailscalePeers.IsAddress(tailscale))
            announced = tailscale;
        var shares = new List<RemoteShare>();
        if (root.TryGetProperty("shares", out var array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in array.EnumerateArray())
            {
                var shareId = Str(item, "id");
                var shareName = Str(item, "n");
                if (shareId.Length == 0 || shareName.Length == 0) continue;
                var windows = Str(item, "w");
                shares.Add(new RemoteShare
                {
                    Id = shareId,
                    Kind = Str(item, "k"),
                    Name = shareName,
                    WindowsShare = windows.Length == 0 ? null : windows
                });
            }
        }

        lock (_peerLock)
        {
            if (!_peers.TryGetValue(id, out var peer))
            {
                peer = new PeerState { Id = id };
                _peers[id] = peer;
            }
            if (name.Length > 0) peer.Name = name;
            if (port > 0) peer.Port = port;
            RememberAddress(peer, ip, lan, announced);
            peer.Shares = shares;
            peer.SeenUtc = DateTime.UtcNow;
        }
        Publish();
    }

    static void RememberAddress(PeerState peer, IPAddress source, bool lan, IPAddress? announced)
    {
        if (!lan)
        {
            if (IPAddress.IsLoopback(peer.Ip)) peer.Ip = source;
            return;
        }
        var now = DateTime.UtcNow;
        if (!TailscalePeers.IsAddress(source))
        {
            peer.Ip = source;
            peer.LanSeenUtc = now;
            return;
        }
        peer.TailIp = announced ?? source;
        if (now - peer.LanSeenUtc > TimeSpan.FromSeconds(6))
            peer.Ip = peer.TailIp;
    }

    void Publish()
    {
        List<PeerSnapshot> snapshot;
        var now = DateTime.UtcNow;
        lock (_peerLock)
        {
            snapshot = _peers.Values.Select(p => new PeerSnapshot
            {
                Id = p.Id,
                Name = string.IsNullOrWhiteSpace(p.Name) ? "Bilgisayar" : p.Name,
                Port = p.Port,
                Ip = p.Ip,
                Online = now - p.SeenUtc < TimeSpan.FromSeconds(7),
                Shares = p.Shares.ToList()
            }).ToList();
        }
        Ui(() => PeersUpdated?.Invoke(snapshot));
    }

    void ReplaceLink(Link link)
    {
        if (_links.TryGetValue(link.PeerId, out var old) && old != link)
            old.Drop();
        _links[link.PeerId] = link;
    }

    string BeaconPath() => Path.Combine(_beaconDir, _settings.ComputerId + ".json");

    static string UniqueDownload(string fileName)
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "OrtakAlan");
        Directory.CreateDirectory(dir);
        var safe = SafeFileName(fileName) ? fileName : "dosya";
        var path = Path.Combine(dir, safe);
        if (!File.Exists(path)) return path;
        var stem = Path.GetFileNameWithoutExtension(safe);
        var ext = Path.GetExtension(safe);
        for (var i = 1; i < 1000; i++)
        {
            path = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(path)) return path;
        }
        return Path.Combine(dir, Guid.NewGuid().ToString("N") + ext);
    }

    static bool SafeFileName(string name) =>
        name.Length > 0 && name.Length < 180 && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && name is not "." and not "..";

    static void Ui(Action action)
    {
        try
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.HasShutdownStarted) return;
            dispatcher.BeginInvoke(action);
        }
        catch { /* kapanış */ }
    }

    static string Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    static JsonObject Obj(params (string Key, object? Value)[] fields)
    {
        var obj = new JsonObject();
        foreach (var (key, value) in fields)
        {
            obj[key] = value switch
            {
                null => null,
                string text => JsonValue.Create(text),
                bool flag => JsonValue.Create(flag),
                int number => JsonValue.Create(number),
                long number => JsonValue.Create(number),
                _ => JsonValue.Create(value.ToString())
            };
        }
        return obj;
    }

    sealed class PeerState
    {
        public string Id = "";
        public string Name = "";
        public int Port;
        public IPAddress Ip = IPAddress.Loopback;
        public IPAddress TailIp = IPAddress.Loopback;
        public DateTime SeenUtc;
        public DateTime LanSeenUtc;
        public List<RemoteShare> Shares = new();
        public bool IsOnline => DateTime.UtcNow - SeenUtc < TimeSpan.FromSeconds(7);
    }

    sealed class DownloadWait
    {
        public string FinalPath = "";
        public TaskCompletionSource<string> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    sealed class IncomingTransfer
    {
        public string Name = "";
        public string Temp = "";
        public string? DestPath;
        public FileStream? Stream;
        public DownloadWait? Waiter;
    }

    sealed class Link
    {
        public readonly TcpClient Client;
        readonly NetworkStream _stream;
        readonly SemaphoreSlim _write = new(1, 1);
        public string PeerId;
        public string PeerName;
        public bool Alive = true;
        public ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> Pending { get; } = new();
        public ConcurrentDictionary<string, DownloadWait> Downloads { get; } = new();
        public ConcurrentDictionary<string, IncomingTransfer> Incoming { get; } = new();

        public Link(TcpClient client, string peerId, string peerName)
        {
            Client = client;
            _stream = client.GetStream();
            PeerId = peerId;
            PeerName = peerName;
        }

        public async Task Send(JsonObject obj)
        {
            if (!Alive) throw new IOException("Bağlantı kapalı");
            var bytes = JsonSerializer.SerializeToUtf8Bytes(obj);
            if (bytes.Length is <= 0 or > 2_000_000) throw new InvalidOperationException("Mesaj boyutu geçersiz");
            var length = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
            await _write.WaitAsync();
            try
            {
                await _stream.WriteAsync(length);
                await _stream.WriteAsync(bytes);
            }
            finally
            {
                _write.Release();
            }
        }

        public async Task<JsonElement?> ReadOne(CancellationToken token)
        {
            var lengthBuf = new byte[4];
            if (!await ReadExact(_stream, lengthBuf, token)) return null;
            var length = BinaryPrimitives.ReadInt32LittleEndian(lengthBuf);
            if (length is <= 0 or > 2_000_000) throw new InvalidDataException("Mesaj boyutu geçersiz");
            var buffer = new byte[length];
            if (!await ReadExact(_stream, buffer, token)) return null;
            using var doc = JsonDocument.Parse(buffer);
            return doc.RootElement.Clone();
        }

        public void Drop()
        {
            if (!Alive) return;
            Alive = false;
            try { Client.Close(); } catch { /* kapanış */ }
            foreach (var pending in Pending.Values)
                pending.TrySetException(new IOException("Bağlantı koptu"));
            foreach (var download in Downloads.Values)
                download.Done.TrySetException(new IOException("Bağlantı koptu"));
            foreach (var transfer in Incoming.Values)
            {
                try { transfer.Stream?.Dispose(); } catch { /* kapanış */ }
            }
        }

        static async Task<bool> ReadExact(NetworkStream stream, byte[] buffer, CancellationToken token)
        {
            var offset = 0;
            while (offset < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(offset), token);
                if (read == 0) return false;
                offset += read;
            }
            return true;
        }
    }
}
