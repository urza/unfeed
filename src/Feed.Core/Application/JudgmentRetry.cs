using System.Linq.Expressions;
using Feed.Core.Domain;
namespace Feed.Core.Application;

public static class JudgmentRetry
{
    public static int? Budget(Post p, LlmConfig config)
    {
        if (p.LlmTokenLimit is not { } exhausted || config.MaxTokens > exhausted) return config.MaxTokens;
        return config.TokenRetryBudgets.Where(n => n > exhausted && n > config.MaxTokens).Select(n => (int?)n).FirstOrDefault();
    }
    public static DateTime? After(Post p, LlmConfig config) => Budget(p, config) is null ? null
        : p.LlmAttemptedAt?.AddMinutes(p.LlmTokenLimit is null ? 30 : config.TokenRetryDelayMinutes);
    // Shared SQL predicate: workers, scheduler and reports agree on delay and the finite cap.
    public static Expression<Func<Post, bool>> Due(LlmConfig config, DateTime now)
    {
        var normal = now.AddMinutes(-30); var token = now.AddMinutes(-config.TokenRetryDelayMinutes);
        var ceiling = Math.Max(config.MaxTokens, config.TokenRetryBudgets.DefaultIfEmpty(0).Max());
        return p => p.LlmTokenLimit == null ? p.LlmAttemptedAt == null || p.LlmAttemptedAt <= normal
            : p.LlmTokenLimit < ceiling && (p.LlmAttemptedAt == null || p.LlmAttemptedAt <= token);
    }
}
