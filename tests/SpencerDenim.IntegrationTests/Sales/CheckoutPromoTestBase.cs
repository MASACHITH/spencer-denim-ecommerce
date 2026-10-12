using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Application.Sales.Checkout.Contracts;
using SpencerDenim.Domain.Marketing;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Sales;

/// <summary>
/// Arranging promo codes for the checkout suites (ADM-13).
/// <para>
/// <b>Every code is created by the test that uses it and no code is seeded.</b> Doc 02 line 167 replaces
/// the old demo's hard-coded <c>TUASL10</c> and <c>FREESHIP</c> with admin-managed codes, and the seeder
/// creates none — so a suite that relied on a seeded code would be relying on a row that the shop's own
/// migration deliberately does not create. <see cref="NewPromoCodeAsync"/> makes a uniquely-named one per
/// call, which also keeps rounds of a race test from counting each other's uses.
/// </para>
/// </summary>
/// <remarks>
/// <b>It extends <c>AdminOrderTestBase</c> rather than <c>CheckoutTestBase</c></b> so that one base serves
/// both halves of doc 08 §3.6's usage model: the races, which only place orders, and the return of a use,
/// which has to cancel one and expire another through the real stores. Splitting them would mean two
/// copies of <see cref="NewPromoCodeAsync"/>, and the arrangement of a code is exactly the thing both
/// halves must agree on.
/// </remarks>
public abstract class CheckoutPromoTestBase(SqlServerDatabaseFixture sqlServer)
    : AdminOrderTestBase(sqlServer)
{
    /// <summary>
    /// A fragment of <c>OrderPromoUse.TakeAsync</c>'s per-customer statement, unique to it, used to count
    /// how many concurrent attempts reach the contended section.
    /// </summary>
    /// <remarks>
    /// <b>It is the statement's own alias and not a comment</b>, so it cannot drift out of the production
    /// SQL without the SQL changing. If somebody renames the alias, the watch counts zero and the race
    /// tests fail on the instrumentation assertion — which is the right failure, because the test would
    /// then be measuring nothing.
    /// </remarks>
    private const string PerCustomerStatementFragment = "AS HeldUses";

    /// <summary>
    /// A new, active <c>marketing.PromoCodes</c> row with a code no other test will use.
    /// </summary>
    /// <param name="discountType">Percent, FixedAmount or FreeDelivery.</param>
    /// <param name="discountValue">The percentage or the amount; null for FreeDelivery.</param>
    /// <param name="perCustomerLimit">Doc 08 §3.6's per-customer limit, or null for none.</param>
    /// <param name="totalUsageLimit">Doc 08 §3.6's total limit, or null for none.</param>
    /// <param name="minSpendAmount">Compared with the items subtotal (doc 08 §6 step 7).</param>
    /// <param name="maxDiscountAmount">The cap on a percentage discount.</param>
    /// <param name="startsAtUtc">Defaults to an hour ago, so the code is live.</param>
    /// <param name="endsAtUtc">Defaults to no end.</param>
    /// <param name="isActive">False to arrange a code staff have switched off.</param>
    protected async Task<PromoCode> NewPromoCodeAsync(
        PromoDiscountType discountType,
        decimal? discountValue,
        int? perCustomerLimit = null,
        int? totalUsageLimit = null,
        decimal? minSpendAmount = null,
        decimal? maxDiscountAmount = null,
        DateTime? startsAtUtc = null,
        DateTime? endsAtUtc = null,
        bool isActive = true)
    {
        SqlServer.SkipIfUnavailable();

        var now = DateTime.UtcNow;

        var promoCode = new PromoCode
        {
            // Upper-case and unique. `N` rather than `D`, because a hyphen is legal in a code and a
            // 32-character hex string keeps the whole thing inside varchar(40) with room for the prefix.
            Code = $"T{Guid.NewGuid():N}"[..20].ToUpperInvariant(),
            Description = "Arranged by the integration suite.",
            DiscountType = discountType,
            DiscountValue = discountValue,
            MaxDiscountAmount = maxDiscountAmount,
            MinSpendAmount = minSpendAmount,
            StartsAtUtc = startsAtUtc ?? now.AddHours(-1),
            EndsAtUtc = endsAtUtc,
            TotalUsageLimit = totalUsageLimit,
            PerCustomerLimit = perCustomerLimit,
            IsActive = isActive,
        };

        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        db.PromoCodes.Add(promoCode);
        await db.SaveChangesAsync(Token);

        return promoCode;
    }

    /// <summary>Soft-deletes a code, as the admin screen's remove does.</summary>
    protected async Task RemovePromoCodeAsync(int promoCodeId)
    {
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var promoCode = await db.PromoCodes.SingleAsync(code => code.Id == promoCodeId, Token);
        promoCode.SoftDelete(DateTime.UtcNow, null);

        await db.SaveChangesAsync(Token);
    }

    /// <summary>The cached held-use count (doc 08 §3.6).</summary>
    protected async Task<int> UsageCountAsync(int promoCodeId)
    {
        using var scope = CreateScope();

        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().PromoCodes
            .AsNoTracking()
            .Where(code => code.Id == promoCodeId)
            .Select(code => code.UsageCount)
            .SingleAsync(Token);
    }

    /// <summary>
    /// An email nobody else in the suite uses, so a per-customer limit measures this test alone.
    /// </summary>
    /// <remarks>
    /// Orders are never deleted (doc 08 §1.6), so an email shared between tests would accumulate held
    /// uses across the whole run and a per-customer assertion would pass or fail by test order.
    /// </remarks>
    protected static string UniqueEmail(string label) =>
        string.Create(CultureInfo.InvariantCulture, $"{label}.{Guid.NewGuid():N}@example.com");

    /// <summary>
    /// A valid place-order body carrying a promo code and a chosen email.
    /// </summary>
    protected static Dictionary<string, object?> PromoBody(
        CheckoutSessionDto? session,
        int deliveryMethodId,
        int districtId,
        string email,
        string? promoCode)
    {
        var body = ValidBody(
            session!.CheckoutKey,
            deliveryMethodId,
            districtId,
            QuotedTotal(session));

        body["email"] = email;
        body["promoCode"] = promoCode;

        return body;
    }

    /// <summary>
    /// Quotes a fresh bag with the code applied and places it, returning the response.
    /// </summary>
    /// <remarks>
    /// It takes the quoted total from the quote, so G-10 passes and the assertion is about the promo
    /// rather than about the total check. A test that wants to defeat G-10 builds its own body.
    /// </remarks>
    protected async Task<HttpResponseMessage> PlaceWithPromoAsync(
        int variantId,
        int districtId,
        int deliveryMethodId,
        string email,
        string? promoCode,
        int quantity = 1)
    {
        var (_, _, api) = await NewBagAsync(variantId, quantity);
        var (_, session) = await api.GetAsync(districtId, deliveryMethodId, Token, promoCode);

        var (response, _) = await api.PlaceAsync(
            PromoBody(session, deliveryMethodId, districtId, email, promoCode),
            Token);

        return response;
    }

    /// <summary>
    /// The <c>promoRefusal</c> extension of a 409 problem document, or null when there is none.
    /// </summary>
    /// <remarks>
    /// Read from the body rather than inferred from the status, for the reason
    /// <c>checkoutFailure</c> in the storefront gives: three different 409s come out of this endpoint and
    /// the title is not what tells them apart.
    /// </remarks>
    protected static async Task<string?> PromoRefusalOfAsync(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);

        var body = await response.Content.ReadAsStringAsync(Token);

        using var document = JsonDocument.Parse(body);

        return document.RootElement.TryGetProperty("promoRefusal", out var refusal)
            ? refusal.GetString()
            : null;
    }

    /// <summary>
    /// Starts counting arrivals at <c>OrderPromoUse.TakeAsync</c>'s contended statement, from zero.
    /// </summary>
    /// <remarks>
    /// <b>This is the instrumentation the per-customer race test asserts on</b>, and the reason it exists
    /// is in <c>CheckoutPromoRaceTests</c>' own remarks: a four-way race in which three attempts
    /// short-circuited before the contended statement has the same end state as one that was correctly
    /// serialised, so the end state alone cannot tell a working guard from a test that could not fail.
    /// </remarks>
    protected PromoStatementCount CountStatements()
    {
        CommandCounter.Watch(PerCustomerStatementFragment);

        return new PromoStatementCount(CommandCounter, PerCustomerStatementFragment);
    }

    /// <summary>
    /// A reading of how many commands have reached the per-customer statement since it was created.
    /// </summary>
    /// <remarks>
    /// Disposable only so that a test reads <c>using var counter = CountStatements();</c> and the window
    /// being measured is visibly a block. Nothing is released: the watch is reset by the next
    /// <see cref="CheckoutPromoTestBase.CountStatements"/>, which is what lets one test measure round
    /// after round without the counts accumulating.
    /// </remarks>
    protected sealed class PromoStatementCount(CommandCountingInterceptor counter, string fragment)
        : IDisposable
    {
        /// <summary>
        /// How many attempts have executed the per-customer statement. In a race of N contenders this
        /// must be N: anything less means some attempt was serialised <i>before</i> the contended
        /// section, and the test is not measuring the guard.
        /// </summary>
        public int PerCustomerCountExecutions => counter.CountOf(fragment);

        public void Dispose()
        {
            // Nothing to release. See the type remarks.
        }
    }
}
