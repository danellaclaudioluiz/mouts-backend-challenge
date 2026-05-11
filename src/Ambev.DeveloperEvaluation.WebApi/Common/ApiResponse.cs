using Ambev.DeveloperEvaluation.Common.Validation;
using System.Text.Json.Serialization;

namespace Ambev.DeveloperEvaluation.WebApi.Common;

public class ApiResponse
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public IEnumerable<ValidationErrorDetail>? Errors { get; set; }
}
