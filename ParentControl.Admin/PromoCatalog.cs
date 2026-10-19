using System.Text.RegularExpressions;
using ParentControl.Core;

namespace ParentControl.Admin;

public static class PromoCatalog
{
    private static readonly Regex legacy = new(@"^(?<code>[A-Z0-9-]+) · (?<minutes>\d+) мин × (?<uses>\d+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static bool MigrateLegacy(ManagedClient client, DateTime nowUtc)
    {
        var changed = false;
        foreach (var item in client.CreatedPromoCodes.ToList())
        {
            var match = legacy.Match(item);
            if (!match.Success || !int.TryParse(match.Groups["minutes"].Value, out var minutes) ||
                !int.TryParse(match.Groups["uses"].Value, out var uses)) continue;
            var code = match.Groups["code"].Value;
            if (code.Length < 4) continue;
            var hash = Wire.HashPromo(code);
            if (!client.PromoRecords.Any(p => p.Hash == hash))
            {
                var pending = client.Pending.Any(c => c.Type == "promo" && c.PromoCode?.Hash == hash);
                client.PromoRecords.Add(new PromoRecord
                {
                    Code = code, Label = code[^4..], Hash = hash, Minutes = minutes,
                    TotalUses = uses, RemainingUses = uses, CreatedUtc = client.LastSeenUtc ?? nowUtc,
                    DeliveredUtc = pending ? null : client.LastSeenUtc
                });
            }
            client.CreatedPromoCodes.Remove(item);
            changed = true;
        }
        return changed;
    }

    // Retire the Admin catalog independently of connectivity. Pending revocations must still be delivered.
    public static bool Prune(ManagedClient client, DateTime nowUtc)
    {
        var changed = false;
        foreach (var record in client.PromoRecords)
        {
            if (record.RemainingUses <= 0 && record.ExhaustedUtc is null &&
                record.DeletedUtc is null && record.DeletionRequestedUtc is null)
            {
                record.ExhaustedUtc = nowUtc;
                changed = true;
            }
        }
        var removed = client.PromoRecords.RemoveAll(record =>
        {
            var retiredAt = new DateTime?[]
            {
                record.DeletionRequestedUtc,
                record.RemainingUses <= 0 ? record.ExhaustedUtc : null,
                record.DeletedUtc
            }.Min();
            return retiredAt is { } since && nowUtc >= since.AddHours(24);
        });
        return changed || removed > 0;
    }

    public static bool Reconcile(ManagedClient client, ClientStatus status, DateTime nowUtc)
    {
        if (status.PromoTrackingVersion < 1) return false;
        var changed = false;
        foreach (var record in client.PromoRecords.Where(p => p.DeletedUtc is null))
        {
            var deleted = status.DeletedPromoCodes.FirstOrDefault(p => p.Hash == record.Hash);
            if (deleted != null)
            {
                record.Code = ""; // The secret is removed locally; the last four characters remain for the audit row.
                record.Label = deleted.Label;
                record.RemainingUses = 0;
                record.ExhaustedUtc = deleted.ExhaustedUtc;
                record.DeletedUtc = deleted.DeletedUtc;
                record.DeletedByAdmin = deleted.DeletedByAdmin;
                record.DeletionRequestedUtc ??= deleted.DeletedByAdmin ? deleted.DeletedUtc : null;
                record.MissingOnClient = false;
                changed = true;
                continue;
            }
            var current = status.PromoCodes.FirstOrDefault(p => p.Hash == record.Hash);
            if (current != null)
            {
                var exhaustedUtc = current.RemainingUses <= 0
                    ? current.ExhaustedUtc ?? record.ExhaustedUtc ?? nowUtc : current.ExhaustedUtc;
                if (record.RemainingUses != current.RemainingUses || record.ExhaustedUtc != exhaustedUtc ||
                    record.DeliveredUtc is null || record.MissingOnClient) changed = true;
                record.RemainingUses = current.RemainingUses;
                record.ExhaustedUtc = exhaustedUtc;
                record.DeliveredUtc ??= nowUtc;
                record.MissingOnClient = false;
            }
            else if (record.DeliveredUtc is not null && !client.Pending.Any(c => c.Type == "promo" && c.PromoCode?.Hash == record.Hash))
            {
                if (!record.MissingOnClient) changed = true;
                record.MissingOnClient = true;
            }
        }
        return changed;
    }
}
