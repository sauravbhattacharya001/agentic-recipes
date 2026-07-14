using System.Text.RegularExpressions;
using Xunit;

namespace AgenticRecipes.Tests;

/// <summary>
/// Meta-tests that keep the recipe catalog and the root <c>README.md</c> in sync.
///
/// The README's "Recipes" table is the advertised catalog of orchestration
/// patterns. Two kinds of drift silently make it a lie: (a) a recipe folder is
/// added under <c>recipes/</c> but never listed in the table (an undocumented
/// recipe), or (b) a table row survives after its recipe folder is deleted (a
/// dead link to a pattern that no longer ships). Both are exactly the sort of
/// doc/scope drift this repo is meant to stay free of, so we check the facts
/// here instead of trusting the prose to stay accurate.
///
/// These also enforce the structural contract every recipe must satisfy:
/// a runnable <c>Program.cs</c>, a project file, and its own <c>README.md</c>.
/// </summary>
public class RecipeCatalogTests
{
    [Fact]
    public void EveryRecipeFolder_IsListedInRootReadmeTable()
    {
        var (repoRoot, recipesDir) = FindRepo();
        var folders = RecipeFolders(recipesDir);
        var linked = ReadmeLinkedRecipeSlugs(repoRoot);

        var undocumented = folders.Where(f => !linked.Contains(f)).OrderBy(f => f).ToList();

        Assert.True(
            undocumented.Count == 0,
            "Every recipe under recipes/ must have a row in the root README table, but these are missing:\n  " +
            string.Join("\n  ", undocumented));
    }

    [Fact]
    public void EveryReadmeRecipeLink_PointsToARealRecipeFolder()
    {
        var (repoRoot, recipesDir) = FindRepo();
        var folders = RecipeFolders(recipesDir);
        var linked = ReadmeLinkedRecipeSlugs(repoRoot);

        var dangling = linked.Where(l => !folders.Contains(l)).OrderBy(l => l).ToList();

        Assert.True(
            dangling.Count == 0,
            "Every recipes/<name>/ link in the root README must point to a real recipe folder, but these are dead:\n  " +
            string.Join("\n  ", dangling));
    }

    [Fact]
    public void EveryRecipeFolder_HasProgramCsprojAndReadme()
    {
        var (_, recipesDir) = FindRepo();
        var missing = new List<string>();

        foreach (var dir in Directory.GetDirectories(recipesDir))
        {
            var name = Path.GetFileName(dir);
            if (!File.Exists(Path.Combine(dir, "Program.cs")))
                missing.Add($"{name}: Program.cs");
            if (Directory.GetFiles(dir, "*.csproj").Length == 0)
                missing.Add($"{name}: *.csproj");
            if (!File.Exists(Path.Combine(dir, "README.md")))
                missing.Add($"{name}: README.md");
        }

        Assert.True(
            missing.Count == 0,
            "Every recipe folder must ship a Program.cs, a project file, and a README.md, but these are missing:\n  " +
            string.Join("\n  ", missing));
    }

    // ── Helpers ────────────────────────────────────────────────

    private static IReadOnlySet<string> RecipeFolders(string recipesDir) =>
        Directory.GetDirectories(recipesDir)
            .Select(Path.GetFileName)
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n!)
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// Distinct recipe slugs referenced by <em>relative</em> markdown links of the
    /// form <c>](recipes/&lt;slug&gt;/)</c> in the root README. This is exactly how a
    /// reader navigates to a recipe, so a slug here is a promise that
    /// <c>recipes/&lt;slug&gt;/</c> exists. Anchoring on the <c>](</c> link opener
    /// deliberately excludes absolute CI/coverage badge URLs such as
    /// <c>github.com/&lt;owner&gt;/agentic-recipes/actions/</c>, which merely contain
    /// the substring "recipes/" and are not navigation into the catalog.
    /// </summary>
    private static IReadOnlySet<string> ReadmeLinkedRecipeSlugs(string repoRoot)
    {
        var readme = File.ReadAllText(Path.Combine(repoRoot, "README.md"));
        var slugs = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(readme, @"\]\(recipes/([a-z0-9][a-z0-9-]*)/"))
            slugs.Add(m.Groups[1].Value);
        return slugs;
    }

    // Walk up from the test assembly to the repo root (the directory that holds
    // both README.md and the recipes/ folder). Works from bin/<config>/net8.0
    // and CI without hardcoding a path.
    private static (string RepoRoot, string RecipesDir) FindRepo()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var recipes = Path.Combine(dir.FullName, "recipes");
            var readme = Path.Combine(dir.FullName, "README.md");
            if (Directory.Exists(recipes) && File.Exists(readme) &&
                Directory.GetFiles(recipes, "Program.cs", SearchOption.AllDirectories).Length > 0)
                return (dir.FullName, recipes);
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not locate the repo root (README.md + recipes/) above " + AppContext.BaseDirectory);
    }
}
