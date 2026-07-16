using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using SentenceStudio.Abstractions;
using SentenceStudio.Data;
using SentenceStudio.Services;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.UnitTests.Services;

public sealed class VocabQuizLaunchValidatorTests : IDisposable
{
    private const string UserA = "quiz-route-user-a";
    private const string UserB = "quiz-route-user-b";

    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _provider;
    private readonly LearningResource _resourceA;
    private readonly LearningResource _resourceB;
    private readonly SkillProfile _skillA;
    private readonly SkillProfile _skillB;

    public VocabQuizLaunchValidatorTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var services = new ServiceCollection();
        services.AddDbContext<ApplicationDbContext>(options =>
            options.UseSqlite(_connection)
                .ConfigureWarnings(warnings =>
                    warnings.Ignore(RelationalEventId.PendingModelChangesWarning)));
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Debug));

        var preferences = new Mock<IPreferencesService>();
        preferences.Setup(service => service.Get("active_profile_id", It.IsAny<string>()))
            .Returns(UserA);
        var fileSystem = new Mock<IFileSystemService>();
        fileSystem.Setup(service => service.AppDataDirectory)
            .Returns(Directory.GetCurrentDirectory());

        services.AddSingleton(preferences.Object);
        services.AddSingleton(fileSystem.Object);
        services.AddSingleton<LearningResourceRepository>();
        services.AddSingleton<SkillProfileRepository>();
        services.AddSingleton<VocabQuizLaunchValidator>();

        _provider = services.BuildServiceProvider();

        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.Database.EnsureCreated();

        db.UserProfiles.AddRange(CreateUser(UserA), CreateUser(UserB));
        _resourceA = CreateResource(UserA);
        _resourceB = CreateResource(UserB);
        _skillA = CreateSkill(UserA);
        _skillB = CreateSkill(UserB);
        db.LearningResources.AddRange(_resourceA, _resourceB);
        db.SkillProfiles.AddRange(_skillA, _skillB);
        db.SaveChanges();
    }

    [Fact]
    public async Task OwnedRoute_IsAccepted()
    {
        var result = await Validator.ValidateRouteAsync(
            UserA,
            [_resourceA.Id],
            _skillA.Id);

        result.IsValid.Should().BeTrue();
        result.UserId.Should().Be(UserA);
        result.Resources.Select(resource => resource.Id).Should().Equal(_resourceA.Id);
        result.Skill?.Id.Should().Be(_skillA.Id);
        result.RejectedCount.Should().Be(0);
    }

    [Fact]
    public async Task ForeignResourceRoute_IsRefused()
    {
        var result = await Validator.ValidateRouteAsync(
            UserA,
            [_resourceB.Id],
            _skillA.Id);

        result.IsValid.Should().BeFalse();
        result.RejectedCount.Should().Be(1);
    }

    [Fact]
    public async Task MixedResourceRoute_IsRefusedAllOrNothing()
    {
        var result = await Validator.ValidateRouteAsync(
            UserA,
            [_resourceA.Id, _resourceB.Id],
            _skillA.Id);

        result.IsValid.Should().BeFalse();
        result.Resources.Select(resource => resource.Id).Should().Equal(_resourceA.Id);
        result.RejectedCount.Should().Be(1);
    }

    [Fact]
    public async Task ForeignSkillRoute_IsRefused()
    {
        var result = await Validator.ValidateRouteAsync(
            UserA,
            [_resourceA.Id],
            _skillB.Id);

        result.IsValid.Should().BeFalse();
        result.RejectedCount.Should().Be(1);
    }

    [Fact]
    public async Task EmptyUserRoute_IsRefused()
    {
        var result = await Validator.ValidateRouteAsync(
            string.Empty,
            [_resourceA.Id],
            _skillA.Id);

        result.IsValid.Should().BeFalse();
        result.UserId.Should().BeEmpty();
        result.Resources.Should().BeEmpty();
        result.Skill.Should().BeNull();
    }

    private VocabQuizLaunchValidator Validator =>
        _provider.GetRequiredService<VocabQuizLaunchValidator>();

    private static UserProfile CreateUser(string id) => new()
    {
        Id = id,
        Name = id,
        NativeLanguage = "English",
        TargetLanguage = "Korean",
        PreferredSessionMinutes = 20,
        CreatedAt = DateTime.UtcNow,
    };

    private static LearningResource CreateResource(string userId) => new()
    {
        Title = $"Resource {Guid.NewGuid()}",
        MediaType = "Vocabulary List",
        Language = "Korean",
        UserProfileId = userId,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    private static SkillProfile CreateSkill(string userId) => new()
    {
        Title = $"Skill {Guid.NewGuid()}",
        UserProfileId = userId,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    public void Dispose()
    {
        _provider.Dispose();
        _connection.Dispose();
    }
}
