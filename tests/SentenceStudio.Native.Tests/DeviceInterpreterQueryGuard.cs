#pragma warning disable EF1001 // EF internal API; test-only guard.
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
            // App code may catch the exception and degrade (an empty card, a null result), so a
            // finding is also written where a run can collect it: SS_QUERY_GUARD_LOG=<file>.
            if (Environment.GetEnvironmentVariable("SS_QUERY_GUARD_LOG") is { Length: > 0 } log)
                lock (LogGate)
                    File.AppendAllText(log, finding + Environment.NewLine + Environment.StackTrace
                        .Split('\n').FirstOrDefault(line => line.Contains("SentenceStudio.")
                            && !line.Contains("DeviceInterpreterQueryGuard")) + Environment.NewLine);
            throw new InvalidOperationException(
                "Query evaluates a virtual or interface member on the client, which crashes "
                + "CoreCLR's iOS interpreter (see docs/native-shared-foundation.md). Read it into "
                + $"a local before the query: {finding}");
        }
        return base.ExtractParameters(query, parameters, logger, compiledQuery, generateContextAccessors);
    }

    private static readonly object LogGate = new();

    /// <summary>Registers the guard on every <see cref="SentenceStudio.Data.ApplicationDbContext"/>.</summary>
    public static void Register(IServiceCollection services) =>
        services.ConfigureDbContext<SentenceStudio.Data.ApplicationDbContext>(options =>
            options.ReplaceService<IQueryCompiler, DeviceInterpreterQueryGuard>());

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
            // DbContext.Set<T>() is a query root: EF recognises it rather than evaluating it.
            if (_finding is null && node.Object is { } instance && !ParameterFinder.Contains(instance)
                && Dispatches(node.Method)
                && !(node.Method.Name == nameof(DbContext.Set)
                    && typeof(DbContext).IsAssignableFrom(node.Method.DeclaringType)))
                _finding = $"{node.Method.DeclaringType?.FullName}.{node.Method.Name}() on `{instance}`";
            return base.VisitMethodCall(node);
        }

        private static bool Dispatches(MethodInfo method) =>
            method.DeclaringType is { IsInterface: true }
            || method.IsVirtual && !method.IsFinal && method.DeclaringType is { IsSealed: false };
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
