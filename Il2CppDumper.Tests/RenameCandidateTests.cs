using System.Text.Json;
using Il2CppDumper.Diff;
using Xunit;

namespace Il2CppDumper.Tests;

public sealed class RenameCandidateTests
{
    [Theory]
    [InlineData("Ankama.Animator2D.dll", "96ABD605-871C-4CC8-A73B-21C1F7D27A69", "A809795A-AE19-4CC9-90A9-C3A1DE2A5FC4")]
    [InlineData("Ankama.Dofus.Core.Characters.dll", "5180FB0A-737E-4BB4-94E3-1BB6518060CD", "35B28E9E-9FDA-461D-9647-9375EBBEDC17")]
    [InlineData("Ankama.Dofus.Protocol.Connection.dll", "8D662074-E843-43A7-A479-4EFF904C32B3", "4ADE3BC8-C11B-40BC-813A-A0B6E0E3901D")]
    [InlineData("Ankama.Dofus.Protocol.Game.dll", "3415F109-BC04-4E4D-831B-709D1A27C57E", "66F8E3B4-D04C-4897-AA34-6EE877B1A301")]
    [InlineData("Core.dll", "CB22598B-AE9F-4989-A466-01138C0A3A23", "D665CA48-B117-4A7E-BBD5-26D86C9BB54D")]
    public void GuidOnlyKeyChangesPreserveLiteralNamesAndIndependentDeclarationChanges(string image, string oldGuid, string newGuid)
    {
        var oldName = $"<PrivateImplementationDetails>{{{oldGuid}}}.a";
        var newName = $"<PrivateImplementationDetails>{{{newGuid}}}.a";
        var left = new JsonDumpType
        {
            image = image, fullName = oldName, assembly = "old-assembly",
            fields = new() { Field("value", "int", "0x10") },
            methods = new() { new() { name = "Read", returnType = "int" } },
            nested = new() { new() { fullName = oldName + ".a_" } }
        };
        var right = new JsonDumpType
        {
            image = image, fullName = newName, assembly = "new-assembly",
            fields = new() { Field("value", "long", "0x18") },
            methods = new() { new() { name = "Read", returnType = "long" } },
            nested = new() { new() { fullName = newName + ".a_" } }
        };
        var diff = DumpDiffer.Compare(Model(left), Model(right));
        Assert.Equal(2, diff.summary.addedTypes);
        Assert.Equal(2, diff.summary.removedTypes);
        Assert.Equal(new[] { $"{image}!{oldName}", $"{image}!{oldName}.a_" }, diff.types.removed);
        Assert.Equal(new[] { $"{image}!{newName}", $"{image}!{newName}.a_" }, diff.types.added);
        Assert.Collection(diff.renameCandidates!.types,
            parent =>
            {
                AssertGuidCandidate(parent, image, oldName, newName);
                var changes = parent.declarationChanges!;
                Assert.Equal("old-assembly", changes["assembly"].left.GetString());
                Assert.Equal("new-assembly", changes["assembly"].right.GetString());
                Assert.Equal("int", changes["fields"].left[0].GetProperty("type").GetString());
                Assert.Equal("long", changes["methods"].right[0].GetProperty("returnType").GetString());
                Assert.DoesNotContain("nested", changes.Keys);
                Assert.Equal("shifted", Assert.Single(parent.layoutChanges!.fields).drift);
            },
            child => AssertGuidCandidate(child, image, oldName + ".a_", newName + ".a_"));

        var drift = FieldOffsetDiff.Compare(Model(left), Model(right));
        Assert.Equal(JsonSerializer.Serialize(diff.renameCandidates), JsonSerializer.Serialize(drift.renameCandidates));
        Assert.True(drift.HasDrift);
        Assert.Single(drift.structs.Single(type => type.@struct == $"{image}!{oldName}").fields);

        var extraChild = newName + ".Added";
        right.nested.Add(new JsonDumpType { fullName = extraChild });
        var parentWithNewChild = DumpDiffer.Compare(Model(left), Model(right)).renameCandidates!.types[0];
        var membership = parentWithNewChild.declarationChanges!["nested"];
        Assert.Equal(oldName + ".a_", membership.left[0].GetString());
        Assert.Equal(extraChild, membership.right[1].GetString());
    }

    [Fact]
    public void StateMachineAndFieldCandidatesPreserveConfirmedDriftAndNominalCounts()
    {
        var oldState = StateMachine(13);
        var newState = StateMachine(14);
        var left = Model(new JsonDumpType
        {
            image = "Core.dll", fullName = "eph",
            fields = new()
            {
                Field("doqm", "fou", "0x28"), Field("previous", "int", "0x2c"),
                Field("gone", "int", "0x30"), Field("moved", "int", "0x40")
            },
            nested = new() { oldState }
        });
        var right = Model(new JsonDumpType
        {
            image = "Core.dll", fullName = "eph",
            fields = new()
            {
                Field("bjxc", "fmf", "0x28"), Field("current", "int", "2C"),
                Field("moved", "int", "0x44"), Field("added", "int", "0x50")
            },
            nested = new() { newState }
        });

        var diff = DumpDiffer.Compare(left, right);
        var drift = FieldOffsetDiff.Compare(left, right);
        var state = Assert.Single(drift.renameCandidates!.types);
        Assert.Equal("candidate", state.status);
        Assert.Equal("Core.dll!eph.<DoCurrentMapEvent>d__13", Assert.Single(state.left).type);
        Assert.Equal("Core.dll!eph.<DoCurrentMapEvent>d__14", Assert.Single(state.right).type);
        Assert.Equal(7277, state.left[0].typeDefIndex);
        Assert.Equal(0x02001C6Fu, state.right[0].typeToken);
        Assert.Contains("state-machine-ordinal", state.evidence);
        Assert.Contains("matching-field-method-shape", state.evidence);
        Assert.Contains("same-type-token", state.evidence);
        Assert.Contains("same-type-def-index", state.evidence);
        Assert.Equal(4, state.matchingInstanceOffsets);
        Assert.Null(state.layoutChanges);
        Assert.Empty(state.declarationChanges!);

        Assert.Collection(drift.renameCandidates.fields,
            incompatible =>
            {
                Assert.Equal("ambiguous", incompatible.status);
                Assert.Contains("different-declared-field-types", incompatible.ambiguities);
                Assert.Equal("doqm", Assert.Single(incompatible.left).name);
                Assert.Equal("bjxc", Assert.Single(incompatible.right).name);
            },
            possibleRename =>
            {
                Assert.Equal("candidate", possibleRename.status);
                Assert.Equal("0x2C", possibleRename.offset);
                Assert.Equal("previous", Assert.Single(possibleRename.left).name);
                Assert.Equal("current", Assert.Single(possibleRename.right).name);
            });

        var parent = drift.structs.Single(type => type.@struct == "Core.dll!eph");
        Assert.Equal(3, parent.verdict.added);
        Assert.Equal(3, parent.verdict.removed);
        Assert.Equal(1, parent.verdict.shifted);
        Assert.Contains(parent.fields, field => field.name == "added" && field.drift == "added");
        Assert.Contains(parent.fields, field => field.name == "gone" && field.drift == "removed");
        Assert.Contains(parent.fields, field => field.name == "moved" && field.drift == "shifted");
        Assert.Equal(1, diff.summary.addedTypes);
        Assert.Equal(1, diff.summary.removedTypes);
        Assert.True(diff.HasDifferences);
        Assert.True(drift.HasDrift);
        Assert.Equal(JsonSerializer.Serialize(diff.renameCandidates), JsonSerializer.Serialize(drift.renameCandidates));
    }

    [Fact]
    public void SameShapeWithMultipleTargetsProducesOneAmbiguityGroup()
    {
        var diff = DumpDiffer.Compare(Model(Shape("Old")), Model(Shape("NewA"), Shape("NewB")));
        var group = Assert.Single(diff.renameCandidates!.types);
        Assert.Equal("ambiguous", group.status);
        Assert.Contains("multiple-matches", group.ambiguities);
        Assert.Equal(new[] { "A.dll!NewA", "A.dll!NewB" }, group.right.Select(type => type.type));
        Assert.Null(group.declarationChanges);
        Assert.Null(group.matchingInstanceOffsets);
    }

    [Fact]
    public void ATokenReassignedToAnAlreadyMatchedTypeBlocksAProposedPair()
    {
        var left = StateMachine(13);
        var right = StateMachine(14);
        right.typeToken = 0x02002000;
        var stableLeft = new JsonDumpType { image = "Core.dll", fullName = "Stable", typeToken = 0x02002001 };
        var stableRight = new JsonDumpType { image = "Core.dll", fullName = "Stable", typeToken = left.typeToken };
        left.image = right.image = "Core.dll";
        var report = DumpDiffer.Compare(Model(left, stableLeft), Model(right, stableRight));
        var group = Assert.Single(report.renameCandidates!.types);
        Assert.Equal("ambiguous", group.status);
        Assert.Contains("type-token-conflict", group.ambiguities);
        Assert.Null(group.layoutChanges);
        Assert.Empty(report.types.changed);
    }

    [Fact]
    public void IdenticalTokenAndOffsetWithoutMatchingShapeCannotEstablishATypeRename()
    {
        var left = Shape("Old");
        var right = Shape("New");
        left.typeToken = right.typeToken = 0x02000001;
        right.fields[0].type = "double";
        var group = Assert.Single(DumpDiffer.Compare(Model(left), Model(right)).renameCandidates!.types);
        Assert.Equal("ambiguous", group.status);
        Assert.Contains("token-without-declaration-match", group.ambiguities);

        right.image = "B.dll";
        Assert.Null(DumpDiffer.Compare(Model(left), Model(right)).renameCandidates);
    }

    [Fact]
    public void SharedOffsetInTheSameOwnerIsAmbiguousAndStaticFieldsAreExcluded()
    {
        var left = new JsonDumpType { fullName = "Owner", fields = new() { Field("old", "int", "0x4"), Field("same", "int", "0x4") } };
        var right = new JsonDumpType { fullName = "Owner", fields = new() { Field("new", "int", "0x4"), Field("same", "int", "0x4") } };
        var group = Assert.Single(FieldOffsetDiff.Compare(Model(left), Model(right)).renameCandidates!.fields);
        Assert.Equal("ambiguous", group.status);
        Assert.Contains("shared-instance-offset", group.ambiguities);

        right.fields[0].isStatic = true;
        Assert.Null(FieldOffsetDiff.Compare(Model(left), Model(right)).renameCandidates);
    }

    private static void AssertGuidCandidate(TypeRenameCandidate candidate, string image, string oldName, string newName)
    {
        Assert.Equal("candidate", candidate.status);
        Assert.Contains("private-implementation-guid", candidate.evidence);
        Assert.Equal($"{image}!{oldName}", Assert.Single(candidate.left).type);
        Assert.Equal($"{image}!{newName}", Assert.Single(candidate.right).type);
    }

    private static JsonDumpType StateMachine(int ordinal) => new()
    {
        fullName = $"eph.<DoCurrentMapEvent>d__{ordinal}", typeDefIndex = 7277, typeToken = 0x02001C6F,
        bases = new() { "IAsyncStateMachine" },
        attributes = new() { "[CompilerGenerated]" },
        fields = new()
        {
            Field("<>1__state", "int", "0x0"), Field("<>t__builder", "AsyncUniTaskVoidMethodBuilder", "0x8"),
            Field("<>4__this", "eph", "0x10"), Field("<>u__1", "UniTask.Awaiter", "0x18")
        },
        methods = new()
        {
            new() { name = "MoveNext", returnType = "void" },
            new() { name = "SetStateMachine", returnType = "void", parameters = new() { new() { name = "stateMachine", type = "IAsyncStateMachine" } } }
        }
    };

    private static JsonDumpType Shape(string name) => new()
    {
        fullName = name, image = "A.dll", fields = new() { Field("value", "int", "0x10") },
        methods = new() { new() { name = "Read", returnType = "int" } }
    };

    private static JsonDumpField Field(string name, string type, string offset) =>
        new() { name = name, type = type, offset = offset };

    private static JsonDumpModel Model(params JsonDumpType[] types) => new() { types = types.ToList() };
}
