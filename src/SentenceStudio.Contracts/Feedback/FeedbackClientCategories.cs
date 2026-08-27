namespace SentenceStudio.Contracts.Feedback;

/// <summary>
/// Where in the app the learner was when they filed feedback, as a closed set.
/// </summary>
/// <remarks>
/// <para>
/// This replaces the raw route string the client used to send. A route is not a neutral label: it
/// carries entity identifiers (<c>/resources/4821</c>), query strings (<c>?email=…</c>,
/// <c>?token=…</c>), and occasionally free text the learner typed into a search box. All of it was
/// copied verbatim into a <em>public</em> GitHub issue body, which is a disclosure the learner
/// never agreed to and that no amount of downstream escaping can take back.
/// </para>
/// <para>
/// A closed enum makes that structurally impossible rather than conditionally unlikely: there is no
/// value here that can carry an identifier, and an unrecognised value maps to
/// <see cref="Unknown"/> rather than falling through as text. Members may only be appended —
/// inserting one silently re-labels rows already written and tokens already signed.
/// </para>
/// </remarks>
public enum FeedbackRouteCategory
{
    /// <summary>The route did not match any known category, or none was supplied.</summary>
    Unknown = 0,

    /// <summary>The dashboard / today's plan surface.</summary>
    Dashboard = 1,

    /// <summary>A learning activity (quiz, reading, writing, translation, and so on).</summary>
    Activity = 2,

    /// <summary>Vocabulary and learning-resource management.</summary>
    Resources = 3,

    /// <summary>Skill profiles and their management.</summary>
    Skills = 4,

    /// <summary>The learner's own profile and settings.</summary>
    Profile = 5,

    /// <summary>Sign-in, registration, password reset, and account linking.</summary>
    Account = 6,

    /// <summary>The Learning Coach conversation surface.</summary>
    Coach = 7,

    /// <summary>Progress, history, and reporting surfaces.</summary>
    Progress = 8,

    /// <summary>The feedback page itself.</summary>
    Feedback = 9,

    /// <summary>The application root / landing page.</summary>
    Home = 10
}

/// <summary>
/// Which kind of host the client is running in, as a closed set.
/// </summary>
/// <remarks>
/// Deliberately coarse. The shared Blazor UI cannot honestly distinguish more than this: it runs
/// both server-side (where <c>OperatingSystem.IsIOS()</c> answers for the <em>server</em>) and
/// inside a native WebView, and a value that is wrong in half the deployments is worse for triage
/// than a value that is vague in all of them. Members may only be appended.
/// </remarks>
public enum FeedbackPlatform
{
    /// <summary>Not determined.</summary>
    Unknown = 0,

    /// <summary>A browser talking to the hosted web app.</summary>
    Web = 1,

    /// <summary>A native app head hosting the UI in a WebView.</summary>
    Native = 2
}
