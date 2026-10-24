using System.Reflection;
using System.Xml.Linq;

namespace Insurance.Web.Tests.Architecture;

/// <summary>
/// The layers of ADR 0001 (docs/architecture/adr/0001-layers.md), checked twice: the project files' references (what the build allows)
/// and the compiled assemblies' references (what the code uses). Domain is pure; Application knows Domain and, until its services move
/// to Infrastructure (B2/B6), Dapper and SqlClient — listed here so that the list can only shrink; Infrastructure knows the Application
/// and the Domain, never the API or the site; the API knows the Application's ports and the Domain, never a database library, the
/// adapters or the site. Only the site, the composition root, references all of them.
/// </summary>
public sealed class LayerTests
{
    private static readonly string Src = Path.Combine(Root(), "src");

    private static string Root()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "Insurance.sln"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("Insurance.sln not found above the test's directory");
    }

    private static string[] ProjectReferences(string project) =>
        XDocument.Load(Path.Combine(Src, project, project + ".csproj")).Descendants("ProjectReference")
            .Select(e => Path.GetFileNameWithoutExtension(e.Attribute("Include")!.Value.Replace('\\', '/')))
            .Order(StringComparer.Ordinal).ToArray();

    private static string[] PackageReferences(string project) =>
        XDocument.Load(Path.Combine(Src, project, project + ".csproj")).Descendants("PackageReference")
            .Select(e => e.Attribute("Include")!.Value).Order(StringComparer.Ordinal).ToArray();

    private static string[] AssemblyReferences(string assembly) =>
        Assembly.Load(assembly).GetReferencedAssemblies().Select(a => a.Name!).ToArray();

    [Fact]
    public void Each_project_references_only_the_layers_below_it()
    {
        Assert.Empty(ProjectReferences("Insurance.Domain"));
        Assert.Equal(["Insurance.Domain"], ProjectReferences("Insurance.Application"));
        Assert.Equal(["Insurance.Application", "Insurance.Domain"], ProjectReferences("Insurance.Infrastructure"));
        Assert.Equal(["Insurance.Application", "Insurance.Domain"], ProjectReferences("Insurance.Api"));
        Assert.Equal(["Insurance.Api", "Insurance.Application", "Insurance.Infrastructure"], ProjectReferences("Insurance.Web"));
        Assert.Equal(["Insurance.Application"], ProjectReferences("Insurance.Tools"));
    }

    [Fact]
    public void The_domain_has_no_packages_and_the_api_no_database_library()
    {
        Assert.Empty(PackageReferences("Insurance.Domain"));
        Assert.Equal(["Microsoft.AspNetCore.OpenApi"], PackageReferences("Insurance.Api"));
        // The Application's transitional database libraries (B2/B6 move the services that use them to Infrastructure).
        Assert.Contains("Dapper", PackageReferences("Insurance.Application"));
        Assert.Contains("Microsoft.Data.SqlClient", PackageReferences("Insurance.Application"));
    }

    [Fact]
    public void The_compiled_code_keeps_to_the_layers()
    {
        var domain = AssemblyReferences("Insurance.Domain");
        Assert.All(domain, a => Assert.True(a is "System.Runtime" || a.StartsWith("System.", StringComparison.Ordinal) || a == "netstandard",
            $"Insurance.Domain references {a}"));
        var api = AssemblyReferences("Insurance.Api");
        foreach (var forbidden in new[] { "Dapper", "Microsoft.Data.SqlClient", "Insurance.Infrastructure", "Insurance.Web" })
            Assert.DoesNotContain(forbidden, api);
        var infrastructure = AssemblyReferences("Insurance.Infrastructure");
        foreach (var forbidden in new[] { "Insurance.Api", "Insurance.Web", "Microsoft.AspNetCore.Http.Abstractions", "Microsoft.AspNetCore.Routing" })
            Assert.DoesNotContain(forbidden, infrastructure);
        Assert.DoesNotContain("Insurance.Infrastructure", AssemblyReferences("Insurance.Application"));
        Assert.DoesNotContain("Insurance.Api", AssemblyReferences("Insurance.Application"));
    }
}
