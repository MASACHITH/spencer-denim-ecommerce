using System.Reflection;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using SpencerDenim.Application.Sales.Payments.Abstractions;
using SpencerDenim.Application.Sales.Payments.Contracts;
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
}
