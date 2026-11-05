#pragma warning disable EF1001 // EF internal API; test-only guard.
using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.Internal;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace SentenceStudio.Native.Tests;

/// <summary>
/// Fails any EF query that would make EF evaluate a virtual or interface member on the client.
/// EF evaluates such captured sub-expressions through the LINQ expression interpreter, which
/// calls the member through an open virtual delegate, and CoreCLR's iOS interpreter (device, and
/// Release simulator builds) crashes on that (<c>CID_VirtualOpenDelegateDispatch</c>; DX24,
/// 2026-11-05: <c>db.Database.IsSqlite()</c> in resource detail, and
/// <c>IReadOnlyList.Count</c> in the quiz commit). Read such values into locals before the query.
/// See docs/native-shared-foundation.md.
/// <para>A finding throws, and is also added to the <see cref="QueryGuardFindings"/> registered
/// in the test's services, so a caller that catches the exception and degrades still fails the
/// test (<see cref="NativeSliceFixture"/> checks the list when it is disposed). The check is
/// conservative: it also flags a bare member chain that EF would read by reflection.</para>
/// </summary>
internal sealed class DeviceInterpreterQueryGuard(
    IQueryContextFactory queryContextFactory,
    ICompiledQueryCache compiledQueryCache,
    ICompiledQueryCacheKeyGenerator compiledQueryCacheKeyGenerator,
    IDatabase database,
    IDiagnosticsLogger<DbLoggerCategory.Query> logger,
    ICurrentDbContext currentContext,
    IEvaluatableExpressionFilter evaluatableExpressionFilter,
    IModel model)
    : QueryCompiler(queryContextFactory, compiledQueryCache, compiledQueryCacheKeyGenerator,
        database, logger, currentContext, evaluatableExpressionFilter, model)
{
    public override Expression ExtractParameters(
        Expression query,
        Dictionary<string, object?> parameters,
        IDiagnosticsLogger<DbLoggerCategory.Query> logger,
        bool compiledQuery = false,
        bool generateContextAccessors = false)
    {
        if (ClientDispatchFinder.Find(query) is { } finding)
        {
            var caller = Environment.StackTrace.Split('\n')
                .FirstOrDefault(line => line.Contains("SentenceStudio.")
                    && !line.Contains("DeviceInterpreterQueryGuard."))?.Trim();
            var record = $"{finding} ({caller})";
            // App code may catch the exception and degrade (an empty card, a null result).
            var findings = currentContext.Context.GetService<IDbContextOptions>()
                .FindExtension<CoreOptionsExtension>()?.ApplicationServiceProvider
                ?.GetService<QueryGuardFindings>();
            findings?.Add(record);
            if (Environment.GetEnvironmentVariable("SS_QUERY_GUARD_LOG") is { Length: > 0 } log)
                lock (LogGate)
                    File.AppendAllText(log, record + Environment.NewLine);
            throw new InvalidOperationException(
                "Query evaluates a virtual or interface member on the client, which can crash "
                + "CoreCLR's iOS interpreter (see docs/native-shared-foundation.md). Read it into "
                + $"a local before the query: {finding}");
        }
        return base.ExtractParameters(query, parameters, logger, compiledQuery, generateContextAccessors);
    }

    private static readonly object LogGate = new();

    /// <summary>
    /// Registers the guard on every <see cref="SentenceStudio.Data.ApplicationDbContext"/> the
    /// services create, and the <see cref="QueryGuardFindings"/> it reports to.
    /// </summary>
    public static void Register(IServiceCollection services, QueryGuardFindings? findings = null)
    {
        services.AddSingleton(findings ?? new QueryGuardFindings());
        services.ConfigureDbContext<SentenceStudio.Data.ApplicationDbContext>(options =>
            options.ReplaceService<IQueryCompiler, DeviceInterpreterQueryGuard>());
    }

    /// <summary>Guards a context a test builds itself. Findings are reported by the throw only.</summary>
    public static DbContextOptionsBuilder<TContext> Guard<TContext>(
        DbContextOptionsBuilder<TContext> options) where TContext : DbContext
    {
        options.ReplaceService<IQueryCompiler, DeviceInterpreterQueryGuard>();
        return options;
    }

    /// <summary>The first finding in <paramref name="query"/>, or null.</summary>
    internal static string? FindClientDispatch(Expression query) => ClientDispatchFinder.Find(query);

    private sealed class ClientDispatchFinder : ExpressionVisitor
    {
        private string? _finding;

        public static string? Find(Expression query)
        {
            var finder = new ClientDispatchFinder();
            finder.Visit(query);
            return finder._finding;
        }

        protected override Expression VisitMember(MemberExpression node)
        {
            if (_finding is null && node.Expression is { } instance && !ParameterFinder.Contains(instance)
                && node.Member is PropertyInfo { GetMethod: { } getter } property && Dispatches(getter))
                _finding = $"{property.DeclaringType?.FullName}.{property.Name} on `{instance}`";
            return base.VisitMember(node);
        }

        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            // ids.Contains(x.Id) uses the row, so EF translates it to SQL IN rather than running
            // it on the client. db.Set<T>() inside a query is exempt on device evidence: DX24 ran
            // VocabularyProgressRepository.GetVocabSummaryCountsAsync's nested db.Set<T>()
            // subqueries under the interpreter on 2026-11-05 without a crash.
            if (_finding is null && node.Object is { } instance && !ParameterFinder.Contains(instance)
                && !(node.Method.Name == nameof(ICollection<int>.Contains)
                    && node.Arguments.Any(ParameterFinder.Contains))
                && Dispatches(node.Method)
                && !(node.Method.Name == nameof(DbContext.Set)
                    && typeof(DbContext).IsAssignableFrom(node.Method.DeclaringType)))
                _finding = $"{node.Method.DeclaringType?.FullName}.{node.Method.Name}() on `{instance}`";
            return base.VisitMethodCall(node);
        }

        // Any virtual method, including final ones (List<T>.Count implements an interface) and
        // members of sealed types: nothing shows the interpreter binds those without virtual
        // dispatch, and no current query needs them (checked 2026-11-05).
        private static bool Dispatches(MethodInfo method) =>
            method.DeclaringType is { IsInterface: true } || method.IsVirtual;
    }

    private sealed class ParameterFinder : ExpressionVisitor
    {
        private bool _found;

        public static bool Contains(Expression expression)
        {
            var finder = new ParameterFinder();
            finder.Visit(expression);
            return finder._found;
        }

        protected override Expression VisitParameter(ParameterExpression node)
        {
            _found = true;
            return node;
        }
    }
}

/// <summary>Query-guard findings for one test's services; see <see cref="DeviceInterpreterQueryGuard"/>.</summary>
internal sealed class QueryGuardFindings
{
    private readonly ConcurrentQueue<string> _items = new();

    public void Add(string finding) => _items.Enqueue(finding);

    /// <summary>Returns the findings so far and clears them.</summary>
    public IReadOnlyList<string> Drain()
    {
        var drained = new List<string>();
        while (_items.TryDequeue(out var item))
            drained.Add(item);
        return drained;
    }
}
