namespace Ambev.DeveloperEvaluation.Domain.Exceptions;

/// <summary>
/// Thrown when a write operation conflicts with the current state of the
/// system — typically a uniqueness violation surfaced before the database
/// constraint fires (e.g. duplicate SaleNumber). Translated to HTTP 409 by
/// the WebApi exception middleware.
/// </summary>
public class ConflictException : DomainException
{
    public ConflictException(string message) : base(message)
    {
    }
}
