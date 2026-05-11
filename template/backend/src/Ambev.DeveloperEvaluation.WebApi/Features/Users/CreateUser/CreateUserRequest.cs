namespace Ambev.DeveloperEvaluation.WebApi.Features.Users.CreateUser;

/// <summary>
/// Self-service user creation payload. Deliberately omits Role/Status:
/// accepting them on a public endpoint is a mass-assignment + privilege-
/// escalation hole (any anonymous caller could ship Role=Admin). The
/// handler hard-codes role=Customer and status=Active; role changes are
/// only allowed through a separate admin-only endpoint.
/// </summary>
public class CreateUserRequest
{
    /// <summary>Username — must be unique and contain only valid characters.</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>Password — must meet the configured complexity policy.</summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>Phone in format (XX) XXXXX-XXXX.</summary>
    public string Phone { get; set; } = string.Empty;

    /// <summary>Email address.</summary>
    public string Email { get; set; } = string.Empty;
}