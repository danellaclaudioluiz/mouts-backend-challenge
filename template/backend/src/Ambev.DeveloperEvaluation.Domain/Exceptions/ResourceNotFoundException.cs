namespace Ambev.DeveloperEvaluation.Domain.Exceptions;

/// <summary>
/// Thrown when an aggregate or entity referenced by id cannot be found.
/// Translated to HTTP 404 by the WebApi exception middleware.
/// </summary>
public class ResourceNotFoundException : DomainException
{
    public ResourceNotFoundException(string message) : base(message)
    {
    }

    public ResourceNotFoundException(string resource, Guid id)
        : base($"{resource} with id '{id}' was not found.")
    {
    }
}
