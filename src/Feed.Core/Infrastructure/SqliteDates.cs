using System.Globalization;
using Feed.Core.Domain;
using Microsoft.Data.Sqlite;
namespace Feed.Core.Infrastructure;

// Microsoft.Data.Sqlite's GetDateTime tries a long list of DateTime.TryParseExact formats on every call.
// EF Core writes timestamps in ISO-8601 "yyyy-MM-dd HH:mm:ss[.fffffff]", so parsing that shape directly
// avoids the format-array loop while preserving exact ticks and DateTimeKind.Unspecified parity with EF.
public static class SqliteDates
{
    static bool Digit2(ReadOnlySpan<char> s, int offset, out int value)
    {
        uint d0 = (uint)(s[offset] - '0'), d1 = (uint)(s[offset + 1] - '0');
        if (d0 <= 9 && d1 <= 9) { value = (int)(d0 * 10 + d1); return true; }
        value = 0; return false;
    }

    static bool Digit4(ReadOnlySpan<char> s, int offset, out int value)
    {
        uint d0 = (uint)(s[offset] - '0'), d1 = (uint)(s[offset + 1] - '0'), d2 = (uint)(s[offset + 2] - '0'), d3 = (uint)(s[offset + 3] - '0');
        if (d0 <= 9 && d1 <= 9 && d2 <= 9 && d3 <= 9) { value = (int)(d0 * 1000 + d1 * 100 + d2 * 10 + d3); return true; }
        value = 0; return false;
    }

    public static DateTime Parse(string value)
    {
        var span = value.AsSpan();
        if (span.Length >= 19 && span[4] == '-' && span[7] == '-' && (span[10] == ' ' || span[10] == 'T') && span[13] == ':' && span[16] == ':'
            && Digit4(span, 0, out int y) && Digit2(span, 5, out int mo) && Digit2(span, 8, out int d)
            && Digit2(span, 11, out int h) && Digit2(span, 14, out int mi) && Digit2(span, 17, out int sec)
            && y >= 1 && mo is >= 1 and <= 12 && d is >= 1 and <= 31 && h <= 23 && mi <= 59 && sec <= 59
            && d <= DateTime.DaysInMonth(y, mo))
        {
            if (span.Length == 19) return new DateTime(y, mo, d, h, mi, sec, DateTimeKind.Unspecified);
            if (span[19] == '.' && span.Length is >= 21 and <= 27)
            {
                long frac = 0; int digits = 0; bool ok = true;
                for (int i = 20; i < span.Length; i++)
                {
                    uint c = (uint)(span[i] - '0');
                    if (c > 9) { ok = false; break; }
                    frac = frac * 10 + c;
                    digits++;
                }
                if (ok)
                {
                    while (digits < 7) { frac *= 10; digits++; }
                    return new DateTime(y, mo, d, h, mi, sec, DateTimeKind.Unspecified).AddTicks(frac);
                }
            }
        }
        return DateTime.Parse(value, CultureInfo.InvariantCulture);
    }

    public static long ParseTicks(string value) => Parse(value).Ticks;

    public static async Task<Author[]> ReadAuthorsAsync(SqliteConnection conn, SqliteTransaction? tx, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT Id, Platform, PlatformAuthorId, DisplayName, Url, IsFriend, AvatarPath, RefsJson, FirstSeenAt, LastSeenAt FROM Authors";
        var list = new List<Author>(256);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add(new Author
            {
                Id = r.GetInt64(0),
                Platform = r.GetString(1),
                PlatformAuthorId = r.IsDBNull(2) ? null : r.GetString(2),
                DisplayName = r.IsDBNull(3) ? null : r.GetString(3),
                Url = r.IsDBNull(4) ? null : r.GetString(4),
                IsFriend = r.GetInt32(5) != 0,
                AvatarPath = r.IsDBNull(6) ? null : r.GetString(6),
                RefsJson = r.GetString(7),
                FirstSeenAt = Parse(r.GetString(8)),
                LastSeenAt = Parse(r.GetString(9))
            });
        }
        return list.ToArray();
    }
}
