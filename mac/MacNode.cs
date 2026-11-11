using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OrtakAlan.Mac;

sealed class MacNode : IDisposable
{
    const int DiscoveryPort = 47470;
    const long MaxFileBytes = 150L * 1024 * 1024;
    public const string RoomId = "ortak";
    static readonly IPAddress Multicast = IPAddress.Parse("239.255.74.70");

    readonly Desk _desk;
    readonly CancellationTokenSource _cts = new();
    readonly object _peerLock = new();
    readonly Dictionary<string, Peer> _peers = new(StringComparer.Ordinal);
    readonly ConcurrentDictionary<string, Link> _links = new();
    readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new();
    readonly HashSet<string> _roomSeen = new(StringComparer.Ordinal);
    readonly string _chatDir;
    readonly List<Note> _room = new();

    TcpListener? _listener;
    UdpClient? _udp;
    int _tcpPort;

    public event Action? PeersChanged;
    public event Action<string, Note>? NoteArrived;
    public Func<string, string, Task<bool?>>? AskApproval { get; set; }

    public MacNode(Desk desk)
    {
        _desk = desk;
        _chatDir = Path.Combine(desk.DirectoryPath, "sohbet");
        Directory.CreateDirectory(_chatDir);
        LoadRoom();
    }

    public IReadOnlyList<Peer> Peers()
    {
        lock (_peerLock) return _peers.Values.OrderBy(p => p.Name).ToList();
    }

    public List<Note> Messages(string chatId) =>
        chatId == RoomId ? _room.ToList() : LoadChat(chatId);

    public void Start()
    {
        _listener = new TcpListener(IPAddress.Any, 0);
        _listener.Start(200);
        _tcpPort = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = Task.Run(AcceptLoop);
        StartUdp();
        _ = Task.Run(BeaconLoop);
        _ = Task.Run(SweepLoop);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener?.Stop(); } catch { /* kapanış */ }
        try { _udp?.Dispose(); } catch { /* kapanış */ }
        foreach (var link in _links.Values) link.Drop();
        _cts.Dispose();
    }

    public async Task<string?> SendChat(string peerId, string text)
    {
        if (text.Length > 4000) text = text[..4000];
        if (!_desk.Fits(peerId)) return _desk.StopText;
        var note = new Note
        {
            Id = Guid.NewGuid().ToString("N"),
            Text = text,
            Mine = true,
            UtcTicks = DateTime.UtcNow.Ticks
        };
        AddNote(peerId, note);
        if (!IsOnline(peerId)) return null;
        var (link, error) = await Ensure(peerId);
        if (link == null) return error;
        await link.Send(Obj(("t", "chat"), ("id", note.Id), ("text", text)));
        return null;
    }

    public async Task<string?> SendRoom(string text)
    {
        if (!_desk.Usable) return Desk.LockedStatus;
        if (text.Length > 4000) text = text[..4000];
        var note = new Note
        {
            Id = Guid.NewGuid().ToString("N"),
            Text = text,
            Mine = true,
            Author = _desk.Profile.ComputerName,
            AuthorId = _desk.Profile.ComputerId,
            UtcTicks = DateTime.UtcNow.Ticks
        };
        _roomSeen.Add(note.Id);
        AddNote(RoomId, note);
        var ids = OnlineIds();
        foreach (var id in ids)
        {
            try
            {
                var (link, _) = await Ensure(id);
                if (link == null) continue;
                await link.Send(RoomObj(note));
            }
            catch { /* bir sonraki duyuru */ }
        }
        return ids.Count == 0 ? null : null;
    }

    public async Task<string?> SendFile(string peerId, string path, string? roomId)
    {
        if (!File.Exists(path)) return "Dosya bulunamadı";
        var info = new FileInfo(path);
        if (info.Length > MaxFileBytes) return "Dosya 150 MB sınırını aşıyor";
        if (!_desk.Usable) return Desk.LockedStatus;
        if (roomId == null && !_desk.Fits(peerId)) return _desk.StopText;
        var id = Guid.NewGuid().ToString("N");
        var name = Path.GetFileName(path);
        if (roomId == null)
        {
            AddNote(peerId, new Note { Id = id, Text = name, Mine = true, FileName = name, FilePath = path, UtcTicks = DateTime.UtcNow.Ticks });
            var (link, error) = await Ensure(peerId);
            if (link == null) return error;
            await SendFileOn(link, id, path, null);
            return null;
        }

        AddNote(RoomId, new Note
        {
            Id = id,
            Text = name,
            Mine = true,
            FileName = name,
            FilePath = path,
            Author = _desk.Profile.ComputerName,
            AuthorId = _desk.Profile.ComputerId,
            UtcTicks = DateTime.UtcNow.Ticks
        });
        _roomSeen.Add(id);
        foreach (var peer in OnlineIds())
        {
            var (link, _) = await Ensure(peer);
            if (link == null) continue;
            await link.Send(RoomObj(new Note
            {
                Id = id,
                Text = "",
                FileName = name,
                Author = _desk.Profile.ComputerName,
                AuthorId = _desk.Profile.ComputerId,
                UtcTicks = DateTime.UtcNow.Ticks
            }));
            await SendFileOn(link, id, path, id);
        }
        return null;
    }

    public async Task<string?> SendPrint(string peerId, string shareId, string path)
    {
        if (!_desk.Usable) return Desk.LockedStatus;
        if (!_desk.Fits(peerId)) return _desk.StopText;
        if (!File.Exists(path)) return "Dosya bulunamadı";
        var info = new FileInfo(path);
        if (info.Length <= 0) return "Dosya boş";
        if (info.Length > MaxFileBytes) return "Dosya 150 MB sınırını aşıyor";
        var (link, error) = await Ensure(peerId);
        if (link == null) return error;
        var id = Guid.NewGuid().ToString("N");
        var wait = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        link.Pending[id] = wait;
        await link.Send(Obj(("t", "file-begin"), ("id", id), ("name", Path.GetFileName(path)), ("size", info.Length), ("print", shareId)));
        await using var stream = File.OpenRead(path);
        await SendBody(link, id, stream);
        await link.Send(Obj(("t", "file-end"), ("id", id)));
        var done = await Task.WhenAny(wait.Task, Task.Delay(TimeSpan.FromMinutes(3), _cts.Token));
        if (done != wait.Task) return "Yazıcı yanıt vermedi";
        var problem = Text(await wait.Task, "error");
        return problem.Length == 0 ? null : problem;
    }

    async Task SendFileOn(Link link, string id, string path, string? roomId)
    {
        var info = new FileInfo(path);
        if (roomId == null)
            await link.Send(Obj(("t", "file-begin"), ("id", id), ("name", info.Name), ("size", info.Length)));
        else
            await link.Send(Obj(("t", "file-begin"), ("id", id), ("name", info.Name), ("size", info.Length), ("room", roomId), ("who", _desk.Profile.ComputerName), ("from", _desk.Profile.ComputerId)));
        await using var stream = File.OpenRead(path);
        await SendBody(link, id, stream);
        await link.Send(Obj(("t", "file-end"), ("id", id)));
    }

    static async Task SendBody(Link link, string id, Stream stream)
    {
        var buffer = new byte[48 * 1024];
        var index = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer)) > 0)
        {
            var slice = new byte[read];
            Buffer.BlockCopy(buffer, 0, slice, 0, read);
            await link.Send(Obj(("t", "file-chunk"), ("id", id), ("i", index++), ("data", Convert.ToBase64String(slice))));
        }
    }

    async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested && _listener != null)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_cts.Token); }
            catch { break; }
            client.NoDelay = true;
            _ = Task.Run(() => Handle(client));
        }
    }

    async Task Handle(TcpClient client)
    {
        var link = new Link(client);
        try
        {
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            wait.CancelAfter(TimeSpan.FromSeconds(10));
            var first = await link.ReadOne(wait.Token);
            if (first == null || Text(first.Value, "t") != "intro")
            {
                link.Drop();
                return;
            }
            var id = Text(first.Value, "id");
            var name = Text(first.Value, "name");
            if (id.Length == 0 || id == _desk.Profile.ComputerId)
            {
                link.Drop();
                return;
            }
            TakeLicense(first.Value);
            link.PeerId = id;
            link.PeerName = name.Length == 0 ? "Bilgisayar" : name;
            var remote = client.Client.RemoteEndPoint is IPEndPoint end ? end.Address : null;
            var decision = await Approve(id, link.PeerName, remote);
            if (decision == true)
            {
                var welcome = Obj(("t", "welcome"));
                AttachLicense(welcome);
                await link.Send(welcome);
                Replace(link);
                await SendHistory(link);
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
        catch
        {
            link.Drop();
        }
    }

    async Task<bool?> Approve(string id, string name, IPAddress? remote)
    {
        if (_desk.IsApproved(id)) return true;
        if (_desk.IsRejected(id)) return false;
        if (!_desk.Fits(id)) return null;
        if (remote != null && SameLan(remote))
        {
            _desk.Decide(id, name, true);
            _desk.Remember(id, name);
            return true;
        }
        var ask = AskApproval;
        if (ask == null) return null;
        var decision = await ask(id, name);
        if (decision == true)
        {
            _desk.Decide(id, name, true);
            _desk.Remember(id, name);
        }
        else if (decision == false) _desk.Decide(id, name, false);
        return decision;
    }

    async Task<(Link? Link, string? Error)> Ensure(string peerId)
    {
        if (_links.TryGetValue(peerId, out var live) && live.Alive) return (live, null);
        if (!_desk.Fits(peerId)) return (null, _desk.StopText);
        Peer? peer;
        lock (_peerLock) _peers.TryGetValue(peerId, out peer);
        if (peer == null || !peer.IsOnline || peer.Port <= 0) return (null, "Çevrimdışı");
        var gate = _gates.GetOrAdd(peerId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(_cts.Token);
        try
        {
            if (_links.TryGetValue(peerId, out var existing) && existing.Alive) return (existing, null);
            return await Dial(peer);
        }
        catch
        {
            return (null, "Bağlanılamadı");
        }
        finally
        {
            gate.Release();
        }
    }

    async Task<(Link? Link, string? Error)> Dial(Peer peer)
    {
        var client = new TcpClient { NoDelay = true };
        using var connect = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        connect.CancelAfter(TimeSpan.FromSeconds(8));
        await client.ConnectAsync(peer.Ip, peer.Port, connect.Token);
        var link = new Link(client) { PeerId = peer.Id, PeerName = peer.Name };
        var intro = Obj(("t", "intro"), ("id", _desk.Profile.ComputerId), ("name", _desk.Profile.ComputerName));
        AttachLicense(intro);
        await link.Send(intro);
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        wait.CancelAfter(TimeSpan.FromSeconds(90));
        var reply = await link.ReadOne(wait.Token);
        if (reply == null)
        {
            link.Drop();
            return (null, "Cevap gelmedi");
        }
        var kind = Text(reply.Value, "t");
        if (kind == "denied")
        {
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
        TakeLicense(reply.Value);
        if (SameLan(peer.Ip) && !_desk.IsApproved(peer.Id) && !_desk.IsRejected(peer.Id))
            _desk.Decide(peer.Id, peer.Name, true);
        Replace(link);
        _ = Task.Run(() => ReadLoop(link));
        _ = SendHistory(link);
        return (link, null);
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
        catch { /* kopuş */ }
        finally
        {
            link.Drop();
            if (_links.TryGetValue(link.PeerId, out var current) && current == link)
                _links.TryRemove(link.PeerId, out _);
        }
    }

    async Task RefuseLocked(Link link, JsonElement message, string kind)
    {
        var error = Desk.LockedStatus;
        var id = Text(message, "id");
        var req = Text(message, "req");
        try
        {
            if (kind == "file-begin")
                Fail(link, id, Text(message, "print"), error);
            else if (kind == "room-get" && req.Length > 0)
                await link.Send(Obj(("t", "room-miss"), ("req", req)));
            else if (kind == "list" && req.Length > 0)
                await link.Send(Obj(("t", "dir"), ("req", req), ("error", error)));
            else if (req.Length > 0 && kind is "get-file" or "mkdir" or "delete" or "rename" or "put-begin")
                await link.Send(Obj(("t", "fs"), ("req", req), ("error", error)));
        }
        catch
        {
            /* karşı taraf bekleyebilir */
        }
    }

    async Task Dispatch(Link link, JsonElement message)
    {
        var kind = Text(message, "t");
        var req = Text(message, "req");
        if (!_desk.Usable && kind is "chat" or "drop" or "room" or "room-get" or "list" or "get-file" or "mkdir" or "delete" or "rename" or "put-begin" or "file-begin" or "file-chunk" or "file-end")
        {
            await RefuseLocked(link, message, kind);
            return;
        }
        if (kind == "print-result")
        {
            var printId = Text(message, "id");
            if (printId.Length > 0 && link.Pending.TryRemove(printId, out var printWait))
                printWait.TrySetResult(message);
            return;
        }
        if (req.Length > 0 && kind is "dir" or "fs" && link.Pending.TryRemove(req, out var waiter))
        {
            waiter.TrySetResult(message);
            return;
        }

        switch (kind)
        {
            case "chat":
                AddNote(link.PeerId, new Note
                {
                    Id = Text(message, "id"),
                    Text = Text(message, "text"),
                    Author = link.PeerName,
                    AuthorId = link.PeerId,
                    UtcTicks = DateTime.UtcNow.Ticks
                });
                break;
            case "drop":
                Strike(Text(message, "chat") is { Length: > 0 } chat ? chat : link.PeerId, Text(message, "id"));
                break;
            case "room":
                AcceptRoom(link, message);
                break;
            case "room-get":
                await ReplyRoomFile(link, message);
                break;
            case "list":
                await ReplyList(link, message);
                break;
            case "get-file":
                await ReplyGet(link, message);
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
            case "put-begin":
                BeginPut(link, message);
                break;
            case "file-begin":
                BeginIncoming(link, message);
                break;
            case "file-chunk":
                WriteChunk(link, message);
                break;
            case "file-end":
                await Finish(link, Text(message, "id"), null);
                break;
            case "file-error":
                await Finish(link, Text(message, "id"), Text(message, "error"));
                break;
        }
    }

    void AcceptRoom(Link link, JsonElement message)
    {
        var id = Text(message, "id");
        if (id.Length == 0 || !_roomSeen.Add(id)) return;
        var file = Text(message, "file");
        var text = Text(message, "text");
        if (text.Length == 0) text = file;
        var authorId = Text(message, "from");
        if (authorId.Length == 0) authorId = link.PeerId;
        var author = Text(message, "name");
        if (author.Length == 0) author = link.PeerName;
        var mine = authorId == _desk.Profile.ComputerId;
        var note = new Note
        {
            Id = id,
            Text = text,
            Mine = mine,
            Author = mine ? "" : author,
            AuthorId = authorId,
            FileName = file,
            UtcTicks = LongOf(message, "utc"),
            Struck = message.TryGetProperty("struck", out var struck) && struck.ValueKind == JsonValueKind.True
        };
        AddNote(RoomId, note);
        var body = message;
        _ = Relay(link.PeerId, body);
    }

    async Task Relay(string except, JsonElement message)
    {
        foreach (var other in _links.Values.ToArray())
        {
            if (!other.Alive || other.PeerId == except || other.PeerId.Length == 0) continue;
            try { await other.Send(JsonNode.Parse(message.GetRawText())!.AsObject()); }
            catch { /* kopuk hat */ }
        }
    }

    async Task ReplyList(Link link, JsonElement message)
    {
        var req = Text(message, "req");
        if (!TryResolve(Text(message, "share"), Text(message, "rel"), false, out var full, out var error))
        {
            await link.Send(Obj(("t", "dir"), ("req", req), ("error", error ?? "Paylaşım yok")));
            return;
        }
        if (!Directory.Exists(full))
        {
            await link.Send(Obj(("t", "dir"), ("req", req), ("error", "Klasör bulunamadı")));
            return;
        }
        var entries = new JsonArray();
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
        await link.Send(new JsonObject { ["t"] = "dir", ["req"] = req, ["entries"] = entries });
    }

    async Task ReplyGet(Link link, JsonElement message)
    {
        var id = Text(message, "id");
        if (!TryResolve(Text(message, "share"), Text(message, "rel"), true, out var full, out var error) || !File.Exists(full))
        {
            await link.Send(Obj(("t", "file-error"), ("id", id), ("error", error ?? "Dosya yok")));
            return;
        }
        var info = new FileInfo(full);
        if (info.Length > MaxFileBytes)
        {
            await link.Send(Obj(("t", "file-error"), ("id", id), ("error", "Dosya 150 MB sınırını aşıyor")));
            return;
        }
        await link.Send(Obj(("t", "file-begin"), ("id", id), ("name", info.Name), ("size", info.Length)));
        await using var stream = File.OpenRead(full);
        await SendBody(link, id, stream);
        await link.Send(Obj(("t", "file-end"), ("id", id)));
    }

    async Task ReplyMkdir(Link link, JsonElement message)
    {
        var req = Text(message, "req");
        var name = Path.GetFileName(Text(message, "name"));
        string? error = null;
        var full = "";
        if (!SafeName(name) || !TryResolve(Text(message, "share"), Child(Text(message, "rel"), name), false, out full, out error))
        {
            await Fs(link, req, error ?? "Klasör açılamadı");
            return;
        }
        try
        {
            Directory.CreateDirectory(full);
            await Fs(link, req, null);
        }
        catch
        {
            await Fs(link, req, "Klasör açılamadı");
        }
    }

    async Task ReplyDelete(Link link, JsonElement message)
    {
        var req = Text(message, "req");
        if (!TryResolve(Text(message, "share"), Text(message, "rel"), false, out var full, out var error))
        {
            await Fs(link, req, error);
            return;
        }
        try
        {
            if (Directory.Exists(full)) Directory.Delete(full, true);
            else if (File.Exists(full)) File.Delete(full);
            await Fs(link, req, null);
        }
        catch
        {
            await Fs(link, req, "Silinemedi");
        }
    }

    async Task ReplyRename(Link link, JsonElement message)
    {
        var req = Text(message, "req");
        var name = Path.GetFileName(Text(message, "name"));
        string? error = null;
        var full = "";
        if (!SafeName(name) || !TryResolve(Text(message, "share"), Text(message, "rel"), false, out full, out error))
        {
            await Fs(link, req, error ?? "Ad geçersiz");
            return;
        }
        var dest = Path.Combine(Path.GetDirectoryName(full)!, name);
        try
        {
            if (Directory.Exists(full)) Directory.Move(full, dest);
            else if (File.Exists(full)) File.Move(full, dest);
            await Fs(link, req, null);
        }
        catch
        {
            await Fs(link, req, "Ad değiştirilemedi");
        }
    }

    void BeginPut(Link link, JsonElement message)
    {
        var id = Text(message, "id");
        var name = Path.GetFileName(Text(message, "name"));
        string? error = null;
        var dest = "";
        if (!SafeName(name) || !TryResolve(Text(message, "share"), Child(Text(message, "rel"), name), false, out dest, out error))
        {
            _ = Fs(link, id, error ?? "Dosya adı geçersiz");
            return;
        }
        var parent = Path.GetDirectoryName(dest);
        if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent))
        {
            _ = Fs(link, id, "Klasör bulunamadı");
            return;
        }
        var temp = Path.Combine(Path.GetTempPath(), "ortakalan-" + id);
        link.Incoming[id] = new Incoming
        {
            Name = name,
            Temp = temp,
            Stream = File.Create(temp),
            Dest = dest
        };
    }

    void BeginIncoming(Link link, JsonElement message)
    {
        var id = Text(message, "id");
        var printShare = Text(message, "print");
        var name = Path.GetFileName(Text(message, "name"));
        if (!SafeName(name))
        {
            Fail(link, id, printShare, "Dosya adı geçersiz");
            return;
        }
        var size = message.TryGetProperty("size", out var sizeEl) && sizeEl.TryGetInt64(out var n) ? n : 0;
        if (size > MaxFileBytes || (printShare.Length > 0 && size <= 0))
        {
            Fail(link, id, printShare, size <= 0 ? "Dosya boş" : "Dosya 150 MB sınırını aşıyor");
            return;
        }
        if (printShare.Length > 0 && _desk.Shares().All(s => s.Id != printShare || s.Kind != "printer"))
        {
            Fail(link, id, printShare, "Yazıcı paylaşımı yok");
            return;
        }
        var temp = Path.Combine(Path.GetTempPath(), "ortakalan-" + id);
        link.Incoming[id] = new Incoming
        {
            Name = name,
            Temp = temp,
            Stream = File.Create(temp),
            Print = printShare.Length == 0 ? null : printShare,
            Room = Text(message, "room") is { Length: > 0 } room ? room : null,
            Author = Text(message, "who"),
            AuthorId = Text(message, "from")
        };
    }

    void Fail(Link link, string id, string printShare, string error)
    {
        if (printShare.Length > 0)
            _ = link.Send(Obj(("t", "print-result"), ("id", id), ("error", error)));
        else
            _ = Finish(link, id, error);
    }

    void WriteChunk(Link link, JsonElement message)
    {
        var id = Text(message, "id");
        if (!link.Incoming.TryGetValue(id, out var transfer) || transfer.Stream == null) return;
        var data = Text(message, "data");
        if (data.Length == 0) return;
        try
        {
            var bytes = Convert.FromBase64String(data);
            transfer.Stream.Write(bytes, 0, bytes.Length);
            transfer.Done += bytes.Length;
        }
        catch { /* bozuk parça */ }
    }

    async Task Finish(Link link, string id, string? error)
    {
        if (!link.Incoming.TryRemove(id, out var transfer)) return;
        try { transfer.Stream?.Dispose(); } catch { /* kapanış */ }
        if (!string.IsNullOrEmpty(error))
        {
            TryDelete(transfer.Temp);
            if (transfer.Print != null)
                await link.Send(Obj(("t", "print-result"), ("id", id), ("error", error)));
            else if (transfer.Dest != null)
                await Fs(link, id, error);
            return;
        }

        if (transfer.Print != null)
        {
            var share = _desk.Shares().FirstOrDefault(s => s.Id == transfer.Print && s.Kind == "printer");
            var problem = share == null ? "Yazıcı paylaşımı yok" : Desk.Print(share.Path, transfer.Temp);
            TryDelete(transfer.Temp);
            await link.Send(Obj(("t", "print-result"), ("id", id), ("error", problem ?? "")));
            return;
        }

        if (transfer.Dest != null)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(transfer.Dest)!);
                if (File.Exists(transfer.Dest)) File.Delete(transfer.Dest);
                File.Move(transfer.Temp, transfer.Dest);
                await Fs(link, id, null);
            }
            catch
            {
                TryDelete(transfer.Temp);
                await Fs(link, id, "Dosya yazılamadı");
            }
            return;
        }

        var saved = SaveDownload(transfer.Name, transfer.Temp);
        if (transfer.Room != null)
        {
            var existing = _room.FirstOrDefault(n => n.Id == transfer.Room);
            if (existing != null)
            {
                existing.FilePath = saved;
                existing.FileName = transfer.Name;
                SaveRoom();
                NoteArrived?.Invoke(RoomId, existing);
            }
            return;
        }

        AddNote(link.PeerId, new Note
        {
            Id = id,
            Text = transfer.Name,
            Author = link.PeerName,
            AuthorId = link.PeerId,
            FileName = transfer.Name,
            FilePath = saved,
            UtcTicks = DateTime.UtcNow.Ticks
        });
    }

    async Task ReplyRoomFile(Link link, JsonElement message)
    {
        var req = Text(message, "req");
        var id = Text(message, "id");
        var note = _room.FirstOrDefault(n => n.Id == id);
        if (note == null || note.FilePath.Length == 0 || !File.Exists(note.FilePath))
        {
            if (req.Length > 0) await link.Send(Obj(("t", "room-miss"), ("req", req)));
            return;
        }
        await SendFileOn(link, id, note.FilePath, id);
    }

    async Task SendHistory(Link link)
    {
        foreach (var note in _room.TakeLast(80))
        {
            if (note.Id.Length == 0) continue;
            try { await link.Send(RoomObj(note)); }
            catch { return; }
        }
    }

    bool TryResolve(string shareId, string rel, bool file, out string full, out string? error)
    {
        full = "";
        var share = _desk.Shares().FirstOrDefault(s => s.Id == shareId && s.Kind == "folder");
        if (share == null || !Directory.Exists(share.Path))
        {
            error = "Paylaşım yok";
            return false;
        }
        var root = Path.GetFullPath(share.Path);
        var combined = Path.GetFullPath(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)));
        if (!combined.StartsWith(root, StringComparison.Ordinal))
        {
            error = "Geçersiz yol";
            return false;
        }
        if (file && !File.Exists(combined))
        {
            error = "Dosya yok";
            return false;
        }
        full = combined;
        error = null;
        return true;
    }

    void AddNote(string chatId, Note note)
    {
        if (note.Id.Length == 0) note.Id = Guid.NewGuid().ToString("N");
        if (chatId == RoomId)
        {
            if (_room.Any(n => n.Id == note.Id)) return;
            _room.Add(note);
            SaveRoom();
        }
        else
        {
            var list = LoadChat(chatId);
            if (list.Any(n => n.Id == note.Id)) return;
            list.Add(note);
            SaveChat(chatId, list);
        }
        NoteArrived?.Invoke(chatId, note);
    }

    void Strike(string chatId, string id)
    {
        if (id.Length == 0) return;
        if (chatId == RoomId)
        {
            var note = _room.FirstOrDefault(n => n.Id == id);
            if (note == null) return;
            note.Struck = true;
            SaveRoom();
            NoteArrived?.Invoke(chatId, note);
            return;
        }
        var list = LoadChat(chatId);
        var hit = list.FirstOrDefault(n => n.Id == id);
        if (hit == null) return;
        hit.Struck = true;
        SaveChat(chatId, list);
        NoteArrived?.Invoke(chatId, hit);
    }

    List<Note> LoadChat(string id)
    {
        var path = ChatPath(id);
        if (!File.Exists(path)) return new();
        try { return JsonSerializer.Deserialize<List<Note>>(File.ReadAllText(path)) ?? new(); }
        catch { return new(); }
    }

    void SaveChat(string id, List<Note> notes)
    {
        var path = ChatPath(id);
        File.WriteAllText(path, JsonSerializer.Serialize(notes));
    }

    void LoadRoom()
    {
        var path = Path.Combine(_chatDir, "ortak.json");
        if (!File.Exists(path)) return;
        try
        {
            var notes = JsonSerializer.Deserialize<List<Note>>(File.ReadAllText(path)) ?? new();
            _room.AddRange(notes);
            foreach (var note in notes) _roomSeen.Add(note.Id);
        }
        catch { /* boş oda */ }
    }

    void SaveRoom() => File.WriteAllText(Path.Combine(_chatDir, "ortak.json"), JsonSerializer.Serialize(_room));

    string ChatPath(string id) => Path.Combine(_chatDir, id + ".json");

    string SaveDownload(string name, string temp)
    {
        var dir = Path.Combine(_desk.DirectoryPath, "Gelen");
        Directory.CreateDirectory(dir);
        var safe = SafeName(name) ? name : "dosya";
        var path = Path.Combine(dir, safe);
        if (File.Exists(path))
        {
            var stem = Path.GetFileNameWithoutExtension(safe);
            var ext = Path.GetExtension(safe);
            for (var i = 1; i < 1000 && File.Exists(path); i++)
                path = Path.Combine(dir, stem + " (" + i + ")" + ext);
        }
        if (File.Exists(path)) File.Delete(path);
        File.Move(temp, path);
        return path;
    }

    static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* geçici dosya */ }
    }

    static string Child(string rel, string name) => string.IsNullOrEmpty(rel) ? name : rel + "/" + name;

    static bool SafeName(string name) =>
        name.Length > 0 && name.Length < 180 && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && name is not "." and not "..";

    void Replace(Link link)
    {
        if (_links.TryGetValue(link.PeerId, out var old) && old != link) old.Drop();
        _links[link.PeerId] = link;
    }

    bool IsOnline(string id)
    {
        lock (_peerLock) return _peers.TryGetValue(id, out var peer) && peer.IsOnline && peer.Port > 0;
    }

    List<string> OnlineIds()
    {
        lock (_peerLock)
            return _peers.Values.Where(p => p.IsOnline && p.Port > 0).Select(p => p.Id).ToList();
    }

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
            _ = Task.Run(UdpLoop);
        }
        catch
        {
            try { _udp?.Dispose(); } catch { /* kapı dolu */ }
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
                HandleBeacon(Encoding.UTF8.GetString(result.Buffer), result.RemoteEndPoint.Address);
            }
            catch (OperationCanceledException) { break; }
            catch { break; }
        }
    }

    async Task BeaconLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            try { SendBeacon(); }
            catch { /* ağ kapalı */ }
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
            RaisePeers();
        }
    }

    void SendBeacon()
    {
        if (_udp == null || _tcpPort <= 0) return;
        var shares = new JsonArray();
        foreach (var share in _desk.Shares())
        {
            shares.Add(new JsonObject
            {
                ["id"] = share.Id,
                ["k"] = share.Kind,
                ["n"] = share.Name,
                ["w"] = ""
            });
        }
        var obj = new JsonObject
        {
            ["id"] = _desk.Profile.ComputerId,
            ["name"] = _desk.Profile.ComputerName,
            ["port"] = _tcpPort,
            ["utc"] = DateTime.UtcNow.ToString("o"),
            ["shares"] = shares
        };
        var license = _desk.Key;
        if (license.Length > 0) obj["lic"] = license;
        var json = obj.ToJsonString();
        var bytes = Encoding.UTF8.GetBytes(json);
        void Send(IPEndPoint end)
        {
            try { _udp.Send(bytes, bytes.Length, end); } catch { /* yayın kapalı */ }
        }
        Send(new IPEndPoint(Multicast, DiscoveryPort));
        Send(new IPEndPoint(IPAddress.Broadcast, DiscoveryPort));
        foreach (var address in SubnetBroadcasts())
            Send(new IPEndPoint(address, DiscoveryPort));
    }

    void HandleBeacon(string json, IPAddress ip)
    {
        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(json);
            root = doc.RootElement.Clone();
        }
        catch { return; }
        var id = Text(root, "id");
        if (id.Length == 0 || id == _desk.Profile.ComputerId) return;
        var license = Text(root, "lic");
        if (license.Length > 0) _desk.AcceptShared(license);
        var name = Text(root, "name");
        var port = root.TryGetProperty("port", out var portEl) && portEl.TryGetInt32(out var portNo) ? portNo : 0;
        var shares = new List<RemoteShare>();
        if (root.TryGetProperty("shares", out var array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in array.EnumerateArray())
            {
                var shareId = Text(item, "id");
                var shareName = Text(item, "n");
                if (shareId.Length == 0 || shareName.Length == 0) continue;
                shares.Add(new RemoteShare { Id = shareId, Kind = Text(item, "k"), Name = shareName });
            }
        }
        lock (_peerLock)
        {
            if (!_peers.TryGetValue(id, out var peer))
            {
                peer = new Peer { Id = id };
                _peers[id] = peer;
            }
            if (name.Length > 0) peer.Name = name;
            if (port > 0) peer.Port = port;
            if (!IPAddress.IsLoopback(ip)) peer.Ip = ip;
            peer.Shares = shares;
            peer.SeenUtc = DateTime.UtcNow;
        }
        if (name.Length > 0) _desk.Remember(id, name);
        RaisePeers();
    }

    string _peerSig = "";

    void RaisePeers()
    {
        string sig;
        lock (_peerLock)
        {
            sig = string.Join("|", _peers.Values
                .OrderBy(p => p.Id, StringComparer.Ordinal)
                .Select(p => p.Id + ":" + p.Name + ":" + p.IsOnline + ":" + p.Port));
        }
        if (sig == _peerSig) return;
        _peerSig = sig;
        PeersChanged?.Invoke();
    }

    static IEnumerable<IPAddress> SubnetBroadcasts()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                continue;
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

    public static bool SameLan(IPAddress remote)
    {
        if (remote.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(remote)) return false;
        var remoteBytes = remote.GetAddressBytes();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                continue;
            foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork || unicast.IPv4Mask == null)
                    continue;
                var local = unicast.Address.GetAddressBytes();
                var mask = unicast.IPv4Mask.GetAddressBytes();
                if (mask.All(part => part == 0)) continue;
                var same = true;
                for (var i = 0; i < 4; i++)
                {
                    if ((local[i] & mask[i]) != (remoteBytes[i] & mask[i]))
                    {
                        same = false;
                        break;
                    }
                }
                if (same) return true;
            }
        }
        return false;
    }

    void AttachLicense(JsonObject message)
    {
        var key = _desk.Key;
        if (key.Length > 0) message["lic"] = key;
    }

    void TakeLicense(JsonElement message)
    {
        var key = Text(message, "lic");
        if (key.Length > 0) _desk.AcceptShared(key);
    }

    JsonObject RoomObj(Note note) =>
        Obj(("t", "room"), ("id", note.Id), ("text", note.FileName.Length > 0 && note.Text == note.FileName ? "" : note.Text), ("file", note.FileName), ("name", note.Author.Length == 0 ? _desk.Profile.ComputerName : note.Author), ("from", note.AuthorId.Length == 0 ? _desk.Profile.ComputerId : note.AuthorId), ("utc", note.UtcTicks == 0 ? DateTime.UtcNow.Ticks : note.UtcTicks));

    static Task Fs(Link link, string req, string? error) =>
        link.Send(error == null ? Obj(("t", "fs"), ("req", req)) : Obj(("t", "fs"), ("req", req), ("error", error)));

    static string Text(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return "";
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? "",
            JsonValueKind.Number => value.ToString(),
            _ => ""
        };
    }

    static long LongOf(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return 0;
        return value.TryGetInt64(out var number) ? number : 0;
    }

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

    sealed class Link
    {
        public readonly TcpClient Client;
        readonly NetworkStream _stream;
        readonly SemaphoreSlim _write = new(1, 1);
        public string PeerId = "";
        public string PeerName = "";
        public bool Alive = true;
        public ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> Pending { get; } = new();
        public ConcurrentDictionary<string, Incoming> Incoming { get; } = new();

        public Link(TcpClient client)
        {
            Client = client;
            _stream = client.GetStream();
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

    sealed class Incoming
    {
        public string Name = "";
        public string Temp = "";
        public string? Dest;
        public string? Print;
        public string? Room;
        public string Author = "";
        public string AuthorId = "";
        public FileStream? Stream;
        public long Done;
    }
}

sealed class Peer
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public int Port { get; set; }
    public IPAddress Ip { get; set; } = IPAddress.Loopback;
    public DateTime SeenUtc { get; set; }
    public List<RemoteShare> Shares { get; set; } = new();
    public bool IsOnline => DateTime.UtcNow - SeenUtc < TimeSpan.FromSeconds(7);
}

sealed class RemoteShare
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Name { get; set; } = "";
}
