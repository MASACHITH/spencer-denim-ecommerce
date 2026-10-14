namespace SpencerDenim.Application.Sales.Payments.Contracts;

/// <summary>
/// A form the customer's browser must submit to reach a provider's hosted payment page (PAY-03): the
/// method, the absolute address it posts to, and the fields in the order they are to be sent.
/// <para>
/// <b>Why a form and not a URL.</b> Some hosted checkouts accept only a <c>POST</c> whose fields — the
/// merchant id, the amount, a signature over them — the <i>server</i> computed. A link cannot carry that
/// without putting the signature in a query string, and a form the page renders from this record keeps the
/// signed values out of the address bar and out of the browser history. The storefront renders whatever
/// this says and knows nothing about which provider produced it.
/// </para>
/// <para>
/// <b>Nothing secret goes in.</b> The fields are shown to the browser by definition, so a provider may put
/// in a <i>hash</i> it computed from its secret but never the secret itself. No test can see a provider's
/// secret, so this sentence is the control.
/// </para>
/// </summary>
/// <param name="Method">Always <see cref="PostMethod"/>. A property rather than an assumption, so the JSON says it.</param>
/// <param name="Action">The absolute <c>https</c> address to post to.</param>
/// <param name="Fields">The hidden fields, in the order to send them.</param>
public sealed record PaymentRedirectForm(
    string Method,
    string Action,
    IReadOnlyList<PaymentRedirectFormField> Fields)
{
    /// <summary>The only method v1 supports. A redirect that can be a <c>GET</c> uses <c>RedirectUrl</c>.</summary>
    public const string PostMethod = "POST";

    /// <summary>
    /// Builds a <c>POST</c> form, refusing an address the order page would not render.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The action is not an address <see cref="IsSafeAbsoluteUrl"/> accepts, or a field has no name.
    /// </exception>
    public static PaymentRedirectForm Post(string action, IEnumerable<PaymentRedirectFormField> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);

        if (!IsSafeAbsoluteUrl(action))
        {
            throw new ArgumentException(
                "A redirect form must post to an absolute https URL with no user name or password "
                + "(plain http is allowed for localhost and 127.0.0.1 only).",
                nameof(action));
        }

        var list = fields.ToList();

        if (list.Any(field => string.IsNullOrWhiteSpace(field.Name)))
        {
            throw new ArgumentException("Every redirect form field needs a name.", nameof(fields));
        }

        return new PaymentRedirectForm(PostMethod, action.Trim(), list);
    }

    /// <summary>
    /// Whether <paramref name="url"/> is a destination the order page will send a customer to. **This is the
    /// storefront's rule (<c>safeRedirectUrl</c> in <c>payment-redirect.ts</c>), written a second time in the
    /// other language, and it must stay at least as strict:**
    /// <list type="bullet">
    /// <item><description>absolute, after trimming surrounding whitespace;</description></item>
    /// <item><description>
    /// <b>no user name or password</b> — <c>https://trusted.example@evil.example/</c> reads as one host and
    /// goes to another;
    /// </description></item>
    /// <item><description><c>https</c>, any host; or</description></item>
    /// <item><description>
    /// <c>http</c> only when the host is exactly <c>localhost</c> or <c>127.0.0.1</c>. Not "any loopback":
    /// <see cref="Uri.IsLoopback"/> is also true for <c>127.0.0.2</c> and <c>[::1]</c>, which the storefront
    /// refuses.
    /// </description></item>
    /// </list>
    /// <para>
    /// The two sides are kept in step by one list of safe and unsafe addresses that both test suites run
    /// (<c>PaymentRedirectFormTests</c> and <c>payment-redirect.spec.ts</c>, each naming the other), and a
    /// test that fails if an entry is missing from either.
    /// </para>
    /// </summary>
    public static bool IsSafeAbsoluteUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)
            || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)
            || uri.UserInfo.Length > 0)
        {
            return false;
        }

        if (uri.Scheme == Uri.UriSchemeHttps)
        {
            return true;
        }

        // Host is lower-case for a DNS name, and keeps its brackets for an IPv6 literal, so "[::1]" is
        // never equal to either of the two names below.
        return uri.Scheme == Uri.UriSchemeHttp
            && (string.Equals(uri.Host, "localhost", StringComparison.Ordinal)
                || string.Equals(uri.Host, "127.0.0.1", StringComparison.Ordinal));
    }
}

/// <summary>One hidden field of a <see cref="PaymentRedirectForm"/>.</summary>
/// <param name="Name">The field name the provider expects.</param>
/// <param name="Value">The value, as the provider expects it (already formatted).</param>
public sealed record PaymentRedirectFormField(string Name, string Value);
