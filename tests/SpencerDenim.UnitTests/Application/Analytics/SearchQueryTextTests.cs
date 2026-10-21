using SpencerDenim.Application.Abstractions.Auditing;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Application.Analytics.Search;
using SpencerDenim.Domain.Auditing;

namespace SpencerDenim.UnitTests.Application.Analytics;

/// <summary>
/// The privacy and normalisation rule for <c>analytics.SearchQueriesDaily</c> (doc 08 DB25; issue #78). The cases
/// that must be dropped and the cases that must be kept are the storefront's own (`recent-searches.service.spec.ts`,
/// `search-term.spec.ts`), because #78 requires the server to inherit the browser's corrected rule.
/// </summary>
public sealed class SearchQueryTextTests
{
    [Theory]
    [InlineData("jeans", "jeans")]
    [InlineData("  Linen   SHIRT  ", "linen shirt")]
    [InlineData("Black\tDenim\nJacket", "black denim jacket")]
    [InlineData("te\u0000st jeans", "te st jeans")]
    [InlineData("wide\u0085leg", "wide leg")]
    [InlineData("\u007Fsaree", "saree")]
    [InlineData("ab", "ab")]
    [InlineData("Kanjivaram (காஞ்சிபுரம்) Silk-Blend Saree", "kanjivaram (காஞ்சிபுரம்) silk-blend saree")]
    [InlineData("ඔසරිය", "ඔසරිය")]
    [InlineData("SIZE 10 DRESS", "size 10 dress")]
    [InlineData("order 123456", "order 123456")]
    [InlineData("32 waist jeans", "32 waist jeans")]
    public void A_countable_term_is_normalised_as_the_storefront_and_DB25_normalise_it(string raw, string expected) =>
        Assert.Equal(expected, SearchQueryText.Normalise(raw));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a")]
    [InlineData("a b")]
    [InlineData("a b c")]
    [InlineData("\u0000\u0001")]
    public void A_term_the_search_endpoint_would_refuse_is_not_counted(string? raw) =>
        Assert.Null(SearchQueryText.Normalise(raw));

    /// <summary>
    /// Was a defect (tester, 2026-10-21): neither a bidi override nor a zero-width character was stripped from a
    /// search term at all, so "linen‮" and "linen​" were both stored byte for byte. Dropped outright
    /// (not turned into a space, unlike a C0 control): they carry no word boundary of their own.
    /// </summary>
    [Theory]
    [InlineData("wide​leg", "wideleg")]
    [InlineData("wide﻿leg", "wideleg")]
    [InlineData("wi‮de leg", "wide leg")]
    [InlineData("‎‏linen", "linen")]
    [InlineData("⁦linen⁩", "linen")]
    [InlineData("؜linen", "linen")]
    public void A_bidi_control_or_an_invisible_formatting_character_is_dropped(string raw, string expected) =>
        Assert.Equal(expected, SearchQueryText.Normalise(raw));

    /// <summary>
    /// ZWJ and ZWNJ (U+200D, U+200C) are a different family entirely: Sinhala and Tamil conjuncts need them
    /// (#189 D1), and the same term is the one DB25 has to store for either shopper to see it offered again.
    /// </summary>
    [Fact]
    public void Zwj_and_zwnj_are_kept_unlike_the_bidi_and_invisible_characters_dropped_above()
    {
        var sri = new string([(char)0x0DC1, (char)0x0DCA, (char)0x200D, (char)0x0DBB, (char)0x0DD3]);
        var normalised = SearchQueryText.Normalise(sri);

        Assert.Equal(sri.ToLowerInvariant(), normalised);
        Assert.Contains((char)0x200D, normalised!);
    }

    /// <summary>
    /// Was a defect (tester, 2026-10-21): the privacy rule ran on the <b>truncated</b> term, so padding past the
    /// hundredth character pushed the email address clean out of the window the rule was checking. 88 characters
    /// of padding plus an address well past the 100-character column width must still be refused whole.
    /// </summary>
    [Fact]
    public void A_term_that_is_personal_only_past_the_hundred_character_cut_is_still_refused()
    {
        var padded = new string('a', 88) + " john.perera@example.com";

        Assert.True(padded.Length > SpencerDenim.Domain.Analytics.SearchQueryDaily.QueryMaxLength);
        Assert.Null(SearchQueryText.Normalise(padded));
    }

    [Fact]
    public void An_unpaired_surrogate_is_dropped_and_a_paired_one_kept()
    {
        Assert.Equal("linen", SearchQueryText.Normalise("linen\uD83D"));
        Assert.Equal("linen", SearchQueryText.Normalise("\uDC57linen"));
        Assert.Equal("dress 👗", SearchQueryText.Normalise("dress 👗"));
    }

    [Fact]
    public void A_long_term_is_cut_to_100_characters_never_through_a_surrogate_pair_and_with_no_trailing_space()
    {
        Assert.Equal(new string('x', 100), SearchQueryText.Normalise(new string('X', 250)));

        // 99 characters and then an emoji: the cut would land between its two halves, so the emoji goes whole.
        var withEmoji = new string('y', 99) + "👗tail";
        Assert.Equal(new string('y', 99), SearchQueryText.Normalise(withEmoji));

        // A cut that lands on a space does not leave one behind.
        var spaced = new string('z', 99) + " more";
        Assert.Equal(new string('z', 99), SearchQueryText.Normalise(spaced));
    }

    [Theory]
    [InlineData("someone@example.com")]
    [InlineData("contact someone@example.com please")]
    [InlineData("john @ example.com")]
    [InlineData("jane＠example.com")]
    [InlineData("jane﹫example.com")]
    [InlineData("@")]
    [InlineData("077 123 4567")]
    [InlineData("+94-77-123-4567")]
    [InlineData("0771234567")]
    [InlineData("call 077 1234567 about jeans")]
    [InlineData("٠٧٧١٢٣٤٥٦٧")]
    [InlineData("０７７１２３４５６７")]
    [InlineData("1234567")]
    [InlineData("levis 501 32x34")]
    [InlineData("SD-2026-000123")]
    [InlineData("where is sd-2026-12")]
    [InlineData("sd 2026 4")]
    [InlineData("INV-2026-000045")]
    [InlineData("inv2026 9")]
    public void A_term_that_looks_like_an_email_a_phone_number_or_an_order_number_is_never_counted(string raw)
    {
        Assert.Null(SearchQueryText.Normalise(raw));
        Assert.True(SearchQueryText.IsPersonal(raw.ToLowerInvariant()));
    }

    [Theory]
    [InlineData("123456")]
    [InlineData("size 10 dress")]
    [InlineData("sdk jeans 2026")]
    [InlineData("insider 2026")]
    public void Six_digits_and_a_word_that_merely_starts_like_a_prefix_are_kept(string term) =>
        Assert.False(SearchQueryText.IsPersonal(term));

    [Fact]
    public void A_digit_outside_the_basic_plane_counts_once()
    {
        // Osmanya digits (U+104A0..), two UTF-16 units each: six of them are six digits, not twelve.
        var six = string.Concat(Enumerable.Repeat("\U000104A1", 6));
        var seven = string.Concat(Enumerable.Repeat("\U000104A1", 7));

        Assert.False(SearchQueryText.IsPersonal(six));
        Assert.True(SearchQueryText.IsPersonal(seven));
    }

    [Theory]
    [InlineData("Mozilla/5.0 (compatible; Googlebot/2.1; +http://www.google.com/bot.html)", false)]
    [InlineData("curl/8.4.0", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/129.0.0.0 Safari/537.36", true)]
    public void A_bots_search_is_not_counted(string? userAgent, bool counted) =>
        Assert.Equal(counted ? "jeans" : null, SearchSubmissionClassifier.Accept("Jeans", userAgent, automated: false));

    [Fact]
    public void A_browser_driven_by_automation_is_a_bot() =>
        Assert.Null(SearchSubmissionClassifier.Accept(
            "jeans",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/129.0.0.0 Safari/537.36",
            automated: true));

    [Fact]
    public void The_window_is_today_and_the_29_days_before_it()
    {
        var (from, to) = PopularSearchRules.Window(new DateOnly(2026, 10, 21));

        Assert.Equal(new DateOnly(2026, 9, 22), from);
        Assert.Equal(new DateOnly(2026, 10, 21), to);
        Assert.Equal(PopularSearchRules.WindowDays - 1, to.DayNumber - from.DayNumber);
    }

    // ------------------------------------------------------------------ the hide list's handlers

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow { get; } = new(2026, 10, 21, 6, 30, 15, 250, TimeSpan.Zero);
    }

    private sealed class RecordingStore : IHiddenSearchTermStore
    {
        public List<(string Op, string Term, AuditLog Audit)> Calls { get; } = [];

        public bool Answer { get; set; } = true;

        public Task<bool> HideAsync(string term, DateTime hiddenAtUtc, AuditLog audit, CancellationToken cancellationToken)
        {
            Calls.Add(("hide", term, audit));
            return Task.FromResult(Answer);
        }

        public Task<bool> ShowAsync(string term, AuditLog audit, CancellationToken cancellationToken)
        {
            Calls.Add(("show", term, audit));
            return Task.FromResult(Answer);
        }
    }

    private static readonly AdminActor Staff = new(42, "staff@example.com", "203.0.113.9", "Browser", "trace-1");

    [Fact]
    public async Task Hiding_normalises_the_term_as_recording_does_and_audits_who_and_what()
    {
        var store = new RecordingStore();
        var handlers = new HiddenSearchTermHandlers(store, new FixedClock());

        var result = await handlers.HandleAsync(new HideSearchTermCommand("  Black   JEANS ", Staff), TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.True(result.Value);
        var (op, term, audit) = Assert.Single(store.Calls);
        Assert.Equal("hide", op);
        Assert.Equal("black jeans", term);
        Assert.Equal(AuditActions.Create, audit.Action);
        Assert.Equal(HiddenSearchTermHandlers.EntityType, audit.EntityType);
        Assert.Equal("black jeans", audit.EntityId);
        Assert.Equal(42, audit.UserId);
        Assert.Equal("staff@example.com", audit.UserEmail);
        Assert.Null(audit.BeforeJson);
        Assert.Contains("black jeans", audit.AfterJson, StringComparison.Ordinal);
        Assert.Equal(new FixedClock().UtcNow.UtcDateTime, audit.OccurredAtUtc);
    }

    [Fact]
    public async Task Showing_again_is_audited_as_a_delete_and_passes_on_whether_anything_changed()
    {
        var store = new RecordingStore { Answer = false };
        var handlers = new HiddenSearchTermHandlers(store, new FixedClock());

        var result = await handlers.HandleAsync(new ShowSearchTermCommand("Jeans", Staff), TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.False(result.Value);
        var (op, term, audit) = Assert.Single(store.Calls);
        Assert.Equal("show", op);
        Assert.Equal("jeans", term);
        Assert.Equal(AuditActions.Delete, audit.Action);
        Assert.Null(audit.AfterJson);
        Assert.Contains("jeans", audit.BeforeJson, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("a")]
    [InlineData("someone@example.com")]
    [InlineData("077 123 4567")]
    [InlineData("SD-2026-000123")]
    public async Task A_term_that_could_never_have_been_counted_is_refused_and_nothing_is_written(string? raw)
    {
        var store = new RecordingStore();
        var handlers = new HiddenSearchTermHandlers(store, new FixedClock());

        var hide = await handlers.HandleAsync(new HideSearchTermCommand(raw, Staff), TestContext.Current.CancellationToken);
        var show = await handlers.HandleAsync(new ShowSearchTermCommand(raw, Staff), TestContext.Current.CancellationToken);

        Assert.False(hide.IsSuccess);
        Assert.False(show.IsSuccess);
        Assert.Equal("term", Assert.Single(hide.Errors).Field);
        Assert.Empty(store.Calls);
    }

    [Fact]
    public async Task An_absurdly_long_term_is_refused_before_it_is_normalised()
    {
        var store = new RecordingStore();
        var handlers = new HiddenSearchTermHandlers(store, new FixedClock());

        var result = await handlers.HandleAsync(new HideSearchTermCommand(new string('x', 201), Staff), TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Empty(store.Calls);
    }

    private sealed class StubReader(IReadOnlyList<PopularSearchRow> rows) : IPopularSearchReader
    {
        public (DateOnly Today, int Minimum)? Asked { get; private set; }

        public Task<IReadOnlyList<PopularSearchRow>> GetPublicAsync(DateOnly today, int minimumSearches, CancellationToken cancellationToken)
        {
            Asked = (today, minimumSearches);
            return Task.FromResult(rows);
        }

        public Task<IReadOnlyList<PopularSearchRow>> GetFreshAsync(DateOnly today, int minimumSearches, CancellationToken cancellationToken)
        {
            Asked = (today, minimumSearches);
            return Task.FromResult(rows);
        }

        public Task<IReadOnlyList<HiddenSearchTermRow>> GetHiddenAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<HiddenSearchTermRow>>([new HiddenSearchTermRow("hidden", DateTime.UnixEpoch)]);
    }

    [Fact]
    public async Task The_public_list_is_terms_only_asked_for_the_colombo_day_with_the_configured_threshold()
    {
        var reader = new StubReader([new PopularSearchRow("jeans", 40), new PopularSearchRow("linen", 12)]);
        var handler = new GetPopularSearchesQueryHandler(reader, new SearchStatsOptions { PopularMinimumSearches = 7 }, new LateEveningClock());

        var dto = await handler.HandleAsync(new GetPopularSearchesQuery(), TestContext.Current.CancellationToken);

        Assert.Equal(["jeans", "linen"], dto.Terms);

        // 19:00 UTC is 00:30 the next day in Colombo.
        Assert.Equal((new DateOnly(2026, 10, 22), 7), reader.Asked);
    }

    [Fact]
    public async Task The_admin_list_carries_the_counts_the_hidden_terms_and_the_threshold()
    {
        var reader = new StubReader([new PopularSearchRow("jeans", 40)]);
        var handler = new GetAdminPopularSearchesQueryHandler(reader, new SearchStatsOptions { PopularMinimumSearches = 3 }, new FixedClock());

        var dto = await handler.HandleAsync(new GetAdminPopularSearchesQuery(), TestContext.Current.CancellationToken);

        Assert.Equal(40, Assert.Single(dto.Shown).Searches);
        Assert.Equal("hidden", Assert.Single(dto.Hidden).Term);
        Assert.Equal(3, dto.MinimumSearches);
    }

    private sealed class LateEveningClock : IClock
    {
        public DateTimeOffset UtcNow { get; } = new(2026, 10, 21, 19, 0, 0, TimeSpan.Zero);
    }
}
