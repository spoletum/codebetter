namespace CodeBetter.Ingestor;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.IO;

public class DependencyAnalyzer : IDisposable
{
    private readonly IRepository _repository;
    private readonly ILoggingService _logger;
    private bool _disposed;
    private const int BatchSize = 100;

    public DependencyAnalyzer(IRepository repository, ILoggingService logger)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<List<MethodCallInfo>> AnalyzeDependenciesAsync(string filePath)
    {
        if (string.IsNullOrEmpty(filePath))
        {
            throw new ArgumentNullException(nameof(filePath));
        }

        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"The file '{filePath}' does not exist.");
        }

        try
        {
            _logger.LogInformation($"Analyzing file: {filePath}");
            
            // Read file content with proper encoding
            var code = await File.ReadAllTextAsync(filePath);
            
            // Parse the syntax tree with error handling
            var syntaxTree = CSharpSyntaxTree.ParseText(code);
            var diagnostics = syntaxTree.GetDiagnostics();
            
            if (diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error))
            {
                var errors = string.Join(Environment.NewLine, diagnostics.Select(d => d.ToString()));
                _logger.LogError($"Failed to parse C# file. Errors:{Environment.NewLine}{errors}");
                throw new InvalidOperationException($"Failed to parse C# file. Errors:{Environment.NewLine}{errors}");
            }

            var root = syntaxTree.GetRoot() as CompilationUnitSyntax 
                ?? throw new InvalidDataException("Unable to parse the C# file.");

            var namespaceDeclaration = root.Members.OfType<NamespaceDeclarationSyntax>().FirstOrDefault();
            var namespaceName = namespaceDeclaration?.Name.ToString() ?? "Global";

            var methodCalls = new List<MethodCallInfo>();
            var classNames = new HashSet<string>();
            var methodNames = new HashSet<string>();
            var relationships = new List<(string className, string methodName)>();
            var methodCallRelationships = new List<(string caller, string callee)>();

            // Analyze all method declarations to get context
            var methodDeclarations = root.DescendantNodes()
                .OfType<MethodDeclarationSyntax>()
                .ToList();

            _logger.LogInformation($"Found {methodDeclarations.Count} method declarations in file");

            foreach (var methodDecl in methodDeclarations)
            {
                var containingType = methodDecl.Parent as ClassDeclarationSyntax;
                var methodName = methodDecl.Identifier.Text;
                var isStatic = methodDecl.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword));

                var containingTypeName = containingType?.Identifier.Text ?? "Unknown";
                
                if (containingTypeName != "Unknown")
                {
                    classNames.Add(containingTypeName);
                    methodNames.Add($"{containingTypeName}.{methodName}");
                    relationships.Add((containingTypeName, $"{containingTypeName}.{methodName}"));
                }

                // Analyze method invocations within this method
                var invocations = methodDecl.DescendantNodes()
                    .OfType<InvocationExpressionSyntax>();

                foreach (var invocation in invocations)
                {
                    var callInfo = new MethodCallInfo
                    {
                        CallerType = containingTypeName,
                        CallerMethod = methodName,
                        Namespace = namespaceName,
                        LineNumber = invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                        IsStatic = isStatic
                    };

                    // Handle different types of method calls
                    if (invocation.Expression is MemberAccessExpressionSyntax memberAccess)
                    {
                        var caller = memberAccess.Expression.ToString();
                        var callee = memberAccess.Name.ToString();

                        callInfo.CalleeType = caller;
                        callInfo.CalleeMethod = callee;

                        // Detect extension methods
                        callInfo.IsExtensionMethod = memberAccess.Expression is IdentifierNameSyntax 
                            && !caller.Contains(".");

                        // Detect property accessors
                        callInfo.IsPropertyAccessor = memberAccess.Parent is InvocationExpressionSyntax 
                            && memberAccess.Parent.Parent is MemberAccessExpressionSyntax;

                        // Detect event handlers
                        callInfo.IsEventHandler = callee.StartsWith("On") 
                            || callee.EndsWith("Handler") 
                            || callee.EndsWith("Event");

                        methodCallRelationships.Add(($"{containingTypeName}.{methodName}", $"{caller}.{callee}"));
                    }
                    else
                    {
                        var expression = invocation.Expression.ToString();
                        callInfo.CalleeMethod = expression;
                        methodCallRelationships.Add(($"{containingTypeName}.{methodName}", expression));
                    }

                    methodCalls.Add(callInfo);
                }
            }

            // Process in batches
            if (classNames.Any() || methodNames.Any())
            {
                _logger.LogInformation($"Processing {classNames.Count} classes and {methodNames.Count} methods");
                await _repository.AddNodesBatchAsync(classNames, methodNames);
            }

            if (relationships.Any())
            {
                _logger.LogInformation($"Processing {relationships.Count} relationships");
                await _repository.AddRelationshipsBatchAsync(relationships);
            }

            if (methodCallRelationships.Any())
            {
                _logger.LogInformation($"Processing {methodCallRelationships.Count} method calls");
                await _repository.WriteRelationshipsBatchAsync(methodCallRelationships);
            }

            _logger.LogInformation($"Analysis complete. Found {methodCalls.Count} method calls.");
            return methodCalls;
        }
        catch (Exception ex) when (ex is not InvalidOperationException && ex is not InvalidDataException)
        {
            _logger.LogError("Failed to analyze dependencies", ex);
            throw new InvalidOperationException($"Failed to analyze dependencies: {ex.Message}", ex);
        }
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                // Dispose managed resources if needed
            }
            _disposed = true;
        }
    }
}