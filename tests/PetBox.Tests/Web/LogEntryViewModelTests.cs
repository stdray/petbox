using PetBox.Log.Core.Data;
using PetBox.Web.Pages.Logs;

namespace PetBox.Tests.Web;

public sealed class LogEntryViewModelTests
{
	[Fact]
	public void Render_SubstitutesProperties()
	{
		var vm = LogEntryViewModel.FromRecord(new LogEntryRecord
		{
			Message = "GET /x -> 200",
			MessageTemplate = "{Method} {Path} -> {Status}",
			PropertiesJson = """{"Method":"GET","Path":"/x","Status":200}""",
		});

		vm.RenderedMessage.Should().Contain("GET").And.Contain("/x").And.Contain("200");
		vm.RenderedMessage.Should().NotContain("{Method}");
	}

	[Fact]
	public void Render_MissingProperty_RendersNullNotLiteralPlaceholder()
	{
		// Null-valued arguments are dropped from PropertiesJson at capture — the
		// re-render must show (null), like the stored Message, not "{Project}".
		var vm = LogEntryViewModel.FromRecord(new LogEntryRecord
		{
			Message = "POST /v1/traces -> 200 (7 ms) project=(null)",
			MessageTemplate = "{Method} {Path} -> {Status} ({Elapsed} ms) project={Project}",
			PropertiesJson = """{"Method":"POST","Path":"/v1/traces","Status":200,"Elapsed":7}""",
		});

		vm.RenderedMessage.Should().NotContain("{Project}");
		vm.RenderedMessage.Should().Contain("(null)");
	}
}
