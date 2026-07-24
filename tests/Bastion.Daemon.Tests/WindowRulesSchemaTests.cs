using System.Text.Json.Nodes;
using Xunit;
using static VerifyXunit.Verifier;

namespace Bastion.Daemon.Tests;

/// <summary>
/// GitHub issue #9's schema-export sanity check: <see cref="WindowRulesSchemaWriter.BuildSchema"/>'s
/// shape is Verify-snapshotted (docs/engineering/testing.md §6, json-ipc-config.md §3's "snapshot
/// it with Verify... regenerate as part of the build/release step") so an unintentional change to
/// <c>WindowRuleAction</c>/<c>WindowRuleMatch</c>/<c>WindowRule</c>/<c>WindowRulesDocument</c>'s
/// public shape surfaces as a reviewable diff rather than shipping silently.
/// </summary>
public sealed class WindowRulesSchemaTests
{
    [Fact]
    public Task BuildSchemaShapeMatchesSnapshot()
    {
        // VerifyJson(string), not Verify(JsonNode): the generic object-serializing Verify(...)
        // reflects over JsonNode as a plain .NET object (producing an empty "{}" for every JsonValue
        // leaf -- confirmed empirically), rather than treating it as already-serialized JSON text.
        JsonNode schema = WindowRulesSchemaWriter.BuildSchema();
        return VerifyJson(schema.ToJsonString());
    }

    [Fact]
    public void BuildSchemaEncodesTheNonEmptyNameConstraint()
    {
        // Regression test for a review finding: the exported schema reflects the DTO shape alone,
        // so without WindowRulesSchemaWriter's TransformSchemaNode an editor validating against the
        // published schema would accept an empty "name" that WindowRulesDocument.ValidateRules then
        // rejects. Asserted explicitly (not just via the broad snapshot above) so a future schema
        // change can't silently drop this constraint and still pass review as "just a snapshot update."
        JsonNode schema = WindowRulesSchemaWriter.BuildSchema();
        JsonNode? nameSchema = schema["properties"]?["rules"]?["items"]?["properties"]?["name"];

        Assert.NotNull(nameSchema);
        Assert.Equal(1, (int?)nameSchema["minLength"]);
    }

    [Fact]
    public void BuildSchemaEncodesTheAtLeastOneMatchCriterionConstraint()
    {
        // Same rationale as BuildSchemaEncodesTheNonEmptyNameConstraint, for the other half of
        // WindowRulesDocument.ValidateRules: WindowRuleMatch must have at least one non-null field.
        JsonNode schema = WindowRulesSchemaWriter.BuildSchema();
        JsonNode? matchSchema = schema["properties"]?["rules"]?["items"]?["properties"]?["match"];

        Assert.NotNull(matchSchema);
        JsonArray anyOf = Assert.IsType<JsonArray>(matchSchema["anyOf"]);
        var requiredFieldNames = anyOf
            .Select(static branch => (string)branch!["required"]![0]!)
            .ToArray();
        Assert.Equal(["appUserModelId", "executablePath", "className"], requiredFieldNames);
    }

    [Fact]
    public async Task WriteAsyncWritesAParsableSchemaFileToTheGivenPath()
    {
        string tempDirectory = Directory.CreateTempSubdirectory("bastion-schema-tests-").FullName;
        try
        {
            string schemaPath = Path.Combine(tempDirectory, "rules.schema.json");

            await WindowRulesSchemaWriter.WriteAsync(schemaPath, TestContext.Current.CancellationToken);

            Assert.True(File.Exists(schemaPath));
            string content = await File.ReadAllTextAsync(schemaPath, TestContext.Current.CancellationToken);
            var parsed = JsonNode.Parse(content);
            Assert.NotNull(parsed);
        }
        finally
        {
            // Best-effort, matching every other test fixture's cleanup in this project: a transient
            // file lock (e.g. antivirus/indexer) must not fail an otherwise-successful test.
            TryDeleteDirectory(tempDirectory);
        }
    }

    [Fact]
    public async Task WriteAsyncCreatesTheDestinationDirectoryIfMissing()
    {
        string tempDirectory = Directory.CreateTempSubdirectory("bastion-schema-tests-").FullName;
        try
        {
            string nestedSchemaPath = Path.Combine(tempDirectory, "nested", "does-not-exist-yet", "rules.schema.json");

            await WindowRulesSchemaWriter.WriteAsync(nestedSchemaPath, TestContext.Current.CancellationToken);

            Assert.True(File.Exists(nestedSchemaPath));
        }
        finally
        {
            // Best-effort, matching every other test fixture's cleanup in this project: a transient
            // file lock (e.g. antivirus/indexer) must not fail an otherwise-successful test.
            TryDeleteDirectory(tempDirectory);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
