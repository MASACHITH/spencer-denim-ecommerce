using System.Reflection;

namespace SpencerDenim.Application;

/// <summary>Marker type used to locate the Application assembly.</summary>
public static class ApplicationAssembly
{
    public static readonly Assembly Reference = typeof(ApplicationAssembly).Assembly;
}
