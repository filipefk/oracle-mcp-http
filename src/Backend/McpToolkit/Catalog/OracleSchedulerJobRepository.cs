using McpToolkit.Ddl;
using Oracle.ManagedDataAccess.Client;
using System.Text.Json.Nodes;

namespace McpToolkit.Catalog;

// Portado de OracleTools2026.Core\Data\SchedulerJobRepository (tela "Jobs de banco").
internal sealed class OracleSchedulerJobRepository(OracleConnection connection, int commandTimeoutSeconds)
{
    // As comparações com SYSTIMESTAMP são feitas no banco para respeitar o fuso horário de cada data.
    // As datas retornadas ficam no fuso da sessão.
    public async Task<List<SchedulerJob>> GetJobsAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT O.CREATED,
                   O.LAST_DDL_TIME,
                   J.OWNER,
                   J.JOB_NAME,
                   J.JOB_ACTION,
                   J.ENABLED,
                   J.JOB_TYPE,
                   J.PROGRAM_OWNER,
                   J.PROGRAM_NAME,
                   J.SCHEDULE_OWNER,
                   J.SCHEDULE_NAME,
                   J.SCHEDULE_TYPE,
                   J.REPEAT_INTERVAL,
                   CAST(J.LAST_START_DATE AT TIME ZONE SESSIONTIMEZONE AS DATE) AS LAST_START_DATE,
                   J.LAST_RUN_DURATION,
                   CAST(J.NEXT_RUN_DATE AT TIME ZONE SESSIONTIMEZONE AS DATE) AS NEXT_RUN_DATE,
                   J.RUN_COUNT,
                   J.FAILURE_COUNT,
                   J.COMMENTS,
                   J.STATE,
                   CASE WHEN J.NEXT_RUN_DATE < SYSTIMESTAMP - INTERVAL '1' MINUTE THEN 1 ELSE 0 END AS PAST_NEXT_RUN_DATE,
                   CASE WHEN J.LAST_START_DATE < SYSTIMESTAMP - INTERVAL '1' HOUR THEN 1 ELSE 0 END AS STARTED_OVER_AN_HOUR_AGO
            FROM ALL_SCHEDULER_JOBS J
                 INNER JOIN ALL_OBJECTS O
                    ON O.OWNER = J.OWNER
                   AND O.OBJECT_NAME = J.JOB_NAME
                   AND O.OBJECT_TYPE = 'JOB'
            ORDER BY J.ENABLED DESC, J.LAST_START_DATE DESC NULLS LAST
            """;

        await using var command = connection.CreateTextCommand(sql);
        if (commandTimeoutSeconds > 0)
        {
            command.CommandTimeout = commandTimeoutSeconds;
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        long Int64OrZero(int ordinal) => reader.IsDBNull(ordinal) ? 0 : (long)reader.GetDecimal(ordinal);

        var jobs = new List<SchedulerJob>();
        while (await reader.ReadAsync(cancellationToken))
        {
            jobs.Add(new SchedulerJob(
                reader.GetDateTimeOrNull(0),
                reader.GetDateTimeOrNull(1),
                reader.GetStringOrEmpty(2),
                reader.GetStringOrEmpty(3),
                reader.GetStringOrEmpty(4),
                reader.GetStringOrEmpty(5).Equals("TRUE", StringComparison.OrdinalIgnoreCase),
                reader.GetStringOrEmpty(6),
                reader.GetStringOrEmpty(7),
                reader.GetStringOrEmpty(8),
                reader.GetStringOrEmpty(9),
                reader.GetStringOrEmpty(10),
                reader.GetStringOrEmpty(11),
                reader.GetStringOrEmpty(12),
                reader.GetDateTimeOrNull(13),
                reader.IsDBNull(14) ? null : reader.GetTimeSpan(14),
                reader.GetDateTimeOrNull(15),
                Int64OrZero(16),
                Int64OrZero(17),
                reader.GetStringOrEmpty(18),
                reader.GetStringOrEmpty(19),
                Int64OrZero(20) == 1,
                Int64OrZero(21) == 1));
        }

        return jobs;
    }
}

internal enum SchedulerJobHealth
{
    Ok,
    Disabled,
    Overdue,
    LongRunning
}

internal sealed record SchedulerJob(
    DateTime? CreatedAt,
    DateTime? LastDdlAt,
    string Owner,
    string Name,
    string Action,
    bool Enabled,
    string JobType,
    string ProgramOwner,
    string ProgramName,
    string ScheduleOwner,
    string ScheduleName,
    string ScheduleType,
    string RepeatInterval,
    DateTime? LastStartDate,
    TimeSpan? LastRunDuration,
    DateTime? NextRunDate,
    long RunCount,
    long FailureCount,
    string Comments,
    string State,
    bool IsPastNextRunDate,
    bool StartedOverAnHourAgo)
{
    // Mesma classificação do original: jobs de execução única não são considerados atrasados nem demorados.
    public SchedulerJobHealth Health
    {
        get
        {
            if (!Enabled)
            {
                return SchedulerJobHealth.Disabled;
            }

            if (ScheduleType is "ONCE" or "NAMED" or "IMMEDIATE")
            {
                return SchedulerJobHealth.Ok;
            }

            return State == "RUNNING"
                ? StartedOverAnHourAgo ? SchedulerJobHealth.LongRunning : SchedulerJobHealth.Ok
                : IsPastNextRunDate ? SchedulerJobHealth.Overdue : SchedulerJobHealth.Ok;
        }
    }

    public string HealthText => Health switch
    {
        SchedulerJobHealth.Disabled => "Desabilitado",
        SchedulerJobHealth.Overdue => "Atrasado (possivelmente travado)",
        SchedulerJobHealth.LongRunning => "Rodando há mais de 1 hora",
        _ => "OK"
    };

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;

    public JsonObject ToJson() => new()
    {
        ["created"] = CatalogFormat.Date(CreatedAt),
        ["lastDdlTime"] = CatalogFormat.Date(LastDdlAt),
        ["owner"] = Owner,
        ["jobName"] = Name,
        ["jobAction"] = NullIfEmpty(Action),
        ["enabled"] = Enabled,
        ["jobType"] = NullIfEmpty(JobType),
        ["programOwner"] = NullIfEmpty(ProgramOwner),
        ["programName"] = NullIfEmpty(ProgramName),
        ["scheduleOwner"] = NullIfEmpty(ScheduleOwner),
        ["scheduleName"] = NullIfEmpty(ScheduleName),
        ["scheduleType"] = NullIfEmpty(ScheduleType),
        ["repeatInterval"] = NullIfEmpty(RepeatInterval),
        ["lastStartDate"] = CatalogFormat.Date(LastStartDate),
        ["lastRunDuration"] = CatalogFormat.Duration(LastRunDuration),
        ["nextRunDate"] = CatalogFormat.Date(NextRunDate),
        ["runCount"] = RunCount,
        ["failureCount"] = FailureCount,
        ["comments"] = NullIfEmpty(Comments),
        ["state"] = NullIfEmpty(State),
        ["health"] = HealthText
    };
}
