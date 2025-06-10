using Grpc.Core;
using RiskEvaluator.Services.Rules;
using System.Diagnostics;

namespace RiskEvaluator.Services;

public class EvaluatorService : Evaluator.EvaluatorBase
{
    private readonly ILogger<EvaluatorService> _logger;
    private readonly IEnumerable<IRule> _rules;

    public static readonly ActivitySource ActivitySource = new ActivitySource("Tracing.NET");

    public EvaluatorService(ILogger<EvaluatorService> logger, IEnumerable<IRule> rules)
    {
        _logger = logger;
        _rules = rules;
    }

    public override Task<RiskEvaluationReply> Evaluate(RiskEvaluationRequest request, ServerCallContext context)
    {
        _logger.LogInformation("Evaluating risk for {Email}", request.Email);

        //-------------------------------------------------------------
        var httpClient = new HttpClient();
        var zzz = httpClient.GetAsync("https://kalapos.net").Result;
        var zz = EvaluatorService.ActivitySource;
        var act = ActivitySource.StartActivity("RiskEvaluation", ActivityKind.Server, "https://kalapos.net")!;
        act.Stop();
        //-------------------------------------------------------------

        var score = _rules.Sum(rule => rule.Evaluate(request));

        var level = score switch
        {
            <= 5 =>  RiskLevel.Low,
            <= 20 => RiskLevel.Medium,
            _ => RiskLevel.High
        };
        
        _logger.LogInformation("Risk level for {Email} is {Level}", request.Email, level);
        
        return Task.FromResult(new RiskEvaluationReply()
        {
            RiskLevel = level,
        });
    }
}