using System.Reflection;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using SentenceStudio.Abstractions;
using SentenceStudio.Data;
using SentenceStudio.Services;
using SentenceStudio.Shared.Models;
using SentenceStudio.UnitTests;

namespace SentenceStudio.UnitTests.Data;

public sealed class FocusVocabularyRepositoryScopingTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _provider;
    private readonly CollectingLoggerProvider _logs;

    public FocusVocabularyRepositoryScopingTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var services = new ServiceCollection();
        services.AddDbContext<ApplicationDbContext>(opt =>
            opt.UseSqlite(_connection)
               .ConfigureWarnings(w =>
                   w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning)));

        var preferences = new Mock<IPreferencesService>();
        preferences.Setup(p => p.Get("active_profile_id", It.IsAny<string>())).Returns(string.Empty);
        var fileSystem = new Mock<IFileSystemService>();
        fileSystem.Setup(f => f.AppDataDirectory).Returns(Directory.GetCurrentDirectory());

        _logs = new CollectingLoggerProvider();
        services.AddLogging(builder =>
        {
            builder.ClearProviders();
            builder.AddProvider(_logs);
            builder.SetMinimumLevel(LogLevel.Debug);
        });
        services.AddSingleton(preferences.Object);
        services.AddSingleton(fileSystem.Object);
        services.AddSingleton<ISyncService>(new NoOpSyncService());
        services.AddScoped<UserProfileRepository>();
        services.AddScoped<LearningResourceRepository>();
        services.AddScoped<SkillProfileRepository>();
        services.AddScoped<VocabularyProgressRepository>();
        services.AddScoped<VocabularyLearningContextRepository>();

        _provider = services.BuildServiceProvider();

        using var bootstrap = _provider.CreateScope();
        var db = bootstrap.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.Database.EnsureCreated();
        SeedOtherUserData(db);
    }

    [Fact]
    public async Task FocusVocabulary_NewRepositoryMethodEmptyUserIdReturnsEmptyAndLogsWarning()
    {
        var invocation = FocusVocabularyRepositoryInvocation.TryFind(_provider);

        if (invocation is null)
        {
            return;
        }

        var result = await invocation.InvokeWithEmptyUserAsync();

        FocusVocabularyContractTestHelpers.IsEmptyContractResult(result).Should().BeTrue(
            "empty user id must mean no data, never an unfiltered focus-vocabulary query");
        _logs.HasWarningContaining("user").Should().BeTrue(
            "empty user id should log a warning so cross-tenant leakage attempts are visible");
    }

    private static void SeedOtherUserData(ApplicationDbContext db)
    {
        const string otherUserId = "other-focus-user";
        var today = DateTime.UtcNow.Date;

        db.UserProfiles.Add(new UserProfile
        {
            Id = otherUserId,
            Name = "Other Focus User",
            NativeLanguage = "English",
            TargetLanguage = "Korean",
            PreferredSessionMinutes = 30,
            CreatedAt = DateTime.UtcNow,
        });

        db.DailyPlans.Add(new DailyPlan
        {
            Id = "other-focus-plan",
            UserProfileId = otherUserId,
            Date = today,
            GeneratedAtUtc = DateTime.UtcNow,
            Strategy = "deterministic",
            RationaleFacts = "{}",
            NarrativeFacts = "{}",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });

        for (var i = 0; i < 5; i++)
        {
            var wordId = $"other-focus-word-{i:00}";
            db.VocabularyWords.Add(new VocabularyWord
            {
                Id = wordId,
                TargetLanguageTerm = $"target-{i}",
                NativeLanguageTerm = $"native-{i}",
                Language = "Korean",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            db.VocabularyProgresses.Add(new VocabularyProgress
            {
                Id = $"other-focus-progress-{i:00}",
                UserId = otherUserId,
                VocabularyWordId = wordId,
                MasteryScore = 0.2f,
                ProductionInStreak = 0,
                CurrentStreak = 0,
                TotalAttempts = 1,
                CorrectAttempts = 1,
                NextReviewDate = today.AddDays(-1),
                ReviewInterval = 1,
                EaseFactor = 2.5f,
                FirstSeenAt = today.AddDays(-7),
                LastPracticedAt = today.AddDays(-1),
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
        }

        db.SaveChanges();
    }

    public void Dispose()
    {
        _provider.Dispose();
        _connection.Close();
        _connection.Dispose();
    }

    private sealed class FocusVocabularyRepositoryInvocation
    {
        private readonly object _instance;
        private readonly MethodInfo _method;
        private readonly object?[] _arguments;

        private FocusVocabularyRepositoryInvocation(object instance, MethodInfo method, object?[] arguments)
        {
            _instance = instance;
            _method = method;
            _arguments = arguments;
        }

        public static FocusVocabularyRepositoryInvocation? TryFind(IServiceProvider provider)
        {
            var repositoryTypes = typeof(ApplicationDbContext).Assembly.GetTypes()
                .Where(type => type is { IsClass: true, IsAbstract: false }
                    && type.Namespace == "SentenceStudio.Data"
                    && type.Name.EndsWith("Repository", StringComparison.Ordinal))
                .OrderBy(type => type.Name, StringComparer.Ordinal)
                .ToList();

            foreach (var repositoryType in repositoryTypes)
            {
                var methods = repositoryType.GetMethods(BindingFlags.Instance | BindingFlags.Public)
                    .Where(method => !method.IsSpecialName
                        && !method.IsGenericMethodDefinition
                        && (method.Name.Contains("FocusVocabulary", StringComparison.OrdinalIgnoreCase)
                            || method.Name.Contains("FocusVocab", StringComparison.OrdinalIgnoreCase))
                        && method.GetParameters().Any(IsUserIdParameter))
                    .OrderBy(method => method.Name, StringComparer.Ordinal)
                    .ToList();

                foreach (var method in methods)
                {
                    if (!TryBuildArguments(method, out var args))
                    {
                        continue;
                    }

                    var instance = provider.GetService(repositoryType)
                        ?? ActivatorUtilities.CreateInstance(provider, repositoryType);
                    return new FocusVocabularyRepositoryInvocation(instance, method, args);
                }
            }

            return null;
        }

        public async Task<object?> InvokeWithEmptyUserAsync()
        {
            object? raw;
            try
            {
                raw = _method.Invoke(_instance, _arguments);
            }
            catch (TargetInvocationException ex) when (ex.InnerException is not null)
            {
                throw ex.InnerException;
            }

            if (raw is not Task task)
            {
                return raw;
            }

            await task.ConfigureAwait(false);
            var resultProperty = task.GetType().GetProperty("Result", BindingFlags.Instance | BindingFlags.Public);
            return resultProperty?.GetValue(task);
        }

        private static bool TryBuildArguments(MethodInfo method, out object?[] arguments)
        {
            var parameters = method.GetParameters();
            arguments = new object?[parameters.Length];

            for (var i = 0; i < parameters.Length; i++)
            {
                if (TryBuildArgument(parameters[i], out var value))
                {
                    arguments[i] = value;
                    continue;
                }

                if (parameters[i].HasDefaultValue)
                {
                    arguments[i] = parameters[i].DefaultValue;
                    continue;
                }

                return false;
            }

            return true;
        }

        private static bool TryBuildArgument(ParameterInfo parameter, out object? value)
        {
            var type = parameter.ParameterType;
            var name = parameter.Name ?? string.Empty;

            if (IsUserIdParameter(parameter))
            {
                value = string.Empty;
                return true;
            }

            if (type == typeof(string))
            {
                value = name.Contains("plan", StringComparison.OrdinalIgnoreCase)
                    ? "missing-focus-plan"
                    : "missing-focus-word";
                return true;
            }

            if (type == typeof(DateTime))
            {
                value = DateTime.UtcNow.Date;
                return true;
            }

            if (type == typeof(DateOnly))
            {
                value = DateOnly.FromDateTime(DateTime.UtcNow);
                return true;
            }

            if (type == typeof(CancellationToken))
            {
                value = CancellationToken.None;
                return true;
            }

            if (type == typeof(int))
            {
                value = 20;
                return true;
            }

            if (type == typeof(bool))
            {
                value = false;
                return true;
            }

            if (type == typeof(List<string>)
                || type == typeof(IReadOnlyList<string>)
                || type == typeof(IEnumerable<string>))
            {
                value = new List<string> { "missing-focus-word-01", "missing-focus-word-02" };
                return true;
            }

            if (type == typeof(string[]))
            {
                value = new[] { "missing-focus-word-01", "missing-focus-word-02" };
                return true;
            }

            value = null;
            return false;
        }

        private static bool IsUserIdParameter(ParameterInfo parameter)
        {
            if (parameter.ParameterType != typeof(string))
            {
                return false;
            }

            var name = parameter.Name ?? string.Empty;
            return name.Contains("user", StringComparison.OrdinalIgnoreCase)
                || name.Contains("profile", StringComparison.OrdinalIgnoreCase);
        }
    }
}
