namespace ReconPlatform.Config;

public sealed record ValidationResult
{
    public bool IsValid => Errors.Count == 0;
    public IReadOnlyList<ValidationError> Errors { get; init; } = [];

    public static ValidationResult Success() => new();
    public static ValidationResult Failure(IReadOnlyList<ValidationError> errors) => new() { Errors = errors };
}

public sealed record ValidationError(string Field, string Message);
