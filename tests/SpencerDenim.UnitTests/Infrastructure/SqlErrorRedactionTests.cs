using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging;
using SpencerDenim.Infrastructure.Persistence;

namespace SpencerDenim.UnitTests.Infrastructure;

/// <summary>
/// #207 review: a SQL Server error is described by its number, class, state and (for a duplicate key) its index,
/// never by the value its message quotes. The messages below are SQL Server's own wording.
/// </summary>
public sealed class SqlErrorRedactionTests
{
    private const string Key = "7D1E5C0A-4B3F-4A8E-9C2D-1F6B8A3E5D90";
    private const string Email = "jane.secret@example.com";

    [Fact]
    public void A_duplicate_index_row_is_its_number_index_and_table_without_the_value()
    {
        var text = SqlErrorRedaction.Describe(
            2601, 14, 1, procedure: null, lineNumber: 1,
            $"Cannot insert duplicate key row in object 'sales.Orders' with unique index 'UX_Orders_CheckoutIdempotencyKey'. The duplicate key value is ({Key}).");

        Assert.Equal(
            "SQL Server error 2601 (class 14, state 1, line 1): duplicate key in unique index 'UX_Orders_CheckoutIdempotencyKey' on 'sales.Orders' (the value is withheld).",
            text);
    }

    [Fact]
    public void A_duplicate_constraint_is_its_number_and_constraint_without_the_value()
    {
        var text = SqlErrorRedaction.Describe(
            2627, 14, 1, procedure: "sp_x", lineNumber: 0,
            $"Violation of UNIQUE KEY constraint 'UQ_Users_Email'. Cannot insert duplicate key in object 'identity.Users'. The duplicate key value is ({Email}).");

        Assert.Equal(
            "SQL Server error 2627 (class 14, state 1, procedure sp_x): duplicate key in constraint 'UQ_Users_Email' on 'identity.Users' (the value is withheld).",
            text);
    }

    [Fact]
    public void A_value_that_looks_like_a_name_is_not_taken_for_one()
    {
        // The names are read from the part before the value, so a value shaped like "in object 'x'" is not read.
        var text = SqlErrorRedaction.Describe(
            2601, 14, 1, null, 0,
            "Cannot insert duplicate key row in object with unique index. The duplicate key value is (in object 'leak@example.com' with unique index 'leak').");

        Assert.DoesNotContain("leak", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(2628, "String or binary data would be truncated in table 'SpencerDenim.identity.Users', column 'Email'. Truncated value: 'jane.secret@exa'.")]
    [InlineData(8152, "String or binary data would be truncated. jane.secret@example.com")]
    [InlineData(245, "Conversion failed when converting the nvarchar value 'jane.secret@example.com' to data type int.")]
    [InlineData(50000, "Customer jane.secret@example.com is not allowed.")]
    [InlineData(99999, "An error nobody has met before, quoting jane.secret@example.com.")]
    public void Any_other_error_keeps_its_number_and_withholds_its_message(int number, string message)
    {
        var text = SqlErrorRedaction.Describe(number, 16, 2, null, 3, message);

        Assert.StartsWith($"SQL Server error {number} (class 16, state 2, line 3): message withheld", text, StringComparison.Ordinal);
        Assert.DoesNotContain("jane", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(208, "Invalid object name 'catalog.Departments'.")]
    [InlineData(547, "The INSERT statement conflicted with the FOREIGN KEY constraint \"FK_Orders_Districts\". The conflict occurred in database \"SpencerDenim\", table \"reference.Districts\", column 'Id'.")]
    [InlineData(-2, "Execution Timeout Expired.  The timeout period elapsed prior to completion of the operation or the server is not responding.")]
    public void An_error_whose_message_names_only_schema_objects_keeps_it(int number, string message)
    {
        var text = SqlErrorRedaction.Describe(number, 16, 1, null, 0, message);

        Assert.EndsWith(": " + message, text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData($"Cannot insert duplicate key row in object 'sales.Orders' with unique index 'UX_A'. The duplicate key value is ({Key}).\r\nThe statement has been terminated.", "The statement has been terminated.")]
    [InlineData("... column 'Email'. Truncated value: 'jane.secret@exa'.", "column 'Email'")]
    [InlineData("Conversion failed when converting the nvarchar value 'jane.secret@example.com' to data type int.", "Conversion failed when converting the nvarchar value (withheld).")]
    public void Scrub_cuts_each_value_quoting_shape_to_the_end_of_its_line(string text, string kept)
    {
        var scrubbed = SqlErrorRedaction.Scrub(text);

        Assert.DoesNotContain(Key, scrubbed, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("jane", scrubbed, StringComparison.Ordinal);
        Assert.Contains(kept, scrubbed, StringComparison.Ordinal);
    }

    [Fact]
    public void Scrub_leaves_ordinary_text_alone()
    {
        const string text = "HTTP POST /api/checkout/orders responded 201 in 12.3 ms";

        Assert.Same(text, SqlErrorRedaction.Scrub(text));
    }

    [Fact]
    public void A_wrapper_that_copied_the_message_is_cut_too()
    {
        var wrapped = new InvalidOperationException(
            "Placing the order failed: Cannot insert duplicate key row in object 'sales.Orders' with unique index 'UX_A'. "
            + $"The duplicate key value is ({Key}).");

        Assert.True(SqlErrorRedaction.NeedsRedaction(wrapped));
        Assert.DoesNotContain(Key, SqlErrorRedaction.SafeMessage(wrapped), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_exception_with_no_database_error_needs_nothing()
    {
        var plain = new InvalidOperationException("The bag is empty.");

        Assert.False(SqlErrorRedaction.NeedsRedaction(plain));
        Assert.Equal("The bag is empty.", SqlErrorRedaction.SafeMessage(plain));
        Assert.Empty(SqlErrorRedaction.Replacements(plain));
    }

    /// <summary>
    /// The first layer: EF Core writes the two events that print a failed command's exception at Debug, which
    /// production does not write, rather than at Error.
    /// </summary>
    [Theory]
    [InlineData("SaveChangesFailed")]
    [InlineData("QueryIterationFailed")]
    [InlineData("CommandError")]
    public void The_context_writes_the_database_error_events_at_debug(string name)
    {
        var id = name switch
        {
            "SaveChangesFailed" => CoreEventId.SaveChangesFailed,
            "QueryIterationFailed" => CoreEventId.QueryIterationFailed,
            _ => RelationalEventId.CommandError,
        };

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer("Server=unused;Database=unused;TrustServerCertificate=True")
            .Options;

        using var context = new AppDbContext(options);
        var warnings = context.GetService<IDbContextOptions>().FindExtension<CoreOptionsExtension>()!.WarningsConfiguration;

        Assert.Equal(LogLevel.Debug, warnings.GetLevel(id));
    }
}
