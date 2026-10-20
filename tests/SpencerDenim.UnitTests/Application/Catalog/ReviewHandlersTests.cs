using SpencerDenim.Application.Abstractions.Auditing;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Application.Catalog.Reviews;
using SpencerDenim.Domain.Catalog;
using SpencerDenim.Infrastructure.Catalog.Reviews;

namespace SpencerDenim.UnitTests.Application.Catalog;

/// <summary>
/// The review use cases' own decisions (PDP-08, ADM-16): validation is all or nothing and reaches no store, the
/// approval switch is read and passed through, outcomes map to the message the customer sees, and a moderation
/// without a well-formed rowversion is refused before the store.
/// </summary>
public sealed class ReviewHandlersTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 6, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task An_invalid_review_never_reaches_the_store_or_the_settings()
    {
        var store = new RecordingStore();
        var settings = new FixedSettings(requireApproval: false);

        var result = await new ReviewWriteHandlers(store, settings, new FixedClock()).HandleAsync(
            new CreateReviewCommand(7, 3, 0, "One\nTwo", null),
            CancellationToken.None);

        var invalid = Assert.IsType<ReviewCommandResult.Invalid>(result);
        Assert.Equal(["rating", "title"], invalid.Errors.Select(error => error.Field));
        Assert.Empty(store.Writes);
        Assert.Equal(0, settings.Reads);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_approval_switch_reaches_the_store_with_the_cleaned_review_and_the_clock(bool requireApproval)
    {
        var store = new RecordingStore();

        await new ReviewWriteHandlers(store, new FixedSettings(requireApproval), new FixedClock()).HandleAsync(
            new EditReviewCommand(7, 3, 4, "  Lovely  ", "Fits well\r\n"),
            CancellationToken.None);

        var write = Assert.Single(store.Writes);
        Assert.Equal("edit", write.Kind);
        Assert.Equal(new ReviewWrite(7, 3, new CleanReview(4, "Lovely", "Fits well"), requireApproval, Now.UtcDateTime), write.Write);
    }

    [Theory]
    [InlineData(ReviewStatus.Pending, ReviewSubmissionOutcome.Pending)]
    [InlineData(ReviewStatus.Approved, ReviewSubmissionOutcome.Published)]
    [InlineData(ReviewStatus.Hidden, ReviewSubmissionOutcome.StillHidden)]
    public async Task A_saved_review_tells_the_customer_what_happened(ReviewStatus status, ReviewSubmissionOutcome expected)
    {
        var store = new RecordingStore { Next = new ReviewWriteOutcome.Saved(Mine(status)) };

        var result = await new ReviewWriteHandlers(store, new FixedSettings(true), new FixedClock()).HandleAsync(
            new CreateReviewCommand(7, 3, 5, null, null),
            CancellationToken.None);

        Assert.Equal(expected, Assert.IsType<ReviewCommandResult.Saved>(result).Submission.Outcome);
    }

    [Fact]
    public async Task Every_store_refusal_maps_to_its_own_result()
    {
        async Task<ReviewCommandResult> Run(ReviewWriteOutcome outcome) =>
            await new ReviewWriteHandlers(new RecordingStore { Next = outcome }, new FixedSettings(true), new FixedClock())
                .HandleAsync(new CreateReviewCommand(7, 3, 5, null, null), CancellationToken.None);

        Assert.IsType<ReviewCommandResult.ProductNotFound>(await Run(new ReviewWriteOutcome.ProductNotFound()));
        Assert.IsType<ReviewCommandResult.NotReviewed>(await Run(new ReviewWriteOutcome.NotReviewed()));
        Assert.IsType<ReviewCommandResult.SignedOut>(await Run(new ReviewWriteOutcome.NotACustomer()));
        Assert.Equal(9, Assert.IsType<ReviewCommandResult.AlreadyReviewed>(
            await Run(new ReviewWriteOutcome.AlreadyReviewed(Mine(ReviewStatus.Pending) with { Id = 9 }))).Existing.Id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not base64!")]
    [InlineData("AAAA")]              // three bytes
    [InlineData("AAAAAAAAAAAAAA==")]  // ten bytes
    public async Task A_moderation_without_an_eight_byte_rowversion_is_refused_before_the_store(string? rowVersion)
    {
        var store = new RecordingStore();

        var result = await new ModerateReviewHandler(store, new FixedClock()).HandleAsync(
            new ModerateReviewCommand(1, ReviewModeration.Approve, rowVersion, null, AdminActor.Unknown),
            CancellationToken.None);

        Assert.Equal("rowVersion", Assert.Single(Assert.IsType<ModerationResult.Invalid>(result).Errors).Field);
        Assert.Empty(store.Moderations);
    }

    [Fact]
    public async Task A_moderation_reaches_the_store_with_its_decoded_version_and_trimmed_note()
    {
        var store = new RecordingStore();
        var version = Convert.ToBase64String([0, 0, 0, 0, 0, 0, 7, 209]);

        await new ModerateReviewHandler(store, new FixedClock()).HandleAsync(
            new ModerateReviewCommand(4, ReviewModeration.Hide, version, "  Personal data  ", AdminActor.Unknown),
            CancellationToken.None);

        var request = Assert.Single(store.Moderations);
        Assert.Equal(4, request.ReviewId);
        Assert.Equal(ReviewModeration.Hide, request.Action);
        Assert.Equal(new byte[] { 0, 0, 0, 0, 0, 0, 7, 209 }, request.ExpectedRowVersion);
        Assert.Equal("Personal data", request.Note);
        Assert.Equal(Now.UtcDateTime, request.NowUtc);
    }

    [Fact]
    public async Task A_note_over_the_limit_is_refused_with_the_rowversion_check()
    {
        var result = await new ModerateReviewHandler(new RecordingStore(), new FixedClock()).HandleAsync(
            new ModerateReviewCommand(4, ReviewModeration.Hide, null, new string('n', 501), AdminActor.Unknown),
            CancellationToken.None);

        Assert.Equal(["rowVersion", "note"], Assert.IsType<ModerationResult.Invalid>(result).Errors.Select(error => error.Field));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("true", true)]
    [InlineData("True", true)]
    [InlineData("false", false)]
    [InlineData(" FALSE ", false)]
    [InlineData("0", true)]
    [InlineData("no", true)]
    public void Anything_but_a_readable_false_means_reviews_wait(string? stored, bool expected) =>
        Assert.Equal(expected, ReviewSettingsReader.Interpret(stored));

    [Fact]
    public async Task The_admin_list_trims_and_caps_the_search_and_clamps_the_page()
    {
        var reader = new RecordingReader();

        await new ReviewQueryHandlers(reader).HandleAsync(
            new GetAdminReviewsQuery(AdminReviewStatusFilter.All, true, "  " + new string('s', 150) + " ", 0, 1000),
            CancellationToken.None);

        var criteria = Assert.Single(reader.AdminCriteria);
        Assert.Equal(AdminReviewCriteria.MaxSearchLength, criteria.Search!.Length);
        Assert.Equal(1, criteria.Page);
        Assert.Equal(AdminReviewCriteria.MaxPageSize, criteria.PageSize);

        await new ReviewQueryHandlers(reader).HandleAsync(
            new GetAdminReviewsQuery(AdminReviewStatusFilter.Pending, null, "   ", null, null),
            CancellationToken.None);

        Assert.Null(reader.AdminCriteria[1].Search);
        Assert.Equal(AdminReviewCriteria.DefaultPageSize, reader.AdminCriteria[1].PageSize);
    }

    private static MyReviewDto Mine(ReviewStatus status) =>
        new(1, 3, 5, null, null, "Nimal P.", false, status, Now.UtcDateTime, null);

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    private sealed class FixedSettings(bool requireApproval) : IReviewSettingsReader
    {
        public int Reads { get; private set; }

        public Task<bool> RequireApprovalAsync(CancellationToken cancellationToken)
        {
            Reads++;
            return Task.FromResult(requireApproval);
        }
    }

    private sealed class RecordingStore : IProductReviewStore
    {
        public List<(string Kind, ReviewWrite Write)> Writes { get; } = [];

        public List<ReviewModerationRequest> Moderations { get; } = [];

        public ReviewWriteOutcome? Next { get; init; }

        public Task<ReviewWriteOutcome> CreateAsync(ReviewWrite write, CancellationToken cancellationToken)
        {
            Writes.Add(("create", write));
            return Task.FromResult(Next ?? new ReviewWriteOutcome.Saved(Mine(ReviewStatus.Pending)));
        }

        public Task<ReviewWriteOutcome> EditAsync(ReviewWrite write, CancellationToken cancellationToken)
        {
            Writes.Add(("edit", write));
            return Task.FromResult(Next ?? new ReviewWriteOutcome.Saved(Mine(ReviewStatus.Pending)));
        }

        public Task<ReviewModerationOutcome> ModerateAsync(ReviewModerationRequest request, CancellationToken cancellationToken)
        {
            Moderations.Add(request);
            return Task.FromResult<ReviewModerationOutcome>(new ReviewModerationOutcome.NotFound());
        }
    }

    private sealed class RecordingReader : IProductReviewReader
    {
        public List<AdminReviewCriteria> AdminCriteria { get; } = [];

        public Task<ProductReviewPageDto?> ListPublishedAsync(string slug, int page, int pageSize, CancellationToken cancellationToken) =>
            Task.FromResult<ProductReviewPageDto?>(null);

        public Task<MyProductReviewDto?> MineAsync(int customerUserId, int productId, CancellationToken cancellationToken) =>
            Task.FromResult<MyProductReviewDto?>(null);

        public Task<AdminReviewListPage> ListForAdminAsync(AdminReviewCriteria criteria, CancellationToken cancellationToken)
        {
            AdminCriteria.Add(criteria);
            return Task.FromResult(new AdminReviewListPage([], criteria.Page, criteria.PageSize, 0, new ReviewStatusCountsDto(0, 0, 0)));
        }
    }
}
