using FluentValidation;
using PetBox.LlmRouter.Contract;

namespace PetBox.LlmRouter.Registry;

// Validates a registry before it is persisted (llm-config-driven): endpoint names unique
// and non-empty, base URLs absolute http/https, every route points at a declared endpoint,
// timeouts sane. Surfaced by ILlmRegistryAdmin.SetAsync as a ValidationException.
public sealed class LlmRegistryValidator : AbstractValidator<LlmRegistry>
{
	public LlmRegistryValidator()
	{
		RuleForEach(r => r.Endpoints).ChildRules(ep =>
		{
			ep.RuleFor(e => e.Name)
				.NotEmpty().WithMessage("endpoint name is required");
			ep.RuleFor(e => e.BaseUrl)
				.Must(BeAbsoluteHttpUrl)
				.WithMessage(e => $"endpoint '{e.Name}' BaseUrl must be an absolute http(s) URL (got '{e.BaseUrl}')");
			ep.RuleFor(e => e.ConnectTimeoutMs)
				.GreaterThan(0).WithMessage(e => $"endpoint '{e.Name}' ConnectTimeoutMs must be > 0");
			ep.RuleFor(e => e.RequestTimeoutMs)
				.GreaterThan(0).WithMessage(e => $"endpoint '{e.Name}' RequestTimeoutMs must be > 0");
		});

		RuleFor(r => r.Endpoints)
			.Must(eps => eps.Select(e => e.Name).Distinct(StringComparer.Ordinal).Count() == eps.Count)
			.WithMessage("endpoint names must be unique");

		RuleForEach(r => r.Routes).ChildRules(route =>
		{
			route.RuleFor(x => x.Model)
				.NotEmpty().WithMessage("route model is required");
			route.RuleFor(x => x.Endpoint)
				.NotEmpty().WithMessage("route endpoint is required");
		});

		// Cross-field: every route's endpoint must be declared.
		RuleFor(r => r).Custom((reg, ctx) =>
		{
			var names = reg.Endpoints.Select(e => e.Name).ToHashSet(StringComparer.Ordinal);
			foreach (var route in reg.Routes)
				if (!string.IsNullOrEmpty(route.Endpoint) && !names.Contains(route.Endpoint))
					ctx.AddFailure($"route for {route.Capability} references unknown endpoint '{route.Endpoint}'");
		});
	}

	static bool BeAbsoluteHttpUrl(string url) =>
		Uri.TryCreate(url, UriKind.Absolute, out var u)
		&& (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps);
}
