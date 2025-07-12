# Sentence Studio

Sentence Studio is a comprehensive language learning application that empowers learners to master their target language through interactive, AI-powered exercises. As language educators know, consistent practice with meaningful feedback is essential for developing fluency, accuracy, and confidence. This application provides personalized sentence building exercises, conversation practice, and vocabulary development tools that adapt to each learner's progress and proficiency level.

## Overview

Sentence Studio leverages artificial intelligence to create engaging language learning experiences across multiple platforms. Whether you're a beginner building foundational skills or an advanced learner refining your command of the language, the application provides structured practice opportunities that mirror effective classroom instruction methods.

### Core Learning Features

- **AI-Powered Conversation Practice**: Engage in natural conversations with AI tutoring that provides immediate feedback and guidance
- **Sentence Building Exercises**: Interactive activities that help learners construct grammatically correct and contextually appropriate sentences
- **Photo Description Activities**: Visual prompts that encourage descriptive language use and vocabulary expansion
- **Cloze Exercises**: Fill-in-the-blank activities that reinforce grammar patterns and vocabulary retention
- **Translation Practice**: Bidirectional translation exercises that develop cross-linguistic competency
- **Vocabulary Matching**: Systematic vocabulary building through context-based matching activities
- **Shadowing Practice**: Audio-based pronunciation and rhythm development exercises
- **Writing Assignments**: Guided writing tasks with AI feedback and scoring
- **Scene Description**: Visual storytelling exercises that develop narrative and descriptive skills

### Technical Architecture

- **Cross-Platform Support**: Native applications for iOS, Android, macOS, and Windows using .NET MAUI
- **Modern UI Framework**: Built with MauiReactor using Model-View-Update (MVU) architecture patterns
- **AI Integration**: OpenAI API for intelligent content generation, assessment, and personalized feedback
- **Text-to-Speech**: ElevenLabs integration for high-quality pronunciation modeling
- **Data Persistence**: SQLite database with CoreSync for cross-device synchronization
- **Offline Capability**: Local data storage enabling practice sessions without internet connectivity
- **Template Engine**: Scriban for dynamic prompt generation and content customization

## System Requirements

### Development Environment

- **.NET 9.0 SDK or later** (targeting .NET 10.0 frameworks)
- **Visual Studio 2022** (version 17.8 or later) or **Visual Studio Code** with C# extensions
- **MAUI Workloads**: Install using `dotnet workload install maui`

### Platform-Specific Requirements

#### Windows Development
- Windows 10 version 1903 (build 18362) or later
- Windows App SDK for Windows target framework

#### macOS Development  
- macOS 12.0 (Monterey) or later
- Xcode 14.0 or later for iOS/macOS targets

#### Mobile Development
- Android API level 21 (Android 5.0) or later
- iOS 12.2 or later

## Development Environment Setup

### 1. Install .NET and MAUI Workloads

```bash
# Install .NET 9.0 SDK or later
# Download from: https://dotnet.microsoft.com/download

# Install MAUI workloads
dotnet workload install maui

# Verify installation
dotnet workload list
```

### 2. Clone and Restore Dependencies

```bash
# Clone the repository
git clone https://github.com/davidortinau/SentenceStudio.git
cd SentenceStudio

# Navigate to source directory
cd src

# Restore NuGet packages
dotnet restore
```

### 3. API Keys Configuration

Sentence Studio requires API keys for AI and text-to-speech functionality. You'll need to obtain and configure the following:

#### Required API Keys

1. **OpenAI API Key** - For AI-powered language learning features
   - Visit: https://platform.openai.com/api-keys
   - Create an account and generate an API key
   - Ensure your account has sufficient credits or an active subscription

2. **ElevenLabs API Key** - For text-to-speech functionality
   - Visit: https://elevenlabs.io/app/speech-synthesis
   - Create an account and generate an API key
   - Free tier available with usage limitations

#### Desktop Configuration (Development)

For desktop development, set environment variables:

**Windows (PowerShell):**
```powershell
$env:AI__OpenAI__ApiKey = "your-openai-api-key-here"
$env:ElevenLabsKey = "your-elevenlabs-api-key-here"
```

**macOS/Linux (Terminal):**
```bash
export AI__OpenAI__ApiKey="your-openai-api-key-here"
export ElevenLabsKey="your-elevenlabs-api-key-here"
```

#### Mobile Configuration

For mobile deployments, create an `appsettings.json` file in the `src/SentenceStudio` project directory:

```json
{
  "Settings": {
    "OpenAIKey": "your-openai-api-key-here",
    "ElevenLabsKey": "your-elevenlabs-api-key-here"
  }
}
```

**Important Security Note**: Never commit API keys to version control. The project's `.gitignore` excludes `appsettings.json` to prevent accidental exposure of sensitive credentials.

## Building and Running

### Build Commands

```bash
# Navigate to source directory
cd src

# Build for specific platforms
dotnet build SentenceStudio/SentenceStudio.csproj -f net10.0-maccatalyst
dotnet build SentenceStudio/SentenceStudio.csproj -f net10.0-android
dotnet build SentenceStudio/SentenceStudio.csproj -f net10.0-ios
dotnet build SentenceStudio/SentenceStudio.csproj -f net10.0-windows10.0.19041.0
```

### Running the Application

#### Desktop (macOS Catalyst)
```bash
dotnet run --project SentenceStudio/SentenceStudio.csproj -f net10.0-maccatalyst
```

#### Desktop (Windows)
```bash
dotnet run --project SentenceStudio/SentenceStudio.csproj -f net10.0-windows10.0.19041.0
```

#### Mobile Deployment
Use Visual Studio or Visual Studio Code with the MAUI extension to deploy to physical devices or emulators.

## Project Structure

```
SentenceStudio/
├── src/
│   ├── SentenceStudio/              # Main MAUI application
│   │   ├── Pages/                   # Application pages and views
│   │   ├── Services/                # Business logic and AI integration
│   │   ├── Data/                    # Data access and repositories
│   │   ├── Models/                  # Data models and view models
│   │   └── Resources/               # Assets, fonts, and localization
│   ├── SentenceStudio.Shared/       # Shared models and utilities
│   ├── SentenceStudio.Web/          # Web components (if applicable)
│   ├── SentenceStudio.AppHost/      # Application hosting configuration
│   └── SentenceStudio.ServiceDefaults/ # Default service configurations
└── docs/                            # Documentation and specifications
```

## Data Storage

### SQLite Database

The application uses SQLite for local data storage, providing offline functionality and fast access to learning content. The database includes:

- User progress and activity tracking
- Vocabulary lists and learning resources
- Exercise history and performance metrics
- Skill profiles and learning preferences

**Database Location**: The SQLite database (`sstudio.db3`) is stored in the application's data directory.

**Database Management**: For viewing and editing the database during development:
- **macOS**: [SQLite Browser](https://sqlitebrowser.org)
- **Windows**: [DB Browser for SQLite](https://sqlitebrowser.org)
- **Cross-platform**: [SQLiteStudio](https://sqlitestudio.pl)

### CoreSync Integration

The application includes CoreSync functionality for data synchronization across devices. This feature enables:
- Cross-device progress synchronization
- Backup and restore capabilities
- Collaborative learning features (when implemented)

## Troubleshooting

### Common Build Issues

**Error: NETSDK1045 - .NET SDK version not supported**
- Ensure you have .NET 9.0 SDK or later installed
- Verify with: `dotnet --version`

**Missing MAUI workloads**
- Install required workloads: `dotnet workload install maui`
- Repair if needed: `dotnet workload repair`

**API Key Issues**
- Verify environment variables are set correctly
- Check that API keys are valid and have sufficient quotas
- Ensure `appsettings.json` is properly formatted (mobile only)

### Platform-Specific Issues

**iOS/macOS Development**
- Ensure Xcode is installed and up to date
- Verify Apple Developer account configuration
- Check provisioning profiles for device deployment

**Android Development**
- Install Android SDK components through Visual Studio
- Verify Android emulator configuration
- Check device USB debugging settings

### Runtime Issues

**AI Features Not Working**
- Verify OpenAI API key is configured and valid
- Check internet connectivity for AI service calls
- Review application logs for API error messages

**Text-to-Speech Not Working**
- Verify ElevenLabs API key configuration
- Check audio permissions on mobile devices
- Ensure device audio output is functioning

## Contributing

This is an educational language learning application designed to demonstrate effective pedagogical approaches through technology. Contributions that enhance the learning experience, improve accessibility, or extend platform support are welcome.

### Development Guidelines

- Follow .NET and MAUI best practices
- Maintain consistency with MauiReactor MVU patterns
- Ensure cross-platform compatibility
- Include appropriate error handling and user feedback
- Respect user privacy and data protection principles

### Code Quality

- Use meaningful variable and method names
- Include XML documentation for public APIs
- Follow established coding conventions
- Write unit tests for business logic components
- Ensure responsive UI design across form factors

For more detailed development specifications, see the documentation in the `docs/` directory.

## License

Please refer to the repository license for usage and distribution terms.

## Support

For technical issues, feature requests, or educational methodology discussions, please use the GitHub Issues system to ensure proper tracking and community visibility.