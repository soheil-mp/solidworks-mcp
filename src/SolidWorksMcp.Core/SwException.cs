namespace SolidWorksMcp.Core;

/// <summary>Stable, machine-readable error codes. Every tool error message starts with one of these.</summary>
public static class ErrorCodes
{
    public const string NotConnected = "NOT_CONNECTED";
    public const string NoActiveDoc = "NO_ACTIVE_DOC";
    public const string WrongDocType = "WRONG_DOC_TYPE";
    public const string InvalidArgument = "INVALID_ARGUMENT";
    public const string EntityNotFound = "ENTITY_NOT_FOUND";
    public const string EntityAmbiguous = "ENTITY_AMBIGUOUS";
    public const string EntityStale = "ENTITY_STALE";
    public const string SketchState = "SKETCH_STATE";
    public const string FeatureFailed = "FEATURE_FAILED";
    public const string RebuildError = "REBUILD_ERROR";
    public const string VerifyFailed = "VERIFY_FAILED";
    public const string Timeout = "TIMEOUT";
    public const string Busy = "BUSY";
    public const string DialogBlocking = "DIALOG_BLOCKING";
    public const string IoError = "IO_ERROR";
    public const string NotSupported = "NOT_SUPPORTED";
    public const string AddinUnavailable = "ADDIN_UNAVAILABLE";
    public const string VbaError = "VBA_ERROR";
    public const string Disabled = "DISABLED";
    public const string NotOwned = "NOT_OWNED";
    public const string DocClosed = "DOC_CLOSED";
    public const string UserBusy = "USER_BUSY";
    public const string Dirty = "DIRTY";
    public const string Internal = "INTERNAL";
}

/// <summary>
/// The only exception type services throw on purpose. <see cref="Code"/> is one of <see cref="ErrorCodes"/>;
/// <see cref="Hint"/> tells the agent what to do next.
/// </summary>
public sealed class SwException : Exception
{
    public string Code { get; }
    public string? Hint { get; }

    public SwException(string code, string message, string? hint = null, Exception? inner = null)
        : base(message, inner)
    {
        Code = code;
        Hint = hint;
    }

    /// <summary>"CODE: message Hint: hint" — the text an MCP client sees.</summary>
    public string ToAgentText() => Hint is null ? $"{Code}: {Message}" : $"{Code}: {Message} Hint: {Hint}";

    public override string ToString() => ToAgentText();

    public static SwException InvalidArgument(string message, string? hint = null) =>
        new(ErrorCodes.InvalidArgument, message, hint);
}
