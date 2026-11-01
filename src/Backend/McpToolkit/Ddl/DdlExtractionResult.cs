namespace McpToolkit.Ddl;

public sealed record DdlExtractionResult(string QualifiedName, string ObjectType, string Source, string? Error)
{
    public bool Succeeded => Error is null && !string.IsNullOrWhiteSpace(Source);

    public static DdlExtractionResult Failure(string qualifiedName, string error, string objectType = "") =>
        new(qualifiedName, objectType, "", error);
}
