namespace ScheduleManager.Application.Errors;

public enum ErrorKind { Validation, Unauthorized, Forbidden, NotFound, Conflict, Unprocessable }

public sealed class AppException(string code, string message, ErrorKind kind, IReadOnlyDictionary<string, string[]>? errors = null)
    : Exception(message)
{
    public string Code { get; } = code;
    public ErrorKind Kind { get; } = kind;
    public IReadOnlyDictionary<string, string[]> Errors { get; } = errors ?? new Dictionary<string, string[]>();

    public static AppException Validation(IReadOnlyDictionary<string, string[]> errors) =>
        new("VALIDATION_ERROR", "Um ou mais campos são inválidos.", ErrorKind.Validation, errors);

    public static AppException Unauthorized(string code, string message) => new(code, message, ErrorKind.Unauthorized);
    public static AppException Forbidden() => new("ACCESS_DENIED", "Você não possui permissão para esta operação.", ErrorKind.Forbidden);
    public static AppException NotFound(string code, string message) => new(code, message, ErrorKind.NotFound);
    public static AppException Conflict(string code, string message) => new(code, message, ErrorKind.Conflict);
    public static AppException Rule(string code, string message) => new(code, message, ErrorKind.Unprocessable);
}
