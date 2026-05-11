using System.Text;

namespace Ambev.DeveloperEvaluation.ORM.Repositories;

/// <summary>
/// Opaque cursor encoding used by the keyset pagination mode of
/// <see cref="SaleRepository.ListAsync"/>. Encodes the (SaleDate, Id) pair
/// of the last row of a page so the next request can resume from there in
/// O(log n) without a COUNT(*) round-trip.
/// </summary>
/// <remarks>
/// The wire format is base64url(<c>{date-iso-8601}|{guid}</c>) — opaque
/// from the client's perspective; the server alone parses it. Tampering or
/// truncating the value yields <see cref="ArgumentException"/>, mapped to
/// HTTP 400 by the WebApi middleware.
/// </remarks>
internal static class SaleCursor
{
    public static string Encode(DateTime saleDate, Guid id)
    {
        var raw = $"{saleDate:O}|{id}";
        var bytes = Encoding.UTF8.GetBytes(raw);
        return Base64UrlEncode(bytes);
    }

    public static (DateTime SaleDate, Guid Id) Decode(string cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor))
            throw new ArgumentException("Cursor cannot be empty.", nameof(cursor));

        string raw;
        try
        {
            raw = Encoding.UTF8.GetString(Base64UrlDecode(cursor));
        }
        catch (FormatException ex)
        {
            throw new ArgumentException("Cursor is not a valid base64 value.", nameof(cursor), ex);
        }

        var parts = raw.Split('|', 2);
        if (parts.Length != 2 ||
            !DateTime.TryParse(parts[0], null,
                System.Globalization.DateTimeStyles.RoundtripKind, out var date) ||
            !Guid.TryParse(parts[1], out var id))
        {
            throw new ArgumentException("Cursor is malformed.", nameof(cursor));
        }

        return (date, id);
    }

    private static string Base64UrlEncode(byte[] bytes)
    {
        var s = Convert.ToBase64String(bytes);
        return s.TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static byte[] Base64UrlDecode(string input)
    {
        var s = input.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
        }
        return Convert.FromBase64String(s);
    }
}
