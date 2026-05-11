namespace Ambev.DeveloperEvaluation.Common.Security;

public class BCryptPasswordHasher : IPasswordHasher
{
    /// <summary>
    /// BCrypt work factor. OWASP 2024 guidance for high-sensitivity APIs
    /// (fintech / regulated) is &gt;= 12. Each +1 doubles cost; 12 is the
    /// minimum that won't lag a login on commodity 2024+ hardware.
    /// </summary>
    private const int WorkFactor = 12;

    /// <summary>
    /// A frozen BCrypt hash of a throwaway value, computed once per process
    /// at the same work factor as real hashes. Login flows use it as the
    /// comparison target when the email belongs to no user, so VerifyPassword
    /// burns the same ~hundreds of ms either way and an attacker can't
    /// enumerate emails through response timing.
    /// </summary>
    public static readonly Lazy<string> TimingLevelHash = new(() =>
        BCrypt.Net.BCrypt.HashPassword("not-a-real-password-just-for-timing", WorkFactor));

    public string HashPassword(string password)
    {
        return BCrypt.Net.BCrypt.HashPassword(password, WorkFactor);
    }

    public bool VerifyPassword(string password, string hash)
    {
        return BCrypt.Net.BCrypt.Verify(password, hash);
    }
}
