namespace SentenceStudio.Contracts.Coach;

/// <summary>
/// The closed vocabulary of reason codes carried on a notice message, and the rules for reading it.
/// </summary>
/// <remarks>
/// <para>
/// A notice is the only message kind that can stand for "the coach did not do the thing you asked".
/// Because the same conversation is rendered twice — once from the live turn response and once from
/// the durable ledger — the meaning of a notice has to travel as data rather than be re-derived on
/// each side. This type is that data: a fixed set of strings and two predicates over it.
/// </para>
/// <para>
/// <b>Why a code and not the stop reason.</b> The durable ledger renumbers every stored row into its
/// own ordinal, so a client reading history cannot tell that a receipt row and a notice row came from
/// the same turn. Any rule of the form "unless the turn also wrote something" therefore has to be
/// resolved where the turn is still whole — on the server, at the moment the notice is written. The
/// code is the resolved answer.
/// </para>
/// <para>
/// <b>Why the default is not empty.</b> Every notice carries a code so that a missing code means a
/// malformed record rather than an informational notice. <see cref="Default"/> is the code for a
/// notice that reports something other than a refusal, and it is deliberately outside the set that
/// <see cref="IndicatesNoChange"/> accepts.
/// </para>
/// </remarks>
public static class CoachNoticeReasonCodes
{
    /// <summary>An informational notice: the coach is telling the learner something, not refusing.</summary>
    /// <remarks>
    /// Also the code for a turn that stopped for a refusal-shaped reason but still produced a change
    /// receipt. Writing something and then reporting a problem is not "no change applied".
    /// </remarks>
    public const string Default = "coach_notice";

    /// <summary>The learner stopped the turn.</summary>
    public const string Cancelled = "cancelled";

    /// <summary>The learner is over their allowance for the day.</summary>
    public const string RateLimited = "rate_limited";

    /// <summary>The turn ran out of time.</summary>
    public const string Timeout = "timeout";

    /// <summary>The request was refused before any work started.</summary>
    public const string InputRejected = "input_rejected";

    /// <summary>The proposed change did not survive validation.</summary>
    public const string ValidationFailed = "validation_failed";

    /// <summary>A tool the turn depended on failed.</summary>
    public const string ToolFailure = "tool_failure";

    /// <summary>The turn hit its iteration ceiling.</summary>
    public const string IterationLimit = "iteration_limit";

    /// <summary>The turn hit its output ceiling.</summary>
    public const string OutputTokenLimit = "output_token_limit";

    /// <summary>Another turn was already running.</summary>
    public const string ConcurrencyLimit = "concurrency_limit";

    /// <summary>The session was gone before the turn could finish.</summary>
    public const string SessionExpired = "session_expired";

    /// <summary>The turn failed for a reason with no better description.</summary>
    public const string Failed = "failed";

    /// <summary>
    /// The plan change landed but the coach's account of it did not survive the crash.
    /// </summary>
    /// <remarks>
    /// Informational, and deliberately outside <see cref="NoChangeCodes"/>. A recovered turn is the
    /// one case where the learner's plan definitely moved and the explanation definitely did not;
    /// marking it "no change applied" would be a false statement about data the learner can go and
    /// look at. It is its own code rather than <see cref="Default"/> so that the durable row still
    /// names why the thread has a gap in it.
    /// </remarks>
    public const string Recovered = "recovered";

    private static readonly HashSet<string> NoChangeCodes = new(StringComparer.Ordinal)
    {
        Cancelled,
        RateLimited,
        Timeout,
        InputRejected,
        ValidationFailed,
        ToolFailure,
        IterationLimit,
        OutputTokenLimit,
        ConcurrencyLimit,
        SessionExpired,
        Failed
    };

    /// <summary>
    /// The codes that report something other than a refusal. Never marked "no change applied".
    /// </summary>
    private static readonly HashSet<string> InformationalCodes = new(StringComparer.Ordinal)
    {
        Default,
        Recovered
    };

    /// <summary>Every code in the vocabulary, refusal and informational alike.</summary>
    public static IReadOnlyCollection<string> All { get; } =
        NoChangeCodes.Concat(InformationalCodes).ToArray();

    /// <summary>True when the code is one this build authors and can render.</summary>
    /// <remarks>
    /// The gate on new writes. Reading is deliberately more forgiving: a row written by another
    /// build stays readable, and <see cref="IndicatesNoChange"/> simply declines to claim anything
    /// about a code it does not recognize.
    /// </remarks>
    public static bool IsKnown(string? code) =>
        code is not null && (NoChangeCodes.Contains(code) || InformationalCodes.Contains(code));

    /// <summary>Maps a stop reason to the code that describes it, ignoring what the turn produced.</summary>
    /// <remarks>
    /// Use <see cref="ForNotice"/> when writing a notice. This overload exists for callers that only
    /// have a stop reason and are naming it, not deciding whether it counts as a refusal.
    /// </remarks>
    public static string FromStopReason(CoachStopReason reason) => reason switch
    {
        CoachStopReason.Cancelled => Cancelled,
        CoachStopReason.RateLimit => RateLimited,
        CoachStopReason.Timeout => Timeout,
        CoachStopReason.InputRejected => InputRejected,
        CoachStopReason.ValidationFailed => ValidationFailed,
        CoachStopReason.ToolFailure => ToolFailure,
        CoachStopReason.IterationLimit => IterationLimit,
        CoachStopReason.OutputTokenLimit => OutputTokenLimit,
        CoachStopReason.ConcurrencyLimit => ConcurrencyLimit,
        CoachStopReason.SessionExpired => SessionExpired,
        CoachStopReason.Failed => Failed,
        _ => Default
    };

    /// <summary>
    /// The code to store on a notice, given the turn's stop reason and whether that same turn
    /// actually wrote a change.
    /// </summary>
    /// <param name="reason">Why the turn stopped.</param>
    /// <param name="turnProducedChange">True when the turn emitted a change receipt.</param>
    /// <remarks>
    /// A receipt outranks the stop reason. A turn that applied a change and then hit a tool failure
    /// on a later step has changed the learner's plan; telling them nothing happened would be a
    /// false statement about their own data, and the more dangerous of the two errors.
    /// </remarks>
    public static string ForNotice(CoachStopReason reason, bool turnProducedChange) =>
        turnProducedChange ? Default : FromStopReason(reason);

    /// <summary>True when the code means the learner's plan was left untouched.</summary>
    /// <remarks>
    /// Deliberately closed: an unrecognized code is not treated as a refusal. A newer server that
    /// invents a code an older client has never seen leaves that client silent rather than making it
    /// assert something about data it cannot interpret.
    /// </remarks>
    public static bool IndicatesNoChange(string? code) =>
        code is not null && NoChangeCodes.Contains(code);
}
