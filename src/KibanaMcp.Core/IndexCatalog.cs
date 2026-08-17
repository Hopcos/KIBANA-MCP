namespace KibanaMcp;

/// <summary>
/// Static PE logical index catalog used to annotate search_index results.
/// Kept identical to the index catalog bundled in the original ElasticSearch MCP so the tool output
/// stays byte-compatible; descriptions and hints describe the logical index families, not the
/// transport that backs them.
/// </summary>
public static class IndexCatalog
{
    private static readonly IReadOnlyDictionary<string, string> Entries = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["ubs-lottery-api"] = "It covers data from individual invocations of query Lottery Program For Player, assign Ticket By Batch Reward, assign Ticket By Reward, and query Claim Program For Player. Its fields include apiType,domainId,hostname,payload,playerPayload,success,userId,clientId,exceptionMsg,lotteryProgramId,programType.",
        ["ubs-lottery-draw"] = "It represents interfaces triggered after players place bets, spin the wheel, or play a game. Its fields include exceptionMsg,hostname,idempotencyKey,lotteryProgramVersion,playerPayload,programType,success,userId,actionType,domainId,exceptionStackTrace,lotteryProgramId,lotteryProgramName,milliseconds.",
        ["ubs-lottery-exception"] = "It represents errors reported by the API. Its fields include exceptionMsg,exceptionStackTrace,exceptionTypeName,hostname,requestUri,statusCode.",
        ["ubs-lottery-external"] = "It covers data from individual invocations of assign Loyalty, assign Role, assign Tournament, assign UBS, and player From Ubs. Its fields include domainId,endpoint,exceptionMsg,exceptionStackTrace,hostname,milliseconds,payload,success,url.?path,userId,idempotencyKey,isValid,requestPayload.",
        ["ubs-lottery-kafka-agent"] = "It represents the status of data sent to the Kafka‑Agent service. Its fields include domainID,endpoint,exceptionMsg,exceptionStackTrace,hostname,msgType,payload,retryCount,success,uniqueMsgId,milliseconds.",
        ["ubs-lottery-logger"] = "It represents other uncategorized logs. Its fields include category,exception,exceptionType,hostname,logLevel,message.",
        ["ubs-lottery-orleans"] = "It represents logs from Orleans. Its fields include domainId,grainKey,grainName,hostname,milliseconds,userId,success.",
        ["ubs-lottery-player-push"] = "It represents logs triggered when players log in to the game, launch the game, or finish a game session. Its fields include casinoGameId,countryCode,currency,domainId,enabledGameLaunchForApi,roles,success,userId,enabledGameEndedForApi,enabledLoginForApi,enabledTicketLoginTriggerForApi,exceptionMsg,exceptionStackTrace,internalUserId,.",
        ["ubs-lottery-rmp-produce"] = "It consists mainly of RabbitMQ logs generated for ticket remaining‑status pushes. Its fields include domainID,exceptionMsg,exceptionStackTrace,hostname,milliseconds,msgSource,msgTimestamp,payload,success,userID.",
        ["ubs-lottery-ticket-release"] = "It represents logs for ticket release operations. Its fields include domainId,expiredTime,gainTime,idempotencyKey,lotteryProgramId,message,status,success,ticketType,userId,clientId,exceptionMsg,exceptionStackTrace,hostname,lotteryProgramVersion,milliseconds,playerPayload,ticketId.",
        ["ubs-lottery-ticket-source"] = "It tracks multiple scenarios that trigger ticket generation, including user login, opening the game list, game launch, etc. Its fields include casinoGameId,domainId,exceptionMsg,programIdempotencyKeyMap,programIds,success,ticketSource,ticketSourceType,userId,exceptionStackTrace.",
    };

    public static string? TryGetDescription(string prefix)
    {
        return Entries.TryGetValue(prefix, out string? description) ? description : null;
    }
}
