using LinqToDB;
using PetBox.Core.Data;
using PetBox.Core.Models;

namespace PetBox.Core.Auth;

public sealed class DbApiKeyLookup(PetBoxDb db) : IApiKeyLookup
{
	public ApiKey? FindByKey(string key) =>
		db.ApiKeys.FirstOrDefault(k => k.Key == key);
}
