namespace Auditor.Tests;

public class SchemaTests
{
    [Fact]
    public void FifteenOperationsInSpecOrder()
    {
        Assert.Equal(15, Schema.Operations.Length);
        var names = Schema.Operations.Select(o => o.Operation).ToList();
        Assert.Equal("REPORT_REMAINING_RECORD", names[0]);
        Assert.Equal("DISABLE_DATA_FLOW", names[4]);              // flows stopped before dependent changes
        Assert.Equal("BLOCK_RESTORE", names[5]);
        Assert.Equal("VERIFY_RESTORE_BLOCKED", names[^1]);
        Assert.Equal(15, Schema.AllowedTriples.Count);
    }

    [Fact]
    public void MakeStepDerivesState()
    {
        Assert.Equal("BLOCKED", Schema.MakeStep("REMEDIATION", "BLOCK_RESTORE", "backup_service", "backup_1").State);
        Assert.Equal("ACTIVE", Schema.MakeStep("FINDING", "REPORT_RESTORE_RISK", "backup_service", "backup_1", "evidence_01").State);
    }

    [Fact]
    public void CanonicalOrderIsPhaseThenOperationThenSystemThenResource()
    {
        var steps = new[]
        {
            Schema.MakeStep("VERIFICATION", "VERIFY_RECORD_ABSENT", "search_service", "search_1"),
            Schema.MakeStep("REMEDIATION", "DELETE_RECORD", "search_service", "search_1"),
            Schema.MakeStep("FINDING", "REPORT_REMAINING_RECORD", "search_service", "search_1", "evidence_01"),
            Schema.MakeStep("REMEDIATION", "DISABLE_DATA_FLOW", "integration_service", "data_flow_1"),
            Schema.MakeStep("VERIFICATION", "VERIFY_RECORD_ABSENT", "account_service", "account_1"),
        };
        var output = Schema.Canonicalize(steps);
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, output.Select(s => s.StepNo));
        Assert.Equal(new[]
        {
            ("FIND", "REPORT_REMAINING_RECORD", "search_service"), ("REME", "DISABLE_DATA_FLOW", "integration_service"), ("REME", "DELETE_RECORD", "search_service"),
            ("VERI", "VERIFY_RECORD_ABSENT", "account_service"), ("VERI", "VERIFY_RECORD_ABSENT", "search_service"),
        }, output.Select(s => (s.Phase[..4], s.Operation, s.SystemId)));
    }

    [Fact]
    public void ResourceIdTiesUsePlainStringOrder()
    {
        // Decision (NOTES.md): ties on system_id are broken by PLAIN STRING order of resource_id; ids are not zero-padded, so string order differs from numeric order.
        var a = Schema.MakeStep("VERIFICATION", "VERIFY_RECORD_PRESENT", "security_service", "audit_record_987");
        var b = Schema.MakeStep("VERIFICATION", "VERIFY_RECORD_PRESENT", "security_service", "audit_record_9528");
        var c = Schema.MakeStep("VERIFICATION", "VERIFY_RECORD_PRESENT", "security_service", "audit_record_10");
        Assert.True(string.CompareOrdinal("audit_record_9528", "audit_record_987") < 0);       // the premise: string != numeric
        var output = Schema.Canonicalize(new[] { a, b, c });
        Assert.Equal(new[] { "audit_record_10", "audit_record_9528", "audit_record_987" }, output.Select(s => s.ResourceId));
        var key = Schema.SortKey(a.Phase, a.Operation, a.SystemId, a.ResourceId);
        Assert.Equal(("security_service", "audit_record_987"), (key.System, key.Resource));
    }

    [Fact]
    public void CanonicalizeDoesNotMutateInput()
    {
        var s = Schema.MakeStep("FINDING", "REPORT_REMAINING_RECORD", "account_service", "account_1", "evidence_01");
        Schema.Canonicalize(new[] { s });
        Assert.Equal(0, s.StepNo);
    }

    [Theory]
    [InlineData("account_7", true)]
    [InlineData("data_flow_12", true)]
    [InlineData("audit_record_9528", true)]
    [InlineData("account", false)]
    [InlineData("Account_7", false)]
    [InlineData("account_7\n", false)]
    [InlineData("SELECT * FROM t", false)]
    public void ResourceIdShape(string id, bool valid) => Assert.Equal(valid, Schema.ValidResourceId(id));
}
