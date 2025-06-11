using Amazon.CloudWatchEvents;
using Amazon.CloudWatchEvents.Model;
using Dalmarkit.Common.Validation;
using System.Net;

namespace Dalmarkit.Cloud.Aws.Services;

public class AwsCloudWatchEventsService(AmazonCloudWatchEventsClient cloudWatchEventsClient) : IAwsCloudWatchEventsService
{
    private readonly AmazonCloudWatchEventsClient _cloudWatchEventsClient = Guard.NotNull(cloudWatchEventsClient, nameof(cloudWatchEventsClient));

    public async Task<string> CreateScheduleTriggerAsync(string triggerName, string scheduleExpression, string target, string description)
    {
        _ = Guard.NotNullOrWhiteSpace(triggerName, nameof(triggerName));
        _ = Guard.NotNullOrWhiteSpace(scheduleExpression, nameof(scheduleExpression));
        _ = Guard.NotNullOrWhiteSpace(target, nameof(target));
        _ = Guard.NotNullOrWhiteSpace(description, nameof(description));

        PutRuleRequest putRuleRequest = new()
        {
            Description = description,
            Name = triggerName,
            ScheduleExpression = scheduleExpression,
            State = RuleState.ENABLED
        };

        PutRuleResponse putRuleResponse = await _cloudWatchEventsClient.PutRuleAsync(putRuleRequest);
        if (putRuleResponse.HttpStatusCode is < HttpStatusCode.OK or >= HttpStatusCode.Ambiguous)
        {
            throw new HttpRequestException(
                $"Error status for PutRule({triggerName}, {scheduleExpression}, {target}, {description}) request: {putRuleResponse.HttpStatusCode}",
                null,
                putRuleResponse.HttpStatusCode);
        }

        Target scheduleTarget = new()
        {
            Arn = target,
            Id = GetTriggerTargetId(triggerName),
        };

        PutTargetsRequest putTargetsRequest = new()
        {
            Rule = triggerName,
            Targets = [scheduleTarget]
        };

        PutTargetsResponse putTargetsResponse;

        try
        {
            putTargetsResponse = await _cloudWatchEventsClient.PutTargetsAsync(putTargetsRequest);
        }
        catch (Exception ex)
        {
            DeleteRuleRequest deleteRuleRequest = new()
            {
                Name = triggerName
            };

            DeleteRuleResponse deleteRuleResponse = await _cloudWatchEventsClient.DeleteRuleAsync(deleteRuleRequest);
            if (deleteRuleResponse.HttpStatusCode is < HttpStatusCode.OK or >= HttpStatusCode.Ambiguous)
            {
                throw new HttpRequestException(
                    $"Error status for DeleteRule for PutTargets({triggerName}, {scheduleExpression}, {target}, {description}) request `{deleteRuleResponse.HttpStatusCode}` with message {ex.Message} and inner exception {ex.InnerException}: ${ex.StackTrace}",
                    null,
                    deleteRuleResponse.HttpStatusCode);
            }

            throw;
        }

        if (putTargetsResponse.HttpStatusCode is < HttpStatusCode.OK or >= HttpStatusCode.Ambiguous)
        {
            DeleteRuleRequest deleteRuleRequest = new()
            {
                Name = triggerName
            };

            DeleteRuleResponse deleteRuleResponse = await _cloudWatchEventsClient.DeleteRuleAsync(deleteRuleRequest);
            if (deleteRuleResponse.HttpStatusCode is < HttpStatusCode.OK or >= HttpStatusCode.Ambiguous)
            {
                throw new HttpRequestException(
                    $"Error status for DeleteRule for PutTargets({triggerName}, {scheduleExpression}, {target}, {description}) request `{deleteRuleResponse.HttpStatusCode}`: {putTargetsResponse.HttpStatusCode}",
                    null,
                    deleteRuleResponse.HttpStatusCode);
            }

            throw new HttpRequestException(
                $"Error status for PutTargets({triggerName}, {scheduleExpression}, {target}, {description}) request: {putTargetsResponse.HttpStatusCode}",
                null,
                putTargetsResponse.HttpStatusCode);
        }

        return putRuleResponse.RuleArn;
    }

    public async Task DeleteScheduleTriggerAsync(string triggerName)
    {
        RemoveTargetsRequest removeTargetsRequest = new()
        {
            Rule = triggerName,
            Ids = [GetTriggerTargetId(triggerName)]
        };

        RemoveTargetsResponse removeTargetsResponse = await _cloudWatchEventsClient.RemoveTargetsAsync(removeTargetsRequest);
        if (removeTargetsResponse.HttpStatusCode is < HttpStatusCode.OK or >= HttpStatusCode.Ambiguous)
        {
            throw new HttpRequestException(
                $"Error status for RemoveTargets for DeleteScheduleTrigger({triggerName}) request: {removeTargetsResponse.HttpStatusCode}",
                null,
                removeTargetsResponse.HttpStatusCode);
        }

        DeleteRuleRequest deleteRuleRequest = new()
        {
            Name = triggerName
        };

        DeleteRuleResponse deleteRuleResponse = await _cloudWatchEventsClient.DeleteRuleAsync(deleteRuleRequest);
        if (deleteRuleResponse.HttpStatusCode is < HttpStatusCode.OK or >= HttpStatusCode.Ambiguous)
        {
            throw new HttpRequestException(
                $"Error status for DeleteRule for DeleteScheduleTrigger({triggerName}) request: {deleteRuleResponse.HttpStatusCode}",
                null,
                deleteRuleResponse.HttpStatusCode);
        }
    }

    private static string GetTriggerTargetId(string triggerName)
    {
        return $"{triggerName}-target";
    }
}
