namespace Dalmarkit.Common.Services;

public interface IScheduleService
{
    Task<string> CreateScheduleTriggerAsync(string triggerName, string scheduleExpression, string target, string description);
    Task DeleteScheduleTriggerAsync(string triggerName);
}
