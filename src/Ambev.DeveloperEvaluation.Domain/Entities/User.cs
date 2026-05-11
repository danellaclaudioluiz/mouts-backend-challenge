using Ambev.DeveloperEvaluation.Common.Security;
using Ambev.DeveloperEvaluation.Common.Validation;
using Ambev.DeveloperEvaluation.Domain.Common;
using Ambev.DeveloperEvaluation.Domain.Enums;
using Ambev.DeveloperEvaluation.Domain.Validation;

namespace Ambev.DeveloperEvaluation.Domain.Entities;

/// <summary>
/// User aggregate. Encapsulates identity + authentication state behind a
/// factory so callers can never construct a User in an invalid state
/// (e.g. role smuggled in via mass assignment). State changes go through
/// intent-named methods (<see cref="Activate"/>, <see cref="Suspend"/>,
/// <see cref="ChangeRole"/>, <see cref="ChangePassword"/>) — direct
/// property writes from outside the assembly are blocked by
/// <c>private set</c>.
/// </summary>
/// <remarks>
/// The parameterless constructor is kept <c>public</c> on purpose: EF
/// Core materialises entities via that ctor + reflection-based property
/// writes, and Bogus' <c>Faker&lt;User&gt;.RuleFor</c> in the test
/// fixtures relies on the same pair. Both can write private setters via
/// reflection — only the public surface is closed.
/// </remarks>
public class User : BaseEntity, IUser
{
    /// <summary>Username — display label, doubles as login identifier in some flows.</summary>
    public string Username { get; private set; } = string.Empty;

    /// <summary>Email — unique across the table (enforced by `IX_Users_Email`).</summary>
    public string Email { get; private set; } = string.Empty;

    /// <summary>Phone in international format.</summary>
    public string Phone { get; private set; } = string.Empty;

    /// <summary>BCrypt hash of the user's password — never the plaintext.</summary>
    public string Password { get; private set; } = string.Empty;

    /// <summary>
    /// Role bound to this user. Mutations route through <see cref="ChangeRole"/>
    /// so an audit trail / outbox event can be hung off the call site
    /// without touching every caller.
    /// </summary>
    public UserRole Role { get; private set; }

    /// <summary>Current lifecycle status — drives login admission.</summary>
    public UserStatus Status { get; private set; }

    public DateTime CreatedAt { get; private set; }
    public DateTime? UpdatedAt { get; private set; }

    string IUser.Id => Id.ToString();
    string IUser.Username => Username;
    string IUser.Role => Role.ToString();

    /// <summary>
    /// Reserved for EF Core (materialisation) and Bogus (test fixtures).
    /// Application code should never <c>new</c> a User directly — use the
    /// <see cref="Create(string, string, string, string)"/> factory or
    /// the admin-only role-assignment factory below.
    /// </summary>
    public User()
    {
        CreatedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Self-service signup factory. The role / status are HARD-CODED to
    /// <see cref="UserRole.Customer"/> and <see cref="UserStatus.Active"/>
    /// so a mass-assignment attempt on the public POST /users body cannot
    /// escalate to admin.
    /// </summary>
    public static User Create(string username, string passwordHash, string email, string phone)
    {
        if (string.IsNullOrWhiteSpace(username))
            throw new ArgumentException("Username is required.", nameof(username));
        if (string.IsNullOrWhiteSpace(passwordHash))
            throw new ArgumentException("Password hash is required.", nameof(passwordHash));
        if (string.IsNullOrWhiteSpace(email))
            throw new ArgumentException("Email is required.", nameof(email));
        if (string.IsNullOrWhiteSpace(phone))
            throw new ArgumentException("Phone is required.", nameof(phone));

        return new User
        {
            Id = Guid.NewGuid(),
            Username = username,
            Password = passwordHash,
            Email = email,
            Phone = phone,
            Role = UserRole.Customer,
            Status = UserStatus.Active,
            CreatedAt = DateTime.UtcNow
        };
    }

    /// <summary>
    /// Validates the aggregate against the rules in <see cref="UserValidator"/>.
    /// Kept on the entity so the same checks run regardless of caller
    /// (handler, importer, console tool).
    /// </summary>
    public ValidationResultDetail Validate()
    {
        var validator = new UserValidator();
        var result = validator.Validate(this);
        return new ValidationResultDetail
        {
            IsValid = result.IsValid,
            Errors = result.Errors.Select(o => (ValidationErrorDetail)o)
        };
    }

    /// <summary>Activates the account — used after a suspension / inactive period.</summary>
    public void Activate()
    {
        Status = UserStatus.Active;
        UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>Marks the account inactive — kept distinct from Suspend (which implies admin action).</summary>
    public void Deactivate()
    {
        Status = UserStatus.Inactive;
        UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>Suspends the account — admin-driven, blocks login regardless of password.</summary>
    public void Suspend()
    {
        Status = UserStatus.Suspended;
        UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Admin-only role assignment. The signup factory never calls this —
    /// any escalation has to go through an explicit handler that an
    /// `[Authorize(Roles = "Admin")]` endpoint protects.
    /// </summary>
    public void ChangeRole(UserRole newRole)
    {
        if (newRole == UserRole.None)
            throw new ArgumentException("Role cannot be set to None.", nameof(newRole));
        Role = newRole;
        UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>Rotates the password hash. Callers must hash before passing in.</summary>
    public void ChangePassword(string newPasswordHash)
    {
        if (string.IsNullOrWhiteSpace(newPasswordHash))
            throw new ArgumentException("Password hash is required.", nameof(newPasswordHash));
        Password = newPasswordHash;
        UpdatedAt = DateTime.UtcNow;
    }
}
