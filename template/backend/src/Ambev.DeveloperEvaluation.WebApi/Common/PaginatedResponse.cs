using System.Text.Json.Serialization;

namespace Ambev.DeveloperEvaluation.WebApi.Common;

public class PaginatedResponse<T> : ApiResponseWithData<IEnumerable<T>>
{
    public int CurrentPage { get; set; }

    /// <summary>
    /// Total page count in offset/page mode. <c>null</c> in cursor (keyset)
    /// mode — that mode doesn't run a COUNT(*).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? TotalPages { get; set; }

    /// <summary>
    /// Total matching rows in offset/page mode. <c>null</c> in cursor mode.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? TotalCount { get; set; }

    /// <summary>
    /// Opaque cursor for the next page (keyset mode only). Clients pass it
    /// back as the <c>_cursor</c> query parameter to resume.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? NextCursor { get; set; }
}
