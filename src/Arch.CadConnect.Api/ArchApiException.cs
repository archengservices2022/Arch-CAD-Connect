namespace Arch.CadConnect.Api;

/// <summary>
/// Why an Arch API call failed, at a granularity the UI and the connection
/// state machine can act on.
/// </summary>
public enum ArchApiFailureKind
{
    /// <summary>DNS / connect / TLS failure, or the request was cancelled.</summary>
    Network,

    /// <summary>The request exceeded the client timeout.</summary>
    Timeout,

    /// <summary>400 - the request was malformed or the transport was rejected.</summary>
    BadRequest,

    /// <summary>401 - credentials/session rejected. Discard the local session.</summary>
    Unauthorized,

    /// <summary>404 - the resource does not exist for this tenant.</summary>
    NotFound,

    /// <summary>409 / 422 - a controlled business-rule rejection.</summary>
    Conflict,

    /// <summary>5xx, or a response that did not match the expected contract.</summary>
    Server,

    /// <summary>The operation is not implemented in this client milestone.</summary>
    NotImplemented,
}

/// <summary>
/// The single exception type the Api layer throws. Its <see cref="Message"/> is
/// always safe to show a user: it is one of a small set of fixed strings and
/// NEVER contains a raw server error body, a stack trace, a URL with a query
/// string, or a token. Diagnostic detail (if any) goes only to
/// <see cref="Diagnostic"/>, which callers may log but must not display.
/// </summary>
public sealed class ArchApiException : Exception
{
    public ArchApiException(
        ArchApiFailureKind kind,
        string userMessage,
        int? httpStatus = null,
        string? diagnostic = null,
        Exception? inner = null)
        : base(userMessage, inner)
    {
        Kind = kind;
        HttpStatus = httpStatus;
        Diagnostic = diagnostic;
    }

    public ArchApiFailureKind Kind { get; }

    public int? HttpStatus { get; }

    /// <summary>Non-user-facing detail. Safe to write to a local log, not to a dialog.</summary>
    public string? Diagnostic { get; }

    public bool IsAuthFailure => Kind == ArchApiFailureKind.Unauthorized;

    public bool IsServerUnavailable => Kind is ArchApiFailureKind.Network
        or ArchApiFailureKind.Timeout
        or ArchApiFailureKind.Server;

    public static ArchApiException NotImplemented(string operation) => new(
        ArchApiFailureKind.NotImplemented,
        $"'{operation}' is not available in this milestone (P4A foundation).");
}
