namespace CodeBetter.Ingestor;

using Neo4j.Driver;
using System;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;

public interface IRepository
{
    Task AddClassNodeAsync(string className);
    Task AddMethodNodeAsync(string methodName, string className);
    Task AddEdgeAsync(string className, string methodName);
    Task WriteRelationshipAsync(string caller, string callee);
    Task WriteNodeAsync(string expression);
    Task ClearDatabaseAsync();
    Task AddNodesBatchAsync(IEnumerable<string> classNames, IEnumerable<string> methodNames);
    Task AddRelationshipsBatchAsync(IEnumerable<(string className, string methodName)> relationships);
    Task WriteRelationshipsBatchAsync(IEnumerable<(string caller, string callee)> relationships);
}

public class Neo4jRepository: IRepository, IDisposable
{
    private readonly IDriver _driver;
    private bool _disposed;
    private static readonly HashSet<string> ExcludedNamespaces = new()
    {
        "System",
        "Microsoft",
        "mscorlib",
        "netstandard"
    };

    public Neo4jRepository(string uri, string user, string password)
    {
        _driver = GraphDatabase.Driver(uri, AuthTokens.Basic(user, password));
    }

    public async Task AddClassNodeAsync(string className)
    {
        try
        {
            var query = "MERGE (c:Class {name: $className})";
            await using var session = _driver.AsyncSession();
            await session.RunAsync(query, new { className });
        }
        catch (Exception ex)
        {
            throw new RepositoryException($"Failed to add class node: {className}", ex);
        }
    }

    public async Task AddMethodNodeAsync(string methodName, string className)
    {
        try
        {
            var query = "MERGE (m:Method {name: $methodName, className: $className})";
            await using var session = _driver.AsyncSession();
            await session.RunAsync(query, new { methodName, className });
        }
        catch (Exception ex)
        {
            throw new RepositoryException($"Failed to add method node: {methodName}", ex);
        }
    }

    public async Task AddEdgeAsync(string className, string methodName)
    {
        try
        {
            var query = @"
                MATCH (c:Class {name: $className})
                MATCH (m:Method {name: $methodName})
                MERGE (c)-[:CONTAINS]->(m)";
            await using var session = _driver.AsyncSession();
            await session.RunAsync(query, new { className, methodName });
        }
        catch (Exception ex)
        {
            throw new RepositoryException($"Failed to add edge between class {className} and method {methodName}", ex);
        }
    }

    public async Task WriteRelationshipAsync(string caller, string callee)
    {
        try
        {
            var query = @"MERGE (caller:Method {name: $caller})
                          MERGE (callee:Method {name: $callee})
                          MERGE (caller)-[:CALLS]->(callee)";
            await using var session = _driver.AsyncSession();
            await session.RunAsync(query, new { caller, callee });
        }
        catch (Exception ex)
        {
            throw new RepositoryException($"Failed to write relationship between {caller} and {callee}", ex);
        }
    }

    public async Task WriteNodeAsync(string expression)
    {
        try
        {
            var query = "MERGE (n:Method {name: $expression})";
            await using var session = _driver.AsyncSession();
            await session.RunAsync(query, new { expression });
        }
        catch (Exception ex)
        {
            throw new RepositoryException($"Failed to write node: {expression}", ex);
        }
    }

    public async Task ClearDatabaseAsync()
    {
        try
        {
            var query = "MATCH (n) DETACH DELETE n";
            await using var session = _driver.AsyncSession();
            await session.RunAsync(query);
        }
        catch (Exception ex)
        {
            throw new RepositoryException("Failed to clear database", ex);
        }
    }

    private bool ShouldExcludeNode(string name)
    {
        return ExcludedNamespaces.Any(ns => name.StartsWith(ns, StringComparison.OrdinalIgnoreCase));
    }

    public async Task AddNodesBatchAsync(IEnumerable<string> classNames, IEnumerable<string> methodNames)
    {
        try
        {
            var filteredClassNames = classNames.Where(c => !ShouldExcludeNode(c)).ToList();
            var filteredMethodNames = methodNames.Where(m => !ShouldExcludeNode(m)).ToList();

            var classQuery = "UNWIND $classNames as className MERGE (c:Class {name: className})";
            var methodQuery = "UNWIND $methodData as data MERGE (m:Method {name: data.methodName, className: data.className})";

            var methodData = filteredMethodNames
                .Select(m => new Dictionary<string, string>
                {
                    ["methodName"] = m,
                    ["className"] = m.Split('.')[0]
                })
                .ToList();

            await using var session = _driver.AsyncSession();
            await session.RunAsync(classQuery, new { classNames = filteredClassNames });
            await session.RunAsync(methodQuery, new { methodData });
        }
        catch (Exception ex)
        {
            throw new RepositoryException("Failed to add nodes in batch", ex);
        }
    }

    public async Task AddRelationshipsBatchAsync(IEnumerable<(string className, string methodName)> relationships)
    {
        try
        {
            var filteredRelationships = relationships
                .Where(r => !ShouldExcludeNode(r.className) && !ShouldExcludeNode(r.methodName))
                .Select(r => new Dictionary<string, string>
                {
                    ["className"] = r.className,
                    ["methodName"] = r.methodName
                })
                .ToList();

            var query = @"
                UNWIND $relationships as rel
                MATCH (c:Class {name: rel.className})
                MATCH (m:Method {name: rel.methodName})
                MERGE (c)-[:CONTAINS]->(m)";

            await using var session = _driver.AsyncSession();
            await session.RunAsync(query, new { relationships = filteredRelationships });
        }
        catch (Exception ex)
        {
            throw new RepositoryException("Failed to add relationships in batch", ex);
        }
    }

    public async Task WriteRelationshipsBatchAsync(IEnumerable<(string caller, string callee)> relationships)
    {
        try
        {
            var filteredRelationships = relationships
                .Where(r => !ShouldExcludeNode(r.caller) && !ShouldExcludeNode(r.callee))
                .Select(r => new Dictionary<string, string> 
                {
                    ["caller"] = r.caller,
                    ["callee"] = r.callee
                })
                .ToList();

            var query = @"
                UNWIND $relationships as rel
                MERGE (caller:Method {name: rel.caller})
                MERGE (callee:Method {name: rel.callee})
                MERGE (caller)-[:CALLS]->(callee)";

            await using var session = _driver.AsyncSession();
            await session.RunAsync(query, new { relationships = filteredRelationships });
        }
        catch (Exception ex)
        {
            throw new RepositoryException("Failed to write relationships in batch", ex);
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
                _driver?.Dispose();
            }
            _disposed = true;
        }
    }
}

public class RepositoryException : Exception
{
    public RepositoryException(string message) : base(message)
    {
    }

    public RepositoryException(string message, Exception innerException) : base(message, innerException)
    {
    }
}