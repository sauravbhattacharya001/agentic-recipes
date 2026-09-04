using System.Text.RegularExpressions;
using Xunit;

namespace AgenticRecipes.Tests;

/// <summary>
/// Meta-tests that keep the test suite's <b>mirror convention</b> honest.
///
/// Each recipe is a standalone top-level program, not a project reference, so the
/// tests re-declare ("mirror") the recipe's supporting types and logic into this
/// assembly and exercise the copy. That copy is the single biggest silent-drift
/// risk in the repo: a recipe can change a record's fields (or add/remove a
/// supporting type) while its mirrored copy in <c>tests/</c> stays stale — leaving
/// a green suite that no longer tests what the recipe actually does. Prose in the
/// test headers ("mirrors recipes/&lt;name&gt;/Program.cs") can't enforce that, so
/// these tests do.
///
/// They check two structural facts that must hold for the mirror to be trustworthy:
///  1. every test file that claims to mirror a recipe names a recipe that exists; and
///  2. every positional <c>record</c> mirrored in a test has a type in that recipe's
///     <c>Program.cs</c> with the SAME ordered field signature (types + names),
///     allowing only a disambiguating type-name prefix (e.g. <c>Reflexion</c>) that
///     the tests add to avoid collisions in the shared global namespace; and
///  3. every <c>enum</c> mirrored in a test has the SAME ordered member list as the
///     recipe's enum (order is behavioural — it is the default backing value).
/// A drift that these can't see (a changed method body) is out of scope here; these
/// pin the type-shape contract, which is where field renames/reorders would bite.
/// </summary>
public class MirrorContractTests
{
    private static readonly Regex MirrorRef =
        new(@"mirror(?:s|ed)?\s+(?:from\s+)?recipes/(?<name>[a-z0-9\-]+)/Program\.cs",
            RegexOptions.IgnoreCase);

    // A positional record header: `record Foo(...args...)` possibly spanning lines.
    private static readonly Regex PositionalRecord =
        new(@"\brecord\s+(?<name>\w+)\s*\((?<args>[^)]*)\)", RegexOptions.Singleline);

    // An enum declaration with its brace body: `enum Foo { A, B = 2, C }` (possibly
    // spanning lines). The body is captured so we can pin the ordered member list.
    private static readonly Regex EnumDecl =
        new(@"\benum\s+(?<name>\w+)\s*\{(?<body>[^}]*)\}", RegexOptions.Singleline);

    [Fact]
    public void EveryMirrorReferenceNamesAnExistingRecipe()
    {
        var recipesDir = FindDir("recipes");
        var testsDir = FindDir("tests");
        var offenders = new List<string>();
        var checkedAny = false;

        foreach (var testFile in Directory.GetFiles(testsDir, "*Tests.cs", SearchOption.TopDirectoryOnly))
        {
            var text = File.ReadAllText(testFile);
            foreach (Match m in MirrorRef.Matches(text))
            {
                checkedAny = true;
                var recipe = m.Groups["name"].Value;
                var program = Path.Combine(recipesDir, recipe, "Program.cs");
                if (!File.Exists(program))
                    offenders.Add($"{Path.GetFileName(testFile)} mirrors recipes/{recipe}/Program.cs — no such recipe");
            }
        }

        Assert.True(checkedAny, "Expected at least one 'mirrors recipes/<name>/Program.cs' reference in the test suite.");
        Assert.True(offenders.Count == 0,
            "Every mirror reference must point at a real recipe, but found:\n  " + string.Join("\n  ", offenders));
    }

    [Fact]
    public void MirroredPositionalRecordsMatchTheRecipeFieldSignature()
    {
        var recipesDir = FindDir("recipes");
        var testsDir = FindDir("tests");
        var offenders = new List<string>();
        var comparisons = 0;

        foreach (var testFile in Directory.GetFiles(testsDir, "*Tests.cs", SearchOption.TopDirectoryOnly))
        {
            var testText = File.ReadAllText(testFile);
            var refMatch = MirrorRef.Match(testText);
            if (!refMatch.Success) continue;

            var recipe = refMatch.Groups["name"].Value;
            var program = Path.Combine(recipesDir, recipe, "Program.cs");
            if (!File.Exists(program)) continue; // covered by the other test

            // Index the recipe's positional records by normalized field signature so we
            // can match a test's mirrored record even when it carries a disambiguation
            // prefix on the TYPE name (fields must still line up exactly).
            var recipeRecords = PositionalRecord.Matches(File.ReadAllText(program))
                .Select(m => (Name: m.Groups["name"].Value, Fields: NormalizeFields(m.Groups["args"].Value)))
                .ToList();

            foreach (Match tm in PositionalRecord.Matches(testText))
            {
                var name = tm.Groups["name"].Value;
                var fields = NormalizeFields(tm.Groups["args"].Value);
                if (fields.Count == 0) continue; // parameterless/edge — nothing to compare

                comparisons++;

                // Accept a match if some recipe record shares this field signature AND its
                // type name equals the test's name or a suffix of it (the tests only ever
                // PREFIX for disambiguation, e.g. Evaluation -> ReflexionEvaluation).
                var ok = recipeRecords.Any(r =>
                    FieldsMatch(r.Fields, fields) &&
                    (r.Name == name || name.EndsWith(r.Name, StringComparison.Ordinal)));

                if (!ok)
                    offenders.Add(
                        $"{Path.GetFileName(testFile)}: mirrored record '{name}({string.Join(", ", fields)})' " +
                        $"has no field-matching counterpart in recipes/{recipe}/Program.cs");
            }
        }

        Assert.True(comparisons > 0, "Expected to compare at least one mirrored positional record.");
        Assert.True(offenders.Count == 0,
            "Mirrored records must match the recipe's field signature (drift detected):\n  " +
            string.Join("\n  ", offenders));
    }

    /// <summary>
    /// Closes the gap that lets a mirror escape the field-signature contract entirely:
    /// the check above only inspects test files that carry a
    /// <c>mirrors recipes/&lt;name&gt;/Program.cs</c> reference. A test file that
    /// re-declares a recipe-LOCAL positional record but omits that phrase would never be
    /// compared, so a field rename/reorder in that recipe could drift silently past a
    /// green suite. This asserts the inverse: if a test re-declares a positional record
    /// whose exact field signature matches a recipe-local record, that test file MUST
    /// carry a mirror reference (so the comparison above actually runs on it).
    /// </summary>
    [Fact]
    public void TestsThatMirrorRecipeLocalRecordsCarryAMirrorReference()
    {
        var recipesDir = FindDir("recipes");
        var testsDir = FindDir("tests");

        // Index every recipe-local positional record by normalized field signature.
        var recipeSignatures = new HashSet<string>();
        foreach (var program in Directory.GetFiles(recipesDir, "Program.cs", SearchOption.AllDirectories))
            foreach (Match m in PositionalRecord.Matches(File.ReadAllText(program)))
            {
                var fields = NormalizeFields(m.Groups["args"].Value);
                if (fields.Count > 0) recipeSignatures.Add(SignatureKey(fields));
            }

        var offenders = new List<string>();
        foreach (var testFile in Directory.GetFiles(testsDir, "*Tests.cs", SearchOption.TopDirectoryOnly))
        {
            var text = File.ReadAllText(testFile);
            if (MirrorRef.IsMatch(text)) continue; // already under the contract

            foreach (Match tm in PositionalRecord.Matches(text))
            {
                var fields = NormalizeFields(tm.Groups["args"].Value);
                if (fields.Count == 0) continue;
                if (recipeSignatures.Contains(SignatureKey(fields)))
                    offenders.Add(
                        $"{Path.GetFileName(testFile)}: re-declares record '{tm.Groups["name"].Value}' " +
                        $"matching a recipe-local record but carries no " +
                        "'mirrors recipes/<name>/Program.cs' reference — add one so the mirror is validated");
            }
        }

        Assert.True(offenders.Count == 0,
            "Test files that mirror a recipe-local record must reference the recipe so the " +
            "field-signature contract covers them:\n  " + string.Join("\n  ", offenders));
    }

    // A field signature ignores the type NAME (which the tests may prefix to
    // disambiguate) and compares on ordered field names + normalized non-nullable
    // core types, matching the tolerance in <see cref="FieldsMatch"/>.
    private static string SignatureKey(List<string> fields)
    {
        var parts = new List<string>(fields.Count);
        foreach (var f in fields)
        {
            var (type, name) = SplitField(f);
            var nullable = type.EndsWith("?", StringComparison.Ordinal) ? "?" : "";
            parts.Add($"{type.TrimEnd('?')}{nullable} {name}");
        }
        return string.Join(", ", parts);
    }

    // Split a record's argument list into normalized "type name" fields, tolerant of
    // default values, generic commas (IReadOnlyList<string> has none, but be safe),
    // and whitespace/newlines.
    private static List<string> NormalizeFields(string args)
    {
        var trimmed = args.Trim();
        if (trimmed.Length == 0) return new List<string>();

        var fields = new List<string>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i < trimmed.Length; i++)
        {
            var c = trimmed[i];
            if (c is '<' or '(' or '[') depth++;
            else if (c is '>' or ')' or ']') depth--;
            else if (c == ',' && depth == 0)
            {
                fields.Add(NormalizeField(trimmed[start..i]));
                start = i + 1;
            }
        }
        fields.Add(NormalizeField(trimmed[start..]));
        return fields;
    }

    private static string NormalizeField(string field)
    {
        // Drop a default value ("= 1.0") and collapse whitespace to a single space so
        // "double  Confidence = 1.0" and "double Confidence" compare as the same field.
        var eq = field.IndexOf('=');
        if (eq >= 0) field = field[..eq];
        return Regex.Replace(field.Trim(), @"\s+", " ");
    }

    // Compare two field lists field-by-field. Names must match exactly; the TYPE may
    // differ only by a disambiguation prefix the tests add to a mirrored type (e.g. the
    // recipe's `Evaluation` becomes `ReflexionEvaluation` in the test, so a field typed
    // `Evaluation Evaluation` mirrors to `ReflexionEvaluation Evaluation`). Anything else
    // — a real rename, reorder, or added/removed field — is genuine drift and fails.
    private static bool FieldsMatch(List<string> recipe, List<string> test)
    {
        if (recipe.Count != test.Count) return false;
        for (var i = 0; i < recipe.Count; i++)
        {
            var (rType, rName) = SplitField(recipe[i]);
            var (tType, tName) = SplitField(test[i]);
            if (rName != tName) return false;
            if (tType == rType) continue;
            // Allow a prefixed type name, comparing on the non-nullable core so
            // `string?` still lines up with `string?`.
            var rCore = rType.TrimEnd('?');
            var tCore = tType.TrimEnd('?');
            var nullMatch = rType.EndsWith("?", StringComparison.Ordinal) == tType.EndsWith("?", StringComparison.Ordinal);
            if (!(nullMatch && (tCore == rCore || tCore.EndsWith(rCore, StringComparison.Ordinal))))
                return false;
        }
        return true;
    }

    // Split "IReadOnlyList<string> OpenIssues" into (type, name) on the LAST space that
    // isn't inside angle brackets — the record parameter name is always the final token.
    private static (string Type, string Name) SplitField(string field)
    {
        var depth = 0;
        for (var i = field.Length - 1; i >= 0; i--)
        {
            var c = field[i];
            if (c is '>' or ')' or ']') depth++;
            else if (c is '<' or '(' or '[') depth--;
            else if (c == ' ' && depth == 0)
                return (field[..i].Trim(), field[(i + 1)..].Trim());
        }
        return (field, "");
    }

    /// <summary>
    /// Extends the mirror contract to <c>enum</c>s. Records are covered above, but
    /// several recipes also mirror an enum (e.g. <c>GuardAction</c>, <c>ReflexionOutcome</c>,
    /// <c>ToTOutcome</c>) into their test file, and enum member ORDER is behavioural in
    /// C# — the default backing value is the ordinal position, so a reorder silently
    /// changes every comparison, serialization, and switch-default that leans on it.
    /// The record check can't see that, so a mirrored enum could drift (a reordered,
    /// renamed, added, or removed member) past a green suite. This asserts that any
    /// enum re-declared in a test whose name matches a recipe-local enum has the SAME
    /// ordered member names as the recipe's definition.
    /// </summary>
    [Fact]
    public void MirroredEnumsMatchTheRecipeMemberOrder()
    {
        var recipesDir = FindDir("recipes");
        var testsDir = FindDir("tests");

        // Index every recipe-local enum by name → ordered member list. Enum names are
        // not prefixed for disambiguation (unlike mirrored record types), so a plain
        // name match is exact.
        var recipeEnums = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var program in Directory.GetFiles(recipesDir, "Program.cs", SearchOption.AllDirectories))
            foreach (Match m in EnumDecl.Matches(File.ReadAllText(program)))
                recipeEnums[m.Groups["name"].Value] = EnumMembers(m.Groups["body"].Value);

        var offenders = new List<string>();
        var comparisons = 0;

        foreach (var testFile in Directory.GetFiles(testsDir, "*Tests.cs", SearchOption.TopDirectoryOnly))
        {
            var text = File.ReadAllText(testFile);
            foreach (Match tm in EnumDecl.Matches(text))
            {
                var name = tm.Groups["name"].Value;
                if (!recipeEnums.TryGetValue(name, out var recipeMembers)) continue;

                var testMembers = EnumMembers(tm.Groups["body"].Value);
                if (testMembers.Count == 0) continue;
                comparisons++;

                if (!recipeMembers.SequenceEqual(testMembers, StringComparer.Ordinal))
                    offenders.Add(
                        $"{Path.GetFileName(testFile)}: mirrored enum '{name}' members " +
                        $"[{string.Join(", ", testMembers)}] differ from the recipe's " +
                        $"[{string.Join(", ", recipeMembers)}] (order matters — it is the backing value)");
            }
        }

        Assert.True(comparisons > 0, "Expected to compare at least one mirrored enum.");
        Assert.True(offenders.Count == 0,
            "Mirrored enums must match the recipe's ordered member list (drift detected):\n  " +
            string.Join("\n  ", offenders));
    }

    // Parse an enum body into its ordered member NAMES, dropping explicit `= value`
    // assignments, line/block comments, and empty trailing entries. A member value
    // can itself contain commas (e.g. `= (1 << 0)`), so split on top-level commas.
    private static List<string> EnumMembers(string body)
    {
        // Strip comments first so a `// A, B` note can't be read as members.
        body = Regex.Replace(body, @"/\*.*?\*/", " ", RegexOptions.Singleline);
        body = Regex.Replace(body, @"//[^\n]*", " ");

        var members = new List<string>();
        var depth = 0;
        var start = 0;
        void Flush(string raw)
        {
            var token = raw.Trim();
            if (token.Length == 0) return;
            var eq = token.IndexOf('=');
            if (eq >= 0) token = token[..eq].Trim();
            if (token.Length > 0) members.Add(token);
        }
        for (var i = 0; i < body.Length; i++)
        {
            var c = body[i];
            if (c is '(' or '[' or '<') depth++;
            else if (c is ')' or ']' or '>') depth--;
            else if (c == ',' && depth == 0)
            {
                Flush(body[start..i]);
                start = i + 1;
            }
        }
        Flush(body[start..]);
        return members;
    }

    private static string FindDir(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, name);
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException($"Could not locate the '{name}' directory above {AppContext.BaseDirectory}");
    }
}
