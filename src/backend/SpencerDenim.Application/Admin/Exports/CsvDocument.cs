using System.Text;

namespace SpencerDenim.Application.Admin.Exports;

/// <summary>
/// Builds a CSV file that a shop owner can open in Excel safely and legibly.
/// </summary>
/// <remarks>
/// <para>
/// <b>Formula injection is neutralised in every cell.</b> A cell whose text begins with <c>=</c>, <c>+</c>,
/// <c>-</c> or <c>@</c> — or with a tab or a carriage return, which some spreadsheet programs skip before
/// deciding — is read by Excel and LibreOffice as a formula when the file is opened, <b>quoted or not</b>.
/// An export is made of text the public typed (an email address is not checked for any of those
/// characters at its start; <c>NewsletterEmail</c> only checks its shape), so a subscriber could otherwise
/// put a live formula on the shop owner's computer. Such a cell is prefixed with a single quote, the OWASP
/// recommendation: the value is then plain text, at the cost of a visible <c>'</c> in that one cell.
/// Leading spaces are looked past before deciding, because a formula after a space is still a formula to
/// some programs.
/// </para>
/// <para>
/// <b>Every cell is quoted</b>, with embedded quotes doubled (RFC 4180), so a comma, a quote or a line
/// break in a value can never start a new column or row.
/// </para>
/// <para>
/// <b>UTF-8 with a byte-order mark, and CRLF line ends.</b> Excel on Windows opens a CSV without a BOM in
/// the system's ANSI code page, which turns a Sinhala or Tamil name into mojibake; the BOM is what makes
/// it read UTF-8. CRLF is RFC 4180's line end.
/// </para>
/// </remarks>
public sealed class CsvDocument
{
    /// <summary>The media type an export is served with.</summary>
    public const string ContentType = "text/csv; charset=utf-8";

    private static readonly UTF8Encoding Utf8WithBom = new(encoderShouldEmitUTF8Identifier: true);

    private readonly StringBuilder _text = new();

    /// <summary>Appends one row.</summary>
    public CsvDocument Row(params string?[] cells)
    {
        ArgumentNullException.ThrowIfNull(cells);

        for (var index = 0; index < cells.Length; index++)
        {
            if (index > 0)
            {
                _text.Append(',');
            }

            _text.Append(Cell(cells[index]));
        }

        _text.Append("\r\n");

        return this;
    }

    /// <summary>The file: a UTF-8 byte-order mark followed by the rows.</summary>
    public byte[] ToBytes()
    {
        var preamble = Utf8WithBom.GetPreamble();
        var body = Utf8WithBom.GetBytes(_text.ToString());

        var bytes = new byte[preamble.Length + body.Length];
        preamble.CopyTo(bytes, 0);
        body.CopyTo(bytes, preamble.Length);

        return bytes;
    }

    /// <summary>One cell, neutralised and quoted.</summary>
    public static string Cell(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "\"\"";
        }

        var text = StartsLikeAFormula(value) ? "'" + value : value;

        return "\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    /// <summary>
    /// Whether a spreadsheet program would read <paramref name="value"/> as a formula: its first character
    /// after any leading spaces is one of <c>= + - @</c>, or it starts with a tab or a carriage return.
    /// </summary>
    public static bool StartsLikeAFormula(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (value.Length == 0)
        {
            return false;
        }

        if (value[0] is '\t' or '\r')
        {
            return true;
        }

        var first = value.TrimStart(' ');

        return first.Length > 0 && first[0] is '=' or '+' or '-' or '@';
    }
}
