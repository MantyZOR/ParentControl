using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ParentControl.Core;

namespace ParentControl.Client;

internal static class Store
{
    public static string DirectoryPath => Environment.GetEnvironmentVariable("PARENTCONTROL_TEST_DATA")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ParentControl");
    public static string KeyPath => Path.Combine(DirectoryPath, "key.dat");
    public static string StatePath => Path.Combine(DirectoryPath, "state.dat");
    public static string ImagePath => Path.Combine(DirectoryPath, "background.jpg");
    public static string BackgroundDirectory => Path.Combine(DirectoryPath, "backgrounds");
    public static string BackgroundPath(BackgroundPhotoInfo photo) =>
        Path.Combine(BackgroundDirectory, photo.Id + photo.Extension);
    public static byte[] Key() => LocalSecret.Unprotect(File.ReadAllBytes(KeyPath));
    public static ClientState Load() => File.Exists(StatePath)
        ? JsonSerializer.Deserialize<ClientState>(LocalSecret.Unprotect(File.ReadAllBytes(StatePath)), Wire.Json) ?? new ClientState()
        : new ClientState { ProtectionArmed = false };
    public static void Save(ClientState state)
    {
        Directory.CreateDirectory(DirectoryPath);
        var temp = StatePath + ".tmp";
        File.WriteAllBytes(temp, LocalSecret.Protect(JsonSerializer.SerializeToUtf8Bytes(state, Wire.Json), true));
        File.Move(temp, StatePath, true);
    }
}

internal sealed class ServiceHost : IDisposable
{
    private readonly object gate = new();
    private readonly ClientState state = Store.Load();
    private readonly byte[] key = Store.Key();
    private readonly HttpListener listener = new();
    private readonly CancellationTokenSource stop = new();
    private long previousTick;
    private long previousSave;
    private long lastOverlayAttempt;
    private long lastTrayAttempt;
    private bool exiting;
    private DateTime promoWindow = DateTime.MinValue;
    private int promoAttempts;
    private DateTime maintenanceWindow = DateTime.MinValue;
    private int maintenanceAttempts;
    private long maintenanceUntilTick;

    public void Start()
    {
        listener.Prefixes.Add(Environment.GetEnvironmentVariable("PARENTCONTROL_TEST_DATA") == null
            ? "http://+:48731/" : "http://localhost:48731/");
        listener.Start();
        previousTick = previousSave = Environment.TickCount64;
        lock (gate)
        {
            if (File.Exists(Store.ImagePath) && state.Backgrounds.Count == 0)
            {
                Directory.CreateDirectory(Store.BackgroundDirectory);
                var legacy = new BackgroundPhotoInfo
                {
                    Id = Guid.NewGuid().ToString("N"), Name = "Прежний фон", Extension = ".jpg",
                    SizeBytes = new FileInfo(Store.ImagePath).Length
                };
                File.Move(Store.ImagePath, Store.BackgroundPath(legacy));
                state.Backgrounds.Add(legacy);
                Log("Прежний фон перенесён в галерею");
            }
            if (state.MaintenanceCredential is null ||
                (string.IsNullOrEmpty(state.MaintenanceCredential.Salt) && string.IsNullOrEmpty(state.MaintenanceCredential.Hash)))
            {
                state.MaintenanceCredential = MaintenancePassword.Create("1234");
                Log("Задан начальный пароль техобслуживания; смените его в Admin");
            }
            if (DateTime.UtcNow < state.LastUtc.AddMinutes(-2)) { state.ClockWarning = true; Log("Системное время переведено назад"); }
            state.SessionUnlocked = Native.ActiveSessionUnlocked() ?? false;
            Log("Служба запущена"); Persist();
        }
        _ = Task.Run(AcceptLoop);
        _ = Task.Run(TickLoop);
        _ = Task.Run(DiscoveryLoop);
    }

    private async Task DiscoveryLoop()
    {
        try
        {
            using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, DiscoveryProtocol.Port));
            while (!stop.IsCancellationRequested)
            {
                var packet = await udp.ReceiveAsync(stop.Token);
                if (packet.Buffer.Length is < 30 or > 4096) continue;
                try
                {
                    var probe = Wire.Decrypt<DiscoveryProbe>(Encoding.UTF8.GetString(packet.Buffer), key);
                    if (!DiscoveryProtocol.ValidProbe(probe, DateTime.UtcNow)) continue;
                    var reply = new DiscoveryReply { RequestId = probe.RequestId, Machine = Environment.MachineName };
                    var bytes = Encoding.UTF8.GetBytes(Wire.Encrypt(reply, key));
                    await udp.SendAsync(bytes, packet.RemoteEndPoint, stop.Token);
                }
                catch (CryptographicException) { }
                catch (FormatException) { }
                catch (JsonException) { }
                catch (InvalidDataException) { }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Program.Error(ex); }
    }

    public void SessionChanged(int type)
    {
        lock (gate)
        {
            if (type is 0x2 or 0x4 or 0x6 or 0x7) state.SessionUnlocked = false; // disconnect, logoff, lock
            if (type is 0x1 or 0x3 or 0x5 or 0x8) state.SessionUnlocked = true; // connect, logon, unlock
            Log("Событие сеанса Windows: " + type); Persist();
        }
    }

    private async Task TickLoop()
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(1000, stop.Token);
                lock (gate)
                {
                    state.SessionUnlocked = Native.ActiveSessionUnlocked() ?? state.SessionUnlocked;
                    var now = Environment.TickCount64;
                    var gap = now - previousTick;
                    // A long gap means Windows was asleep or the service was paused; do not debit the bank for it.
                    var elapsed = gap > 5000 ? 0 : Math.Clamp(gap, 0, 5000);
                    previousTick = now;
                    if (DateTime.UtcNow < state.LastUtc.AddMinutes(-2)) { state.ClockWarning = true; Log("Системное время переведено назад"); }
                    var counting = state.ProtectionArmed != false && state.SessionUnlocked && IsAllowed() &&
                        state.ManualOverride != true && MaintenanceSeconds() == 0;
                    if (counting)
                    {
                        var old = state.BalanceSeconds;
                        state.BalanceSeconds = Math.Max(0, old - Math.Max(1, (elapsed + 500) / 1000));
                        if (old > 0 && state.BalanceSeconds == 0) Log("Время закончилось");
                    }
                    if (state.WarningMinutes == 0 || state.BalanceSeconds > state.WarningMinutes * 60L)
                        state.WarningIssued = false;
                    else if (AccessPolicy.ShouldWarn(state, counting))
                    {
                        state.WarningIssued = true;
                        Log($"Предупреждение: осталось {(state.BalanceSeconds + 59) / 60} мин");
                        state.WarningSequence = state.Sequence;
                        Persist();
                    }
                    PruneExpiredPromos();
                    if (now - previousSave >= 5000) { Persist(); previousSave = now; }
                    if (!exiting && state.SessionUnlocked && !IsAllowed()) EnsureOverlay();
                    if (!exiting && state.SessionUnlocked) EnsureTray();
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { Program.Error(ex); }
        }
    }

    private void PruneExpiredPromos()
    {
        var result = PromoLifecycle.Prune(state, DateTime.UtcNow);
        foreach (var promo in result.Deleted)
            Log($"Исчерпанный промокод {promo.Label} удалён через 24 часа");
        if (result.Changed) Persist();
    }

    private int MaintenanceSeconds() => (int)Math.Clamp((maintenanceUntilTick - Environment.TickCount64 + 999) / 1000, 0, 1800);

    private bool IsAllowed() => AccessPolicy.IsAllowed(state, MaintenanceSeconds(), DateTime.Now);

    private void EnsureOverlay()
    {
        if (Environment.GetEnvironmentVariable("PARENTCONTROL_TEST_DATA") != null) return;
        try
        {
            if (Mutex.TryOpenExisting("Global\\ParentControlOverlay", out var existing)) { existing.Dispose(); return; }
        }
        catch (UnauthorizedAccessException) { return; } // The overlay exists under another session token.
        if (Environment.TickCount64 - lastOverlayAttempt < 3000) return;
        lastOverlayAttempt = Environment.TickCount64;
        var unified = Path.Combine(AppContext.BaseDirectory, "ParentControl.ClientApp.exe");
        var path = File.Exists(unified) ? unified : Path.Combine(AppContext.BaseDirectory, "ParentControl.Overlay.exe");
        if (!File.Exists(path)) return;
        var session = Native.WTSGetActiveConsoleSessionId();
        if (session == uint.MaxValue || !Native.WTSQueryUserToken(session, out var token)) return;
        try
        {
            var startup = new Native.StartupInfo { Size = System.Runtime.InteropServices.Marshal.SizeOf<Native.StartupInfo>(), Desktop = "winsta0\\default" };
            var commandLine = path == unified ? $"\"{path}\" --overlay" : $"\"{path}\"";
            if (Native.CreateProcessAsUser(token, path, commandLine, IntPtr.Zero, IntPtr.Zero, false, 0, IntPtr.Zero, AppContext.BaseDirectory, ref startup, out var process))
            { Native.CloseHandle(process.Process); Native.CloseHandle(process.Thread); Log("Экран блокировки запущен"); }
        }
        finally { Native.CloseHandle(token); }
    }

    private void EnsureTray()
    {
        if (Environment.GetEnvironmentVariable("PARENTCONTROL_TEST_DATA") != null) return;
        if (Environment.TickCount64 - lastTrayAttempt < 5000) return;
        lastTrayAttempt = Environment.TickCount64;
        var session = Native.WTSGetActiveConsoleSessionId();
        if (session == uint.MaxValue) return;
        try
        {
            if (Mutex.TryOpenExisting($"Global\\ParentControlClientTray_{session}", out var existing))
            {
                existing.Dispose();
                return;
            }
        }
        catch (UnauthorizedAccessException) { return; }
        var path = Path.Combine(AppContext.BaseDirectory, "ParentControl.ClientApp.exe");
        if (!File.Exists(path) || !Native.WTSQueryUserToken(session, out var token)) return;
        try
        {
            var startup = new Native.StartupInfo
            {
                Size = System.Runtime.InteropServices.Marshal.SizeOf<Native.StartupInfo>(),
                Desktop = "winsta0\\default"
            };
            if (Native.CreateProcessAsUser(token, path, $"\"{path}\" --tray", IntPtr.Zero, IntPtr.Zero,
                    false, 0, IntPtr.Zero, AppContext.BaseDirectory, ref startup, out var process))
            {
                Native.CloseHandle(process.Process);
                Native.CloseHandle(process.Thread);
            }
        }
        finally { Native.CloseHandle(token); }
    }

    private async Task AcceptLoop()
    {
        while (!stop.IsCancellationRequested)
        {
            try { var context = await listener.GetContextAsync(); _ = Task.Run(() => Handle(context)); }
            catch (HttpListenerException) when (stop.IsCancellationRequested) { break; }
            catch (Exception ex) { Program.Error(ex); }
        }
    }

    private async Task Handle(HttpListenerContext context)
    {
        var response = context.Response;
        try
        {
            var path = context.Request.Url?.AbsolutePath ?? "";
            var loopback = context.Request.RemoteEndPoint?.Address is { } address && IPAddress.IsLoopback(address);
            if (path == "/public/status" && loopback)
            {
                object data;
                lock (gate) data = new { allowed = IsAllowed(), seconds = state.BalanceSeconds,
                    armed = state.ProtectionArmed != false, paused = state.UserPaused,
                    rulesMarkdown = state.RulesMarkdown ?? "",
                    rulesRevision = state.RulesRevision,
                    rulesUnread = state.RulesRevision > state.RulesSeenRevision,
                    warningSequence = state.WarningSequence, warningMinutes = state.WarningMinutes,
                    warningActive = state.WarningIssued && state.WarningMinutes > 0 && state.SessionUnlocked &&
                        IsAllowed() && state.ManualOverride != true && MaintenanceSeconds() == 0 &&
                        state.BalanceSeconds > 0 && state.BalanceSeconds <= state.WarningMinutes * 60L,
                    maintenanceSeconds = MaintenanceSeconds(), exiting };
                await Send(response, JsonSerializer.Serialize(data, Wire.Json)); return;
            }
            if (path == "/public/rules-seen" && loopback && context.Request.HttpMethod == "POST")
            {
                var text = await ReadLimited(context.Request, 64);
                bool seen;
                lock (gate)
                {
                    seen = long.TryParse(text, out var revision) &&
                        revision > 0 && revision == state.RulesRevision;
                    if (seen && state.RulesSeenRevision != revision)
                    {
                        state.RulesSeenRevision = revision;
                        Log("Пользователь открыл обновлённые правила");
                        Persist();
                    }
                }
                await Send(response, JsonSerializer.Serialize(new CommandResult
                { Ok = seen, Error = seen ? "" : "Rules revision changed" }, Wire.Json)); return;
            }
            if (path == "/public/arm" && loopback && context.Request.HttpMethod == "POST")
            {
                lock (gate)
                {
                    if (state.ProtectionArmed == false)
                    {
                        state.ProtectionArmed = true;
                        Log("Защита включена локальным администратором");
                        Persist();
                    }
                }
                await Send(response, JsonSerializer.Serialize(new CommandResult { Ok = true }, Wire.Json)); return;
            }
            if (path == "/public/pause" && loopback && context.Request.HttpMethod == "POST")
            {
                CommandResult result;
                lock (gate)
                {
                    if (state.ProtectionArmed == false) result = new() { Error = "Сначала включите защиту." };
                    else if (state.UserPaused) result = new() { Ok = true };
                    else if (!IsAllowed() || MaintenanceSeconds() > 0) result = new() { Error = "Доступ уже ограничен." };
                    else
                    {
                        state.UserPaused = true;
                        Log("Пользователь сохранил минуты и включил паузу");
                        Persist();
                        EnsureOverlay();
                        result = new() { Ok = true };
                    }
                }
                await Send(response, JsonSerializer.Serialize(result, Wire.Json)); return;
            }
            if (path == "/public/resume" && loopback && context.Request.HttpMethod == "POST")
            {
                CommandResult result;
                lock (gate)
                {
                    if (state.UserPaused)
                    {
                        state.UserPaused = false;
                        if (IsAllowed())
                        {
                            Log("Пользователь завершил паузу");
                            Persist();
                        }
                        else state.UserPaused = true;
                    }
                    result = IsAllowed() ? new() { Ok = true } : new() { Error = "Доступ пока ограничен правилами Admin или закончились минуты." };
                }
                await Send(response, JsonSerializer.Serialize(result, Wire.Json)); return;
            }
            if (path == "/public/end-maintenance" && loopback && context.Request.HttpMethod == "POST")
            {
                lock (gate)
                {
                    if (MaintenanceSeconds() > 0)
                    {
                        maintenanceUntilTick = 0;
                        Log("Техобслуживание завершено досрочно на клиенте");
                        Persist();
                        if (!IsAllowed()) EnsureOverlay();
                    }
                }
                await Send(response, JsonSerializer.Serialize(new CommandResult { Ok = true }, Wire.Json)); return;
            }
            if (path == "/public/background" && loopback)
            {
                BackgroundPhotoInfo? photo;
                lock (gate)
                {
                    var available = state.Backgrounds.Where(p => File.Exists(Store.BackgroundPath(p))).ToArray();
                    photo = available.Length == 0 ? null : available[Random.Shared.Next(available.Length)];
                }
                var imagePath = photo is null ? Store.ImagePath : Store.BackgroundPath(photo);
                if (!File.Exists(imagePath)) { response.StatusCode = 404; return; }
                response.ContentType = photo?.Extension == ".png" ? "image/png" : "image/jpeg";
                await response.OutputStream.WriteAsync(await File.ReadAllBytesAsync(imagePath)); return;
            }
            if (path == "/public/maintenance" && loopback && context.Request.HttpMethod == "POST")
            {
                var password = await ReadLimited(context.Request, 256);
                CommandResult result;
                lock (gate) result = StartMaintenance(password);
                await Send(response, JsonSerializer.Serialize(result, Wire.Json)); return;
            }
            if (path == "/public/exit" && loopback && context.Request.HttpMethod == "POST")
            {
                var password = await ReadLimited(context.Request, 256);
                CommandResult result;
                lock (gate) result = ExitClient(password);
                await Send(response, JsonSerializer.Serialize(result, Wire.Json));
                if (result.Ok) _ = Task.Run(async () =>
                {
                    await Task.Delay(3500);
                    Program.RequestStop();
                });
                return;
            }
            if (path == "/public/redeem" && loopback && context.Request.HttpMethod == "POST")
            {
                var code = await ReadLimited(context.Request, 256);
                CommandResult result;
                lock (gate) result = Redeem(code);
                await Send(response, JsonSerializer.Serialize(result, Wire.Json)); return;
            }
            if (!path.StartsWith("/api/")) { response.StatusCode = 404; return; }
            if (context.Request.HttpMethod != "POST") { response.StatusCode = 405; return; }
            var request = Wire.Decrypt<ApiRequest>(await ReadLimited(context.Request, 8_000_000), key);
            if (request.RequestId.Length is < 16 or > 100 || Math.Abs((DateTime.UtcNow - request.Utc).TotalMinutes) > 5) { response.StatusCode = 401; return; }
            if (path == "/api/background-image")
            {
                string? image = null;
                lock (gate)
                {
                    var photo = state.Backgrounds.FirstOrDefault(p => p.Id == request.Command?.BackgroundId);
                    if (photo != null && File.Exists(Store.BackgroundPath(photo)))
                        image = Convert.ToBase64String(File.ReadAllBytes(Store.BackgroundPath(photo)));
                }
                if (image == null) { response.StatusCode = 404; return; }
                await Send(response, Wire.Encrypt(new ApiResponse<string> { RequestId = request.RequestId, Data = image }, key)); return;
            }
            if (path == "/api/status")
            {
                ClientStatus snapshot;
                lock (gate) snapshot = Snapshot();
                await Send(response, Wire.Encrypt(new ApiResponse<ClientStatus> { RequestId = request.RequestId, Data = snapshot }, key)); return;
            }
            if (path == "/api/command" && request.Command != null)
            {
                CommandResult result;
                lock (gate) result = Apply(request.Command);
                await Send(response, Wire.Encrypt(new ApiResponse<CommandResult> { RequestId = request.RequestId, Data = result }, key)); return;
            }
            response.StatusCode = 404;
        }
        catch (CryptographicException) { response.StatusCode = 401; }
        catch (Exception ex) { Program.Error(ex); response.StatusCode = 400; }
        finally { try { response.Close(); } catch { } }
    }

    private static async Task<string> ReadLimited(HttpListenerRequest request, int max)
    {
        if (request.ContentLength64 > max) throw new InvalidDataException("Request too large");
        using var memory = new MemoryStream();
        var buffer = new byte[8192]; int n;
        while ((n = await request.InputStream.ReadAsync(buffer)) > 0)
        { if (memory.Length + n > max) throw new InvalidDataException("Request too large"); memory.Write(buffer, 0, n); }
        return Encoding.UTF8.GetString(memory.ToArray());
    }
    private static async Task Send(HttpListenerResponse response, string body)
    {
        response.ContentType = "application/json; charset=utf-8";
        var bytes = Encoding.UTF8.GetBytes(body); response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes);
    }
    private ClientStatus Snapshot() => new()
    {
        Machine = Environment.MachineName, BalanceSeconds = state.BalanceSeconds,
        ProtectionArmed = state.ProtectionArmed != false, Allowed = IsAllowed(),
        UserPaused = state.UserPaused, WarningMinutes = state.WarningMinutes,
        MaintenanceSeconds = MaintenanceSeconds(),
        SessionUnlocked = state.SessionUnlocked, ClockWarning = state.ClockWarning, ManualOverride = state.ManualOverride,
        Schedule = state.Schedule.ToList(), PromoTrackingVersion = 2, SupportsBalanceSubtraction = true,
        SupportsRulesMarkdown = true, RulesMarkdown = state.RulesMarkdown ?? "",
        SupportsBackgroundGallery = true,
        Backgrounds = state.Backgrounds.Select(p => new BackgroundPhotoInfo
        { Id = p.Id, Name = p.Name, Extension = p.Extension, AddedUtc = p.AddedUtc,
          SizeBytes = File.Exists(Store.BackgroundPath(p)) ? new FileInfo(Store.BackgroundPath(p)).Length : 0 }).ToList(),
        PromoCodes = state.PromoCodes.Select(p => new PromoCode
        {
            Hash = p.Hash, Label = p.Label, Minutes = p.Minutes,
            RemainingUses = p.RemainingUses, ExhaustedUtc = p.ExhaustedUtc
        }).ToList(),
        DeletedPromoCodes = state.DeletedPromoCodes.ToList(),
        Events = state.Events.ToList(), Sequence = state.Sequence, Utc = DateTime.UtcNow
    };

    private CommandResult Apply(Command command)
    {
        if (string.IsNullOrWhiteSpace(command.Id) || command.Id.Length > 100) return new() { Error = "Invalid id" };
        if (state.AppliedCommandIds.Contains(command.Id)) return new() { Ok = true };
        switch (command.Type)
        {
            case "add":
                if (command.Minutes is < 1 or > 1440) return new() { Error = "Minutes must be 1–1440" };
                var pauseGrantedTime = PauseNewTimeAtZero();
                state.BalanceSeconds = Math.Min(10_000_000, state.BalanceSeconds + command.Minutes * 60L);
                Log($"Добавлено {command.Minutes} мин; остаток {state.BalanceSeconds / 60} мин");
                if (pauseGrantedTime) Log("Новые минуты сохранены на паузе до нажатия «Продолжить работу»");
                break;
            case "subtract":
                if (command.Minutes is < 1 or > 1440) return new() { Error = "Minutes must be 1–1440" };
                var beforeSubtract = state.BalanceSeconds;
                state.BalanceSeconds = Math.Max(0, beforeSubtract - command.Minutes * 60L);
                Log($"Списано {(beforeSubtract - state.BalanceSeconds) / 60.0:0.#} мин; остаток {(state.BalanceSeconds + 59) / 60} мин");
                break;
            case "override":
                maintenanceUntilTick = 0;
                state.ManualOverride = command.ManualOverride;
                if (command.ManualOverride == true) state.UserPaused = false;
                Log("Ручной режим: " + (command.ManualOverride switch { true => "разрешить", false => "блокировать", null => "автоматически" })); break;
            case "warning":
                if (command.WarningMinutes is < 0 or > 1440) return new() { Error = "Warning minutes must be 0–1440" };
                state.WarningMinutes = command.WarningMinutes;
                state.WarningIssued = false;
                Log(command.WarningMinutes == 0 ? "Предупреждение об окончании времени выключено" :
                    $"Предупреждение за {command.WarningMinutes} мин до конца"); break;
            case "schedule":
                if (command.Schedule == null || command.Schedule.Count > 100 || command.Schedule.Any(w => w.Day is < 0 or > 6 || w.StartMinute < 0 || w.EndMinute > 1440 || w.StartMinute >= w.EndMinute))
                    return new() { Error = "Invalid schedule" };
                state.Schedule = command.Schedule;
                Log("Расписание обновлено"); break;
            case "rules":
                if (command.RulesMarkdown == null || command.RulesMarkdown.Length > 12000 || command.RulesMarkdown.Contains('\0'))
                    return new() { Error = "Invalid rules markdown" };
                if (state.RulesMarkdown != command.RulesMarkdown)
                {
                    state.RulesMarkdown = command.RulesMarkdown;
                    state.RulesRevision++;
                    Log("Правила получения минут обновлены");
                }
                break;
            case "promo":
                var promo = command.PromoCode;
                if (promo == null || promo.Hash.Length != 64 || promo.Minutes is < 1 or > 1440 || promo.RemainingUses is < 1 or > 100)
                    return new() { Error = "Invalid promo" };
                if (state.DeletedPromoCodes.Any(p => p.Hash == promo.Hash && p.DeletedByAdmin))
                    return new() { Error = "Promo revoked" };
                if (state.PromoCodes.Any(p => p.Hash == promo.Hash)) return new() { Error = "Promo already exists" };
                state.PromoCodes.Add(promo);
                Log($"Промокод {promo.Label} добавлен ({promo.Minutes} мин, {promo.RemainingUses} применений)"); break;
            case "delete-promo":
                if (command.PromoHash is not { Length: 64 } hash || !hash.All(Uri.IsHexDigit))
                    return new() { Error = "Invalid promo hash" };
                var revoked = PromoLifecycle.Revoke(state, hash, command.PromoLabel ?? hash[^4..], DateTime.UtcNow);
                Log($"Промокод {revoked.Label} удалён администратором"); break;
            case "add-background":
                var upload = command.BackgroundUpload;
                if (upload == null || !Guid.TryParseExact(upload.Id, "N", out _) ||
                    upload.Name.Length is < 1 or > 100)
                    return new() { Error = "Invalid background metadata" };
                if (state.Backgrounds.Any(p => p.Id == upload.Id))
                    return new() { Error = "Background already exists" };
                byte[] imageBytes;
                try { imageBytes = Convert.FromBase64String(upload.ImageBase64); }
                catch { return new() { Error = "Invalid background image" }; }
                if (imageBytes.Length is < 100 or > 3_000_000 || state.Backgrounds.Count >= 8)
                    return new() { Error = "Maximum 8 images, 3 MB each" };
                var extension = imageBytes.Length >= 8 &&
                    imageBytes[0] == 0x89 && imageBytes[1] == 0x50 && imageBytes[2] == 0x4e &&
                    imageBytes[3] == 0x47 && imageBytes[4] == 0x0d && imageBytes[5] == 0x0a &&
                    imageBytes[6] == 0x1a && imageBytes[7] == 0x0a ? ".png" :
                    imageBytes[0] == 0xff && imageBytes[1] == 0xd8 && imageBytes[2] == 0xff ? ".jpg" : "";
                if (extension.Length == 0) return new() { Error = "JPEG or PNG required" };
                var currentBytes = state.Backgrounds.Sum(p =>
                    File.Exists(Store.BackgroundPath(p)) ? new FileInfo(Store.BackgroundPath(p)).Length : 0);
                if (currentBytes + imageBytes.Length > 16_000_000)
                    return new() { Error = "Gallery total must stay under 16 MB" };
                Directory.CreateDirectory(Store.BackgroundDirectory);
                var background = new BackgroundPhotoInfo
                { Id = upload.Id, Name = Path.GetFileName(upload.Name), Extension = extension,
                    SizeBytes = imageBytes.Length };
                File.WriteAllBytes(Store.BackgroundPath(background), imageBytes);
                state.Backgrounds.Add(background);
                if (File.Exists(Store.ImagePath)) File.Delete(Store.ImagePath);
                Log($"Фото экрана блокировки добавлено: {background.Name}"); break;
            case "delete-background":
                if (!Guid.TryParseExact(command.BackgroundId, "N", out _))
                    return new() { Error = "Invalid background id" };
                var removedBackground = state.Backgrounds.FirstOrDefault(p => p.Id == command.BackgroundId);
                if (removedBackground != null)
                {
                    state.Backgrounds.Remove(removedBackground);
                    if (File.Exists(Store.BackgroundPath(removedBackground)))
                        File.Delete(Store.BackgroundPath(removedBackground));
                    Log($"Фото экрана блокировки удалено: {removedBackground.Name}");
                }
                break;
            case "background":
                try
                {
                    var bytes = Convert.FromBase64String(command.ImageBase64 ?? "");
                    if (bytes.Length is < 100 or > 3_000_000 || bytes[0] != 0xff || bytes[1] != 0xd8) return new() { Error = "JPEG up to 3 MB required" };
                    File.WriteAllBytes(Store.ImagePath, bytes); Log("Фон блокировки обновлён");
                }
                catch { return new() { Error = "Invalid image" }; }
                break;
            case "maintenance-password":
                if (!MaintenancePassword.IsValid(command.MaintenanceCredential))
                    return new() { Error = "Invalid maintenance credential" };
                state.MaintenanceCredential = command.MaintenanceCredential!;
                Log("Пароль локального техобслуживания изменён"); break;
            case "clear-clock-warning": state.ClockWarning = false; Log("Предупреждение о времени снято"); break;
            default: return new() { Error = "Unknown command" };
        }
        state.AppliedCommandIds.Add(command.Id);
        if (state.AppliedCommandIds.Count > 10000) state.AppliedCommandIds.RemoveRange(0, state.AppliedCommandIds.Count - 10000);
        Persist(); return new() { Ok = true };
    }

    private CommandResult VerifyLocalPassword(string password)
    {
        if ((DateTime.UtcNow - maintenanceWindow).TotalMinutes >= 1)
        {
            maintenanceAttempts = 0;
            maintenanceWindow = DateTime.UtcNow;
        }
        if (++maintenanceAttempts > 5)
            return new() { Error = "Слишком много попыток. Подождите минуту." };
        if (!MaintenancePassword.Verify(password, state.MaintenanceCredential))
        {
            Log("Неверный пароль локального техобслуживания");
            Persist();
            return new() { Error = "Неверный пароль" };
        }
        maintenanceAttempts = 0;
        return new() { Ok = true };
    }

    private CommandResult StartMaintenance(string password)
    {
        var result = VerifyLocalPassword(password);
        if (!result.Ok) return result;
        maintenanceUntilTick = Environment.TickCount64 + 30 * 60 * 1000;
        Log("Локальное техобслуживание: доступ открыт на 30 минут без расхода банка");
        Persist();
        return result;
    }

    private CommandResult ExitClient(string password)
    {
        if (exiting) return new() { Ok = true };
        var result = VerifyLocalPassword(password);
        if (!result.Ok) return result;
        exiting = true;
        maintenanceUntilTick = Environment.TickCount64 + 30_000;
        Log("Клиент остановлен по паролю локального администратора");
        Persist();
        return result;
    }

    private CommandResult Redeem(string code)
    {
        if ((DateTime.UtcNow - promoWindow).TotalMinutes >= 1) { promoAttempts = 0; promoWindow = DateTime.UtcNow; }
        if (++promoAttempts > 10) return new() { Error = "Слишком много попыток. Подождите минуту." };
        var hash = Wire.HashPromo(code);
        var promo = state.PromoCodes.FirstOrDefault(p => p.RemainingUses > 0 && p.Hash == hash);
        if (promo == null) { Log("Неверный промокод"); Persist(); return new() { Error = "Код не найден или уже использован" }; }
        promo.RemainingUses--;
        if (promo.RemainingUses == 0) promo.ExhaustedUtc = DateTime.UtcNow;
        var pausePromoTime = PauseNewTimeAtZero();
        state.BalanceSeconds += promo.Minutes * 60L;
        Log($"Промокод {promo.Label}: +{promo.Minutes} мин; осталось применений {promo.RemainingUses}");
        if (pausePromoTime) Log("Новые минуты сохранены на паузе до нажатия «Продолжить работу»");
        Persist(); return new() { Ok = true };
    }
    private bool PauseNewTimeAtZero()
    {
        if (state.BalanceSeconds > 0 || state.ProtectionArmed == false ||
            state.ManualOverride == true || state.UserPaused)
            return false;
        state.UserPaused = true;
        return true;
    }
    private void Log(string message)
    {
        state.Events.Add(new LogEntry { Sequence = ++state.Sequence, Utc = DateTime.UtcNow, Message = message });
        if (state.Events.Count > 1000) state.Events.RemoveRange(0, state.Events.Count - 1000);
    }
    private void Persist() { state.LastUtc = DateTime.UtcNow; Store.Save(state); }
    public void Dispose() { stop.Cancel(); listener.Stop(); lock (gate) { Log("Служба остановлена"); Persist(); } stop.Dispose(); }
}
