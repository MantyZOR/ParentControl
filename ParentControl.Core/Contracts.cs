using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ParentControl.Core;

public sealed class TimeWindow
{
    public int Day { get; set; } // 0 = Sunday
    public int StartMinute { get; set; }
    public int EndMinute { get; set; }
}

public sealed class BackgroundPhotoInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Extension { get; set; } = ".jpg";
    public long SizeBytes { get; set; }
    public DateTime AddedUtc { get; set; } = DateTime.UtcNow;
}

public sealed class BackgroundUpload
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string ImageBase64 { get; set; } = "";
}

public sealed class PromoCode
{
    public string Hash { get; set; } = "";
    public int Minutes { get; set; }
    public int RemainingUses { get; set; }
    public string Label { get; set; } = "";
    public DateTime? ExhaustedUtc { get; set; }
}

public sealed class DeletedPromoCode
{
    public string Hash { get; set; } = "";
    public string Label { get; set; } = "";
    public DateTime ExhaustedUtc { get; set; }
    public DateTime DeletedUtc { get; set; }
    public bool DeletedByAdmin { get; set; }
}

public sealed record PromoPruneResult(bool Changed, IReadOnlyList<DeletedPromoCode> Deleted);

public static class PromoLifecycle
{
    public static DeletedPromoCode Revoke(ClientState state, string hash, string label, DateTime nowUtc)
    {
        var current = state.PromoCodes.FirstOrDefault(p => p.Hash == hash);
        if (current != null) state.PromoCodes.Remove(current);
        var deleted = state.DeletedPromoCodes.FirstOrDefault(p => p.Hash == hash);
        if (deleted != null)
        {
            deleted.DeletedByAdmin = true;
            return deleted;
        }
        deleted = new DeletedPromoCode
        {
            Hash = hash, Label = current?.Label ?? label,
            ExhaustedUtc = current?.ExhaustedUtc ?? nowUtc,
            DeletedUtc = nowUtc, DeletedByAdmin = true
        };
        state.DeletedPromoCodes.Add(deleted);
        return deleted;
    }

    public static PromoPruneResult Prune(ClientState state, DateTime nowUtc)
    {
        var changed = false;
        var deleted = new List<DeletedPromoCode>();
        foreach (var promo in state.PromoCodes.Where(p => p.RemainingUses <= 0 && p.ExhaustedUtc is null))
        {
            promo.ExhaustedUtc = nowUtc; // Older saved codes start the grace period after this update.
            changed = true;
        }
        foreach (var promo in state.PromoCodes.Where(p => p.ExhaustedUtc is { } utc && nowUtc >= utc.AddHours(24)).ToList())
        {
            state.PromoCodes.Remove(promo);
            var tombstone = new DeletedPromoCode
            {
                Hash = promo.Hash, Label = promo.Label,
                ExhaustedUtc = promo.ExhaustedUtc!.Value, DeletedUtc = nowUtc
            };
            state.DeletedPromoCodes.Add(tombstone);
            deleted.Add(tombstone);
            changed = true;
        }
        // Admin revocations remain as deny-list entries, even after many automatic expirations.
        while (state.DeletedPromoCodes.Count > 2000)
        {
            var oldestAutomatic = state.DeletedPromoCodes.FindIndex(p => !p.DeletedByAdmin);
            if (oldestAutomatic < 0) break;
            state.DeletedPromoCodes.RemoveAt(oldestAutomatic);
            changed = true;
        }
        return new PromoPruneResult(changed, deleted);
    }
}

public sealed class ClientState
{
    public long BalanceSeconds { get; set; }
    public bool? ProtectionArmed { get; set; } // null keeps existing installations armed.
    public bool? ManualOverride { get; set; }
    public bool UserPaused { get; set; }
    public int WarningMinutes { get; set; }
    public string RulesMarkdown { get; set; } = "";
    public long RulesRevision { get; set; }
    public long RulesSeenRevision { get; set; }
    public List<BackgroundPhotoInfo> Backgrounds { get; set; } = [];
    public bool WarningIssued { get; set; }
    public long WarningSequence { get; set; }
    public List<TimeWindow> Schedule { get; set; } = [];
    public List<PromoCode> PromoCodes { get; set; } = [];
    public List<DeletedPromoCode> DeletedPromoCodes { get; set; } = [];
    public List<LogEntry> Events { get; set; } = [];
    public List<string> AppliedCommandIds { get; set; } = [];
    public long Sequence { get; set; }
    public DateTime LastUtc { get; set; } = DateTime.UtcNow;
    public bool SessionUnlocked { get; set; }
    public bool ClockWarning { get; set; }
    public MaintenanceCredential MaintenanceCredential { get; set; } = new();
}

public static class AccessPolicy
{
    public static bool IsAllowed(ClientState state, int maintenanceSeconds, DateTime localNow)
    {
        if (state.ProtectionArmed == false) return true;
        if (maintenanceSeconds > 0) return true;
        if (state.UserPaused || state.ClockWarning) return false;
        if (state.ManualOverride.HasValue) return state.ManualOverride.Value;
        if (state.BalanceSeconds <= 0) return false;
        if (state.Schedule.Count == 0) return true;
        var minute = localNow.Hour * 60 + localNow.Minute;
        return state.Schedule.Any(w => w.Day == (int)localNow.DayOfWeek &&
            w.StartMinute <= minute && minute < w.EndMinute);
    }

    public static bool ShouldWarn(ClientState state, bool counting) =>
        counting && !state.WarningIssued && state.WarningMinutes > 0 &&
        state.BalanceSeconds > 0 && state.BalanceSeconds <= state.WarningMinutes * 60L;
}

public sealed class LogEntry
{
    public long Sequence { get; set; }
    public DateTime Utc { get; set; }
    public string Message { get; set; } = "";
}

public sealed class ClientStatus
{
    public string Machine { get; set; } = "";
    public long BalanceSeconds { get; set; }
    public bool ProtectionArmed { get; set; }
    public bool Allowed { get; set; }
    public bool SessionUnlocked { get; set; }
    public bool ClockWarning { get; set; }
    public int MaintenanceSeconds { get; set; }
    public bool? ManualOverride { get; set; }
    public bool UserPaused { get; set; }
    public int WarningMinutes { get; set; }
    public List<TimeWindow> Schedule { get; set; } = [];
    public int PromoTrackingVersion { get; set; }
    public bool SupportsBalanceSubtraction { get; set; }
    public bool SupportsRulesMarkdown { get; set; }
    public string RulesMarkdown { get; set; } = "";
    public bool SupportsBackgroundGallery { get; set; }
    public List<BackgroundPhotoInfo> Backgrounds { get; set; } = [];
    public List<PromoCode> PromoCodes { get; set; } = [];
    public List<DeletedPromoCode> DeletedPromoCodes { get; set; } = [];
    public List<LogEntry> Events { get; set; } = [];
    public long Sequence { get; set; }
    public DateTime Utc { get; set; }
}

public sealed class Command
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Type { get; set; } = "";
    public int Minutes { get; set; }
    public bool? ManualOverride { get; set; }
    public List<TimeWindow>? Schedule { get; set; }
    public PromoCode? PromoCode { get; set; }
    public string? PromoHash { get; set; }
    public string? PromoLabel { get; set; }
    public int WarningMinutes { get; set; }
    public string? RulesMarkdown { get; set; }
    public BackgroundUpload? BackgroundUpload { get; set; }
    public string? BackgroundId { get; set; }
    public string? ImageBase64 { get; set; }
    public MaintenanceCredential? MaintenanceCredential { get; set; }
}

public sealed class MaintenanceCredential
{
    public string Salt { get; set; } = "";
    public string Hash { get; set; } = "";
}

public static class MaintenancePassword
{
    private const int Iterations = 120_000;

    public static MaintenanceCredential Create(string password)
    {
        if (password.Length is < 4 or > 64)
            throw new ArgumentException("Пароль должен содержать от 4 до 64 символов.");
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        return new MaintenanceCredential { Salt = Convert.ToBase64String(salt), Hash = Convert.ToBase64String(hash) };
    }

    public static bool IsValid(MaintenanceCredential? credential)
    {
        if (credential is null || string.IsNullOrEmpty(credential.Salt) || string.IsNullOrEmpty(credential.Hash)) return false;
        try { return Convert.FromBase64String(credential.Salt).Length == 16 && Convert.FromBase64String(credential.Hash).Length == 32; }
        catch (FormatException) { return false; }
    }

    public static bool Verify(string password, MaintenanceCredential? credential)
    {
        if (password.Length is < 4 or > 64 || !IsValid(credential)) return false;
        var salt = Convert.FromBase64String(credential!.Salt);
        var expected = Convert.FromBase64String(credential.Hash);
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}

public sealed class CommandResult
{
    public bool Ok { get; set; }
    public string Error { get; set; } = "";
}

public sealed class ApiRequest
{
    public string RequestId { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime Utc { get; set; } = DateTime.UtcNow;
    public Command? Command { get; set; }
}

public sealed class ApiResponse<T>
{
    public string RequestId { get; set; } = "";
    public T? Data { get; set; }
}

public sealed class DiscoveryProbe
{
    public string RequestId { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime Utc { get; set; } = DateTime.UtcNow;
}

public sealed class DiscoveryReply
{
    public string RequestId { get; set; } = "";
    public string Machine { get; set; } = "";
}

public static class DiscoveryProtocol
{
    public const int Port = 48732;

    public static bool ValidProbe(DiscoveryProbe probe, DateTime nowUtc) =>
        probe.RequestId is { Length: >= 16 and <= 100 }
        && Math.Abs((nowUtc - probe.Utc).TotalMinutes) <= 5;

    public static bool Matches(DiscoveryProbe probe, DiscoveryReply reply) =>
        reply.RequestId == probe.RequestId && !string.IsNullOrWhiteSpace(reply.Machine);
}

public sealed class EncryptedMessage
{
    public string Nonce { get; set; } = "";
    public string Data { get; set; } = "";
    public string Tag { get; set; } = "";
}

public static class Wire
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string Encrypt<T>(T value, byte[] key)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var plain = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, plain, cipher, tag);
        return JsonSerializer.Serialize(new EncryptedMessage
        {
            Nonce = Convert.ToBase64String(nonce), Data = Convert.ToBase64String(cipher), Tag = Convert.ToBase64String(tag)
        }, Json);
    }

    public static T Decrypt<T>(string envelope, byte[] key)
    {
        var msg = JsonSerializer.Deserialize<EncryptedMessage>(envelope, Json) ?? throw new InvalidDataException("Empty message");
        var nonce = Convert.FromBase64String(msg.Nonce);
        var cipher = Convert.FromBase64String(msg.Data);
        var tag = Convert.FromBase64String(msg.Tag);
        if (nonce.Length != 12 || tag.Length != 16 || cipher.Length > 8_000_000) throw new InvalidDataException("Invalid encrypted message");
        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(key, 16);
        aes.Decrypt(nonce, cipher, tag, plain);
        return JsonSerializer.Deserialize<T>(plain, Json) ?? throw new InvalidDataException("Empty payload");
    }

    public static string HashPromo(string code) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(NormalizePromo(code))));
    public static string NormalizePromo(string code) => new string(code.Where(char.IsAsciiLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
    public static string NewPromo()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var bytes = RandomNumberGenerator.GetBytes(16);
        return string.Concat(bytes.Select(b => alphabet[b % alphabet.Length]).Chunk(4).Select(group => new string(group)))
            .Insert(12, "-").Insert(8, "-").Insert(4, "-");
    }
}
