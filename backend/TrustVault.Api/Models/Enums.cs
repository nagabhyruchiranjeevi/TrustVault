using System.Text.Json.Serialization;

namespace TrustVault.Api.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PermissionLevel
{
    Read = 1,
    Edit = 2
}

public static class Roles
{
    public const string Admin = "Admin";
    public const string Owner = "Owner";
    public const string Viewer = "Viewer";

    public static readonly string[] All = [Admin, Owner, Viewer];
}
