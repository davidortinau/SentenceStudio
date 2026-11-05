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
    /// services create. Findings also go to <paramref name="findings"/>, which the caller must
    /// check when the test ends (a caught exception would otherwise hide them).
    /// </summary>
    public static void Register(IServiceCollection services, QueryGuardFindings findings)
    {
        services.AddSingleton(findings);
        services.ConfigureDbContext<SentenceStudio.Data.ApplicationDbContext>(options =>
            options.ReplaceService<IQueryCompiler, DeviceInterpreterQueryGuard>());
    }

    /// <summary>
    /// Guards a context a test builds itself. Such a context has no application services, so a
    /// finding is reported by the throw only; app code that catches it hides it.
    /// </summary>
    public static DbContextOptionsBuilder<TContext> Guard<TContext>(
        DbContextOptionsBuilder<TContext> options) where TContext : DbContext
    {
        options.ReplaceService<IQueryCompiler, DeviceInterpreterQueryGuard>();
        return options;
    }

    /// <summary>The first finding in <paramref name="query"/>, or null.</summary>
    internal static string? FindClientDispatch(Expression query) => ClientDispatchFinder.Find(query);

    /// <summary>
    /// Mirrors EF's funcletizer: a subtree that uses no outer lambda parameter and no query
    /// source is evaluated on the client, through the LINQ interpreter, so every virtual or
    /// interface call inside it is flagged, including calls inside its own lambdas
    /// (<c>lists.Any(l =&gt; l.Count &gt; 0)</c>). Outside those subtrees a call on a captured
    /// instance is flagged unless EF translates it with the row (<c>ids.Contains(x.Id)</c>).
    /// </summary>
    private sealed class ClientDispatchFinder : ExpressionVisitor
    {
        // One-argument calls EF translates when the argument uses the row, so they never run
        // on the client (ids.Contains(x.Id) becomes SQL IN). comparer.Equals(a, b) does not.
        private static readonly HashSet<string> TranslatedWithRow =
            [nameof(ICollection<int>.Contains), nameof(Equals), nameof(IComparable.CompareTo)];

        private readonly ExpressionFacts _facts;
        private string? _finding;

        private ClientDispatchFinder(ExpressionFacts facts) => _facts = facts;

        public static string? Find(Expression query)
        {
            var finder = new ClientDispatchFinder(ExpressionFacts.Of(query));
            finder.Visit(query);
            return finder._finding;
        }

        public override Expression? Visit(Expression? node)
        {
            if (node is null || _finding is not null)
                return node;
            if (_facts.IsClientEvaluated(node))
            {
                _finding = ClientEvaluatedScan.Find(node);
                return node;
            }
            return base.Visit(node);
        }

        protected override Expression VisitMember(MemberExpression node)
        {
            if (_finding is null && node.Expression is { } instance
                && node.Member is PropertyInfo { GetMethod: { } getter } property && Dispatches(getter)
                && !_facts.UsesOuterParameter(instance))
                _finding = $"{property.DeclaringType?.FullName}.{property.Name} on `{instance}`";
            return base.VisitMember(node);
        }

        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            if (_finding is null && node.Object is { } instance && Dispatches(node.Method)
                && !IsQuerySource(node)
                && !(node.Arguments.Count == 1 && TranslatedWithRow.Contains(node.Method.Name)
                    && _facts.UsesOuterParameter(node.Arguments[0]))
                && !_facts.UsesOuterParameter(instance))
                _finding = $"{node.Method.DeclaringType?.FullName}.{node.Method.Name}() on `{instance}`";
            return base.VisitMethodCall(node);
        }

        /// <summary>Everything in a client-evaluated subtree runs in the interpreter.</summary>
        private sealed class ClientEvaluatedScan : ExpressionVisitor
        {
            private string? _finding;

            public static string? Find(Expression node)
            {
                var scan = new ClientEvaluatedScan();
                scan.Visit(node);
                return scan._finding;
            }

            public override Expression? Visit(Expression? node) =>
                _finding is null ? base.Visit(node) : node;

            protected override Expression VisitMember(MemberExpression node)
            {
                if (node.Member is PropertyInfo { GetMethod: { } getter } property && Dispatches(getter))
                    _finding ??= $"{property.DeclaringType?.FullName}.{property.Name} on `{node.Expression}`";
                return base.VisitMember(node);
            }

            protected override Expression VisitMethodCall(MethodCallExpression node)
            {
                if (node.Object is not null && Dispatches(node.Method))
                    _finding ??= $"{node.Method.DeclaringType?.FullName}.{node.Method.Name}() on `{node.Object}`";
                return base.VisitMethodCall(node);
            }

            // A captured delegate's Invoke is virtual; an inline lambda is not a delegate call.
            protected override Expression VisitInvocation(InvocationExpression node)
            {
                if (node.Expression is not LambdaExpression)
                    _finding ??= $"delegate invocation of `{node.Expression}`";
                return base.VisitInvocation(node);
            }

            // A collection initializer (new HashSet<string> { a, b }) calls a virtual Add.
            protected override ElementInit VisitElementInit(ElementInit node)
            {
                if (Dispatches(node.AddMethod))
                    _finding ??= $"{node.AddMethod.DeclaringType?.FullName}.{node.AddMethod.Name}() in a collection initializer";
                return base.VisitElementInit(node);
            }
        }

        // db.Set<T>() is a query source, never evaluated as a value. Nested db.Set<T>()
        // subqueries also ran on DX24 under the interpreter on 2026-11-05 without a crash
        // (VocabularyProgressRepository.GetVocabSummaryCountsAsync).
        private static bool IsQuerySource(MethodCallExpression node) =>
            node.Method.Name == nameof(DbContext.Set)
            && typeof(DbContext).IsAssignableFrom(node.Method.DeclaringType);

        // Any virtual method, including final ones (List<T>.Count implements an interface) and
        // members of sealed types: nothing shows the interpreter binds those without virtual
        // dispatch, and no current query needs them (checked 2026-11-05).
        private static bool Dispatches(MethodInfo method) =>
            method.DeclaringType is { IsInterface: true } || method.IsVirtual;
    }

    /// <summary>
    /// One bottom-up pass over the query: for each node, whether it uses a lambda parameter
    /// bound outside it, and whether it contains a query source (an <see cref="IQueryable"/>).
    /// </summary>
    private sealed class ExpressionFacts : ExpressionVisitor
    {
        private readonly Dictionary<Expression, bool> _usesOuter = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<Expression, bool> _hasQuery = new(ReferenceEqualityComparer.Instance);
        private readonly Stack<HashSet<ParameterExpression>> _frames = new();
        private readonly Stack<bool> _queryFrames = new();

        public static ExpressionFacts Of(Expression query)
        {
            var facts = new ExpressionFacts();
            facts._frames.Push([]);
            facts._queryFrames.Push(false);
            facts.Visit(query);
            return facts;
        }

        public bool UsesOuterParameter(Expression node) => _usesOuter.GetValueOrDefault(node);

        /// <summary>EF evaluates this subtree on the client (EF's funcletizer rule).</summary>
        public bool IsClientEvaluated(Expression node) =>
            node.NodeType is not (ExpressionType.Constant or ExpressionType.Parameter
                or ExpressionType.Lambda or ExpressionType.Quote)
            && !_usesOuter.GetValueOrDefault(node)
            && !_hasQuery.GetValueOrDefault(node);

        public override Expression? Visit(Expression? node)
        {
            if (node is null)
                return node;
            _frames.Push([]);
            _queryFrames.Push(false);
            base.Visit(node);
            var free = _frames.Pop();
            var hasQuery = _queryFrames.Pop() || typeof(IQueryable).IsAssignableFrom(node.Type);
            switch (node)
            {
                case LambdaExpression lambda:
                    free.ExceptWith(lambda.Parameters);
                    break;
                case BlockExpression block:
                    free.ExceptWith(block.Variables);
                    break;
                case ParameterExpression parameter:
                    free.Add(parameter);
                    break;
            }
            _usesOuter[node] = free.Count > 0;
            _hasQuery[node] = hasQuery;
            _frames.Peek().UnionWith(free);
            if (hasQuery)
            {
                _queryFrames.Pop();
                _queryFrames.Push(true);
            }
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
