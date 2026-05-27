using LinqToDB;
using YobaBox.Core.Data;
using YobaBox.Core.Models;

namespace YobaBox.Core.Auth;

public sealed class DbApiKeyLookup(YobaBoxDb db) : IApiKeyLookup
{
	public ApiKey? FindByKey(string key) =>
		db.ApiKeys.FirstOrDefault(k => k.Key == key);
}
