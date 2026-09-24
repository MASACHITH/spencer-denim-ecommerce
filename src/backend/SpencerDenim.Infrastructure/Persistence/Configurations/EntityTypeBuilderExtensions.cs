using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SpencerDenim.Domain.Common;

namespace SpencerDenim.Infrastructure.Persistence.Configurations;

/// <summary>
/// The standard column groups of doc 08 §1.5, applied identically everywhere so no table drifts.
/// </summary>
internal static class EntityTypeBuilderExtensions
{
    /// <summary>
    /// Audit columns for a table marked <c>[A]</c>. <c>CreatedAtUtc</c> keeps a
    /// <c>sysutcdatetime()</c> default so a row inserted by a script or a migration is still stamped;
    /// the <c>SaveChanges</c> interceptor sets the value for everything the application writes.
    /// </summary>
    public static EntityTypeBuilder<TEntity> ApplyAuditColumns<TEntity>(this EntityTypeBuilder<TEntity> builder)
        where TEntity : class, IAuditable
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Property(entity => entity.CreatedAtUtc).HasDefaultValueSql("sysutcdatetime()").IsRequired();
        builder.Property(entity => entity.CreatedByUserId);
        builder.Property(entity => entity.UpdatedAtUtc);
        builder.Property(entity => entity.UpdatedByUserId);

        return builder;
    }

    /// <summary>
    /// Soft-delete columns and the named query filter for a table marked <c>[SD]</c>
    /// (doc 08 §1.5, §10.2).
    /// </summary>
    public static EntityTypeBuilder<TEntity> ApplySoftDelete<TEntity>(this EntityTypeBuilder<TEntity> builder)
        where TEntity : class, ISoftDeletable
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Property(entity => entity.IsDeleted).HasDefaultValue(false).IsRequired();
        builder.Property(entity => entity.DeletedAtUtc);
        builder.Property(entity => entity.DeletedByUserId);

        // The predicate is a constant, so EF renders "[IsDeleted] = CAST(0 AS bit)" as a literal and
        // the filtered indexes in doc 08 §3 can still be matched (DB28).
        builder.HasQueryFilter(AppDbContext.SoftDeleteFilter, entity => !entity.IsDeleted);

        return builder;
    }

    /// <summary>Concurrency token for a table marked <c>[RV]</c> (doc 08 §1.5), exchanged as an ETag.</summary>
    public static PropertyBuilder<byte[]?> ApplyRowVersion<TEntity>(
        this EntityTypeBuilder<TEntity> builder,
        System.Linq.Expressions.Expression<Func<TEntity, byte[]?>> property)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.Property(property).IsRowVersion();
    }
}
