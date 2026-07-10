using PetBox.Memory.Data;
using PetBox.Tasks.Data;

namespace PetBox.Tests.Data;

// ChangedPayloadFields must agree with SamePayload field-for-field on every production
// row type: empty exactly when SamePayload is true, and naming exactly the fields that
// differ — it is the informed-Stale surface (intake stale-baseline-blind-retry), so a
// drifted implementation would either hide a moved field or report a phantom one.
public sealed class ChangedPayloadFieldsTests
{
	[Fact]
	public void PlanNode_NamesExactlyTheMovedFields()
	{
		var a = new PlanNode { Key = "k", Status = "Pending", Type = "bug", Name = "t", Body = "b", Priority = 0 };

		a.ChangedPayloadFields(a).Should().BeEmpty();
		a.SamePayload(a).Should().BeTrue();

		var moved = a with { Status = "Done", Body = "b2" };
		moved.SamePayload(a).Should().BeFalse();
		moved.ChangedPayloadFields(a).Should().BeEquivalentTo(["status", "body"]);

		// Wire names, not CLR names: Name surfaces as "title".
		(a with { Name = "t2" }).ChangedPayloadFields(a).Should().Equal("title");
		(a with { Priority = 5 }).ChangedPayloadFields(a).Should().Equal("priority");
		(a with { Type = "chore" }).ChangedPayloadFields(a).Should().Equal("type");
	}

	[Fact]
	public void MemoryEntry_NamesExactlyTheMovedFields()
	{
		var a = new MemoryEntry { Key = "k", Type = MemoryType.Project, Description = "d", Body = "b", Tags = "", Metadata = "" };

		a.ChangedPayloadFields(a).Should().BeEmpty();

		var moved = a with { Type = MemoryType.Reference, Body = "b2" };
		moved.SamePayload(a).Should().BeFalse();
		moved.ChangedPayloadFields(a).Should().BeEquivalentTo(["type", "body"]);
		(a with { Description = "d2" }).ChangedPayloadFields(a).Should().Equal("description");
		(a with { Tags = "x" }).ChangedPayloadFields(a).Should().Equal("tags");
		(a with { Metadata = "{}" }).ChangedPayloadFields(a).Should().Equal("metadata");
	}

	[Fact]
	public void CommentRow_And_MethodologyDefRow_NameTheirMovedFields()
	{
		var c = new CommentRow { Key = "c1", Board = "work", NodeId = "n", Author = "a", Body = "b", ParentId = null };
		c.ChangedPayloadFields(c).Should().BeEmpty();
		(c with { Body = "b2" }).ChangedPayloadFields(c).Should().Equal("body");
		(c with { Author = "z", ParentId = "p" }).ChangedPayloadFields(c).Should().BeEquivalentTo(["author", "parentId"]);

		var d = new MethodologyDefRow { Key = MethodologyDefRow.SingletonKey, Json = "{}" };
		d.ChangedPayloadFields(d).Should().BeEmpty();
		(d with { Json = "{\"a\":1}" }).ChangedPayloadFields(d).Should().Equal("definition");

		var t = new MethodologyTemplateRow { Key = "my-tmpl", Json = "{}" };
		t.ChangedPayloadFields(t).Should().BeEmpty();
		(t with { Json = "{\"a\":1}" }).ChangedPayloadFields(t).Should().Equal("definition");
	}
}
