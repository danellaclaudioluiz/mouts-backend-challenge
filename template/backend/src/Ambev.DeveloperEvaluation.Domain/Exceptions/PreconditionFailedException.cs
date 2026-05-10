namespace Ambev.DeveloperEvaluation.Domain.Exceptions;

/// <summary>
/// Thrown when an HTTP precondition (typically an If-Match header carrying
/// an ETag) is supplied but does not match the current resource version.
/// Translated to HTTP 412 Precondition Failed by the WebApi exception
/// middleware.
/// </summary>
public class PreconditionFailedException : DomainException
{
    public PreconditionFailedException(string message) : base(message)
    {
    }
}
