using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace SpencerDenim.Infrastructure.Persistence;

/// <summary>
/// Doc 08 §1.4: every <c>datetime2(3)</c> column holds UTC. On write a local value is converted, and
/// on read the value comes back with <see cref="DateTimeKind.Utc"/> instead of
/// <see cref="DateTimeKind.Unspecified"/>, so nothing downstream can mistake it for local time.
/// </summary>
internal sealed class UtcDateTimeConverter() : ValueConverter<DateTime, DateTime>(
    value => value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : value,
    value => DateTime.SpecifyKind(value, DateTimeKind.Utc));
