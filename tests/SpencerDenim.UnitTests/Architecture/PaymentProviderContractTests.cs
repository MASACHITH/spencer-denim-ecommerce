using System.Reflection;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Application.Ops.Email;
using SpencerDenim.Application.Sales.Payments.Abstractions;
using SpencerDenim.Application.Sales.Payments.Contracts;
using SpencerDenim.Application.Sales.Payments.PayHere;
using SpencerDenim.Domain.Sales;
using SpencerDenim.Infrastructure.Persistence.Seeding;
using SpencerDenim.Infrastructure.Sales.Payments;

namespace SpencerDenim.UnitTests.Architecture;

/// <summary>
/// PAY-03's contract, run over <b>every</b> registered <see cref="IPaymentProvider"/> (doc 03 §3c: "every
/// <c>IPaymentProvider</c> honours the same contract… Contract tests run against each one").
/// <para>
/// <b>Written against the interface and discovered from the registration</b>, so <c>PayPalProvider</c> is
/// covered the day it is added to <c>AddInfrastructure</c> rather than the day somebody remembers to copy
/// a test. That is the whole reason doc 03 asks for a contract test here rather than a test per provider.
/// </para>
/// </summary>
public sealed class PaymentProviderContractTests
{
    /// <summary>
    /// Every provider in the Infrastructure assembly, built by reflection.
    /// </summary>
    /// <remarks>
    /// <b>Discovered from the assembly rather than from DI</b>, deliberately: a provider class that exists
    /// and was never registered is a provider checkout cannot reach, and this suite should still hold it to
    /// the contract — a half-wired gateway is exactly the state in which somebody enables its
    /// <c>sales.PaymentProviders</c> row and discovers the problem with a customer's money. The separate
    /// registration test below is what says the two agree.
    /// </remarks>
    public static TheoryData<string> ProviderTypeNames()
    {
        var data = new TheoryData<string>();

        foreach (var type in ProviderTypes())
        {
            data.Add(type.FullName!);
        }

        return data;
    }

    [Fact]
    public void There_is_at_least_one_payment_provider()
    {
        // A vacuous theory is the failure mode this guards: if the discovery below ever stopped finding
        // anything, every case in this file would pass by having nothing to run.
        Assert.NotEmpty(ProviderTypes());
    }

    [Theory]
    [MemberData(nameof(ProviderTypeNames))]
    public void A_provider_s_code_is_one_the_shop_actually_seeds(string typeName)
    {
        var provider = Build(typeName);

        Assert.False(string.IsNullOrWhiteSpace(provider.Code));

        // Doc 08 §3.5 makes PaymentProviders.Code a natural key referenced by FKs from Orders and
        // Payments, so a provider whose code is not a seeded row could not write a payment at all: the
        // foreign key would refuse it. Checked against doc 08 §9's seed rather than against the constants,
        // because the seed is what is in the database.
        Assert.Contains(
            provider.Code,
            SalesReferenceData.PaymentProviders.Select(seeded => seeded.Code));

        // varchar(30) in the database.
        Assert.InRange(provider.Code.Length, 1, PaymentProviderCodes.MaxCodeLength);
    }

    [Theory]
    [MemberData(nameof(ProviderTypeNames))]
    public async Task A_provider_opens_a_payment_the_database_will_accept(string typeName)
    {
        var provider = Build(typeName);
        var placedAt = new DateTime(2026, 10, 6, 9, 0, 0, DateTimeKind.Utc);

        var initiation = await provider.InitiateAsync(
            new PaymentInitiationRequest(9_184m, placedAt),
            TestContext.Current.CancellationToken);

        // Doc 08 §3.5's UX_Payments_OneOpen covers statuses 0 and 1, and a placed order's one payment row
        // has to be inside that filter — otherwise "at most one open attempt per order" would be a rule
        // about nothing, and a second attempt could be created beside the first.
        Assert.Contains(initiation.Status, new[] { PaymentStatus.Initiated, PaymentStatus.Pending });

        // CK_Payments_IsOrderSettlement allows the flag only for statuses 3, 8 and 9, so neither opening
        // status could claim it. Stated here because it is the invariant that makes the flag meaningful.
        Assert.DoesNotContain(
            initiation.Status,
            new[] { PaymentStatus.Succeeded, PaymentStatus.PartiallyRefunded, PaymentStatus.Refunded });

        // **Strictly after the placement time.** A provider that answered with a deadline already past
        // would have its orders expired by the job before the customer could pay, and the order row would
        // carry a lie. `PlaceOrderCommandHandler` throws rather than writing one; this is the other half.
        Assert.True(
            initiation.PaymentDueAtUtc > placedAt,
            $"{provider.Code} returned a payment due time of {initiation.PaymentDueAtUtc:O}, which is not "
            + $"after the placement time {placedAt:O}.");

        // A provider currency implies an amount and a rate, and none of the three means none of them:
        // doc 08 §4.2 step 3.1 compares a captured amount against ProviderAmount, so a half-filled set
        // would make "this payment was converted" indistinguishable from "it was not".
        var hasConversion = initiation.ProviderCurrency is not null;

        Assert.Equal(hasConversion, initiation.ProviderAmount is not null);
        Assert.Equal(hasConversion, initiation.ExchangeRate is not null);
    }

    /// <summary>
    /// Whether a provider can take an order right now is asked on every view of the checkout page, so asking
    /// must be safe — and a provider that says yes must then be able to open a payment, which
    /// <see cref="A_provider_opens_a_payment_the_database_will_accept"/> proves against the same stubs.
    /// </summary>
    [Theory]
    [MemberData(nameof(ProviderTypeNames))]
    public async Task A_provider_says_whether_it_is_available_without_throwing(string typeName)
    {
        var provider = Build(typeName);

        var available = await provider.IsAvailableAsync(TestContext.Current.CancellationToken);

        // The stubs describe a provider that has everything it needs, so every provider here is available:
        // an unconfigured one is covered by its own suite.
        Assert.True(available, $"{provider.Code} says it is unavailable when everything it needs is present.");
    }

    [Theory]
    [MemberData(nameof(ProviderTypeNames))]
    public async Task A_provider_describes_itself_without_throwing(string typeName)
    {
        var provider = Build(typeName);

        // Null is a permitted answer — for a bank transfer it means the shop has not filled its details in
        // (doc 11 P7's precondition) — so what is asserted is that asking is safe, and that a non-null
        // answer is internally consistent.
        var instructions = await provider.DescribeAsync(
            new PaymentInstructionsRequest(
                "SD-2026-000123",
                9_184m,
                new DateTime(2026, 10, 8, 9, 0, 0, DateTimeKind.Utc),
                OrderUrl: null),
            TestContext.Current.CancellationToken);

        if (instructions is null)
        {
            return;
        }

        Assert.Equal(provider.Code, instructions.ProviderCode);
        Assert.Equal(9_184m, instructions.AmountLkr);

        // Every field has a label and a value. A labelled blank is a line on a customer's order page
        // saying "Account number:" with nothing after it, which is the state the whole null answer exists
        // to avoid.
        foreach (var field in instructions.Fields)
        {
            Assert.False(string.IsNullOrWhiteSpace(field.Label));
            Assert.False(string.IsNullOrWhiteSpace(field.Value));
        }
    }

    /// <summary>
    /// Asking for the instructions of an order that already has a payment attempt is as safe as asking at
    /// placement, when there is none (<see cref="PaymentInstructionsRequest.PaymentId"/>).
    /// </summary>
    /// <remarks>
    /// <b>The order page fills the attempt in; checkout cannot</b> — the payment row does not exist yet when
    /// checkout asks — so a provider must answer for both shapes of request. This runs the provider with an
    /// attempt present and holds the answer to the same consistency rules as the one without, including the
    /// redirect rules a gateway that sends the customer elsewhere has to meet.
    /// </remarks>
    [Theory]
    [MemberData(nameof(ProviderTypeNames))]
    public async Task A_provider_describes_itself_for_an_attempt_that_already_exists(string typeName)
    {
        var provider = Build(typeName);

        var instructions = await provider.DescribeAsync(
            new PaymentInstructionsRequest(
                "SD-2026-000123",
                9_184m,
                new DateTime(2026, 10, 8, 9, 0, 0, DateTimeKind.Utc),
                OrderUrl: null,
                PaymentId: 4711,
                ProviderReference: "REF-4711"),
            TestContext.Current.CancellationToken);

        if (instructions is null)
        {
            return;
        }

        AssertInstructionsAreConsistent(provider.Code, 9_184m, instructions);
    }

    /// <summary>
    /// The rules a provider's instructions must meet, held in one place so the redirect rules are checked
    /// the day a redirecting provider is added — and exercised here with instructions of the kind that does
    /// not exist yet, so the checker itself is not untested code waiting for its first use.
    /// </summary>
    [Fact]
    public void The_consistency_rules_accept_a_well_formed_redirect_and_a_well_formed_bank_transfer()
    {
        var due = new DateTime(2026, 10, 8, 9, 0, 0, DateTimeKind.Utc);

        AssertInstructionsAreConsistent(
            "Gateway",
            9_184m,
            new PaymentInstructions("Gateway", null, 9_184m, due, [], null, "https://pay.example.test/x", false));

        AssertInstructionsAreConsistent(
            "Gateway",
            9_184m,
            new PaymentInstructions(
                "Gateway",
                null,
                9_184m,
                due,
                [],
                null,
                RedirectUrl: null,
                AcceptsProof: false,
                RedirectForm: PaymentRedirectForm.Post(
                    "https://pay.example.test/checkout",
                    [new PaymentRedirectFormField("order_id", "SD-2026-000123-1")])));

        AssertInstructionsAreConsistent(
            "BankTransfer",
            9_184m,
            new PaymentInstructions(
                "BankTransfer",
                "SD-2026-000123",
                9_184m,
                due,
                [new PaymentInstructionField("Account number", "123", Copyable: true)],
                null,
                RedirectUrl: null,
                AcceptsProof: true));
    }

    [Fact]
    public void The_consistency_rules_reject_a_redirect_the_order_page_would_not_follow()
    {
        var due = new DateTime(2026, 10, 8, 9, 0, 0, DateTimeKind.Utc);

        // A link to a non-https address.
        Assert.ThrowsAny<Exception>(() => AssertInstructionsAreConsistent(
            "Gateway",
            9_184m,
            new PaymentInstructions("Gateway", null, 9_184m, due, [], null, "http://pay.example.test/x", false)));

        // A form whose action is not safe, built by hand because the factory refuses to.
        Assert.ThrowsAny<Exception>(() => AssertInstructionsAreConsistent(
            "Gateway",
            9_184m,
            new PaymentInstructions(
                "Gateway",
                null,
                9_184m,
                due,
                [],
                null,
                RedirectUrl: null,
                AcceptsProof: false,
                RedirectForm: new PaymentRedirectForm("POST", "javascript:alert(1)", []))));

        // A form that is not a POST: the order page renders a POST form and nothing else.
        Assert.ThrowsAny<Exception>(() => AssertInstructionsAreConsistent(
            "Gateway",
            9_184m,
            new PaymentInstructions(
                "Gateway",
                null,
                9_184m,
                due,
                [],
                null,
                RedirectUrl: null,
                AcceptsProof: false,
                RedirectForm: new PaymentRedirectForm("GET", "https://pay.example.test/x", []))));

        // A gateway that confirms itself does not take a bank slip.
        Assert.ThrowsAny<Exception>(() => AssertInstructionsAreConsistent(
            "Gateway",
            9_184m,
            new PaymentInstructions("Gateway", null, 9_184m, due, [], null, "https://pay.example.test/x", true)));
    }

    /// <summary>
    /// What a non-null answer from <c>DescribeAsync</c> must satisfy, for any provider.
    /// </summary>
    private static void AssertInstructionsAreConsistent(
        string providerCode,
        decimal amountLkr,
        PaymentInstructions instructions)
    {
        Assert.Equal(providerCode, instructions.ProviderCode);
        Assert.Equal(amountLkr, instructions.AmountLkr);

        foreach (var field in instructions.Fields)
        {
            Assert.False(string.IsNullOrWhiteSpace(field.Label));
            Assert.False(string.IsNullOrWhiteSpace(field.Value));
        }

        if (instructions.RedirectUrl is not null)
        {
            // The order page renders only an absolute https address (http for localhost) as a link.
            Assert.True(
                PaymentRedirectForm.IsSafeAbsoluteUrl(instructions.RedirectUrl),
                $"{providerCode} returned a redirect URL the order page would not follow.");
        }

        if (instructions.RedirectForm is { } form)
        {
            Assert.Equal(PaymentRedirectForm.PostMethod, form.Method);
            Assert.True(
                PaymentRedirectForm.IsSafeAbsoluteUrl(form.Action),
                $"{providerCode} returned a redirect form whose action the order page would not follow.");
            Assert.All(form.Fields, field => Assert.False(string.IsNullOrWhiteSpace(field.Name)));
        }

        if (instructions.RedirectUrl is not null || instructions.RedirectForm is not null)
        {
            // A gateway confirms itself (doc 11 P8: only the provider's server confirms payment), so the
            // customer has no slip to send. The order page also shows no upload for a redirect.
            Assert.False(
                instructions.AcceptsProof,
                $"{providerCode} redirects the customer to pay but also says it takes a bank slip.");
        }
    }

    /// <summary>
    /// Every provider class is registered, and no two claim one code.
    /// </summary>
    /// <remarks>
    /// The registry throws on a duplicate code at its first resolution, which is a startup failure rather
    /// than a coin toss about which provider takes the money. This asserts both halves without a host:
    /// that the set DI would resolve is the set that exists, and that building a registry over them does
    /// not throw.
    /// </remarks>
    [Fact]
    public void Every_provider_class_is_registered_exactly_once()
    {
        // Read off AddInfrastructure's own source rather than by calling it: that method needs
        // configuration, a database and a host, and the question being asked is only "is each provider
        // class on the list".
        var registered = RegisteredProviderTypes();

        Assert.Equal(
            ProviderTypes().Select(type => type.FullName).Order(StringComparer.Ordinal),
            registered.Select(type => type.FullName).Order(StringComparer.Ordinal));

        var registry = new PaymentProviderRegistry(
            ProviderTypes().Select(type => Build(type.FullName!)));

        Assert.Equal(
            ProviderTypes().Count,
            registry.ImplementedCodes.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    /// <summary>
    /// The registry finds a provider however the database spelled its code.
    /// </summary>
    /// <remarks>
    /// The column is <c>varchar(30)</c> in a <c>Latin1_General_100_CI_AS_SC</c> database, so
    /// <c>banktransfer</c> and <c>BankTransfer</c> are the same row to SQL Server. An ordinal lookup here
    /// would find neither and refuse the checkout with "that payment method cannot be used" — on a row the
    /// shop had enabled.
    /// </remarks>
    [Fact]
    public void The_registry_matches_a_code_case_insensitively_and_ignores_surrounding_space()
    {
        var registry = new PaymentProviderRegistry(
            ProviderTypes().Select(type => Build(type.FullName!)));

        foreach (var code in registry.ImplementedCodes)
        {
            Assert.NotNull(registry.Find(code));
            Assert.NotNull(registry.Find(code.ToUpperInvariant()));
            Assert.NotNull(registry.Find(code.ToLowerInvariant()));
            Assert.NotNull(registry.Find($"  {code}  "));
        }

        Assert.Null(registry.Find(null));
        Assert.Null(registry.Find(string.Empty));
        Assert.Null(registry.Find("Koko"));
    }

    [Fact]
    public void Two_providers_claiming_one_code_is_a_startup_failure()
    {
        var provider = Build(typeof(BankTransferProvider).FullName!);

        // Loudly, at the first resolution rather than at the first order: "the last one registered wins"
        // is not an answer anybody would choose about which class takes money.
        var thrown = Assert.Throws<InvalidOperationException>(
            () => new PaymentProviderRegistry([provider, provider]));

        Assert.Contains(provider.Code, thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>Every concrete <see cref="IPaymentProvider"/> in the Infrastructure assembly.</summary>
    private static List<Type> ProviderTypes() =>
        typeof(BankTransferProvider).Assembly
            .GetTypes()
            .Where(type => type is { IsClass: true, IsAbstract: false })
            .Where(type => typeof(IPaymentProvider).IsAssignableFrom(type))
            .OrderBy(type => type.FullName, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// The provider types <c>AddInfrastructure</c> registers, read out of its own source.
    /// </summary>
    /// <remarks>
    /// Reflection cannot see a DI registration without running it, and running
    /// <c>AddInfrastructure</c> needs configuration and a database. The registration list is a handful of
    /// lines in one file, so the honest cheap check is to read them — and the assertion above makes the
    /// two lists agree, which is the thing worth knowing.
    /// </remarks>
    private static List<Type> RegisteredProviderTypes() =>
        ProviderTypes()
            .Where(type => RegistrationSource.Contains(
                $"AddScoped<IPaymentProvider, {type.Name}>",
                StringComparison.Ordinal))
            .ToList();

    /// <summary>
    /// <c>DependencyInjection.cs</c>'s text, found relative to this test assembly.
    /// </summary>
    /// <remarks>
    /// A source read is unusual in a unit test and is confined to this one question. It is here rather
    /// than in an integration test because the thing being checked — "is every provider class on the DI
    /// list" — is a fact about the source, and an integration test could only observe it by resolving the
    /// container, which would pass for a provider class that exists and is not registered.
    /// </remarks>
    private static string RegistrationSource { get; } = ReadRegistrationSource();

    private static string ReadRegistrationSource()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(
                directory.FullName,
                "src",
                "backend",
                "SpencerDenim.Infrastructure",
                "DependencyInjection.cs");

            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            "Could not find SpencerDenim.Infrastructure/DependencyInjection.cs by walking up from "
            + AppContext.BaseDirectory);
    }

    private static IPaymentProvider Build(string typeName)
    {
        var type = ProviderTypes().Single(candidate => candidate.FullName == typeName);

        // Every provider's dependencies are ports, and a provider is not allowed to need anything of them
        // to answer these two questions. Built with stubs rather than through DI so the contract is proved
        // without a database.
        var arguments = type
            .GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Single()
            .GetParameters()
            .Select(parameter => StubFor(parameter.ParameterType))
            .ToArray();

        return (IPaymentProvider)Activator.CreateInstance(type, arguments)!;
    }

    private static object StubFor(Type type)
    {
        if (type == typeof(IBankTransferSettingsReader))
        {
            return new NoBankDetails();
        }

        if (type == typeof(SpencerDenim.Application.Sales.Payments.BankTransferOptions))
        {
            return new SpencerDenim.Application.Sales.Payments.BankTransferOptions();
        }

        // The one-per-process warning gate, which came with the logger below. A fresh one, so a provider
        // under this test is never silenced by something another test did.
        if (type == typeof(BankDetailsWarningOnce))
        {
            return new BankDetailsWarningOnce();
        }

        // A logger, which W3-03's review added to the bank provider so an unconfigured shop is not silent
        // (doc 08 §3.5's unenforced "a provider needs valid settings"). NullLogger rather than a spy: what
        // this file proves is the contract, and a provider that logs is still a provider that must answer.
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ILogger<>))
        {
            return Activator.CreateInstance(
                typeof(NullLogger<>).MakeGenericType(type.GetGenericArguments()))!;
        }

        // The ports a provider confirmed by the provider itself builds on (W4-01 groundwork). **Stubs that
        // refuse, except the reader**, and that is the assertion: answering "what is the payment row and
        // what do I tell the customer" must not need to settle an order or write an event - those are the
        // webhook's business, not checkout's or the order page's. The attempt reader answers "none", which
        // is a real answer for an order that has no live attempt, so a provider may ask it.
        if (type == typeof(IOrderSettlementStore))
        {
            return new RefusingSettlementStore();
        }

        if (type == typeof(IPaymentEventStore))
        {
            return new RefusingEventStore();
        }

        if (type == typeof(IPaymentAttemptReader))
        {
            return new NoPaymentAttempt();
        }

        if (type == typeof(IClock))
        {
            return new FixedClock(new DateTimeOffset(2026, 10, 6, 9, 0, 0, TimeSpan.Zero));
        }

        // PayHere (issue #141). **Configured, with an order to describe**, unlike the bank stub that has
        // nothing: the contract has to be proved against the answer a redirecting provider gives when it
        // can answer, which is a signed form, and the consistency rules below hold that form to the same
        // standard as any other redirect. A provider that is not configured answers null and is covered by
        // PayHereProviderTests.
        if (type == typeof(IPayHereSettingsReader))
        {
            return new ConfiguredPayHere();
        }

        if (type == typeof(IPayHerePaymentStore))
        {
            return new OnePayHereOrder();
        }

        if (type == typeof(EmailOptions))
        {
            return new EmailOptions { StorefrontBaseUrl = "https://shop.example.test" };
        }

        throw new NotSupportedException(
            $"A payment provider now depends on {type.Name}, which this contract test has no stub for. "
            + "Add one: the contract has to be provable without a database.");
    }

    /// <summary>
    /// A settings reader that has nothing — the state doc 11 P7's precondition describes, and the one a
    /// contract test should use, because a provider must answer safely in it.
    /// </summary>
    private sealed class NoBankDetails : IBankTransferSettingsReader
    {
        public Task<BankTransferDetails?> ReadAsync(CancellationToken cancellationToken) =>
            Task.FromResult<BankTransferDetails?>(null);
    }

    /// <summary>A PayHere account that is set up, in sandbox.</summary>
    private sealed class ConfiguredPayHere : IPayHereSettingsReader
    {
        public Task<PayHereSettings> ReadAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new PayHereSettings("sandbox", "1210000", "test-secret-not-a-real-one", 5, 60));
    }

    /// <summary>One PayHere order whose payment is Initiated, whatever id is asked for.</summary>
    private sealed class OnePayHereOrder : IPayHerePaymentStore
    {
        public Task<PayHerePayment?> FindAsync(string orderNumber, int paymentId, CancellationToken cancellationToken) =>
            throw new NotSupportedException("A provider's contract questions must not look up a notification's payment.");

        public Task<PayHereCheckoutContext?> FindCheckoutContextAsync(int paymentId, CancellationToken cancellationToken) =>
            Task.FromResult<PayHereCheckoutContext?>(new PayHereCheckoutContext(
                paymentId,
                PaymentStatus.Initiated,
                "SD-2026-000123",
                Guid.Parse("11111111-2222-3333-4444-555555555555"),
                9_184m,
                "Test",
                "Buyer",
                "buyer@example.test",
                "0771234567",
                "1 Example Road",
                "Colombo"));

        public Task RecordReferenceAsync(
            int paymentId,
            int orderId,
            string payHerePaymentId,
            DateTime nowUtc,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<PayHereTransition> MarkPendingAsync(
            int paymentId,
            int orderId,
            string payHerePaymentId,
            int pendingMaxDays,
            DateTime nowUtc,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<PayHereFailureResult> MarkFailedAsync(PayHereFailure failure, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    /// <summary>An order with no live payment attempt - the state at placement, and after a rejection.</summary>
    private sealed class NoPaymentAttempt : IPaymentAttemptReader
    {
        public Task<PaymentAttempt?> FindInFlightAsync(Guid orderPublicId, CancellationToken cancellationToken) =>
            Task.FromResult<PaymentAttempt?>(null);
    }

    /// <summary>Settling an order is not something answering the two PAY-03 questions may do.</summary>
    private sealed class RefusingSettlementStore : IOrderSettlementStore
    {
        public Task<PaymentSettlementResult> SettleAsync(
            PaymentSettlement settlement,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("A provider's contract questions must not settle an order.");

        public Task<PaymentSettlementResult> RouteToExceptionAsync(
            PaymentExceptionRouting routing,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("A provider's contract questions must not route a payment.");
    }

    /// <summary>Recording an event is the webhook endpoint's job, not InitiateAsync's or DescribeAsync's.</summary>
    private sealed class RefusingEventStore : IPaymentEventStore
    {
        public Task<PaymentEventReceipt> RecordAsync(PaymentEventRecord record, CancellationToken cancellationToken) =>
            throw new NotSupportedException("A provider's contract questions must not record an event.");

        public Task<bool> MarkProcessedAsync(long eventId, DateTime nowUtc, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> MarkIgnoredAsync(long eventId, DateTime nowUtc, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> MarkFailedAsync(
            long eventId,
            string failure,
            DateTime? retryAtUtc,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> LinkAsync(long eventId, int? paymentId, int? orderId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }
}
