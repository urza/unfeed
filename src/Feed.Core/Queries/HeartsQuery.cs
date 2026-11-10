using Feed.Core.Domain;
using Feed.Core.Infrastructure;
using Microsoft.EntityFrameworkCore;
namespace Feed.Core.Queries;
/// <summary>One heart (a Likes row) with its post and author, for the Manage Hearts tab. <paramref name="CanRetry"/> is true for a failed heart whose post has no pending or sent row, so a new press is possible (spec 11.2).</summary>
public sealed record HeartRow(Like Like, Post Post, Author? Author, bool CanRetry)
{
    public string PersonName => Author?.DisplayName ?? Post.ObservedAuthorName ?? "Unknown person";
    public string Snippet => string.IsNullOrWhiteSpace(Post.Text) ? "" : Post.Text.Length > 80 ? Post.Text[..80].TrimEnd() + "…" : Post.Text;
}
public static class HeartsQuery
{
    /// <summary>The newest <paramref name="limit"/> hearts across all platforms, newest first. Read-only.</summary>
    public static async Task<HeartRow[]> Read(FeedDb db, int limit = 100, CancellationToken ct = default)
    {
        var likes = await db.Likes.AsNoTracking().OrderByDescending(l => l.Id).Take(limit).ToArrayAsync(ct);
        var postIds = likes.Select(l => l.PostId).Distinct().ToArray();
        var posts = await db.Posts.AsNoTracking().Where(p => postIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
        var authorIds = posts.Values.Where(p => p.AuthorId != null).Select(p => p.AuthorId!.Value).Distinct().ToArray();
        var authors = await db.Authors.AsNoTracking().Where(a => authorIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, ct);
        // A post keeps every row it ever had; only a post with no pending or sent row accepts a new press.
        var active = (await db.Likes.AsNoTracking().Where(l => postIds.Contains(l.PostId) && (l.State == "pending" || l.State == "sent")).Select(l => l.PostId).ToArrayAsync(ct)).ToHashSet();
        return likes.Where(l => posts.ContainsKey(l.PostId)).Select(l =>
        {
            var post = posts[l.PostId];
            return new HeartRow(l, post, post.AuthorId is { } id && authors.TryGetValue(id, out var a) ? a : null, l.State == "failed" && !active.Contains(l.PostId));
        }).ToArray();
    }
}
