namespace YobaBox.Core.Auth;

#pragma warning disable CA1724
public static class YobaBoxClaims
#pragma warning restore CA1724
{
	public const string UserId = "yb:user_id";
	public const string ActiveWorkspace = "yb:ws";
	public const string ProjectKey = "yb:project";
	public const string Scopes = "yb:scopes";
	public const string WorkspaceRoles = "yb:ws_roles";
}
