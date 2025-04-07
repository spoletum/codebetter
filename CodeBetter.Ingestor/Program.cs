using DotMake.CommandLine;
using CodeBetter.Ingestor;
using System;
using System.IO;
using System.Threading.Tasks;
using System.Collections.Generic;

[CliCommand(Description = "Code Analyzer")]
public class Program
{
    private static readonly ILoggingService Logger = new LoggingService("logs/codebetter.log");

    public static async Task Main(string[] args) {
        if (args.Length == 0)
        {
            Logger.LogError("Please provide a path to the solution or project file to analyze.");
            Console.WriteLine("Usage: dotnet run -- <solution-path>");
            Environment.Exit(1);
            return;
        }

        try
        {
            var fullPath = Path.GetFullPath(args[0]);
            if (!File.Exists(fullPath))
            {
                Logger.LogError($"File not found at path: {fullPath}");
                Environment.Exit(1);
                return;
            }

            Logger.LogInformation($"Starting analysis of: {fullPath}");
            
            using var repository = new Neo4jRepository("neo4j://localhost:7687", "neo4j", "ale.san.01");
            
            // Clear existing data
            Logger.LogInformation("Clearing existing database data");
            await repository.ClearDatabaseAsync();
            
            // Determine if we're analyzing a solution or a single file
            if (fullPath.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
            {
                Logger.LogInformation("Analyzing solution file");
                using var solutionAnalyzer = new SolutionAnalyzer(repository, Logger);
                var progress = new Progress<AnalysisProgress>(p => 
                {
                    Console.Write($"\r{p.Message} - {p.Percentage}%");
                });
                
                var methodCalls = await solutionAnalyzer.AnalyzeSolutionAsync(fullPath, progress);
                Console.WriteLine(); // New line after progress
                DisplayResults(methodCalls);
            }
            else if (fullPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                Logger.LogInformation("Analyzing single C# file");
                using var fileAnalyzer = new CodeBetter.Ingestor.DependencyAnalyzer(repository, Logger);
                var methodCalls = await fileAnalyzer.AnalyzeDependenciesAsync(fullPath);
                DisplayResults(methodCalls);
            }
            else
            {
                Logger.LogError($"Unsupported file type: {Path.GetExtension(fullPath)}");
                Console.WriteLine("Only .sln and .cs files are supported.");
                Environment.Exit(1);
                return;
            }
            
            Logger.LogInformation("Analysis completed successfully");
        }
        catch (Exception ex)
        {
            Logger.LogError("Error analyzing code", ex);
            if (ex.InnerException != null)
            {
                Logger.LogError("Inner error", ex.InnerException);
            }
            Environment.Exit(1);
        }
    }
    
    private static void DisplayResults(List<MethodCallInfo> methodCalls)
    {
        Logger.LogInformation($"Analyzed {methodCalls.Count} method calls");
        Console.WriteLine($"\nAnalyzed {methodCalls.Count} method calls:");
        foreach (var call in methodCalls)
        {
            Console.WriteLine($"\n- {call.CallerType}.{call.CallerMethod} -> {call.CalleeType}.{call.CalleeMethod} (Line: {call.LineNumber})");
            if (call.IsStatic) Console.WriteLine("  [Static]");
            if (call.IsExtensionMethod) Console.WriteLine("  [Extension Method]");
            if (call.IsPropertyAccessor) Console.WriteLine("  [Property Accessor]");
            if (call.IsEventHandler) Console.WriteLine("  [Event Handler]");
        }
    }
}