using System.Reflection;

namespace SpencerDenim.Domain;

/// <summary>
/// Marker type used to locate the Domain assembly (architecture tests, assembly scanning).
/// The Domain layer has no project or package dependencies.
/// </summary>
public static class DomainAssembly
{
    public static readonly Assembly Reference = typeof(DomainAssembly).Assembly;
}
