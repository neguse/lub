namespace TinyCs.Tests.SpecConformance;

public class SpecConformanceTests
{
    [Fact]
    public void Extract_ParsesAllAnnotationDirectives()
    {
        const string markdown = """
            <!-- Example: {
              template:"standalone-console",
              name:"AllDirectives",
              replaceEllipsis:false,
              customEllipsisReplacements:[null,"return 0;"],
              expectedErrors:["CS0001"],
              expectedWarnings:["CS0002"],
              ignoredWarnings:["CS0003"],
              expectedOutput:["hello"],
              inferOutput:true,
              ignoreOutput:true,
              expectedException:"Exception",
              additionalFiles:["Support.cs"],
              project:"Aliases",
              externAliasSupport:"ExternalAliases",
              executionArgs:["one","two"]
            } -->
            ```csharp
            class Program { }
            ```
            """;

        var example = Assert.Single(
            new SpecExampleExtractor().Extract("fixture.md", markdown));

        Assert.Equal("fixture.md", example.MdFile);
        Assert.Equal("AllDirectives", example.Name);
        Assert.Equal("standalone-console", example.Template);
        Assert.Equal(1, example.SourceLine);
        Assert.False(example.Annotation.ReplaceEllipsis);
        Assert.Equal(new string?[] { null, "return 0;" },
            example.Annotation.CustomEllipsisReplacements);
        Assert.Equal(["CS0001"], example.Annotation.ExpectedErrors);
        Assert.Equal(["CS0002"], example.Annotation.ExpectedWarnings);
        Assert.Equal(["CS0003"], example.Annotation.IgnoredWarnings);
        Assert.Equal(["hello"], example.Annotation.ExpectedOutput);
        Assert.True(example.Annotation.InferOutput);
        Assert.True(example.Annotation.IgnoreOutput);
        Assert.Equal("Exception", example.Annotation.ExpectedException);
        Assert.Equal(["Support.cs"], example.Annotation.AdditionalFiles);
        Assert.Equal("Aliases", example.Annotation.Project);
        Assert.Equal("ExternalAliases", example.Annotation.ExternAliasSupport);
        Assert.Equal(["one", "two"], example.Annotation.ExecutionArgs);
        Assert.Null(example.ExtractionFailureReason);
    }

    [Fact]
    public void Extract_AssociatesFirstAnnotationWithNextCsharpFence()
    {
        const string markdown = """
            > <!-- Example: {template:"standalone-lib", name:"First"} -->
            > Text and another comment may intervene.
            > <!-- unrelated -->
            > <!-- Example: {template:"standalone-lib", name:"Ignored"} -->
            > ```ANTLR
            > grammar_rule
            > ```
            > ```csharp
            > class C
            > {
            > }
            > ```
            """;

        var example = Assert.Single(
            new SpecExampleExtractor().Extract("fixture.md", markdown));

        Assert.Equal("First", example.Name);
        Assert.Equal("class C\n{\n}", example.Code);
    }

    [Fact]
    public void Extract_AcceptsLongerBacktickFencesUsedByCorpus()
    {
        const string markdown = """
            <!-- Example: {template:"standalone-lib", name:"LongFence"} -->
            ``````csharp
            class C { }
            ``````
            """;

        var example = Assert.Single(
            new SpecExampleExtractor().Extract("fixture.md", markdown));

        Assert.Equal("class C { }", example.Code);
        Assert.Null(example.ExtractionFailureReason);
    }

    [Fact]
    public void Extract_ReplacesEllipsesInOrderAndRemovesChevrons()
    {
        const string markdown = """
            <!-- Example: {template:"standalone-lib", name:"Ellipses", replaceEllipsis:true,
              customEllipsisReplacements:["first",null]} -->
            ```csharp
            ... … ... «Color»
            ```
            """;

        var example = Assert.Single(
            new SpecExampleExtractor().Extract("fixture.md", markdown));

        Assert.Equal("first /* ... */ /* ... */ Color", example.Code);
    }

    [Fact]
    public void Extract_PreservesEllipsesWhenReplacementIsFalse()
    {
        const string markdown = """
            <!-- Example: {template:"standalone-lib", name:"Ellipses"} -->
            ```csharp
            ... … «Color»
            ```
            """;

        var example = Assert.Single(
            new SpecExampleExtractor().Extract("fixture.md", markdown));

        Assert.Equal("... … Color", example.Code);
    }

    [Theory]
    [InlineData("<!-- Example: {not-json} -->", "annotation-parse-error")]
    [InlineData("<!-- Example: {template:\"standalone-lib\", name:\"NoCode\"} -->",
        "annotation-parse-error")]
    public void Extract_ReturnsUnextractedForInvalidAnnotationOrMissingCode(
        string markdown, string reason)
    {
        var example = Assert.Single(
            new SpecExampleExtractor().Extract("fixture.md", markdown));

        Assert.Equal(reason, example.ExtractionFailureReason);
    }

    [Fact]
    public void TemplateExpander_UsesOfficialTemplateAndAddsSources()
    {
        using var fixture = TemplateFixture.Create();
        var example = Example("code-in-main", """
            int value = 1;
            // File Extra.cs:
            class Extra { }
            """, additionalFiles: ["Support.cs"]);

        var expansion = new SpecTemplateExpander(fixture.Root).Expand(example);

        Assert.Null(expansion.UnextractedReason);
        Assert.True(expansion.IsExecutable);
        Assert.Equal(["Program.cs", "Extra.cs", "Support.cs"],
            expansion.Sources.Select(source => source.FileName));
        Assert.Equal("before\nint value = 1;\nafter", expansion.Sources[0].Code);
        Assert.Equal("class Extra { }", expansion.Sources[1].Code);
        Assert.Equal("class Support { }", expansion.Sources[2].Code);
    }

    [Fact]
    public void TemplateExpander_ReturnsUnextractedWhenAdditionalFileIsMissing()
    {
        using var fixture = TemplateFixture.Create();
        var example = Example("standalone-lib", "class C { }",
            additionalFiles: ["Missing.cs"]);

        var expansion = new SpecTemplateExpander(fixture.Root).Expand(example);

        Assert.Equal("additional-file-not-found:Missing.cs",
            expansion.UnextractedReason);
    }

    [Theory]
    [InlineData("extern-lib", null, null, null,
        "unsupported-template:extern-lib")]
    [InlineData("standalone-lib", "Aliases", null, null,
        "unsupported-template:extern-alias-support")]
    [InlineData("standalone-lib", null, "Exception", null,
        "unsupported-template:expected-exception")]
    [InlineData("standalone-lib", null, null, "arg",
        "unsupported-template:execution-args")]
    public void TemplateExpander_ClassifiesC0UnsupportedDirectives(
        string template, string? project, string? expectedException,
        string? executionArg, string reason)
    {
        using var fixture = TemplateFixture.Create();
        var annotation = new SpecAnnotation
        {
            Template = template,
            Name = "Unsupported",
            Project = project,
            ExpectedException = expectedException,
            ExecutionArgs = executionArg is null ? null : [executionArg]
        };

        var expansion = new SpecTemplateExpander(fixture.Root)
            .Expand(new SpecExample("fixture.md", annotation, "class C { }", 1));

        Assert.Equal(reason, expansion.UnextractedReason);
        Assert.Empty(expansion.Sources);
    }

    [Fact]
    public void Classifier_MatchesExpectedCompilerErrors()
    {
        var classifier = Classifier(new TranspileResult
        {
            Errors =
            [
                "a.cs(1,1): error CS0246: missing",
                "a.cs(2,1): error CS0246: missing"
            ]
        });
        var example = Example("standalone-lib", "class C { }",
            expectedErrors: ["CS0246", "CS0246"]);

        Assert.Equal(SpecClassification.CsErr,
            classifier.Classify(example, Expanded(example, false)).Category);
    }

    [Theory]
    [InlineData(false, null, "unexpected-compile-error")]
    [InlineData(true, "CS0001", "expected-error-mismatch")]
    public void Classifier_ClassifiesUnexpectedOrMismatchedCompilerErrors(
        bool hasExpectedErrors, string? expectedError, string reason)
    {
        var classifier = Classifier(new TranspileResult
        {
            Errors = ["a.cs(1,1): error CS0246: missing"]
        });
        var example = Example("standalone-lib", "class C { }",
            expectedErrors: hasExpectedErrors ? [expectedError!] : null);

        var result = classifier.Classify(example, Expanded(example, false));

        Assert.Equal(SpecClassification.Unextracted, result.Category);
        Assert.Equal(reason, result.Reason);
    }

    [Fact]
    public void Classifier_RequiresAnExpectedCompilerErrorToBeRaised()
    {
        var classifier = Classifier(new TranspileResult { Lua = "lua" });
        var example = Example("standalone-lib", "class C { }",
            expectedErrors: ["CS0001"]);

        var result = classifier.Classify(example, Expanded(example, false));

        Assert.Equal(SpecClassification.Unextracted, result.Category);
        Assert.Equal("expected-error-not-raised", result.Reason);
    }

    [Fact]
    public void Classifier_RecognizesTinyCsDiagnosticsAndIgnoresOtherWarnings()
    {
        var classifier = Classifier(new TranspileResult
        {
            Lua = "lua",
            Warnings = ["naming: ignored", "warning TCS1002: unsupported"]
        });
        var example = Example("standalone-console", "class Program { }");

        Assert.Equal(SpecClassification.Diag,
            classifier.Classify(example, Expanded(example, true)).Category);
    }

    [Theory]
    [InlineData("standalone-console", true, SpecClassification.InRun)]
    [InlineData("standalone-lib", false, SpecClassification.InCompile)]
    public void Classifier_ClassifiesPassingConsoleAndLibraryTemplates(
        string template, bool executable, SpecClassification expected)
    {
        var classifier = Classifier(new TranspileResult
        {
            Lua = "-- Generated by TinyC# transpiler"
        });
        var example = Example(template, "class C { }");

        Assert.Equal(expected,
            classifier.Classify(example, Expanded(example, executable)).Category);
    }

    [Fact]
    public void Classifier_RecordsThrowsAndEmptyLuaAsBugs()
    {
        var example = Example("standalone-lib", "class C { }");
        var throwing = new SpecConformanceClassifier(
            _ => throw new InvalidOperationException("boom"));
        var empty = Classifier(new TranspileResult());

        var thrown = throwing.Classify(example, Expanded(example, false));
        Assert.Equal(SpecClassification.Bug, thrown.Category);
        Assert.Contains("boom", thrown.Details);
        Assert.Equal(SpecClassification.Bug,
            empty.Classify(example, Expanded(example, false)).Category);
    }

    [Fact]
    public void SweepFormatting_IsDeterministicAndReportsReasonsAndBugs()
    {
        var examples = new[]
        {
            Classified("b.md", "B", SpecClassification.Bug, details: "boom"),
            Classified("a.md", "A", SpecClassification.Unextracted,
                reason: "annotation-parse-error")
        };

        var baseline = SpecConformanceSweep.CreateBaselineJson(examples);
        var report = SpecConformanceSweep.CreateReport(examples);

        Assert.True(baseline.IndexOf("a.md:A", StringComparison.Ordinal) <
            baseline.IndexOf("b.md:B", StringComparison.Ordinal));
        Assert.Contains("\"reason\": \"annotation-parse-error\"", baseline);
        Assert.Contains("Generated by run-spec-conformance.sh — do not edit",
            report);
        Assert.Contains("annotation-parse-error", report);
        Assert.Contains("b.md:B", report);
        Assert.Contains("boom", report);
    }

    [Fact]
    public void BaselineComparison_ReportsMissingExtraAndChangedEntries()
    {
        const string baseline = """
            {
              "a.md:A": { "category": "InCompile" },
              "missing.md:M": { "category": "Diag" }
            }
            """;
        var actual = new[]
        {
            Classified("a.md", "A", SpecClassification.InRun),
            Classified("extra.md", "E", SpecClassification.InCompile)
        };

        var difference = SpecConformanceSweep.CompareBaseline(baseline, actual);

        Assert.NotNull(difference);
        Assert.Contains("missing from current sweep", difference);
        Assert.Contains("missing.md:M", difference);
        Assert.Contains("extra in current sweep", difference);
        Assert.Contains("extra.md:E", difference);
        Assert.Contains("changed", difference);
        Assert.Contains("a.md:A: InCompile -> InRun", difference);
    }

    [Fact]
    public void CreateBaselineJson_DisambiguatesDuplicateNamesWithLineNumbers()
    {
        var first = ClassifiedAtLine("a.md", "Dup", 10);
        var second = ClassifiedAtLine("a.md", "Dup", 20);
        var unique = ClassifiedAtLine("a.md", "Solo", 30);

        var json = SpecConformanceSweep.CreateBaselineJson(
            [first, second, unique]);

        Assert.Contains("\"a.md:Dup:L10\"", json);
        Assert.Contains("\"a.md:Dup:L20\"", json);
        Assert.Contains("\"a.md:Solo\"", json);
        Assert.DoesNotContain("\"a.md:Dup\":", json);
    }

    [Fact]
    public void Classify_FullReferences_SubsetViolationIsDiagNotCompileError()
    {
        var example = Example("standalone-console", "");
        var expansion = new SpecExpansion([new SpecSourceFile("Example.cs",
            """
            using System;
            using System.IO;
            using System.Security.Permissions;
            using System.Threading;

            class Program
            {
                static void Main()
                {
                    Console.WriteLine(File.ReadAllText("x"));
                }
            }
            """)], true);

        var result = new SpecConformanceClassifier().Classify(example,
            expansion);

        Assert.True(SpecClassification.Diag == result.Category,
            result.Details ?? "<no details>");
        Assert.Contains("TCS1002", result.Details);
    }

    private static ClassifiedSpecExample ClassifiedAtLine(string mdFile,
        string name, int sourceLine) => new(new SpecExample(mdFile,
            new SpecAnnotation { Template = "standalone-lib", Name = name },
            "class C { }", sourceLine),
        new SpecClassificationResult(SpecClassification.InCompile));

    [Fact]
    public void DotnetExecutor_CapturesConsoleOutput()
    {
        var run = new SpecDotnetExecutor().Run([new SpecSourceFile(
            "Example.cs", """
            using System;

            class Program
            {
                static void Main()
                {
                    Console.WriteLine("hello");
                    Console.WriteLine(1 < 2);
                }
            }
            """)], "SpecDotnetSmoke");

        Assert.True(run.Ok, run.Error ?? "");
        Assert.Equal("hello\ntrue", string.Join("\n",
            run.Output.ReplaceLineEndings("\n").TrimEnd('\n').Split('\n')
                .Select(SpecLuaExecutor.NormalizeExpectedLine)));
    }

    [Fact]
    public void Executor_UsesDotnetOracleWhenNoOutputAnnotation()
    {
        var annotation = new SpecAnnotation
        {
            Template = "standalone-console",
            Name = "DotnetOracle"
        };
        var example = new SpecExample("fixture.md", annotation, "", 1);
        var sources = new List<SpecSourceFile>
        {
            new("Example.cs", """
                using System;

                class Program
                {
                    static void Main()
                    {
                        int total = 0;
                        for (int i = 1; i <= 4; i++)
                        {
                            total += i;
                        }
                        Console.WriteLine("total = " + total);
                        Console.WriteLine(total > 5);
                    }
                }
                """)
        };
        var classified = new ClassifiedSpecExample(example,
            new SpecConformanceClassifier().Classify(example,
                new SpecExpansion(sources, IsExecutable: true)));
        Assert.Equal(SpecClassification.InRun, classified.Result.Category);

        var outcome = new SpecLuaExecutor(SpecConformanceSweep.FindRepoRoot())
            .Execute(classified, "fixture.md:DotnetOracle", sources);

        Assert.True(outcome.Passed, outcome.Details ?? "");
    }

    [Theory]
    [InlineData("x is True", "x is true")]
    [InlineData("False True Falsehood", "false true Falsehood")]
    [InlineData("f = 1.23E+15", "f = 1.23e+15")]
    [InlineData("EOF East 3E8", "EOF East 3e8")]
    public void Executor_NormalizesBooleanAndExponentTokens(
        string expected, string normalized) =>
        Assert.Equal(normalized, SpecLuaExecutor.NormalizeExpectedLine(expected));

    [Fact]
    public void Executor_FindsEntryInvocationForStaticMain()
    {
        Assert.Equal("Hello.Main()", SpecLuaExecutor.FindEntryInvocation(
            [new SpecSourceFile("a.cs",
                "class Hello { static void Main() { } }")]));
        Assert.Equal("A.B.Program.Main()", SpecLuaExecutor.FindEntryInvocation(
            [new SpecSourceFile("a.cs",
                "namespace A.B { class Program { static void Main() { } } }")]));
        Assert.Equal("", SpecLuaExecutor.FindEntryInvocation(
            [new SpecSourceFile("a.cs",
                "System.Console.WriteLine(1);")]));
    }

    [Fact]
    public void Executor_RunsSpecExampleAndComparesNormalizedOutput()
    {
        var annotation = new SpecAnnotation
        {
            Template = "standalone-console",
            Name = "ExecFixture",
            ExpectedOutput = ["1 < 2 is True", "done"]
        };
        var example = new SpecExample("fixture.md", annotation, "", 1);
        var sources = new List<SpecSourceFile>
        {
            new("Example.cs", """
                using System;

                class Program
                {
                    static void Main()
                    {
                        Console.WriteLine($"1 < 2 is {1 < 2}");
                        Console.WriteLine("done");
                    }
                }
                """)
        };
        var classified = new ClassifiedSpecExample(example,
            new SpecConformanceClassifier().Classify(example,
                new SpecExpansion(sources, IsExecutable: true)));
        Assert.Equal(SpecClassification.InRun, classified.Result.Category);

        var executor = new SpecLuaExecutor(SpecConformanceSweep.FindRepoRoot());
        var outcome = executor.Execute(classified, "fixture.md:ExecFixture",
            sources);

        Assert.True(outcome.Passed, outcome.Details ?? "");
        Assert.False(outcome.KnownDifference);
    }

    [Fact]
    public void Executor_ReportsOutputMismatchWithDetails()
    {
        var annotation = new SpecAnnotation
        {
            Template = "standalone-console",
            Name = "ExecMismatch",
            ExpectedOutput = ["other"]
        };
        var example = new SpecExample("fixture.md", annotation, "", 1);
        var sources = new List<SpecSourceFile>
        {
            new("Example.cs", """
                using System;

                class Program
                {
                    static void Main() => Console.WriteLine("actual");
                }
                """)
        };
        var classified = new ClassifiedSpecExample(example,
            new SpecConformanceClassifier().Classify(example,
                new SpecExpansion(sources, IsExecutable: true)));

        var outcome = new SpecLuaExecutor(SpecConformanceSweep.FindRepoRoot())
            .Execute(classified, "fixture.md:ExecMismatch", sources);

        Assert.True(outcome.Executed);
        Assert.False(outcome.Passed);
        Assert.Contains("output mismatch", outcome.Details);
        Assert.Contains("actual", outcome.Details);
    }

    [SpecConformanceFact]
    public void SpecConformanceSweep_ClassifiesEveryAnnotatedExample()
    {
        var sweep = new SpecConformanceSweep(SpecConformanceSweep.FindRepoRoot());
        var markerCount = sweep.CountAnnotationMarkers();
        var result = sweep.Run();

        Assert.Equal(markerCount, result.Examples.Count);
        Assert.Equal(markerCount, result.Classified.Count);
        Assert.All(result.Classified,
            item => Assert.True(Enum.IsDefined(item.Result.Category)));
        // Bug ゼロは C1 の完了条件 (baseline の Bug は起票済みタスクの回帰監視下)。
        // このゲートは「baseline にない新規 Bug / 分類後退がない」を守る。
        Assert.True(result.BaselineDifference is null,
            result.BaselineDifference ?? "");
    }

    private static SpecExample Example(string template, string code,
        string[]? expectedErrors = null, string[]? additionalFiles = null) =>
        new("fixture.md", new SpecAnnotation
        {
            Template = template,
            Name = "Fixture",
            ExpectedErrors = expectedErrors,
            AdditionalFiles = additionalFiles
        }, code, 1);

    private static SpecExpansion Expanded(SpecExample example,
        bool executable) => new(
            [new SpecSourceFile("Example.cs", example.Code)], executable);

    private static SpecConformanceClassifier Classifier(
        TranspileResult result) => new(_ => result);

    private static ClassifiedSpecExample Classified(string mdFile, string name,
        SpecClassification category, string? reason = null,
        string? details = null)
    {
        var example = new SpecExample(mdFile, new SpecAnnotation
        {
            Template = "standalone-lib",
            Name = name
        }, "class C { }", 1);
        return new ClassifiedSpecExample(example,
            new SpecClassificationResult(category, reason, details));
    }

    private sealed class TemplateFixture : IDisposable
    {
        public string Root { get; }

        private TemplateFixture(string root) => Root = root;

        public static TemplateFixture Create()
        {
            var root = Path.Combine(Path.GetTempPath(),
                $"tcs_spec_templates_{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path.Combine(root, "code-in-main"));
            Directory.CreateDirectory(Path.Combine(root, "standalone-lib"));
            Directory.CreateDirectory(Path.Combine(root, "additional-files"));
            File.WriteAllText(Path.Combine(root, "code-in-main", "Program.cs"),
                "before\n$example-code\nafter");
            File.WriteAllText(Path.Combine(root, "standalone-lib", "Library.cs"),
                "$example-code");
            File.WriteAllText(Path.Combine(root, "additional-files", "Support.cs"),
                "class Support { }");
            return new TemplateFixture(root);
        }

        public void Dispose() => Directory.Delete(Root, true);
    }
}

[AttributeUsage(AttributeTargets.Method)]
public sealed class SpecConformanceFactAttribute : FactAttribute
{
    public SpecConformanceFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("TCS_SPEC_CONFORMANCE") != "1")
            Skip = "Set TCS_SPEC_CONFORMANCE=1 to run the corpus sweep.";
    }
}
