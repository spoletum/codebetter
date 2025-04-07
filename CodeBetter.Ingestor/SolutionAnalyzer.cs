namespace CodeBetter.Ingestor;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.MSBuild;

public class SolutionAnalyzer : IDisposable
{
    private readonly IRepository _repository;
    private readonly MSBuildWorkspace _workspace;
    private readonly ILoggingService _logger;
    private bool _disposed;
    private const int BatchSize = 100;

    public SolutionAnalyzer(IRepository repository, ILoggingService logger)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        
        if (!MSBuildLocator.IsRegistered)
        {
            MSBuildLocator.RegisterDefaults();
        }
        
        _workspace = MSBuildWorkspace.Create();
    }

    public async Task<List<MethodCallInfo>> AnalyzeSolutionAsync(string solutionPath, IProgress<AnalysisProgress>? progress = null)
    {
        if (string.IsNullOrEmpty(solutionPath))
        {
            throw new ArgumentNullException(nameof(solutionPath));
        }

        if (!File.Exists(solutionPath))
        {
            throw new FileNotFoundException($"The solution file '{solutionPath}' does not exist.");
        }

        try
        {
            _logger.LogInformation($"Starting analysis of solution: {solutionPath}");
            progress?.Report(new AnalysisProgress("Loading solution...", 0));
            var solution = await _workspace.OpenSolutionAsync(solutionPath);
            
            if (solution.Projects.Count() == 0)
            {
                throw new InvalidOperationException("The solution does not contain any projects.");
            }

            _logger.LogInformation($"Found {solution.Projects.Count()} projects in the solution");
            var methodCallInfos = new List<MethodCallInfo>();
            var allClassNames = new HashSet<string>();
            var allMethodNames = new HashSet<string>();
            var allRelationships = new List<(string className, string methodName)>();
            var allMethodCalls = new List<(string caller, string callee)>();
            
            var totalProjects = solution.Projects.Count();
            var currentProject = 0;
            
            foreach (var project in solution.Projects)
            {
                currentProject++;
                _logger.LogInformation($"Analyzing project: {project.Name}");
                progress?.Report(new AnalysisProgress($"Analyzing project: {project.Name}", 
                    (currentProject - 1) * 100 / totalProjects));
                
                var compilation = await project.GetCompilationAsync();
                if (compilation == null)
                {
                    _logger.LogWarning($"Could not compile project {project.Name}. Skipping.");
                    continue;
                }
                
                var documents = project.Documents.ToList();
                var totalDocuments = documents.Count;
                var currentDocument = 0;
                
                _logger.LogInformation($"Found {totalDocuments} documents in project {project.Name}");
                
                foreach (var document in documents)
                {
                    currentDocument++;
                    if (document.FilePath == null || !document.FilePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    
                    _logger.LogInformation($"Analyzing file: {Path.GetFileName(document.FilePath)}");
                    progress?.Report(new AnalysisProgress(
                        $"Analyzing file: {Path.GetFileName(document.FilePath)}", 
                        ((currentProject - 1) * 100 + (currentDocument * 100 / totalDocuments)) / totalProjects));
                    
                    var syntaxTree = await document.GetSyntaxTreeAsync();
                    var semanticModel = await document.GetSemanticModelAsync();
                    
                    if (syntaxTree == null || semanticModel == null)
                    {
                        _logger.LogWarning($"Could not analyze {document.FilePath}. Skipping.");
                        continue;
                    }
                    
                    var analysisResult = AnalyzeSyntaxTree(syntaxTree.GetRoot(), semanticModel, document.FilePath);
                    
                    methodCallInfos.AddRange(analysisResult.methodCallInfos);
                    allClassNames.UnionWith(analysisResult.classNames);
                    allMethodNames.UnionWith(analysisResult.methodNames);
                    allRelationships.AddRange(analysisResult.relationships);
                    allMethodCalls.AddRange(analysisResult.methodCalls);
                    
                    // Process in batches
                    if (allClassNames.Count >= BatchSize || allMethodNames.Count >= BatchSize)
                    {
                        _logger.LogInformation($"Processing batch of {allClassNames.Count} classes and {allMethodNames.Count} methods");
                        await _repository.AddNodesBatchAsync(allClassNames, allMethodNames);
                        allClassNames.Clear();
                        allMethodNames.Clear();
                    }
                    
                    if (allRelationships.Count >= BatchSize)
                    {
                        _logger.LogInformation($"Processing batch of {allRelationships.Count} relationships");
                        await _repository.AddRelationshipsBatchAsync(allRelationships);
                        allRelationships.Clear();
                    }
                    
                    if (allMethodCalls.Count >= BatchSize)
                    {
                        _logger.LogInformation($"Processing batch of {allMethodCalls.Count} method calls");
                        await _repository.WriteRelationshipsBatchAsync(allMethodCalls);
                        allMethodCalls.Clear();
                    }
                }
            }
            
            // Process remaining items
            if (allClassNames.Any() || allMethodNames.Any())
            {
                _logger.LogInformation($"Processing remaining {allClassNames.Count} classes and {allMethodNames.Count} methods");
                await _repository.AddNodesBatchAsync(allClassNames, allMethodNames);
            }
            
            if (allRelationships.Any())
            {
                _logger.LogInformation($"Processing remaining {allRelationships.Count} relationships");
                await _repository.AddRelationshipsBatchAsync(allRelationships);
            }
            
            if (allMethodCalls.Any())
            {
                _logger.LogInformation($"Processing remaining {allMethodCalls.Count} method calls");
                await _repository.WriteRelationshipsBatchAsync(allMethodCalls);
            }
            
            _logger.LogInformation("Analysis complete");
            progress?.Report(new AnalysisProgress("Analysis complete", 100));
            return methodCallInfos;
        }
        catch (Exception ex) when (ex is not InvalidOperationException && ex is not FileNotFoundException)
        {
            _logger.LogError("Failed to analyze solution", ex);
            throw new InvalidOperationException($"Failed to analyze solution: {ex.Message}", ex);
        }
    }

    private (List<MethodCallInfo> methodCallInfos, HashSet<string> classNames, HashSet<string> methodNames, 
        List<(string className, string methodName)> relationships, List<(string caller, string callee)> methodCalls) 
        AnalyzeSyntaxTree(SyntaxNode root, SemanticModel semanticModel, string filePath)
    {
        var methodCallInfos = new List<MethodCallInfo>();
        var classNames = new HashSet<string>();
        var methodNames = new HashSet<string>();
        var relationships = new List<(string className, string methodName)>();
        var methodCalls = new List<(string caller, string callee)>();
        
        var namespaceDeclaration = root.DescendantNodes().OfType<NamespaceDeclarationSyntax>().FirstOrDefault();
        var namespaceName = namespaceDeclaration?.Name.ToString() ?? "Global";
        
        var methodDeclarations = root.DescendantNodes().OfType<MethodDeclarationSyntax>().ToList();
        
        foreach (var methodDecl in methodDeclarations)
        {
            var containingType = methodDecl.Parent as ClassDeclarationSyntax;
            var methodName = methodDecl.Identifier.Text;
            var isStatic = methodDecl.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword));
            
            var methodSymbol = semanticModel.GetDeclaredSymbol(methodDecl);
            var containingTypeSymbol = methodSymbol?.ContainingType;
            var containingTypeName = containingTypeSymbol?.Name ?? containingType?.Identifier.Text ?? "Unknown";
            
            if (containingTypeName != "Unknown")
            {
                classNames.Add(containingTypeName);
                methodNames.Add(methodName);
                relationships.Add((containingTypeName, methodName));
            }
            
            var invocations = methodDecl.DescendantNodes().OfType<InvocationExpressionSyntax>();
            
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
                
                if (invocation.Expression is MemberAccessExpressionSyntax memberAccess)
                {
                    var caller = memberAccess.Expression.ToString();
                    var callee = memberAccess.Name.ToString();
                    
                    var memberSymbol = semanticModel.GetSymbolInfo(memberAccess).Symbol;
                    var calleeTypeSymbol = memberSymbol?.ContainingType;
                    var calleeTypeName = calleeTypeSymbol?.Name ?? caller;
                    
                    callInfo.CalleeType = calleeTypeName;
                    callInfo.CalleeMethod = callee;
                    
                    if (memberSymbol is IMethodSymbol memberMethodSymbol)
                    {
                        callInfo.IsExtensionMethod = memberMethodSymbol.IsExtensionMethod;
                        
                        // Get the fully qualified name of the method being called
                        var calleeMethodSymbol = memberMethodSymbol;
                        var calleeMethodName = calleeMethodSymbol.ToDisplayString();
                        
                        // Get the fully qualified name of the calling method
                        var callerMethodSymbol = semanticModel.GetDeclaredSymbol(methodDecl);
                        var callerMethodName = callerMethodSymbol?.ToDisplayString() ?? $"{containingTypeName}.{methodName}";
                        
                        // Add the fully qualified method names to the method calls list
                        methodCalls.Add((callerMethodName, calleeMethodName));
                    }
                    else
                    {
                        // For non-method symbols, use the class name and method name
                        methodCalls.Add(($"{containingTypeName}.{methodName}", $"{calleeTypeName}.{callee}"));
                    }
                    
                    callInfo.IsPropertyAccessor = memberSymbol?.Kind == SymbolKind.Property;
                    callInfo.IsEventHandler = callee.StartsWith("On") 
                        || callee.EndsWith("Handler") 
                        || callee.EndsWith("Event");
                }
                else
                {
                    var expression = invocation.Expression.ToString();
                    callInfo.CalleeMethod = expression;
                    
                    // For direct invocations, use the class name and method name
                    methodCalls.Add(($"{containingTypeName}.{methodName}", expression));
                }
                
                methodCallInfos.Add(callInfo);
            }
        }
        
        return (methodCallInfos, classNames, methodNames, relationships, methodCalls);
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
                _workspace?.Dispose();
            }
            _disposed = true;
        }
    }
}

public class AnalysisProgress
{
    public string Message { get; }
    public int Percentage { get; }

    public AnalysisProgress(string message, int percentage)
    {
        Message = message;
        Percentage = percentage;
    }
} 