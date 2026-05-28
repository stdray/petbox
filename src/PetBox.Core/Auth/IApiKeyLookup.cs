using PetBox.Core.Models;

namespace PetBox.Core.Auth;

public interface IApiKeyLookup
{
	ApiKey? FindByKey(string key);
}
