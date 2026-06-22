using System.Linq;
using ProjectChimera.Core;
using Xunit;

namespace ProjectChimera.Sim.Tests;

public class GodotFreeBoundaryTest
{
    // Fixed is compiled INTO this test assembly via shared source, so its assembly
    // is the test assembly. If any included sim file leaked `using Godot` or a
    // ProjectReference dragged GodotSharp in, this assembly would reference it.
    [Fact]
    public void SimAssembly_DoesNotReference_GodotSharp()
    {
        var refs = typeof(Fixed).Assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name)
            .ToArray();

        Assert.DoesNotContain("GodotSharp", refs);
        Assert.DoesNotContain("GodotSharpEditor", refs);
    }
}
