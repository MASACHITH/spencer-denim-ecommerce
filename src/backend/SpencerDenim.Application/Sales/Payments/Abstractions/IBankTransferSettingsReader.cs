namespace SpencerDenim.Application.Sales.Payments.Abstractions;

/// <summary>
/// Read port for the <c>Bank.*</c> keys of <c>ops.Settings</c> (doc 08 §3.8, doc 11 P7 preconditions).
/// <para>
/// <b>Its own port, and deliberately not <c>ISettingsStore</c>.</b> That store hands back tracked rows
/// with their <c>RequiredPermission</c> column, which is exactly what an admin write path needs and
/// exactly what a customer-facing read must not have: the order page is anonymous. This port can only
/// answer one question, and the answer carries five strings and no row.
/// </para>
/// </summary>
public interface IBankTransferSettingsReader
{
    /// <summary>
    /// The bank details, or <see langword="null"/> when the client has not filled in the three that are
    /// indispensable. Never throws for missing or unseeded rows.
    /// </summary>
    Task<BankTransferDetails?> ReadAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The shop's bank account, as the client typed it (doc 08 §3.8 group <c>Bank</c>).
/// </summary>
/// <param name="BankName">Required.</param>
/// <param name="AccountName">Required.</param>
/// <param name="AccountNumber">Required.</param>
/// <param name="Branch">Optional: online banking does not ask for it, a deposit slip does.</param>
/// <param name="Instructions">Optional plain-text note. <b>Never markup</b> — see <c>SettingKeys.Bank</c>.</param>
/// <remarks>
/// <b>Why three are required and two are not, and why "required" is enforced by the only constructor.</b>
/// A shopper cannot make a transfer without a bank, an account name and an account number; they can make
/// one without a branch, and the instructions are the shop's own commentary. Doc 11 P7 lists all five in
/// the instructions, so the split is this type's judgement rather than the document's — and it is the
/// judgement that decides whether the order page shows a payment form at all. Half a form is worse than
/// none: a shopper who sees a bank name and an empty account number may well transfer to whatever they
/// find, and the money then has to be traced by hand.
/// </remarks>
public sealed record BankTransferDetails
{
    /// <summary>
    /// <see langword="null"/> unless all three indispensable values are present and non-blank. The only
    /// way to make one, so there is no path on which a half-filled set reaches a customer.
    /// </summary>
    public static BankTransferDetails? TryCreate(
        string? bankName,
        string? accountName,
        string? accountNumber,
        string? branch,
        string? instructions)
    {
        var bank = Clean(bankName);
        var name = Clean(accountName);
        var number = Clean(accountNumber);

        return bank is null || name is null || number is null
            ? null
            : new BankTransferDetails
            {
                BankName = bank,
                AccountName = name,
                AccountNumber = number,
                Branch = Clean(branch),
                Instructions = Clean(instructions),
            };
    }

    public string BankName { get; private init; } = string.Empty;

    public string AccountName { get; private init; } = string.Empty;

    public string AccountNumber { get; private init; } = string.Empty;

    public string? Branch { get; private init; }

    public string? Instructions { get; private init; }

    /// <summary>
    /// Trims, and treats whitespace as absent — <c>" "</c> in a settings row is a client who cleared the
    /// box, not an account number made of a space.
    /// </summary>
    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
