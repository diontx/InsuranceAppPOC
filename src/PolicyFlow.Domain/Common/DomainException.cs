namespace PolicyFlow.Domain.Common;

/// <summary>
/// Raised when an operation is syntactically valid but violates a business rule
/// or attempts an illegal state transition. The API surface maps this to
/// HTTP 422 (Unprocessable Content) rather than 500.
/// </summary>
public class DomainException : Exception
{
    public DomainException(string message) : base(message) { }

    public DomainException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Raised when an operation conflicts with the current state of a resource,
/// for example creating a second policy from the same quote. Maps to HTTP 409.
/// </summary>
public class BusinessConflictException : DomainException
{
    public BusinessConflictException(string message) : base(message) { }
}
