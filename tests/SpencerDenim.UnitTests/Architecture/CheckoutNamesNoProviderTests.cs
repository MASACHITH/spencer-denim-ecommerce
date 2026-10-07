using System.Reflection;
using SpencerDenim.Application.Sales.Checkout;
using SpencerDenim.Domain.Sales;

namespace SpencerDenim.UnitTests.Architecture;

/// <summary>
/// PAY-03's actual claim, as a test: <b>adding a payment gateway does not edit checkout.</b>
/// <para>
/// Doc 02's PAY-03 is "pluggable payment-provider design so the next gateway plugs in without touching
/// checkout", and doc 03 §3c says the same. That is a claim about code, and a claim like that is worth
/// exactly as much as the test under it — W3-02 shipped with <c>code == PaymentProviderCodes.BankTransfer</c>
/// in its placement handler and a comment promising it would be replaced, which is how the promise gets
/// made and not kept.
/// </para>
/// <para>
/// <b>What this is not.</b> It does not say checkout is unaware that providers exist: it resolves one from
/// <c>IPaymentProviderRegistry</c>, takes its answer and writes it. It says checkout cannot be <i>about</i>
/// a particular gateway — no branch on a code, no 48 hours, no USD.
/// </para>
/// </summary>
public sealed class CheckoutNamesNoProviderTests
{
    /// <summary>The provider names nothing in the checkout namespace may mention.</summary>
    /// <remarks>
    /// Taken from <see cref="PaymentProviderCodes"/> rather than written out, so a third gateway is covered
    /// by adding its constant — which is where it would be added anyway.
    /// </remarks>
    private static readonly string[] ProviderNames =
    [
        PaymentProviderCodes.BankTransfer,
        PaymentProviderCodes.PayPal,
    ];

    /// <summary>
    /// Every type in <c>SpencerDenim.Application.Sales.Checkout</c> and below.
    /// </summary>
    private static IEnumerable<Type> CheckoutTypes() =>
        typeof(PlaceOrderCommandHandler).Assembly
            .GetTypes()
            .Where(type => type.Namespace is { } ns
                && ns.StartsWith("SpencerDenim.Application.Sales.Checkout", StringComparison.Ordinal));

    [Fact]
    public void The_checkout_namespace_is_not_empty()
    {
        // The guard against a vacuous pass: if the namespace filter ever stopped matching, every
        // assertion below would hold by having nothing to check.
        Assert.NotEmpty(CheckoutTypes());
    }

    /// <summary>
    /// No type, member or parameter in the checkout namespace names a gateway.
    /// </summary>
    /// <remarks>
    /// <b>Identifiers, not strings.</b> A string could legitimately appear in an XML comment or in a
    /// message, and this test is about the shape of the code rather than about its prose — the one place a
    /// gateway's name may be mentioned in checkout is a comment explaining that it is not mentioned
    /// anywhere else.
    /// </remarks>
    [Fact]
    public void Nothing_in_checkout_is_named_after_a_payment_provider()
    {
        var offences = new List<string>();

        foreach (var type in CheckoutTypes())
        {
            Check(type.Name, type.FullName!, offences);

            foreach (var member in type.GetMembers(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static
                | BindingFlags.DeclaredOnly))
            {
                Check(member.Name, $"{type.FullName}.{member.Name}", offences);

                if (member is MethodBase method)
                {
                    foreach (var parameter in method.GetParameters())
                    {
                        Check(parameter.Name ?? string.Empty, $"{type.FullName}.{member.Name}({parameter.Name})", offences);
                    }
                }
            }
        }

        Assert.Empty(offences);
    }

    /// <summary>
    /// And checkout holds no <c>PaymentStatus</c> constant either, which is the subtler half.
    /// </summary>
    /// <remarks>
    /// <b>This is the one the first version of the seam would have passed.</b> A handler could name
    /// nothing after a gateway and still decide that a new payment opens <c>Pending</c> — which is a
    /// bank-transfer fact, not a general one (PayPal opens <c>Initiated</c>). Checkout takes the status
    /// from the provider's <c>PaymentInitiation</c> and never mentions one, so a reference to the enum
    /// anywhere in this namespace is the seam leaking.
    /// </remarks>
    [Fact]
    public void Nothing_in_checkout_declares_a_payment_status()
    {
        var offences = CheckoutTypes()
            .SelectMany(type => type
                .GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static
                    | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(field => field.FieldType == typeof(PaymentStatus))
                .Select(field => $"{type.FullName}.{field.Name}"))
            .ToList();

        Assert.Empty(offences);
    }

    private static void Check(string identifier, string where, List<string> offences)
    {
        foreach (var name in ProviderNames)
        {
            if (identifier.Contains(name, StringComparison.OrdinalIgnoreCase))
            {
                offences.Add(
                    $"{where} names the payment provider '{name}'. PAY-03 is the claim that a new gateway "
                    + "plugs in without touching checkout; an identifier here named after one is that "
                    + "claim being broken. Put the behaviour behind IPaymentProvider instead.");
            }
        }
    }
}
