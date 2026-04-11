namespace InterviewTraining_Identity_Api_Migrator.Models;

/// <summary>
/// Модель таблицы additional_user_infos в БД InterviewTraining
/// </summary>
public class AdditionalUserInfo
{
    public Guid id { get; set; }
    public DateTime created_utc { get; set; }
    public DateTime? modified_utc { get; set; }
    public bool is_deleted { get; set; }
    public string identity_user_id { get; set; } = string.Empty;
    public bool is_candidate { get; set; }
    public bool is_expert { get; set; }
}
