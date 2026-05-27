using YobaBox.Core.Models;

namespace YobaBox.Core.Auth;

public interface IApiKeyLookup
{
	ApiKey? FindByKey(string key);
}
