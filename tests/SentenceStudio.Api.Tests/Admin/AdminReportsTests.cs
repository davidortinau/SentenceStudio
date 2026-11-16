using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SentenceStudio.Api.Admin;
using SentenceStudio.Api.Admin.Persistence;
using SentenceStudio.Api.Coach.Opportunities;
using SentenceStudio.Api.Coach.Operations;
using SentenceStudio.Api.Coach.Persistence.History;
using SentenceStudio.Api.Coach.Validation.Claims;
using SentenceStudio.Api.Feedback.Persistence;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.Contracts.Feedback;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Api.Tests.Admin;

/// <summary>
/// The feedback inbox: an admin sees every learner's flagged Sam answers, opens the one flagged
/// question and answer, and gives the report a status and an issue number. Nothing else of a
/// conversation is reachable, and rows no learner flagged are not there at all.
/// </summary>
public sealed class AdminReportsTests : IDisposable
{
    private const string AdminEmail = "captain@admin-tests.local";
    private const string Question = "SECRET-QUESTION why is 은 used here";
    private const string Answer = "SECRET-ANSWER because it marks the topic";

    private readonly AdminApiFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private static async Task<string?> CodeOf(HttpResponseMessage response)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    private async Task<(ApplicationUser Admin, ApplicationUser Learner, string ReportId)> SeedOneAsync()
    {
        var admin = await _factory.SeedAdminAsync(AdminEmail);
        var learner = await _factory.SeedAccountAsync("learner@admin-tests.local");
        var reportId = await _factory.SeedFlaggedAnswerAsync(learner, Question, Answer);
        return (admin, learner, reportId);
    }

    /// <summary>A row the server recorded about itself, with pointers into a real conversation.</summary>
    private async Task<string> SeedAutomaticRowAsync(ApplicationUser learner)
    {
        var flagged = await _factory.SeedFlaggedAnswerAsync(learner, Question, Answer);
        var id = Guid.NewGuid().ToString("n");
        await _factory.WithCoachDbAsync(async db =>
        {
            var source = await db.CoachOpportunities.AsNoTracking().SingleAsync(o => o.Id == flagged);
            db.CoachOpportunities.Add(new CoachOpportunity
            {
                Id = id,
                UserProfileId = source.UserProfileId,
                ConversationId = source.ConversationId,
                Kind = CoachOpportunityKind.AmbiguousFollowUp,
                Disposition = CoachOpportunityDisposition.Product,
                Surface = CoachOpportunitySurface.TurnOutcome,
                CapabilityCode = "referent_lost_after_offer",
                EvidenceMessageId = source.EvidenceMessageId,
                EvidenceMessageSequence = source.EvidenceMessageSequence,
                EvidenceOfferMessageId = source.EvidenceOfferMessageId,
                EvidenceOfferMessageSequence = source.EvidenceOfferMessageSequence,
                Fingerprint = Guid.NewGuid().ToString("n"),
                DedupBucketDate = source.DedupBucketDate,
                FirstObservedAtUtc = source.FirstObservedAtUtc,
                LastObservedAtUtc = source.LastObservedAtUtc.AddMinutes(1)
            });
            await db.SaveChangesAsync();
        });
        return id;
    }

    // ------------------------------------------------------------------ who gets in

    [Theory]
    [InlineData("GET", "/api/admin/reports/attention-count")]
    [InlineData("GET", "/api/admin/reports/sam")]
    [InlineData("GET", "/api/admin/reports/sam/{id}")]
    [InlineData("POST", "/api/admin/reports/sam/{id}/exchange")]
    [InlineData("POST", "/api/admin/reports/sam/{id}/triage")]
    [InlineData("GET", "/api/admin/reports/github")]
    [InlineData("POST", "/api/admin/reports/github/screen-states/fs-000000000000")]
    public async Task A_learner_and_a_caller_with_no_sign_in_get_404_on_every_inbox_route_even_for_their_own_report(
        string method, string route)
    {
        var (_, learner, reportId) = await SeedOneAsync();
        using var learnerClient = _factory.CreateClientFor(learner);
        using var anonymous = _factory.CreateClient();
        var auditBefore = (await _factory.ReadAuditAsync()).Count;

        foreach (var client in new[] { learnerClient, anonymous })
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), route.Replace("{id}", reportId))
            {
                Content = JsonContent.Create(new AdminSamTriageRequest(1, AdminReportCodes.StatusDismissed, 1))
            };
            var response = await client.SendAsync(request);

            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await response.Content.ReadAsStringAsync()).Should().BeEmpty();
        }

        (await _factory.ReadAuditAsync()).Count.Should().Be(auditBefore);
        await _factory.WithCoachDbAsync(async db =>
        {
            var row = await db.CoachOpportunities.AsNoTracking().SingleAsync();
            row.Status.Should().Be(CoachOpportunityStatus.New);
            row.EvidenceRevealCount.Should().Be(0);
        });
    }

    [Fact]
    public async Task The_old_operator_routes_are_gone_for_everyone()
    {
        var (admin, _, reportId) = await SeedOneAsync();
        using var client = _factory.CreateClientFor(admin);

        (await client.GetAsync("/api/v1/coach/operator/opportunities")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.GetAsync($"/api/v1/coach/operator/opportunities/{reportId}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.PostAsJsonAsync($"/api/v1/coach/operator/opportunities/{reportId}/evidence",
            new { acknowledgement = CoachOpportunityLimits.EvidenceRevealAcknowledgement }))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ------------------------------------------------------------------ the list

    [Fact]
    public async Task The_list_shows_every_learners_flagged_answers_newest_first_in_codes()
    {
        var admin = await _factory.SeedAdminAsync(AdminEmail);
        var a = await _factory.SeedAccountAsync("a@admin-tests.local");
        var b = await _factory.SeedAccountAsync("b@admin-tests.local");
        var now = _factory.Time.GetUtcNow().UtcDateTime;
        var older = await _factory.SeedFlaggedAnswerAsync(
            a, Question, Answer, CoachResponseReportReason.DidNotAnswer, now.AddHours(-3));
        var newer = await _factory.SeedFlaggedAnswerAsync(
            b, Question, Answer, CoachResponseReportReason.ExpectedAppAction, now.AddHours(-1));
        await _factory.SeedFlaggedAnswerAsync(
            b, Question, Answer, CoachResponseReportReason.ExpectedAppAction, now.AddMinutes(-30), addToReportId: newer);
        using var client = _factory.CreateClientFor(admin);

        var list = (await client.GetFromJsonAsync<AdminSamReportList>("/api/admin/reports/sam"))!;

        list.Items.Select(i => i.Id).Should().Equal(newer, older);
        list.AttentionCount.Should().Be(2);
        list.UnlistedFlagCount.Should().Be(0);
        list.Truncated.Should().BeFalse();

        var first = list.Items[0];
        first.LearnerId.Should().Be(b.UserProfileId);
        first.Status.Should().Be(AdminReportCodes.StatusNew);
        first.NeedsAttention.Should().BeTrue();
        first.Reason.Should().Be(AdminReportCodes.ReasonExpectedAppAction);
        first.FlagCount.Should().Be(2);
        first.FirstFlaggedAtUtc.Should().Be(now.AddHours(-1));
        first.LastFlaggedAtUtc.Should().Be(now.AddMinutes(-30));
        first.StopReason.Should().Be("completed");
        first.HasExchange.Should().BeTrue();
        first.ExchangeOpenedCount.Should().Be(0);
        first.IssueNumber.Should().BeNull();
        list.Items[1].Reason.Should().Be(AdminReportCodes.ReasonDidNotAnswer);

        list.Learners.Select(l => l.ProfileId).Should().BeEquivalentTo([a.UserProfileId!, b.UserProfileId!]);
        list.Learners.Single(l => l.ProfileId == b.UserProfileId).Email.Should().Be(b.Email);

        var row = (await _factory.ReadAuditAsync()).Single(r => r.Action == AdminActions.ReportsSamList);
        row.TargetId.Should().BeNull();
        row.Outcome.Should().Be(AdminAuditOutcome.Succeeded);
    }

    [Fact]
    public async Task Neither_the_list_nor_the_detail_carries_a_word_of_the_conversation_or_a_message_id()
    {
        var (admin, _, reportId) = await SeedOneAsync();
        using var client = _factory.CreateClientFor(admin);
        string? questionId = null, conversationId = null;
        await _factory.WithCoachDbAsync(async db =>
        {
            var row = await db.CoachOpportunities.AsNoTracking().SingleAsync();
            questionId = row.EvidenceMessageId;
            conversationId = row.ConversationId;
        });

        foreach (var route in new[] { "/api/admin/reports/sam", $"/api/admin/reports/sam/{reportId}" })
        {
            var body = await client.GetStringAsync(route);
            body.Should().NotContain("SECRET").And.NotContain(questionId!).And.NotContain(conversationId!);
        }
    }

    [Fact]
    public async Task Rows_no_learner_flagged_are_not_listed_counted_read_triaged_or_opened()
    {
        var admin = await _factory.SeedAdminAsync(AdminEmail);
        var learner = await _factory.SeedAccountAsync("learner@admin-tests.local");
        var automatic = await SeedAutomaticRowAsync(learner);
        using var client = _factory.CreateClientFor(admin);
        await AdminApiFactory.ConfirmPasswordAsync(client);

        var list = (await client.GetFromJsonAsync<AdminSamReportList>("/api/admin/reports/sam"))!;
        list.Items.Should().ContainSingle().Which.Id.Should().NotBe(automatic);
        list.AttentionCount.Should().Be(1);

        (await client.GetAsync($"/api/admin/reports/sam/{automatic}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.PostAsJsonAsync($"/api/admin/reports/sam/{automatic}/triage",
            new AdminSamTriageRequest(1, AdminReportCodes.StatusDismissed, 7))).StatusCode.Should().Be(HttpStatusCode.NotFound);

        var opened = await client.PostAsync($"/api/admin/reports/sam/{automatic}/exchange", null);
        opened.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await opened.Content.ReadAsStringAsync()).Should().NotContain("SECRET");

        await _factory.WithCoachDbAsync(async db =>
        {
            var row = await db.CoachOpportunities.AsNoTracking().SingleAsync(o => o.Id == automatic);
            row.Status.Should().Be(CoachOpportunityStatus.New);
            row.LinkedIssueNumber.Should().BeNull();
            row.EvidenceRevealCount.Should().Be(0);
        });
    }

    [Fact]
    public async Task The_status_filter_narrows_the_list_and_the_count_to_look_at_stays_whole()
    {
        var (admin, learner, first) = await SeedOneAsync();
        var second = await _factory.SeedFlaggedAnswerAsync(learner, Question, Answer);
        using var client = _factory.CreateClientFor(admin);
        (await client.PostAsJsonAsync($"/api/admin/reports/sam/{first}/triage",
            new AdminSamTriageRequest(1, AdminReportCodes.StatusDeferred, null))).StatusCode.Should().Be(HttpStatusCode.OK);

        var deferred = (await client.GetFromJsonAsync<AdminSamReportList>("/api/admin/reports/sam?status=deferred"))!;
        var fresh = (await client.GetFromJsonAsync<AdminSamReportList>("/api/admin/reports/sam?status=new"))!;

        deferred.Items.Select(i => i.Id).Should().Equal(first);
        deferred.AttentionCount.Should().Be(1);
        deferred.Items.Single().NeedsAttention.Should().BeFalse();
        fresh.Items.Select(i => i.Id).Should().Equal(second);
    }

    [Fact]
    public async Task A_shorter_page_says_it_left_rows_out()
    {
        var (admin, learner, _) = await SeedOneAsync();
        await _factory.SeedFlaggedAnswerAsync(learner, Question, Answer);
        using var client = _factory.CreateClientFor(admin);

        var one = (await client.GetFromJsonAsync<AdminSamReportList>("/api/admin/reports/sam?take=1"))!;
        var two = (await client.GetFromJsonAsync<AdminSamReportList>("/api/admin/reports/sam?take=2"))!;

        one.Items.Should().HaveCount(1);
        one.Truncated.Should().BeTrue();
        two.Items.Should().HaveCount(2);
        two.Truncated.Should().BeFalse();
    }

    [Theory]
    [InlineData("status=New")]
    [InlineData("status=open")]
    [InlineData("status=")]
    [InlineData("take=0")]
    [InlineData("take=201")]
    [InlineData("take=ten")]
    [InlineData("attention=false")]
    [InlineData("attention=1")]
    [InlineData("attention=true&status=new")]
    public async Task A_bad_list_query_is_the_routes_own_400(string query)
    {
        var (admin, _, _) = await SeedOneAsync();
        using var client = _factory.CreateClientFor(admin);

        var response = await client.GetAsync($"/api/admin/reports/sam?{query}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await CodeOf(response)).Should().Be(AdminFailureCodes.InvalidRequest);
    }

    [Fact]
    public async Task A_flag_recorded_without_an_inbox_row_is_counted_so_the_list_does_not_pass_for_complete()
    {
        var (admin, _, _) = await SeedOneAsync();
        await _factory.WithCoachDbAsync(async db =>
        {
            var flag = await db.CoachResponseReports.SingleAsync();
            db.CoachResponseReports.Add(new Api.Coach.Reports.CoachResponseReport
            {
                Id = Guid.NewGuid().ToString("n"),
                UserProfileId = flag.UserProfileId,
                ConversationId = flag.ConversationId,
                CoachMessageId = "another-answer",
                RequestMessageId = "another-question",
                Reason = CoachResponseReportReason.Confusing,
                OpportunityId = null,
                ReportedAtUtc = flag.ReportedAtUtc
            });
            await db.SaveChangesAsync();
        });
        using var client = _factory.CreateClientFor(admin);

        var list = (await client.GetFromJsonAsync<AdminSamReportList>("/api/admin/reports/sam"))!;

        list.Items.Should().HaveCount(1);
        list.UnlistedFlagCount.Should().Be(1);
    }

    [Fact]
    public async Task The_sidebar_count_is_the_reports_nobody_has_looked_at_and_leaves_no_audit_row()
    {
        var (admin, learner, first) = await SeedOneAsync();
        await _factory.SeedFlaggedAnswerAsync(learner, Question, Answer);
        await SeedAutomaticRowAsync(learner);
        using var client = _factory.CreateClientFor(admin);
        var auditBefore = (await _factory.ReadAuditAsync()).Count;

        (await client.GetFromJsonAsync<AdminReportAttentionCount>("/api/admin/reports/attention-count"))!.Sam.Should().Be(3);
        (await _factory.ReadAuditAsync()).Count.Should().Be(auditBefore);

        await client.PostAsJsonAsync($"/api/admin/reports/sam/{first}/triage",
            new AdminSamTriageRequest(1, AdminReportCodes.StatusReviewed));
        (await client.GetFromJsonAsync<AdminReportAttentionCount>("/api/admin/reports/attention-count"))!.Sam.Should().Be(2);
    }

    /// <summary>
    /// A learner's later flag lands on the report they already have for that reason and day, and
    /// the report keeps the status it was given. It must not disappear behind that status.
    /// </summary>
    [Fact]
    public async Task A_report_flagged_again_after_it_was_triaged_needs_attention_until_someone_looks_again()
    {
        var (admin, learner, reportId) = await SeedOneAsync();
        using var client = _factory.CreateClientFor(admin);
        await client.PostAsJsonAsync($"/api/admin/reports/sam/{reportId}/triage",
            new AdminSamTriageRequest(1, AdminReportCodes.StatusAccepted, 412));
        (await client.GetFromJsonAsync<AdminSamReportList>("/api/admin/reports/sam?attention=true"))!
            .Items.Should().BeEmpty();

        _factory.Time.Advance(TimeSpan.FromHours(2));
        await _factory.SeedFlaggedAnswerAsync(learner, "later question", "later answer", addToReportId: reportId);

        var attention = (await client.GetFromJsonAsync<AdminSamReportList>("/api/admin/reports/sam?attention=true"))!;
        attention.AttentionCount.Should().Be(1);
        var row = attention.Items.Should().ContainSingle().Subject;
        row.Status.Should().Be(AdminReportCodes.StatusAccepted, "an accepted report keeps its status");
        row.NeedsAttention.Should().BeTrue();
        row.FlagCount.Should().Be(2);
        (await client.GetFromJsonAsync<AdminSamReportList>("/api/admin/reports/sam?status=new"))!.Items.Should().BeEmpty();
        (await client.GetFromJsonAsync<AdminReportAttentionCount>("/api/admin/reports/attention-count"))!.Sam.Should().Be(1);

        // Looking again is a triage that changes nothing else: the same status, the link left alone.
        _factory.Time.Advance(TimeSpan.FromMinutes(1));
        // A caller that read the report before the new flag is refused, and nothing is marked.
        var stale = await client.PostAsJsonAsync($"/api/admin/reports/sam/{reportId}/triage",
            new AdminSamTriageRequest(1, AdminReportCodes.StatusAccepted, 999));
        stale.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await CodeOf(stale)).Should().Be(AdminFailureCodes.Conflict);
        (await client.GetFromJsonAsync<AdminSamReportDetail>($"/api/admin/reports/sam/{reportId}"))!.Report
            .Should().Match<AdminSamReportRow>(r => r.NeedsAttention && r.IssueNumber == 412,
                "a flag nobody read is never marked as looked at, and a refused triage writes nothing");

        // Having read it with both flags, saying so is enough: no status, no issue.
        var looked = (await (await client.PostAsJsonAsync($"/api/admin/reports/sam/{reportId}/triage",
                new AdminSamTriageRequest(2)))
            .Content.ReadFromJsonAsync<AdminSamReportRow>())!;

        looked.NeedsAttention.Should().BeFalse();
        looked.Status.Should().Be(AdminReportCodes.StatusAccepted);
        looked.IssueNumber.Should().Be(412);
        (await client.GetFromJsonAsync<AdminReportAttentionCount>("/api/admin/reports/attention-count"))!.Sam.Should().Be(0);
    }

    // ------------------------------------------------------------------ the detail

    [Fact]
    public async Task The_detail_lists_each_flag_in_codes_and_names_the_report_in_the_audit_log()
    {
        var (admin, learner, reportId) = await SeedOneAsync();
        var now = _factory.Time.GetUtcNow().UtcDateTime;
        await _factory.SeedFlaggedAnswerAsync(learner, "second question", "second answer",
            flaggedAtUtc: now.AddMinutes(5), addToReportId: reportId);
        await _factory.WithCoachDbAsync(async db =>
        {
            var newest = await db.CoachResponseReports.OrderByDescending(r => r.ReportedAtUtc).FirstAsync();
            newest.TurnStatus = CoachTurnOperationStatus.Completed;
            newest.TurnErrorCode = "tool_failed";
            newest.InvokedToolNames = "get_vocabulary_progress";
            newest.WriteStatus = CoachWriteOperationStatus.Rejected;
            newest.WriteFailureCode = "invalid_arguments";
            newest.GroundingStage = (int)CoachGroundingStage.Observe;
            newest.GroundingRefused = false;
            newest.GroundingFindingCount = 2;
            newest.GroundingRuleCodes = "UnsupportedClaim";
            newest.GroundingLimitationCode = (int)CoachLimitationCode.NotBuilt;
            await db.SaveChangesAsync();
        });
        using var client = _factory.CreateClientFor(admin);

        var detail = (await client.GetFromJsonAsync<AdminSamReportDetail>($"/api/admin/reports/sam/{reportId}"))!;

        detail.Report.Id.Should().Be(reportId);
        detail.Report.FlagCount.Should().Be(2);
        detail.Learner!.Email.Should().Be(learner.Email);
        detail.FlagsTruncated.Should().BeFalse();
        detail.Flags.Should().HaveCount(2);
        detail.Flags[0].Should().BeEquivalentTo(new AdminSamReportFlag(
            now.AddMinutes(5), AdminReportCodes.ReasonIncorrectOrMisleading, "pedagogical_answer",
            "completed", 1, "tool_failed", "get_vocabulary_progress", "rejected", "invalid_arguments",
            "observe", false, null, null, 2, "UnsupportedClaim", "not_built"));
        detail.Flags[1].FlaggedAtUtc.Should().Be(now);
        detail.Flags[1].GroundingStage.Should().BeNull("a flag with no grounding measurement says nothing, not zero");

        var row = (await _factory.ReadAuditAsync()).Single(r => r.Action == AdminActions.ReportsSamRead);
        row.TargetKind.Should().Be(AdminTargetKinds.Report);
        row.TargetId.Should().Be(reportId);
    }

    [Fact]
    public async Task An_id_that_names_no_report_is_a_404_and_one_too_long_to_be_an_id_is_never_looked_up()
    {
        var (admin, _, _) = await SeedOneAsync();
        using var client = _factory.CreateClientFor(admin);

        (await client.GetAsync("/api/admin/reports/sam/no-such-report")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.GetAsync($"/api/admin/reports/sam/{new string('a', 65)}")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        var rows = (await _factory.ReadAuditAsync()).Where(r => r.Action == AdminActions.ReportsSamRead).ToList();
        rows.Should().ContainSingle().Which.TargetId.Should().Be("no-such-report");
    }

    // ------------------------------------------------------------------ the flagged exchange

    [Fact]
    public async Task Opening_the_exchange_asks_an_admin_for_the_password_again_and_reads_nothing_without_it()
    {
        var (admin, _, reportId) = await SeedOneAsync();
        using var client = _factory.CreateClientFor(admin);

        var response = await client.PostAsync($"/api/admin/reports/sam/{reportId}/exchange", null);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await CodeOf(response)).Should().Be(AdminFailureCodes.StepUpRequired);
        (await response.Content.ReadAsStringAsync()).Should().NotContain("SECRET");
        await _factory.WithCoachDbAsync(async db =>
            (await db.CoachOpportunities.AsNoTracking().SingleAsync()).EvidenceRevealCount.Should().Be(0));

        var row = (await _factory.ReadAuditAsync()).Single(r => r.Action == AdminActions.ReportsSamExchangeRead);
        row.Outcome.Should().Be(AdminAuditOutcome.Failed);
        row.FailureCode.Should().Be(AdminFailureCodes.StepUpRequired);
        row.TargetId.Should().Be(reportId);
    }

    [Fact]
    public async Task An_admin_with_a_confirmed_password_reads_another_learners_flagged_question_and_answer()
    {
        var (admin, _, reportId) = await SeedOneAsync();
        using var client = _factory.CreateClientFor(admin);
        await AdminApiFactory.ConfirmPasswordAsync(client);

        var response = await client.PostAsync($"/api/admin/reports/sam/{reportId}/exchange", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        var opened = (await response.Content.ReadFromJsonAsync<AdminSamExchangeResponse>())!;
        opened.ReportId.Should().Be(reportId);
        opened.State.Should().Be(AdminReportCodes.ExchangeAvailable);
        opened.Truncated.Should().BeFalse();
        opened.OpenedCount.Should().Be(1);
        opened.Exchanges.Should().ContainSingle().Which.Should().Be(
            new AdminSamExchange(_factory.Time.GetUtcNow().UtcDateTime, Question, Answer));

        var row = (await _factory.ReadAuditAsync()).Single(r => r.Action == AdminActions.ReportsSamExchangeRead);
        row.ActorId.Should().Be(admin.Id);
        row.TargetKind.Should().Be(AdminTargetKinds.Report);
        row.TargetId.Should().Be(reportId);
        row.Outcome.Should().Be(AdminAuditOutcome.Succeeded);

        await _factory.WithCoachDbAsync(async db =>
        {
            var stored = await db.CoachOpportunities.AsNoTracking().SingleAsync();
            stored.EvidenceRevealCount.Should().Be(1);
            stored.EvidenceLastRevealedAtUtc.Should().Be(_factory.Time.GetUtcNow().UtcDateTime);
        });

        (await client.GetFromJsonAsync<AdminSamReportDetail>($"/api/admin/reports/sam/{reportId}"))!
            .Report.ExchangeOpenedCount.Should().Be(1);
    }

    [Fact]
    public async Task Only_the_flagged_question_and_answer_are_read_not_the_rest_of_the_conversation()
    {
        var (admin, learner, reportId) = await SeedOneAsync();

        // The same learner said more in the same conversation afterwards, and flagged none of it.
        string? conversationId = null;
        await _factory.WithCoachDbAsync(async db =>
            conversationId = (await db.CoachOpportunities.AsNoTracking().SingleAsync()).ConversationId);
        await _factory.AppendUnflaggedMessageAsync(learner, conversationId!, "UNFLAGGED-FOLLOW-UP");
        await _factory.AppendUnflaggedMessageAsync(learner, conversationId!, "UNFLAGGED-REPLY");
        using var client = _factory.CreateClientFor(admin);
        await AdminApiFactory.ConfirmPasswordAsync(client);

        var body = await (await client.PostAsync($"/api/admin/reports/sam/{reportId}/exchange", null))
            .Content.ReadAsStringAsync();

        body.Should().Contain("SECRET-QUESTION").And.Contain("SECRET-ANSWER").And.NotContain("UNFLAGGED");
    }

    [Fact]
    public async Task Each_flag_on_a_report_opens_as_its_own_exchange_newest_first()
    {
        var (admin, learner, reportId) = await SeedOneAsync();
        var later = _factory.Time.GetUtcNow().UtcDateTime.AddMinutes(10);
        await _factory.SeedFlaggedAnswerAsync(learner, "second question", "second answer",
            flaggedAtUtc: later, addToReportId: reportId);
        using var client = _factory.CreateClientFor(admin);
        await AdminApiFactory.ConfirmPasswordAsync(client);

        var opened = (await (await client.PostAsync($"/api/admin/reports/sam/{reportId}/exchange", null))
            .Content.ReadFromJsonAsync<AdminSamExchangeResponse>())!;

        opened.Exchanges.Should().Equal(
            new AdminSamExchange(later, "second question", "second answer"),
            new AdminSamExchange(_factory.Time.GetUtcNow().UtcDateTime, Question, Answer));
    }

    [Fact]
    public async Task A_report_whose_flags_were_pruned_still_opens_the_exchange_it_points_at()
    {
        var (admin, _, reportId) = await SeedOneAsync();
        await _factory.WithCoachDbAsync(db => db.CoachResponseReports.ExecuteDeleteAsync());
        using var client = _factory.CreateClientFor(admin);
        await AdminApiFactory.ConfirmPasswordAsync(client);

        var opened = (await (await client.PostAsync($"/api/admin/reports/sam/{reportId}/exchange", null))
            .Content.ReadFromJsonAsync<AdminSamExchangeResponse>())!;

        opened.State.Should().Be(AdminReportCodes.ExchangeAvailable);
        opened.Exchanges.Should().ContainSingle().Which.Should().Be(new AdminSamExchange(null, Question, Answer));
    }

    [Fact]
    public async Task A_conversation_the_learner_deleted_opens_as_unavailable_not_as_an_error()
    {
        var (admin, _, reportId) = await SeedOneAsync();
        await _factory.WithCoachDbAsync(async db =>
        {
            await db.CoachMessages.ExecuteDeleteAsync();
            await db.CoachConversations.ExecuteDeleteAsync();
        });
        using var client = _factory.CreateClientFor(admin);
        await AdminApiFactory.ConfirmPasswordAsync(client);

        var response = await client.PostAsync($"/api/admin/reports/sam/{reportId}/exchange", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var opened = (await response.Content.ReadFromJsonAsync<AdminSamExchangeResponse>())!;
        opened.State.Should().Be(AdminReportCodes.ExchangeUnavailable);
        opened.Exchanges.Should().ContainSingle().Which.Question.Should().BeNull();
        opened.OpenedCount.Should().Be(1, "an attempt on messages that turn out to be gone is still counted");
    }

    [Fact]
    public async Task A_report_that_points_at_no_messages_opens_as_not_applicable_and_is_not_counted_as_opened()
    {
        var (admin, _, reportId) = await SeedOneAsync();
        await _factory.WithCoachDbAsync(async db =>
        {
            await db.CoachResponseReports.ExecuteDeleteAsync();
            var row = await db.CoachOpportunities.SingleAsync();
            row.ConversationId = null;
            row.EvidenceMessageId = row.EvidenceOfferMessageId = null;
            row.EvidenceMessageSequence = row.EvidenceOfferMessageSequence = null;
            await db.SaveChangesAsync();
        });
        using var client = _factory.CreateClientFor(admin);
        await AdminApiFactory.ConfirmPasswordAsync(client);

        var opened = (await (await client.PostAsync($"/api/admin/reports/sam/{reportId}/exchange", null))
            .Content.ReadFromJsonAsync<AdminSamExchangeResponse>())!;

        opened.State.Should().Be(AdminReportCodes.ExchangeNotApplicable);
        opened.Exchanges.Should().BeEmpty();
        opened.OpenedCount.Should().Be(0);
        await _factory.WithCoachDbAsync(async db =>
        {
            var row = await db.CoachOpportunities.AsNoTracking().SingleAsync();
            row.EvidenceRevealCount.Should().Be(0, "nothing was opened");
            row.EvidenceLastRevealedAtUtc.Should().BeNull();
        });
    }

    [Fact]
    public async Task A_host_without_a_durable_key_ring_refuses_to_open_any_exchange()
    {
        using var factory = new AdminApiFactory { DurableKeyRing = false };
        var admin = await factory.SeedAdminAsync(AdminEmail);
        var learner = await factory.SeedAccountAsync("learner@admin-tests.local");
        var reportId = await factory.SeedFlaggedAnswerAsync(learner, Question, Answer);
        using var client = factory.CreateClientFor(admin);
        await AdminApiFactory.ConfirmPasswordAsync(client);

        foreach (var id in new[] { reportId, "no-such-report" })
        {
            var response = await client.PostAsync($"/api/admin/reports/sam/{id}/exchange", null);

            response.StatusCode.Should().Be(HttpStatusCode.Conflict, "the answer must not say which ids exist");
            (await CodeOf(response)).Should().Be(AdminFailureCodes.KeyRingNotDurable);
            (await response.Content.ReadAsStringAsync()).Should().NotContain("SECRET");
        }
    }

    [Fact]
    public async Task A_token_with_the_learner_content_permission_opens_the_exchange_and_is_recorded_by_its_id()
    {
        var (admin, _, reportId) = await SeedOneAsync();
        var token = await _factory.IssueTokenAsync(admin, AdminTokenPermissionWire.LearnerContent);
        using var client = _factory.CreateClientForToken(token.Token);

        var opened = (await (await client.PostAsync($"/api/admin/reports/sam/{reportId}/exchange", null))
            .Content.ReadFromJsonAsync<AdminSamExchangeResponse>())!;

        opened.Exchanges.Should().ContainSingle().Which.Answer.Should().Be(Answer);
        var row = (await _factory.ReadAuditAsync()).Single(r => r.Action == AdminActions.ReportsSamExchangeRead);
        row.ActorKind.Should().Be(AdminActorKind.AgentToken);
        row.ActorId.Should().Be(token.Row.Id);
        row.TargetId.Should().Be(reportId);
    }

    [Fact]
    public async Task A_token_reads_the_inbox_by_ids_and_is_given_no_name_or_email()
    {
        var (admin, learner, reportId) = await SeedOneAsync();
        var token = await _factory.IssueTokenAsync(admin, AdminTokenPermissionWire.Reports);
        using var client = _factory.CreateClientForToken(token.Token);

        var list = await client.GetStringAsync("/api/admin/reports/sam");
        var detail = await client.GetStringAsync($"/api/admin/reports/sam/{reportId}");
        var github = await client.GetStringAsync("/api/admin/reports/github");

        list.Should().Contain(learner.UserProfileId!).And.NotContain(learner.Email!);
        detail.Should().Contain(learner.UserProfileId!).And.NotContain(learner.Email!);
        github.Should().NotContain(learner.Email!);
        JsonDocument.Parse(detail).RootElement.GetProperty("learner").ValueKind.Should().Be(JsonValueKind.Null);
    }

    // ------------------------------------------------------------------ triage

    [Fact]
    public async Task A_status_and_an_issue_number_stick_and_the_change_is_recorded()
    {
        var (admin, _, reportId) = await SeedOneAsync();
        using var client = _factory.CreateClientFor(admin);

        var response = await client.PostAsJsonAsync($"/api/admin/reports/sam/{reportId}/triage",
            new AdminSamTriageRequest(1, AdminReportCodes.StatusAccepted, 412));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var updated = (await response.Content.ReadFromJsonAsync<AdminSamReportRow>())!;
        updated.Status.Should().Be(AdminReportCodes.StatusAccepted);
        updated.IssueNumber.Should().Be(412);
        updated.IssueUrl.Should().Be("https://github.com/davidortinau/SentenceStudio/issues/412");
        updated.ReviewedAtUtc.Should().Be(_factory.Time.GetUtcNow().UtcDateTime);

        var read = (await client.GetFromJsonAsync<AdminSamReportDetail>($"/api/admin/reports/sam/{reportId}"))!;
        read.Report.Status.Should().Be(AdminReportCodes.StatusAccepted);
        read.Report.IssueNumber.Should().Be(412);

        var row = (await _factory.ReadAuditAsync()).Single(r => r.Action == AdminActions.ReportsSamTriage);
        row.TargetId.Should().Be(reportId);
        row.Outcome.Should().Be(AdminAuditOutcome.Succeeded);
    }

    [Fact]
    public async Task A_triage_that_names_a_flag_count_the_report_does_not_have_is_refused_and_writes_nothing()
    {
        var (admin, _, reportId) = await SeedOneAsync();
        using var client = _factory.CreateClientFor(admin);

        var response = await client.PostAsJsonAsync($"/api/admin/reports/sam/{reportId}/triage",
            new AdminSamTriageRequest(2, AdminReportCodes.StatusDismissed, 7));

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await CodeOf(response)).Should().Be(AdminFailureCodes.Conflict);
        await _factory.WithCoachDbAsync(async db =>
        {
            var row = await db.CoachOpportunities.AsNoTracking().SingleAsync();
            row.Status.Should().Be(CoachOpportunityStatus.New);
            row.LinkedIssueNumber.Should().BeNull();
            row.ReviewedAtUtc.Should().BeNull();
            row.Version.Should().Be(0);
        });
        (await _factory.ReadAuditAsync()).Single(r => r.Action == AdminActions.ReportsSamTriage)
            .FailureCode.Should().Be(AdminFailureCodes.Conflict);
    }

    [Fact]
    public async Task A_token_with_the_triage_permission_does_the_same()
    {
        var (admin, _, reportId) = await SeedOneAsync();
        var token = await _factory.IssueTokenAsync(admin, AdminTokenPermissionWire.Triage);
        using var client = _factory.CreateClientForToken(token.Token);

        var updated = (await (await client.PostAsJsonAsync($"/api/admin/reports/sam/{reportId}/triage",
                new AdminSamTriageRequest(1, AdminReportCodes.StatusReviewed, 9)))
            .Content.ReadFromJsonAsync<AdminSamReportRow>())!;

        updated.Status.Should().Be(AdminReportCodes.StatusReviewed);
        updated.IssueNumber.Should().Be(9);
        (await _factory.ReadAuditAsync()).Single(r => r.Action == AdminActions.ReportsSamTriage)
            .ActorKind.Should().Be(AdminActorKind.AgentToken);
    }

    [Fact]
    public async Task What_a_triage_leaves_out_stays_as_it_is_and_an_issue_is_removed_only_when_asked()
    {
        var (admin, _, reportId) = await SeedOneAsync();
        using var client = _factory.CreateClientFor(admin);
        async Task<AdminSamReportRow> TriageAsync(object body) =>
            (await (await client.PostAsJsonAsync($"/api/admin/reports/sam/{reportId}/triage", body))
                .Content.ReadFromJsonAsync<AdminSamReportRow>())!;

        var linked = await TriageAsync(new { seenFlagCount = 1, status = "reviewed", issueNumber = 5 });
        (linked.Status, linked.IssueNumber).Should().Be((AdminReportCodes.StatusReviewed, 5));

        // A caller that read the report before the link was made saves a status only: the link stays.
        var statusOnly = await TriageAsync(new { seenFlagCount = 1, status = "dismissed" });
        (statusOnly.Status, statusOnly.IssueNumber).Should().Be((AdminReportCodes.StatusDismissed, 5));

        var nullIsLeftOut = await TriageAsync(new { seenFlagCount = 1, status = "deferred", issueNumber = (int?)null });
        (nullIsLeftOut.Status, nullIsLeftOut.IssueNumber).Should().Be((AdminReportCodes.StatusDeferred, 5));

        var issueOnly = await TriageAsync(new { seenFlagCount = 1, issueNumber = 9 });
        (issueOnly.Status, issueOnly.IssueNumber).Should().Be((AdminReportCodes.StatusDeferred, 9));

        var unlinked = await TriageAsync(new { seenFlagCount = 1, unlinkIssue = true });
        (unlinked.Status, unlinked.IssueNumber, unlinked.IssueUrl).Should().Be((AdminReportCodes.StatusDeferred, null, null));
    }

    [Fact]
    public async Task An_accepted_report_keeps_its_status_but_its_issue_can_still_be_changed()
    {
        var (admin, _, reportId) = await SeedOneAsync();
        using var client = _factory.CreateClientFor(admin);
        await client.PostAsJsonAsync($"/api/admin/reports/sam/{reportId}/triage",
            new AdminSamTriageRequest(1, AdminReportCodes.StatusAccepted, 5));

        foreach (var status in new[]
                 {
                     AdminReportCodes.StatusNew, AdminReportCodes.StatusReviewed,
                     AdminReportCodes.StatusDeferred, AdminReportCodes.StatusDismissed
                 })
        {
            var refused = await client.PostAsJsonAsync($"/api/admin/reports/sam/{reportId}/triage",
                new AdminSamTriageRequest(1, status, 6));

            refused.StatusCode.Should().Be(HttpStatusCode.Conflict);
            (await CodeOf(refused)).Should().Be(AdminFailureCodes.TransitionRefused);
        }

        await _factory.WithCoachDbAsync(async db =>
        {
            var row = await db.CoachOpportunities.AsNoTracking().SingleAsync();
            row.Status.Should().Be(CoachOpportunityStatus.Accepted);
            row.LinkedIssueNumber.Should().Be(5, "a refused change writes nothing");
        });

        (await client.PostAsJsonAsync($"/api/admin/reports/sam/{reportId}/triage",
            new AdminSamTriageRequest(1, AdminReportCodes.StatusAccepted, 6))).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("""{"seenFlagCount":1,"status":"Accepted"}""")]
    [InlineData("""{"seenFlagCount":1,"status":"closed"}""")]
    [InlineData("""{"status":"reviewed"}""")]
    [InlineData("""{"seenFlagCount":0,"status":"reviewed"}""")]
    [InlineData("""{"seenFlagCount":-1,"status":"reviewed"}""")]
    [InlineData("""{"seenFlagCount":null,"status":"reviewed"}""")]
    [InlineData("""{"seenFlagCount":1,"status":"reviewed","issueNumber":0}""")]
    [InlineData("""{"seenFlagCount":1,"status":"reviewed","issueNumber":-4}""")]
    [InlineData("""{"seenFlagCount":1,"status":"reviewed","issueNumber":10000000}""")]
    [InlineData("""{"seenFlagCount":1,"status":2}""")]
    [InlineData("""{"seenFlagCount":1,"unlinkIssue":true,"issueNumber":3}""")]
    [InlineData("""{"seenFlagCount":1,"status":"reviewed","unlinkIssue":"yes"}""")]
    [InlineData("""{}""")]
    [InlineData("""null""")]
    [InlineData("""not json""")]
    public async Task A_triage_body_that_is_not_exactly_right_changes_nothing(string body)
    {
        var (admin, _, reportId) = await SeedOneAsync();
        using var client = _factory.CreateClientFor(admin);

        var response = await client.PostAsync($"/api/admin/reports/sam/{reportId}/triage",
            new StringContent(body, System.Text.Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await CodeOf(response)).Should().Be(AdminFailureCodes.InvalidRequest);
        await _factory.WithCoachDbAsync(async db =>
        {
            var row = await db.CoachOpportunities.AsNoTracking().SingleAsync();
            row.Status.Should().Be(CoachOpportunityStatus.New);
            row.LinkedIssueNumber.Should().BeNull();
            row.ReviewedAtUtc.Should().BeNull();
        });
    }

    // ------------------------------------------------------------------ GitHub reports

    private async Task SeedGitHubReportAsync(ApplicationUser learner, string jti, int? issue, string? reference)
    {
        var now = _factory.Time.GetUtcNow().UtcDateTime;
        await _factory.WithFeedbackDbAsync(async db =>
        {
            db.FeedbackSubmissions.Add(new FeedbackSubmission
            {
                Jti = jti,
                UserProfileId = learner.UserProfileId!,
                Status = issue is null ? FeedbackSubmissionStatus.Claimed : FeedbackSubmissionStatus.Submitted,
                ContentDigest = new string('d', 64),
                IssueNumber = issue,
                IssueUrl = issue is null ? null : $"https://github.com/davidortinau/SentenceStudio/issues/{issue}",
                IssueTitle = issue is null ? null : "The quiz skipped a word",
                RouteCategory = FeedbackRouteCategory.Activity,
                Platform = FeedbackPlatform.Native,
                AppVersion = "1.4.2",
                CreatedAtUtc = issue is null ? now.AddMinutes(-5) : now,
                UpdatedAtUtc = now,
                TokenExpiresAtUtc = now.AddMinutes(10)
            });
            if (reference is not null)
            {
                db.FeedbackScreenSnapshots.Add(new FeedbackScreenSnapshot
                {
                    Reference = reference,
                    UserProfileId = learner.UserProfileId!,
                    Jti = jti,
                    ContextJson = """{"route":"/quiz/SECRET-WORD","state":{"word":"SECRET-STATE"}}""",
                    CreatedAtUtc = now
                });
            }

            await db.SaveChangesAsync();
        });
    }

    [Fact]
    public async Task The_GitHub_list_shows_each_report_with_its_public_issue_and_no_private_screen_state()
    {
        var admin = await _factory.SeedAdminAsync(AdminEmail);
        var learner = await _factory.SeedAccountAsync("learner@admin-tests.local");
        await SeedGitHubReportAsync(learner, "jti-filed-000000000001", 77, "fs-0123456789ab");
        await SeedGitHubReportAsync(learner, "jti-doubt-000000000002", null, null);
        using var client = _factory.CreateClientFor(admin);

        var body = await client.GetStringAsync("/api/admin/reports/github");
        var list = JsonSerializer.Deserialize<AdminGitHubReportList>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        list.Truncated.Should().BeFalse();
        list.Items.Should().HaveCount(2);
        list.Items[0].Should().BeEquivalentTo(new AdminGitHubReportRow(
            AdminReportCodes.GitHubSubmitted, 77, "https://github.com/davidortinau/SentenceStudio/issues/77",
            "The quiz skipped a word", null, learner.UserProfileId!, "native", "1.4.2", "activity",
            _factory.Time.GetUtcNow().UtcDateTime, "fs-0123456789ab"));
        list.Items[1].Status.Should().Be(AdminReportCodes.GitHubClaimed);
        list.Items[1].ScreenStateReference.Should().BeNull();
        list.Learners.Should().ContainSingle().Which.Email.Should().Be(learner.Email);

        body.Should().NotContain("SECRET").And.NotContain("jti-filed").And.NotContain("jti-doubt");
        (await _factory.ReadAuditAsync()).Should().ContainSingle(r => r.Action == AdminActions.ReportsGitHubList);
    }

    [Fact]
    public async Task The_saved_screen_state_needs_the_password_again_and_is_recorded_by_its_reference()
    {
        var admin = await _factory.SeedAdminAsync(AdminEmail);
        var learner = await _factory.SeedAccountAsync("learner@admin-tests.local");
        await SeedGitHubReportAsync(learner, "jti-filed-000000000001", 77, "fs-0123456789ab");
        using var client = _factory.CreateClientFor(admin);

        var refused = await client.PostAsync("/api/admin/reports/github/screen-states/fs-0123456789ab", null);
        refused.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await refused.Content.ReadAsStringAsync()).Should().NotContain("SECRET");

        await AdminApiFactory.ConfirmPasswordAsync(client);
        var state = (await (await client.PostAsync("/api/admin/reports/github/screen-states/fs-0123456789ab", null))
            .Content.ReadFromJsonAsync<AdminScreenStateResponse>())!;

        state.Reference.Should().Be("fs-0123456789ab");
        state.LearnerId.Should().Be(learner.UserProfileId);
        state.Context.GetProperty("route").GetString().Should().Be("/quiz/SECRET-WORD");
        state.Context.GetProperty("state").GetProperty("word").GetString().Should().Be("SECRET-STATE");

        (await client.PostAsync("/api/admin/reports/github/screen-states/fs-ffffffffffff", null))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);

        var rows = (await _factory.ReadAuditAsync()).Where(r => r.Action == AdminActions.ReportsScreenStateRead).ToList();
        rows.Select(r => (r.TargetId, r.StatusCode)).Should().Equal(
            ("fs-0123456789ab", 403), ("fs-0123456789ab", 200), ("fs-ffffffffffff", 404));
    }
}

/// <summary>
/// The descriptive codes the inbox writes for stored enums. Pinned whole: renaming a member of
/// any of these enums changes what the inbox page and the agent read, and must fail here first.
/// </summary>
public sealed class AdminReportCodeTests
{
    private static string[] All<TEnum>() where TEnum : struct, Enum =>
        [.. Enum.GetValues<TEnum>().Select(AdminReportCodes.Describe)];

    [Fact]
    public void Response_kinds()
    {
        string[] expected =
            ["text", "clarification", "suggestion", "receipt", "notice", "pedagogical_answer", "unrecognized"];

        All<CoachMessageKind>().Should().Equal(expected);
    }

    [Fact]
    public void Stop_reasons_and_turn_and_write_statuses_are_lowercase_words_joined_by_underscores()
    {
        foreach (var code in All<CoachStopReason>()
                     .Concat(All<CoachTurnOperationStatus>())
                     .Concat(All<CoachWriteOperationStatus>())
                     .Concat(All<CoachGroundingStage>())
                     .Concat(All<CoachLimitationCode>())
                     .Concat(All<FeedbackPlatform>())
                     .Concat(All<FeedbackRouteCategory>()))
        {
            code.Should().MatchRegex("^[a-z]+(_[a-z]+)*$");
        }

        AdminReportCodes.Describe(CoachStopReason.Completed).Should().Be("completed");
        AdminReportCodes.Describe(CoachStopReason.ClarificationRequested).Should().Be("clarification_requested");
        AdminReportCodes.Describe(CoachWriteOperationStatus.Rejected).Should().Be("rejected");
        AdminReportCodes.Describe(CoachGroundingStage.Observe).Should().Be("observe");
        AdminReportCodes.Describe(CoachLimitationCode.NotBuilt).Should().Be("not_built");
        AdminReportCodes.Describe(FeedbackPlatform.Native).Should().Be("native");
        AdminReportCodes.Describe(FeedbackRouteCategory.Activity).Should().Be("activity");
    }

    [Fact]
    public void A_stored_number_no_member_declares_is_unknown_and_an_absent_one_is_absent()
    {
        AdminReportCodes.Describe((CoachStopReason)999).Should().Be(AdminReportCodes.Unknown);
        AdminReportCodes.Describe((CoachGroundingStage?)null).Should().BeNull();
        AdminReportCodes.Of((CoachOpportunityStatus)999).Should().Be(AdminReportCodes.Unknown);
        AdminReportCodes.Of((CoachResponseReportReason?)null).Should().Be(AdminReportCodes.Unknown);
    }

    [Fact]
    public void Every_status_and_reason_has_its_own_code_and_statuses_read_back()
    {
        var statuses = Enum.GetValues<CoachOpportunityStatus>().Select(AdminReportCodes.Of).ToList();
        statuses.Should().Equal("new", "reviewed", "accepted", "deferred", "dismissed");
        foreach (var status in Enum.GetValues<CoachOpportunityStatus>())
        {
            AdminReportCodes.TryParseStatus(AdminReportCodes.Of(status), out var parsed).Should().BeTrue();
            parsed.Should().Be(status);
        }

        Enum.GetValues<CoachResponseReportReason>().Select(r => AdminReportCodes.Of(r)).Should().Equal(
            "did_not_answer", "incorrect_or_misleading", "expected_app_action", "confusing", "other");
        Enum.GetValues<FeedbackSubmissionStatus>().Select(AdminReportCodes.Of).Should().Equal(
            "claimed", "submitted", "failed", "committed");
    }

    [Fact]
    public void A_reason_and_its_capability_code_name_each_other()
    {
        foreach (var reason in Enum.GetValues<CoachResponseReportReason>())
        {
            CoachOpportunityCapabilityCodes.ReportReasonOf(CoachOpportunityCapabilityCodes.ForReportReason(reason))
                .Should().Be(reason);
        }

        CoachOpportunityCapabilityCodes.ReportReasonOf("referent_lost_after_offer").Should().BeNull();
        CoachOpportunityCapabilityCodes.ReportReasonOf(null).Should().BeNull();
    }
}
