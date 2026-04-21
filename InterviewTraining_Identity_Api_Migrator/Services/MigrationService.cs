using Dapper;
using InterviewTraining_Identity_Api_Migrator.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace InterviewTraining_Identity_Api_Migrator.Services;

/// <summary>
/// Сервис миграции данных между IdentityServer и InterviewTraining
/// </summary>
public class MigrationService : IMigrationService
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<MigrationService> _logger;
    private const string RoleCandidate = "Candidate";
    private const string RoleExpert = "Expert";

    public MigrationService(IConfiguration configuration, ILogger<MigrationService> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public async Task RunMigrationCycleAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Начало цикла миграции");

        var identityConnectionString = _configuration.GetConnectionString("IdentityServerConnection");
        var interviewTrainingConnectionString = _configuration.GetConnectionString("InterviewTrainingConnection");

        if (string.IsNullOrEmpty(identityConnectionString) || string.IsNullOrEmpty(interviewTrainingConnectionString))
        {
            _logger.LogError("Строки подключения не настроены");
            return;
        }

        Dictionary<string, HashSet<string>> userRolesDict;
        await using (var identityConnection = new NpgsqlConnection(identityConnectionString))
        {
            _logger.LogInformation("Подключение к IdentityServer БД");
            await identityConnection.OpenAsync(cancellationToken);

            var userRoles = await identityConnection.QueryAsync<UserRoleDto>(
                @"SELECT u.""Id"", r.""Name"" FROM public.""Users"" u
                  LEFT JOIN public.""UserRoles"" ur ON ur.""UserId"" = u.""Id""
                  LEFT JOIN public.""Roles"" r ON r.""Id"" = ur.""RoleId""");

            userRolesDict = new Dictionary<string, HashSet<string>>();
            foreach (var ur in userRoles)
            {
                if (!userRolesDict.ContainsKey(ur.Id))
                {
                    userRolesDict[ur.Id] = new HashSet<string>();
                }
                userRolesDict[ur.Id].Add(ur.Name);
            }

            _logger.LogInformation("Получено {Count} пользователей с ролями из IdentityServer", userRolesDict.Count);
        }

        await using (var trainingConnection = new NpgsqlConnection(interviewTrainingConnectionString))
        {
            _logger.LogInformation("Подключение к InterviewTraining БД");
            await trainingConnection.OpenAsync(cancellationToken);

            if (userRolesDict.Count == 0)
            {
                _logger.LogWarning("Нет записей в выборке из IdentityServer. Помечаем все записи как удаленные");
                await MarkAllRecordsAsDeletedAsync(trainingConnection);
                return;
            }

            var existingRecords = await trainingConnection.QueryAsync<AdditionalUserInfo>(
                @"SELECT id, identity_user_id, 
                         is_candidate, is_expert, is_deleted, modified_utc, created_utc
                  FROM public.additional_user_infos");

            var existingDict = existingRecords.ToDictionary(r => r.identity_user_id, r => r);

            foreach (var kvp in userRolesDict)
            {
                var userId = kvp.Key;
                var roles = kvp.Value;

                var isCandidate = roles.Contains(RoleCandidate);
                var isExpert = roles.Contains(RoleExpert);

                if (existingDict.TryGetValue(userId, out var existingRecord))
                {
                    await UpdateAdditionalUserInfoRolesAsync(trainingConnection, existingRecord, isCandidate, isExpert, userId);
                }
                else
                {
                    await AddAdditionalUserInfoAsync(trainingConnection, isCandidate, isExpert, userId);
                }
            }

            var userIdsFromIdentity = userRolesDict.Keys.ToHashSet();
            var recordsToDelete = existingDict.Keys.Where(k => !userIdsFromIdentity.Contains(k)).ToList();

            if (recordsToDelete.Count > 0)
            {
                _logger.LogInformation("Помечаем {Count} записей как удаленные", recordsToDelete.Count);
                foreach (var userId in recordsToDelete)
                {
                    await trainingConnection.ExecuteAsync(
                        @"UPDATE public.additional_user_infos 
                          SET is_deleted = true, modified_utc = @ModifiedUtc
                          WHERE identity_user_id = @IdentityUserId",
                        new
                        {
                            ModifiedUtc = DateTime.UtcNow,
                            IdentityUserId = userId
                        });
                }
            }

            _logger.LogInformation("Цикл миграции завершен успешно");
        }
    }

    private async Task UpdateAdditionalUserInfoRolesAsync(NpgsqlConnection trainingConnection, AdditionalUserInfo existingRecord, bool isCandidate, bool isExpert, string userId)
    {
        _logger.LogDebug("Обновление записи для IdentityUserId: {UserId}", userId);
        if (existingRecord.is_candidate == isCandidate && existingRecord.is_expert == isExpert)
        {
            _logger.LogDebug("Роли совпадают. Обновление не требуется: {UserId}", userId);
            return;
        }

        await trainingConnection.ExecuteAsync(
            @"UPDATE public.additional_user_infos 
                          SET is_candidate = @IsCandidate, 
                              is_expert = @IsExpert, 
                              modified_utc = @ModifiedUtc
                          WHERE identity_user_id = @IdentityUserId",
            new
            {
                IsCandidate = isCandidate,
                IsExpert = isExpert,
                ModifiedUtc = DateTime.UtcNow,
                IdentityUserId = userId
            });
    }

    private async Task AddAdditionalUserInfoAsync(NpgsqlConnection trainingConnection, bool isCandidate, bool isExpert, string userId)
    {
        _logger.LogDebug("Добавление новой записи для IdentityUserId: {UserId}", userId);
        await trainingConnection.ExecuteAsync(
            @"INSERT INTO public.additional_user_infos 
                          (id, identity_user_id, is_candidate, is_expert, created_utc, modified_utc, is_deleted, time_zone_id)
                          VALUES (@Id, @IdentityUserId, @IsCandidate, @IsExpert, @CreatedUtc, @ModifiedUtc, @IsDeleted, @TimeZoneId)",
            new
            {
                Id = Guid.NewGuid(),
                IdentityUserId = userId,
                IsCandidate = isCandidate,
                IsExpert = isExpert,
                CreatedUtc = DateTime.UtcNow,
                ModifiedUtc = DateTime.UtcNow,
                IsDeleted = false,
                TimeZoneId = Guid.Parse("20000000-0000-0000-0000-000000000089"), // идентификатор в базе - хард код. По умолчанию у всех пользователей, что создаём временная зона это Europe/Moscow
            });
    }

    private async Task MarkAllRecordsAsDeletedAsync(NpgsqlConnection connection)
    {
        await connection.ExecuteAsync(
            @"UPDATE public.additional_user_infos 
              SET is_deleted = true, modified_utc = @ModifiedUtc
              WHERE is_deleted = false",
            new { ModifiedUtc = DateTime.UtcNow });
    }
}
