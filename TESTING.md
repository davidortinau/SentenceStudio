# Testing Infrastructure

This document describes the unit testing setup for SentenceStudio and the architectural discoveries made during implementation.

## ✅ Microsoft Approach Works!

The official Microsoft .NET MAUI unit testing approach (adding `net10.0` target framework) **DOES work** as demonstrated. The main SentenceStudio project now builds for `net10.0` with conditional compilation.

## Current Setup

### Test Projects

- **SentenceStudio.UnitTests** - Unit tests for models and business logic
- **SentenceStudio.IntegrationTests** - Integration tests for database and repositories (configured but no tests yet)

### Testing Framework

- **xUnit 2.9.2** - Test framework (Microsoft recommended for .NET MAUI)
- **FluentAssertions 6.12.0** - Fluent assertion library for readable tests
- **Moq 4.20.70** - Mocking framework for isolating dependencies
- **coverlet.collector 6.0.2** - Code coverage collection

### What's Currently Testable

The unit tests currently reference **only** the `SentenceStudio.Shared` project, which contains:

✅ **Models** - All domain models with computed properties
✅ **Database Context** - ApplicationDbContext and entity configurations
✅ **Migrations** - Database migration files

### Example Tests

See `tests/SentenceStudio.UnitTests/Models/VocabularyProgressTests.cs` for comprehensive examples:

- 27 tests covering the VocabularyProgress model
- Tests for computed properties (Status, Accuracy, IsDueForReview, etc.)
- Theory-based tests with multiple input scenarios
- Default value validation

### Limitations

⚠️ **Services and Repositories are NOT currently testable** because they live in the main `SentenceStudio` project, which:

1. Has dependencies on MAUI-specific UI packages
2. Cannot be built for plain `net10.0` target framework
3. Requires platform-specific targets (net10.0-android, net10.0-ios, etc.)

### To Make Services/Repositories Testable

To test `VocabularyProgressService`, `VocabularyProgressRepository`, and other business logic classes, you'll need to:

1. **Option A: Extract to Core Library** (Recommended)
   - Create `SentenceStudio.Core` class library targeting net10.0
   - Move services, repositories, and interfaces to Core
   - Reference Core from both main app and test projects

2. **Option B: Move to Shared Project**
   - Move services and repositories to `SentenceStudio.Shared`
   - Ensure no MAUI-specific dependencies are used
   - Simpler but less organized than Option A

## Running Tests

### Command Line

```bash
# Run all tests
dotnet test tests/SentenceStudio.UnitTests/SentenceStudio.UnitTests.csproj

# Run with detailed output
dotnet test tests/SentenceStudio.UnitTests/SentenceStudio.UnitTests.csproj --verbosity normal

# Run with code coverage
dotnet test tests/SentenceStudio.UnitTests/SentenceStudio.UnitTests.csproj --collect:"XPlat Code Coverage"
```

### Visual Studio / Rider

Tests should appear in the test explorer and can be run individually or as a group.

## CI/CD

GitHub Actions workflow is configured at `.github/workflows/test.yml`:

- Runs on every push to `main` and all pull requests
- Uses macOS runner (required for .NET MAUI workloads)
- Uploads test results as artifacts
- Generates test report using dorny/test-reporter

## Future Improvements

1. **Extract Business Logic** - Create SentenceStudio.Core library for services
2. **Integration Tests** - Add tests for repository operations with in-memory database
3. **Code Coverage** - Set up Codecov.io integration
4. **UI Tests** - Consider Appium or .NET MAUI UI testing (device/simulator required)
5. **Performance Tests** - Add benchmarks for critical operations

## Notes

- The `Newtonsoft.Json 9.0.1` vulnerability warning is inherited from a dependency and should be addressed
- Tests target `net10.0` framework (standard .NET, not MAUI-specific)
- MAUI workloads are NOT required to run unit tests
