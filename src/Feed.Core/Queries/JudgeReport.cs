using Feed.Core.Application;
using Feed.Core.Domain;
using Feed.Core.Infrastructure;
using Microsoft.EntityFrameworkCore;
namespace Feed.Core.Queries;

// The curate report (spec 9.6): uncurated thumbs grouped by author with the memory the model would see, the thumbs that
// disagree with the current verdict marked, and the owner unhides that disagree. Read-only; the lines go to the console.
public static class JudgeReport
{
    // A thumb disagrees when the owner's vote and the current verdict point in opposite directions: a thumb down on a
    // post the judge lets through, or a thumb up on a post the judge scores below the threshold. Scores are compared
    // only for a verdict of the current content (fail-open, rule 12).
    public static bool Disagrees(Feedback f, Post p, int threshold)
    {
        bool low = p.Judged && p.LlmScore < threshold;
        return f.Value < 0 ? !p.Hidden && p.Judged && !low : f.Value > 0 && (p.Hidden && p.HiddenBy == "llm" || low);
    }
    // An owner unhide of this content against a low score: the owner saw the llm hide and reversed it.
    public static bool UnhideDisagrees(Post p, int threshold) => p.OwnerUnhiddenRevision == p.ContentRevision && p.Judged && p.LlmScore < threshold;
    public static async Task<string[]> Curate(FeedDb db, InstanceSnapshot s, CancellationToken ct)
    {
        int threshold = s.Config.Llm.Threshold; var lines = new List<string>();
        var rows = await db.Feedback.AsNoTracking().Where(f => !f.Curated).OrderByDescending(f => f.Id).ToListAsync(ct);
        var postIds = rows.Select(f => f.PostId).Distinct().ToArray();
        var posts = await db.Posts.AsNoTracking().Where(p => postIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
        var authorIds = rows.Where(f => f.AuthorId != null).Select(f => f.AuthorId!.Value).Distinct().ToArray();
        var authors = await db.Authors.AsNoTracking().Where(a => authorIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, ct);
        // The memory sentences need the author's whole recent feedback, curated rows included, as Processing passes them.
        var memorySource = await db.Feedback.AsNoTracking().Where(f => f.AuthorId != null && authorIds.Contains(f.AuthorId.Value)).OrderByDescending(f => f.Id).Take(200 * Math.Max(1, authorIds.Length)).ToListAsync(ct);
        if (rows.Count == 0) lines.Add("curate: no uncurated signals");
        foreach (var group in rows.GroupBy(f => f.AuthorId).OrderByDescending(g => g.Max(f => f.Id)))
        {
            var author = group.Key is { } id ? authors.GetValueOrDefault(id) : null;
            lines.Add($"author #{group.Key?.ToString() ?? "?"} {author?.DisplayName ?? "(unknown)"} ({author?.Platform ?? group.First().Platform ?? "?"}): {group.Count()} uncurated signal{(group.Count() == 1 ? "" : "s")}");
            foreach (var f in group)
            {
                var post = posts.GetValueOrDefault(f.PostId); var mark = post is not null && Disagrees(f, post, threshold) ? " DISAGREES with the current verdict" + (post.Judged ? $" (score {post.LlmScore}, {(post.Hidden && post.HiddenBy == "llm" ? "hidden by llm" : "visible")})" : "") : "";
                lines.Add($"  thumb #{f.Id} post=#{f.PostId} value={f.Value} view={f.ViewKey} labels={f.CategoriesAtVote} reason={f.ReasonAtVote}{mark}");
            }
            // Memory is per post, so the sentences shown are those a new post by this author would carry (no post excluded).
            foreach (var sentence in Prompts.Memory(memorySource, 0, group.Key)) lines.Add("  memory: " + sentence);
        }
        var unhides = await db.Posts.AsNoTracking().Where(p => p.OwnerUnhiddenRevision == p.ContentRevision && p.CategoriesJson != null && p.VerdictContentRevision == p.ContentRevision && p.LlmScore < threshold).OrderByDescending(p => p.Id).Take(50).ToListAsync(ct);
        lines.Add(unhides.Count == 0 ? "unhides: no owner unhide disagrees with the current verdict" : $"unhides: {unhides.Count} owner unhide{(unhides.Count == 1 ? "" : "s")} against a low score");
        foreach (var p in unhides) lines.Add($"  post #{p.Id} {p.Platform}: score {p.LlmScore}; {p.LlmReason}");
        return lines.ToArray();
    }
}
