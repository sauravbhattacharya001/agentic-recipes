using System.Reflection;
using Xunit;

namespace AgenticRecipes.Tests;

/// <summary>
/// Meta-tests that keep the repo's documented contract honest.
///
/// The root <c>README.md</c> promises the suite "passes out of the box with no
/// skipped tests and no external dependencies ... proven against a deterministic
/// stand-in model rather than a live endpoint." Those are checkable facts, so we
/// check them here instead of trusting prose to stay true: a stray
/// <c>[Fact(Skip = ...)]</c> or a recipe that reaches for a cloud credential
/// would silently make the README a lie. These tests fail first instead.
/// </summary>
public class SuiteContractTests
{
    /// <summary>
    /// No test in the suite may be skipped. The recipes are fully offline
    /// (their logic is mirrored into this assembly and driven by injected
    /// in-memory models), so there is never a legitimate reason to gate a test
    /// behind <c>Skip</c>. This guards the README's "no skipped tests" claim and
    /// catches the drift that previously left 5-7 dead <c>[Fact(Skip=...)]</c>
    /// tests hidden in the suite.
    /// </summary>
    [Fact]
    public void NoTestIsSkipped()
    {
        var skipped = new List<string>();

        foreach (var type in typeof(SuiteContractTests).Assembly.GetTypes())
        {
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            {
                foreach (var attr in method.GetCustomAttributes(inherit: true))
                {
                    // Cover both [Fact] and [Theory] (FactAttribute is the base of both);
                    // read the Skip property generically so we don't need a compile-time
                    // reference to each attribute type.
                    var skipProp = attr.GetType().GetProperty("Skip");
                    if (skipProp is null || skipProp.PropertyType != typeof(string)) continue;
                    if (attr is not FactAttribute) continue;

                    var reason = skipProp.GetValue(attr) as string;
                    if (!string.IsNullOrWhiteSpace(reason))
                        skipped.Add($"{type.Name}.{method.Name} (Skip = \"{reason}\")");
                }
            }
        }

        Assert.True(
            skipped.Count == 0,
            "The suite must have no skipped tests (README: \"no skipped tests\"), but found:\n  " +
            string.Join("\n  ", skipped));
    }

    /// <summary>
    /// No recipe may depend on a live cloud endpoint or API key. Every recipe is
    /// meant to run deterministically offline, so its <c>Program.cs</c> must not
    /// read Azure/OpenAI credentials or any environment variable to function.
    /// This guards the README's "No API keys or cloud endpoints are required."
    /// </summary>
    [Fact]
    public void RecipeProgramsAreOffline()
    {
        var recipesDir = FindRecipesDir();
        var programs = Directory.GetFiles(recipesDir, "Program.cs", SearchOption.AllDirectories);

        Assert.NotEmpty(programs); // sanity: we actually found the recipe sources

        string[] forbidden =
        {
            "AZURE_OPENAI",
            "OPENAI_API_KEY",
            "Environment.GetEnvironmentVariable",
        };

        var offenders = new List<string>();
        foreach (var file in programs)
        {
            var text = File.ReadAllText(file);
            foreach (var needle in forbidden)
                if (text.Contains(needle, StringComparison.Ordinal))
                    offenders.Add($"{Path.GetFileName(Path.GetDirectoryName(file))}/Program.cs contains \"{needle}\"");
        }

        Assert.True(
            offenders.Count == 0,
            "Recipes must run offline with no credentials (README: \"No API keys or cloud endpoints are required.\"), but found:\n  " +
            string.Join("\n  ", offenders));
    }

    // Walk up from the test assembly's location to the repo root (the directory
    // that contains the sibling "recipes" folder). Works from both the local
    // bin/<config>/net8.0 output and CI, without hardcoding a path.
    private static string FindRecipesDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "recipes");
            if (Directory.Exists(candidate) &&
                Directory.GetFiles(candidate, "Program.cs", SearchOption.AllDirectories).Length > 0)
                return candidate;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not locate the 'recipes' directory above " + AppContext.BaseDirectory);
    }
}
