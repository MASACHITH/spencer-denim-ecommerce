using System.Reflection;

namespace SpencerDenim.Infrastructure;

/// <summary>Marker type used to locate the Infrastructure assembly.</summary>
public static class InfrastructureAssembly
{
    public static readonly Assembly Reference = typeof(InfrastructureAssembly).Assembly;
}
