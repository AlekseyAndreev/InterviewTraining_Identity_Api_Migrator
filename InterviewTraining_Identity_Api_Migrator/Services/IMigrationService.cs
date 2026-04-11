namespace InterviewTraining_Identity_Api_Migrator.Services;

public interface IMigrationService
{
    Task RunMigrationCycleAsync(CancellationToken cancellationToken);
}
