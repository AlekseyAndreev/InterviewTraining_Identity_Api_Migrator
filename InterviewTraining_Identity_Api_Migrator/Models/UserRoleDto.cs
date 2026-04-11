namespace InterviewTraining_Identity_Api_Migrator.Models;

/// <summary>
/// DTO для выборки пользователей с ролями из IdentityServer
/// </summary>
public class UserRoleDto
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
}
