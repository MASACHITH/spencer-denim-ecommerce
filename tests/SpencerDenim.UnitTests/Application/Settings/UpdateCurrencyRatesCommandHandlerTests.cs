using SpencerDenim.Application.Abstractions.Auditing;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Application.Admin.CurrentUser;
using SpencerDenim.Application.Settings;
using SpencerDenim.Application.Settings.Abstractions;
using SpencerDenim.Application.Settings.Admin;
using SpencerDenim.Domain.Auditing;
using SpencerDenim.Domain.Identity;
using SpencerDenim.Domain.Settings;

namespace SpencerDenim.UnitTests.Application.Settings;

/// <summary>
/// The display-currency rates' write path (issue #248): the number rules, absent versus empty, the canonical stored
/// form, the order of the checks, one audit row per changed key. The real store is covered by
/// <c>AdminCurrencyRatesTests</c>; this pins the rules in milliseconds.
/// </summary>
public sealed class UpdateCurrencyRatesCommandHandlerTests
{
    private const string Usd = "usd";
    private const string Gbp = "gbp";
    private const string Eur = "eur";

    private static readonly AdminActor Actor = new(7, "staff@example.com", null, null, null);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // ---- the happy path and the audit trail -----------------------------------------------------------

    [Fact]
    public async Task Saving_rates_stores_the_canonical_text_and_writes_one_audit_row_per_changed_key()
    {
        var store = new FakeSettingsStore();
        var audit = new RecordingAuditLog();

        var result = await Handler(store, audit).HandleAsync(
            Command(store, (Usd, "321.5"), (Gbp, "399.50")), Token);

        var succeeded = Assert.IsType<SettingsWriteResult.Succeeded>(result);
        Assert.Equal([SettingKeys.CurrencyDisplay.UsdLkrPerUnit, SettingKeys.CurrencyDisplay.GbpLkrPerUnit], succeeded.ChangedKeys);
        Assert.Equal("321.5", store.Rows[SettingKeys.CurrencyDisplay.UsdLkrPerUnit].Value);
        Assert.Equal("399.5", store.Rows[SettingKeys.CurrencyDisplay.GbpLkrPerUnit].Value);
        Assert.Equal(7, store.Rows[SettingKeys.CurrencyDisplay.UsdLkrPerUnit].UpdatedByUserId);
        Assert.Equal(new DateTime(2026, 10, 24, 9, 0, 0, DateTimeKind.Utc), store.Rows[SettingKeys.CurrencyDisplay.UsdLkrPerUnit].UpdatedAtUtc);
        Assert.Equal(1, store.Saves);

        Assert.Equal(2, audit.Rows.Count);
        Assert.All(audit.Rows, row => Assert.Equal("Setting", row.EntityType));
        Assert.Equal(
            [SettingKeys.CurrencyDisplay.UsdLkrPerUnit, SettingKeys.CurrencyDisplay.GbpLkrPerUnit],
            audit.Rows.Select(row => row.EntityId));
        Assert.Contains("\"399.5\"", audit.Rows[1].AfterJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Leading_and_trailing_spaces_are_trimmed_before_the_number_is_read()
    {
        var store = new FakeSettingsStore();

        var result = await Handler(store).HandleAsync(Command(store, (Usd, "  321.5 ")), Token);

        Assert.IsType<SettingsWriteResult.Succeeded>(result);
        Assert.Equal("321.5", store.Rows[SettingKeys.CurrencyDisplay.UsdLkrPerUnit].Value);
    }

    /// <summary>
    /// A rate typed as 399.50 is the rate 399.5: "did this change" is asked of the canonical text, so repeating a
    /// stored rate writes no audit row, no statement and does not move the rate's age.
    /// </summary>
    [Fact]
    public async Task Saving_the_rate_already_stored_in_another_spelling_is_unchanged_with_no_audit_row_and_no_save()
    {
        var store = new FakeSettingsStore();
        store.Rows[SettingKeys.CurrencyDisplay.GbpLkrPerUnit].Value = "399.5";
        var audit = new RecordingAuditLog();

        var result = await Handler(store, audit).HandleAsync(Command(store, (Gbp, "399.50")), Token);

        Assert.IsType<SettingsWriteResult.Unchanged>(result);
        Assert.Empty(audit.Rows);
        Assert.Equal(0, store.Saves);
        Assert.Null(store.Rows[SettingKeys.CurrencyDisplay.GbpLkrPerUnit].UpdatedAtUtc);
    }

    [Fact]
    public async Task A_save_that_changes_one_rate_writes_one_audit_row_and_leaves_the_others_untouched()
    {
        var store = new FakeSettingsStore();
        store.Rows[SettingKeys.CurrencyDisplay.UsdLkrPerUnit].Value = "321.5";
        var audit = new RecordingAuditLog();

        var result = await Handler(store, audit).HandleAsync(Command(store, (Usd, "321.5"), (Eur, "345")), Token);

        var succeeded = Assert.IsType<SettingsWriteResult.Succeeded>(result);
        Assert.Equal([SettingKeys.CurrencyDisplay.EurLkrPerUnit], succeeded.ChangedKeys);
        Assert.Single(audit.Rows);
        Assert.Null(store.Rows[SettingKeys.CurrencyDisplay.UsdLkrPerUnit].UpdatedAtUtc);
    }

    [Fact]
    public async Task A_field_the_request_leaves_out_is_not_written()
    {
        var store = new FakeSettingsStore();
        store.Rows[SettingKeys.CurrencyDisplay.UsdLkrPerUnit].Value = "321.5";

        var result = await Handler(store).HandleAsync(Command(store, (Gbp, "399.5")), Token);

        Assert.IsType<SettingsWriteResult.Succeeded>(result);
        Assert.Equal("321.5", store.Rows[SettingKeys.CurrencyDisplay.UsdLkrPerUnit].Value);
        Assert.Equal("399.5", store.Rows[SettingKeys.CurrencyDisplay.GbpLkrPerUnit].Value);
    }

    [Fact]
    public async Task An_empty_request_changes_nothing()
    {
        var store = new FakeSettingsStore();

        var result = await Handler(store).HandleAsync(Command(store), Token);

        Assert.IsType<SettingsWriteResult.Unchanged>(result);
        Assert.Equal(0, store.Saves);
    }

    [Theory]
    [MemberData(nameof(EveryCurrency))]
    public async Task Each_field_writes_its_own_currencys_row_and_no_other(string code, string field, string key)
    {
        var store = new FakeSettingsStore();

        var result = await Handler(store).HandleAsync(Command(store, (field, "123.4567")), Token);

        var succeeded = Assert.IsType<SettingsWriteResult.Succeeded>(result);
        Assert.Equal([key], succeeded.ChangedKeys);
        Assert.Equal("123.4567", store.Rows[key].Value);
        Assert.True(
            DisplayCurrencies.All.Count(currency => store.Rows[currency.SettingKey].Value is null) == 4,
            $"{code}'s save changed another currency's row.");
    }

    public static TheoryData<string, string, string> EveryCurrency()
    {
        var data = new TheoryData<string, string, string>();

        foreach (var currency in DisplayCurrencies.All)
        {
            data.Add(currency.Code, currency.Field, currency.SettingKey);
        }

        return data;
    }

    // ---- clearing: an empty field retires a currency ---------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task A_field_sent_empty_clears_the_rate_and_audits_it(string? empty)
    {
        var store = new FakeSettingsStore();
        store.Rows[SettingKeys.CurrencyDisplay.AudLkrPerUnit].Value = "210.25";
        var audit = new RecordingAuditLog();

        var result = await Handler(store, audit).HandleAsync(Command(store, ("aud", empty)), Token);

        var succeeded = Assert.IsType<SettingsWriteResult.Succeeded>(result);
        Assert.Equal([SettingKeys.CurrencyDisplay.AudLkrPerUnit], succeeded.ChangedKeys);
        Assert.Null(store.Rows[SettingKeys.CurrencyDisplay.AudLkrPerUnit].Value);
        Assert.NotNull(store.Rows[SettingKeys.CurrencyDisplay.AudLkrPerUnit].UpdatedAtUtc);
        var row = Assert.Single(audit.Rows);
        Assert.Contains("\"210.25\"", row.BeforeJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Clearing_a_rate_that_is_not_set_is_unchanged()
    {
        var store = new FakeSettingsStore();

        var result = await Handler(store).HandleAsync(Command(store, ("aed", string.Empty)), Token);

        Assert.IsType<SettingsWriteResult.Unchanged>(result);
        Assert.Equal(0, store.Saves);
    }

    // ---- the number rules -------------------------------------------------------------------------------

    [Theory]
    [InlineData("0.0001")]
    [InlineData("1")]
    [InlineData("1000000")]
    [InlineData("1000000.0000")]
    [InlineData("0399.5")]
    public async Task The_edges_of_the_range_are_accepted(string value)
    {
        var store = new FakeSettingsStore();

        var result = await Handler(store).HandleAsync(Command(store, (Usd, value)), Token);

        Assert.IsType<SettingsWriteResult.Succeeded>(result);
    }

    [Theory]
    [InlineData("0.0001", "0.0001")]
    [InlineData("1000000.0000", "1000000")]
    [InlineData("0399.5", "399.5")]
    [InlineData("82.2750", "82.275")]
    public async Task The_stored_form_is_canonical(string typed, string stored)
    {
        var store = new FakeSettingsStore();

        await Handler(store).HandleAsync(Command(store, (Usd, typed)), Token);

        Assert.Equal(stored, store.Rows[SettingKeys.CurrencyDisplay.UsdLkrPerUnit].Value);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("-5")]
    [InlineData("+5")]
    [InlineData("1e3")]
    [InlineData("1E3")]
    [InlineData("1,000")]
    [InlineData("1 000")]
    [InlineData("39 9.5")]
    [InlineData("399,5")]
    [InlineData(".5")]
    [InlineData("5.")]
    [InlineData(".")]
    [InlineData("1.2.3")]
    [InlineData("0x10")]
    [InlineData("Rs. 399")]
    [InlineData("399.5 LKR")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    public async Task Anything_that_is_not_plain_invariant_digits_is_refused_on_that_field(string value)
    {
        var store = new FakeSettingsStore();

        var result = await Handler(store).HandleAsync(Command(store, (Gbp, value)), Token);

        AssertRefused(result, Gbp);
        AssertUntouched(store);
    }

    /// <summary>
    /// Arabic-Indic and fullwidth digits are digits to <c>char.IsDigit</c> and to a culture-aware parse; they are
    /// not what the form sends, and a rate written in them would read differently on another machine.
    /// </summary>
    [Theory]
    [InlineData("٣٩٩")]
    [InlineData("３９９")]
    public async Task Digits_of_other_scripts_are_refused(string value)
    {
        var store = new FakeSettingsStore();

        var result = await Handler(store).HandleAsync(Command(store, (Gbp, value)), Token);

        AssertRefused(result, Gbp);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("0.0")]
    [InlineData("0.0000")]
    [InlineData("1000000.0001")]
    [InlineData("1000001")]
    [InlineData("99999999999999999999999999999999")]
    public async Task A_rate_that_is_zero_or_too_large_is_refused_with_the_range_in_the_message(string value)
    {
        var store = new FakeSettingsStore();

        var result = await Handler(store).HandleAsync(Command(store, (Eur, value)), Token);

        var error = AssertRefused(result, Eur);
        Assert.Contains("1,000,000", error.Message, StringComparison.Ordinal);
        AssertUntouched(store);
    }

    [Theory]
    [InlineData("399.12345")]
    [InlineData("1.00001")]
    [InlineData("0.00001")]
    public async Task More_than_four_decimal_places_is_refused_and_says_so(string value)
    {
        var store = new FakeSettingsStore();

        var result = await Handler(store).HandleAsync(Command(store, (Usd, value)), Token);

        var error = AssertRefused(result, Usd);
        Assert.Contains("at most 4 decimal places", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Every_bad_field_is_reported_in_one_answer_and_nothing_is_written()
    {
        var store = new FakeSettingsStore();

        var result = await Handler(store).HandleAsync(
            Command(store, (Usd, "abc"), (Gbp, "399.5"), (Eur, "0")), Token);

        var invalid = Assert.IsType<SettingsWriteResult.Invalid>(result);
        Assert.Equal([Usd, Eur], invalid.Errors.Select(error => error.Field));
        AssertUntouched(store);
    }

    // ---- the order of the checks ------------------------------------------------------------------------

    [Fact]
    public async Task A_field_error_is_reported_before_the_token_is_looked_at()
    {
        var store = new FakeSettingsStore();

        var result = await Handler(store).HandleAsync(
            new UpdateCurrencyRatesCommand(Fields((Usd, "nope")), null, Actor), Token);

        AssertRefused(result, Usd);
    }

    [Fact]
    public async Task A_missing_token_is_a_conflict_and_nothing_is_written()
    {
        var store = new FakeSettingsStore();

        var result = await Handler(store).HandleAsync(
            new UpdateCurrencyRatesCommand(Fields((Usd, "321.5")), null, Actor), Token);

        var conflict = Assert.IsType<SettingsWriteResult.Conflict>(result);
        Assert.Equal(SettingsConflictCodes.StaleRowVersion, conflict.Code);
        AssertUntouched(store);
    }

    /// <summary>One token covers all five rows: a change to the pound makes a screen that read before it stale.</summary>
    [Fact]
    public async Task A_stale_token_is_a_conflict_even_when_the_other_currency_was_the_one_that_changed()
    {
        var store = new FakeSettingsStore();
        var token = TokenOf(store);
        await Handler(store).HandleAsync(Command(store, (Gbp, "399.5")), Token);

        var result = await Handler(store).HandleAsync(
            new UpdateCurrencyRatesCommand(Fields((Usd, "321.5")), token, Actor), Token);

        Assert.IsType<SettingsWriteResult.Conflict>(result);
        Assert.Null(store.Rows[SettingKeys.CurrencyDisplay.UsdLkrPerUnit].Value);
    }

    [Fact]
    public async Task A_database_without_the_rows_says_so_rather_than_failing_later()
    {
        var store = new FakeSettingsStore();
        var command = Command(store, (Usd, "321.5"));
        store.Rows.Remove(SettingKeys.CurrencyDisplay.AedLkrPerUnit);

        var result = await Handler(store).HandleAsync(command, Token);

        var notSeeded = Assert.IsType<SettingsWriteResult.NotSeeded>(result);
        Assert.Contains(SettingKeys.CurrencyDisplay.AedLkrPerUnit, notSeeded.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_concurrent_save_that_loses_is_a_conflict()
    {
        var store = new FakeSettingsStore { NextSaveOutcome = SettingsSaveOutcome.ConcurrencyConflict };

        var result = await Handler(store).HandleAsync(Command(store, (Usd, "321.5")), Token);

        Assert.IsType<SettingsWriteResult.Conflict>(result);
    }

    // ---- permission -------------------------------------------------------------------------------------

    [Fact]
    public async Task A_caller_without_settings_edit_is_forbidden_and_nothing_is_written()
    {
        var store = new FakeSettingsStore();

        var result = await Handler(store, permissions: [Permissions.Settings.Payments, Permissions.Settings.View])
            .HandleAsync(Command(store, (Usd, "321.5")), Token);

        var forbidden = Assert.IsType<SettingsWriteResult.Forbidden>(result);
        Assert.Equal([SettingKeys.CurrencyDisplay.UsdLkrPerUnit], forbidden.Keys);
        AssertUntouched(store);
    }

    [Fact]
    public async Task A_caller_who_may_edit_settings_is_allowed_without_grants_all()
    {
        var store = new FakeSettingsStore();

        var result = await Handler(store, permissions: [Permissions.Settings.Edit])
            .HandleAsync(Command(store, (Usd, "321.5")), Token);

        Assert.IsType<SettingsWriteResult.Succeeded>(result);
    }

    [Fact]
    public async Task A_request_with_no_acting_user_is_forbidden()
    {
        var store = new FakeSettingsStore();
        var anonymous = new AdminActor(null, null, null, null, null);

        var result = await Handler(store).HandleAsync(
            new UpdateCurrencyRatesCommand(Fields((Usd, "321.5")), TokenOf(store), anonymous), Token);

        Assert.IsType<SettingsWriteResult.Forbidden>(result);
    }

    /// <summary>
    /// <b>The permission is checked for every currency, one at a time.</b> The first version of this suite proved it
    /// for the US dollar only, and a mutation that skipped the check for the dirham alone survived 187 unit and 36
    /// integration tests: a per-currency skip is exactly what a loop over the table can get wrong.
    /// </summary>
    [Theory]
    [MemberData(nameof(EveryCurrency))]
    public async Task Each_currency_is_permission_checked_on_its_own(string code, string field, string key)
    {
        var store = new FakeSettingsStore();

        var result = await Handler(store, permissions: [Permissions.Settings.Payments, Permissions.Settings.View])
            .HandleAsync(Command(store, (field, "123.5")), Token);

        var forbidden = Assert.IsType<SettingsWriteResult.Forbidden>(result);
        Assert.Equal([key], forbidden.Keys);
        Assert.True(store.Rows[key].Value is null, $"{code} was written by a caller without Settings.Edit.");
        Assert.Equal(0, store.Saves);
    }

    [Theory]
    [MemberData(nameof(EveryCurrency))]
    public async Task Each_currency_is_allowed_for_a_caller_who_may_edit_settings(string code, string field, string key)
    {
        var store = new FakeSettingsStore();

        var result = await Handler(store, permissions: [Permissions.Settings.Edit])
            .HandleAsync(Command(store, (field, "123.5")), Token);

        var succeeded = Assert.IsType<SettingsWriteResult.Succeeded>(result);
        Assert.True(succeeded.ChangedKeys.SequenceEqual([key]), $"{code} was not written for a Settings.Edit holder.");
    }

    [Fact]
    public async Task A_save_of_all_five_by_a_caller_without_permission_names_all_five_keys()
    {
        var store = new FakeSettingsStore();

        var result = await Handler(store, permissions: [Permissions.Settings.Payments])
            .HandleAsync(
                Command(store, (Usd, "1"), (Gbp, "2"), (Eur, "3"), ("aud", "4"), ("aed", "5")),
                Token);

        var forbidden = Assert.IsType<SettingsWriteResult.Forbidden>(result);
        Assert.Equal(DisplayCurrencies.All.Select(currency => currency.SettingKey), forbidden.Keys);
    }

    // ---- the words staff read (m3) ----------------------------------------------------------------------

    /// <summary>
    /// The names are written out in full where they are used ("pound sterling", "UAE dirham"), not lower-cased from
    /// a capitalised name, which is how "us dollar" and "uae dirham" reached a staff member.
    /// </summary>
    [Theory]
    [InlineData("usd", "US dollar", "USD")]
    [InlineData("gbp", "pound sterling", "GBP")]
    [InlineData("eur", "euro", "EUR")]
    [InlineData("aud", "Australian dollar", "AUD")]
    [InlineData("aed", "UAE dirham", "AED")]
    public async Task The_messages_name_each_currency_properly(string field, string name, string code)
    {
        var store = new FakeSettingsStore();

        var notANumber = AssertRefused(await Handler(store).HandleAsync(Command(store, (field, "abc")), Token), field);
        var outOfRange = AssertRefused(await Handler(store).HandleAsync(Command(store, (field, "0")), Token), field);

        Assert.Equal(
            $"Enter the {name} rate as digits with an optional decimal point, such as 399.5: no signs, commas, "
            + $"spaces or letters. Leave it empty to stop offering {code}.",
            notANumber.Message);
        Assert.Equal(
            $"Use a rate above 0 and no more than 1,000,000. It is how many rupees one {name} is worth.",
            outOfRange.Message);
    }

    /// <summary>Two of the five spelled out in full, so the template cannot be wrong in both places at once.</summary>
    [Fact]
    public async Task The_exact_words_for_the_two_names_that_used_to_be_lower_cased()
    {
        var store = new FakeSettingsStore();

        var dollar = AssertRefused(await Handler(store).HandleAsync(Command(store, (Usd, "x")), Token), Usd);
        var dirham = AssertRefused(await Handler(store).HandleAsync(Command(store, ("aed", "-3")), Token), "aed");

        Assert.Equal(
            "Enter the US dollar rate as digits with an optional decimal point, such as 399.5: no signs, commas, "
            + "spaces or letters. Leave it empty to stop offering USD.",
            dollar.Message);
        Assert.Equal(
            "Enter the UAE dirham rate as digits with an optional decimal point, such as 399.5: no signs, commas, "
            + "spaces or letters. Leave it empty to stop offering AED.",
            dirham.Message);
        Assert.DoesNotContain("us dollar", dollar.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("uae dirham", dirham.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("399.12345")]
    [InlineData("0.00010")]
    [InlineData("1.50000")]
    public async Task More_than_four_places_is_refused_even_when_the_extra_places_are_zeros(string value)
    {
        var store = new FakeSettingsStore();

        var error = AssertRefused(await Handler(store).HandleAsync(Command(store, (Usd, value)), Token), Usd);

        Assert.Equal("Use at most 4 decimal places.", error.Message);
    }

    // ---- the feed's cache is dropped by a real write and only by one ------------------------------------

    [Fact]
    public async Task A_successful_save_evicts_the_feed_cache_once()
    {
        var store = new FakeSettingsStore();
        var cache = new FakeCache();

        var result = await Handler(store, cache: cache).HandleAsync(Command(store, (Usd, "301.5"), (Gbp, "399.5")), Token);

        Assert.IsType<SettingsWriteResult.Succeeded>(result);
        Assert.Equal(1, cache.Evictions);
    }

    [Fact]
    public async Task Clearing_a_rate_evicts_the_cache_too()
    {
        var store = new FakeSettingsStore();
        store.Rows[SettingKeys.CurrencyDisplay.UsdLkrPerUnit].Value = "301.5";
        var cache = new FakeCache();

        await Handler(store, cache: cache).HandleAsync(Command(store, (Usd, string.Empty)), Token);

        Assert.Equal(1, cache.Evictions);
    }

    /// <summary>A save that wrote nothing changed nothing the shop shows, so nothing is dropped.</summary>
    [Fact]
    public async Task An_unchanged_save_does_not_evict()
    {
        var store = new FakeSettingsStore();
        store.Rows[SettingKeys.CurrencyDisplay.UsdLkrPerUnit].Value = "301.5";
        var cache = new FakeCache();

        var result = await Handler(store, cache: cache).HandleAsync(Command(store, (Usd, "301.50")), Token);

        Assert.IsType<SettingsWriteResult.Unchanged>(result);
        Assert.Equal(0, cache.Evictions);
    }

    [Fact]
    public async Task A_refused_save_does_not_evict_whatever_the_reason()
    {
        var cache = new FakeCache();

        var invalid = new FakeSettingsStore();
        Assert.IsType<SettingsWriteResult.Invalid>(
            await Handler(invalid, cache: cache).HandleAsync(Command(invalid, (Usd, "abc")), Token));

        var forbidden = new FakeSettingsStore();
        Assert.IsType<SettingsWriteResult.Forbidden>(
            await Handler(forbidden, permissions: [Permissions.Settings.Payments], cache: cache)
                .HandleAsync(Command(forbidden, (Usd, "301.5")), Token));

        var stale = new FakeSettingsStore();
        Assert.IsType<SettingsWriteResult.Conflict>(
            await Handler(stale, cache: cache).HandleAsync(
                new UpdateCurrencyRatesCommand(Fields((Usd, "301.5")), null, Actor), Token));

        var lost = new FakeSettingsStore { NextSaveOutcome = SettingsSaveOutcome.ConcurrencyConflict };
        Assert.IsType<SettingsWriteResult.Conflict>(
            await Handler(lost, cache: cache).HandleAsync(Command(lost, (Usd, "301.5")), Token));

        Assert.Equal(0, cache.Evictions);
    }

    // ---- the contract with the endpoint -----------------------------------------------------------------

    [Fact]
    public void The_fields_the_endpoint_reads_are_the_five_lower_case_codes_in_the_offered_order()
    {
        Assert.Equal(["usd", "gbp", "eur", "aud", "aed"], UpdateCurrencyRatesCommandHandler.Fields);
    }

    // ---- helpers ----------------------------------------------------------------------------------------

    private static UpdateCurrencyRatesCommandHandler Handler(
        FakeSettingsStore store,
        RecordingAuditLog? audit = null,
        string[]? permissions = null,
        FakeCache? cache = null) =>
        new(
            store,
            audit ?? new RecordingAuditLog(),
            new FakePermissions(permissions is null, permissions ?? []),
            new FixedClock(),
            cache ?? new FakeCache());

    private static UpdateCurrencyRatesCommand Command(FakeSettingsStore store, params (string Field, string? Value)[] fields) =>
        new(Fields(fields), TokenOf(store), Actor);

    private static Dictionary<string, string?> Fields(params (string Field, string? Value)[] fields) =>
        fields.ToDictionary(field => field.Field, field => field.Value, StringComparer.Ordinal);

    private static string TokenOf(FakeSettingsStore store) =>
        SettingsGroupVersion.For(
            SettingCatalog.KeysInSaveUnitFor(SettingKeys.CurrencyDisplay.Group).Select(key => store.Rows[key]));

    private static SpencerDenim.Application.Abstractions.Results.ValidationError AssertRefused(
        SettingsWriteResult result,
        string field)
    {
        var invalid = Assert.IsType<SettingsWriteResult.Invalid>(result);
        var error = Assert.Single(invalid.Errors);
        Assert.Equal(field, error.Field);
        Assert.False(string.IsNullOrWhiteSpace(error.Message));
        return error;
    }

    private static void AssertUntouched(FakeSettingsStore store)
    {
        Assert.All(DisplayCurrencies.All, currency => Assert.Null(store.Rows[currency.SettingKey].Value));
        Assert.Equal(0, store.Saves);
    }

    /// <summary>
    /// Seeded as <c>SettingsSeeder</c> seeds the real table (every catalogued key, with its seed value, which is
    /// null for the rates) and no more forgiving than the real store: it hands back the instances the handler
    /// mutates and moves a row version on save.
    /// </summary>
    private sealed class FakeSettingsStore : ISettingsStore
    {
        public FakeSettingsStore()
        {
            var version = (byte)1;

            foreach (var definition in SettingCatalog.All)
            {
                Rows[definition.Key] = new Setting
                {
                    Key = definition.Key,
                    GroupKey = definition.GroupKey,
                    ValueType = definition.ValueType,
                    IsSecret = definition.IsSecret,
                    RequiredPermission = definition.RequiredPermission,
                    Description = definition.Description,
                    Value = definition.SeedValue,
                    RowVersion = [0, 0, 0, 0, 0, 0, 0, version++],
                };
            }
        }

        public Dictionary<string, Setting> Rows { get; } = new(StringComparer.OrdinalIgnoreCase);

        public int Saves { get; private set; }

        public SettingsSaveOutcome? NextSaveOutcome { get; set; }

        public Task<IReadOnlyList<Setting>> ListAsync(IReadOnlyCollection<string> keys, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Setting>>(keys.Where(Rows.ContainsKey).Select(key => Rows[key]).ToList());

        public Task<SettingImageAsset?> FindUsableImageAsync(Guid publicId, CancellationToken cancellationToken) =>
            Task.FromResult<SettingImageAsset?>(null);

        public Task<SettingImageAsset?> FindUsableImageByIdAsync(int assetId, CancellationToken cancellationToken) =>
            Task.FromResult<SettingImageAsset?>(null);

        public Task ReplaceMediaUsageAsync(string settingKey, string usageRole, int? newAssetId, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<SettingsSaveOutcome> SaveAsync(int? actingUserId, CancellationToken cancellationToken)
        {
            if (NextSaveOutcome is { } forced)
            {
                return Task.FromResult(forced);
            }

            Saves++;

            foreach (var row in Rows.Values)
            {
                row.RowVersion = [0, 0, 0, 0, 0, 0, (byte)Saves, row.RowVersion![7]];
            }

            return Task.FromResult(SettingsSaveOutcome.Saved);
        }
    }

    private sealed class FakePermissions(bool grantsAll, IReadOnlyCollection<string> permissions)
        : IEffectivePermissionsProvider
    {
        public Task<EffectivePermissions> GetEffectivePermissionsAsync(int userId, CancellationToken cancellationToken) =>
            Task.FromResult(new EffectivePermissions(grantsAll, permissions));
    }

    private sealed class RecordingAuditLog : IAuditLogWriter
    {
        public List<AuditLog> Rows { get; } = [];

        public Task WriteAsync(AuditLog entry, CancellationToken cancellationToken)
        {
            Rows.Add(entry);
            return Task.CompletedTask;
        }

        public void Enlist(AuditLog entry) => Rows.Add(entry);
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => new(2026, 10, 24, 9, 0, 0, TimeSpan.Zero);
    }

    /// <summary>The feed's cache, counting only how often the save asks for it to be dropped.</summary>
    private sealed class FakeCache : ICurrencyRatesCache
    {
        public int Evictions { get; private set; }

        public IReadOnlyList<SpencerDenim.Application.Settings.Contracts.CurrencyRateDto>? GetRates() => null;

        public void SetRates(IReadOnlyList<SpencerDenim.Application.Settings.Contracts.CurrencyRateDto> rates)
        {
        }

        public void Evict() => Evictions++;
    }
}
