namespace CodeBetter.Ingestor;

public class MethodCallInfo
{
    public string CallerType { get; set; } = string.Empty;
    public string CallerMethod { get; set; } = string.Empty;
    public string CalleeType { get; set; } = string.Empty;
    public string CalleeMethod { get; set; } = string.Empty;
    public string Namespace { get; set; } = string.Empty;
    public int LineNumber { get; set; }
    public bool IsStatic { get; set; }
    public bool IsExtensionMethod { get; set; }
    public bool IsConstructor { get; set; }
    public bool IsPropertyAccessor { get; set; }
    public bool IsEventHandler { get; set; }
} 