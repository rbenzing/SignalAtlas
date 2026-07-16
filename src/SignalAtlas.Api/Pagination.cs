using System.Text;

namespace SignalAtlas.Api;

/// <summary>A validated page window: <see cref="Limit"/> rows starting at <see cref="Offset"/>.</summary>
public readonly record struct Page(int Limit, int Offset)
{
    /// <summary>Rows to pull from the repo before skipping the offset (saturating to avoid overflow).</summary>
    public int Take => (int)Math.Min((long)Offset + Limit, int.MaxValue);
}

/// <summary>
/// Query pagination + limits (SPEC §9: cursor pagination, request validation). <c>limit</c> defaults
/// to 100, capped at 1000; <c>offset</c> or an opaque base64 <c>cursor</c> select the window. Invalid
/// input yields a 400 RFC 7807 problem-details (correlationId is added by the global customization).
/// </summary>
public static class Pagination
{
    public const int DefaultLimit = 100;
    public const int MaxLimit = 1000;

    public static bool TryResolve(HttpContext ctx, out Page page, out IResult? error)
    {
        page = default;
        error = null;
        var q = ctx.Request.Query;

        var limit = DefaultLimit;
        if (q.TryGetValue("limit", out var lv) && !string.IsNullOrEmpty(lv))
        {
            if (!int.TryParse(lv, out limit) || limit < 1 || limit > MaxLimit)
            {
                error = Bad($"'limit' must be an integer between 1 and {MaxLimit}.");
                return false;
            }
        }

        var offset = 0;
        if (q.TryGetValue("cursor", out var cv) && !string.IsNullOrEmpty(cv))
        {
            if (!TryDecodeCursor(cv!, out offset))
            {
                error = Bad("'cursor' is not a valid pagination cursor.");
                return false;
            }
        }
        else if (q.TryGetValue("offset", out var ov) && !string.IsNullOrEmpty(ov))
        {
            if (!int.TryParse(ov, out offset) || offset < 0)
            {
                error = Bad("'offset' must be a non-negative integer.");
                return false;
            }
        }

        page = new Page(limit, offset);
        return true;
    }

    /// <summary>Opaque forward cursor: base64 of the next offset (SPEC §9 cursor pagination).</summary>
    public static string EncodeCursor(int offset) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(offset.ToString()));

    private static bool TryDecodeCursor(string cursor, out int offset)
    {
        offset = 0;
        try
        {
            var text = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            return int.TryParse(text, out offset) && offset >= 0;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static IResult Bad(string detail) => Results.Problem(
        statusCode: StatusCodes.Status400BadRequest,
        title: "Invalid pagination parameters.",
        detail: detail);
}
