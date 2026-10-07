using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SpencerDenim.Application.Content.Abstractions;
using SpencerDenim.Application.Content.Admin.Abstractions;
using SpencerDenim.Domain.Content;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.Infrastructure.Persistence.Interceptors;

namespace SpencerDenim.Infrastructure.Content;

/// <summary>
/// The EF Core side of <see cref="IContentPageStore"/> (ADM-17, GLB-10).
/// <para>
/// It hands out tracked entities and keeps one unit of work, so the handlers decide what a change is and
/// this class decides only how it reaches SQL Server — the arrangement <c>CatalogStructureStore</c>
/// established. The two judgements it makes are the two the database alone can make: a row somebody else
/// changed first, and a unique index.
/// </para>
/// </summary>
internal sealed class ContentPageStore(AppDbContext dbContext, AuditAndSoftDeleteInterceptor auditColumns)
    : IContentPageStore
{
    /// <summary>SQL Server: 2627 is a unique-constraint violation, 2601 a unique-index one.</summary>
    private static readonly int[] DuplicateKeyErrors = [2601, 2627];

    public Task<ContentPage?> FindAsync(int pageId, CancellationToken cancellationToken) =>
        dbContext.ContentPages.FirstOrDefaultAsync(page => page.Id == pageId, cancellationToken);

    public void Add(ContentPage page)
    {
        ArgumentNullException.ThrowIfNull(page);

        dbContext.ContentPages.Add(page);
    }

    public Task<bool> SlugTakenAsync(string slug, int? exceptPageId, CancellationToken cancellationToken) =>
        dbContext.ContentPages.AnyAsync(
            page => page.Slug == slug && (exceptPageId == null || page.Id != exceptPageId),
            cancellationToken);

    public Task<bool> DepartmentSlugTakenAsync(string slug, CancellationToken cancellationToken) =>
        // Every department, active or not. A deactivated department's slug is still a route the storefront
        // declares above the content-page route, so a page saved on it would be just as unreachable — and
        // the client can switch the department back on at any time.
        dbContext.Departments.AnyAsync(department => department.Slug == slug, cancellationToken);

    public async Task<ContentSaveOutcome> SaveAsync(int? actingUserId, CancellationToken cancellationToken)
    {
        auditColumns.CurrentUserId = actingUserId;

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return ContentSaveOutcome.Saved;
        }
        catch (DbUpdateConcurrencyException)
        {
            // Nothing was written: the UPDATE's RowVersion predicate matched no row, so SQL Server applied
            // none of the batch. The caller reloads and re-applies (doc 08 §1.5).
            return ContentSaveOutcome.ConcurrencyConflict;
        }
        catch (DbUpdateException exception) when (IsDuplicateKey(exception))
        {
            return ContentSaveOutcome.DuplicateKey;
        }
    }

    /// <summary>
    /// Whether a failed save was a unique index refusing the row. The handlers ask first, so reaching here
    /// means two requests raced between the check and the write — the one case a read-then-write cannot
    /// cover, and the reason the answer is 409 rather than a 500 with a constraint name in it.
    /// </summary>
    private static bool IsDuplicateKey(DbUpdateException exception) =>
        exception.InnerException is SqlException sql && DuplicateKeyErrors.Contains(sql.Number);
}

/// <summary>
/// The EF Core side of <see cref="IContactEnquiryStore"/> (GLB-10).
/// <para>
/// <b>Nothing in this class logs.</b> Doc 11 P8 records that a customer may paste bank account details
/// into the message, and the obvious diagnostic line on an insert failure — "could not save enquiry from
/// {email}" — is the leak. The exception travels to the endpoint, which answers without echoing anything
/// the caller sent.
/// </para>
/// </summary>
internal sealed class ContactEnquiryStore(AppDbContext dbContext) : IContactEnquiryStore
{
    public async Task AddAsync(ContactSubmission submission, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(submission);

        // No AuditAndSoftDeleteInterceptor here, unlike ContentPageStore, and that is not an omission:
        // ContactSubmissions carries no [A] audit columns (see ContactSubmissionConfiguration for why doc
        // 08 leaves them off a table a visitor inserts into), so there is nothing for the interceptor to
        // stamp and nothing to tell it about.
        dbContext.ContactSubmissions.Add(submission);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public Task<ContactSubmission?> FindAsync(int submissionId, CancellationToken cancellationToken) =>
        dbContext.ContactSubmissions.FirstOrDefaultAsync(
            submission => submission.Id == submissionId, cancellationToken);

    public Task SaveAsync(CancellationToken cancellationToken) =>
        dbContext.SaveChangesAsync(cancellationToken);

    public async Task<bool> DeleteAsync(int submissionId, CancellationToken cancellationToken)
    {
        // Doc 08 §10.2 (N12): a hard delete is set-based ExecuteDeleteAsync, never a tracked Remove() —
        // which also means nothing of the row is ever materialised into this process to be logged.
        //
        // The audit row the caller enlisted is committed in the same transaction as the delete. They are
        // two statements against two tables, so the explicit transaction is what makes them one event:
        // without it, ExecuteDeleteAsync commits on its own and a failure on the audit insert would leave
        // a permanent deletion of customer data with no trail (issue #59).
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        var removed = await dbContext.ContactSubmissions
            .Where(submission => submission.Id == submissionId)
            .ExecuteDeleteAsync(cancellationToken);

        if (removed == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        // The enlisted audit row. AuditLogWriter.Enlist added it to this same scoped AppDbContext, so this
        // save writes it inside the transaction opened above.
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return true;
    }
}
