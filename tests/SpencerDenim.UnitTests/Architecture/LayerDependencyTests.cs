using System.Reflection;
using System.Xml.Linq;
using SpencerDenim.Application;
using SpencerDenim.Domain;
using SpencerDenim.Infrastructure;

namespace SpencerDenim.UnitTests.Architecture;

/// <summary>
/// Enforces the clean-architecture dependency rule (doc 03 §3):
/// Domain → nothing; Application → Domain; Infrastructure → Application; Api → Application + Infrastructure.
/// Checked twice: on the project files (declared references) and on the compiled assemblies (actual references).
/// </summary>
public sealed class LayerDependencyTests
{
    private const string Domain = "SpencerDenim.Domain";
    private const string Application = "SpencerDenim.Application";
    private const string Infrastructure = "SpencerDenim.Infrastructure";
    private const string Api = "SpencerDenim.Api";

    private static readonly Assembly ApiAssembly = typeof(SpencerDenim.Api.Endpoints.PingEndpoints).Assembly;

    public static TheoryData<string, string[]> AllowedProjectReferences => new()
    {
        { Domain, [] },
        { Application, [Domain] },
        { Infrastructure, [Application] },
        { Api, [Application, Infrastructure] },
    };

    [Theory]
    [MemberData(nameof(AllowedProjectReferences))]
    public void Project_references_follow_the_dependency_rule(string project, string[] expected)
    {
        var actual = ReadProjectReferences(project);

        Assert.Equal(expected.Order(StringComparer.Ordinal), actual.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Domain_project_has_no_package_references()
    {
        var packages = LoadProject(Domain).Descendants("PackageReference").ToList();

        Assert.Empty(packages);
    }

    [Fact]
    public void Domain_assembly_does_not_reference_other_layers_or_frameworks()
    {
        var references = ReferencedNames(DomainAssembly.Reference);

        Assert.DoesNotContain(references, name => name.StartsWith("SpencerDenim.", StringComparison.Ordinal));
        Assert.DoesNotContain(references, name => name.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal));
        Assert.DoesNotContain(references, name => name.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal));
    }

    [Fact]
    public void Application_assembly_does_not_reference_infrastructure_api_or_ef_core()
    {
        var references = ReferencedNames(ApplicationAssembly.Reference);

        Assert.DoesNotContain(Infrastructure, references);
        Assert.DoesNotContain(Api, references);
        Assert.DoesNotContain(references, name => name.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal));
        Assert.DoesNotContain(references, name => name.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal));
    }

    [Fact]
    public void Infrastructure_assembly_does_not_reference_api()
    {
        var references = ReferencedNames(InfrastructureAssembly.Reference);

        Assert.DoesNotContain(Api, references);
        Assert.Contains(Application, references);
    }

    [Fact]
    public void Api_assembly_is_the_composition_root()
    {
        var references = ReferencedNames(ApiAssembly);

        Assert.Contains(Application, references);
        Assert.Contains(Infrastructure, references);
    }

    private static HashSet<string> ReferencedNames(Assembly assembly) =>
        assembly.GetReferencedAssemblies().Select(name => name.Name ?? string.Empty).ToHashSet(StringComparer.Ordinal);

    private static List<string> ReadProjectReferences(string project) =>
        LoadProject(project)
            .Descendants("ProjectReference")
            .Select(element => (string?)element.Attribute("Include") ?? string.Empty)
            .Select(include => Path.GetFileNameWithoutExtension(include.Replace('\\', '/')))
            .ToList();

    private static XDocument LoadProject(string project)
    {
        var path = Path.Combine(FindBackendDirectory(), project, project + ".csproj");
        Assert.True(File.Exists(path), $"Project file not found: {path}");
        return XDocument.Load(path);
    }

    private static string FindBackendDirectory()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "src", "backend");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException("Could not locate src/backend above the test output directory.");
    }
}
