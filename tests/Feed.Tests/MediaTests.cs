using System.Buffers.Binary;
using System.Collections.Immutable;
using Feed.Core.Application;
using Feed.Core.Domain;
using Feed.Core.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;
namespace Feed.Tests;
public sealed class MediaTests
{
    static byte[] Png(int width,int height){var b=new byte[24];new byte[]{137,80,78,71,13,10,26,10}.CopyTo(b,0);BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(16,4),width);BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(20,4),height);return b;}
    [Fact]public void SigningTokensDoNotChangeMediaIdentityButRenditionsDoOffMetaHosts(){Assert.Equal(PayloadParser.SourceKey("https://cdn.test/a.jpg?oh=old&oe=1"),PayloadParser.SourceKey("https://cdn.test/a.jpg?oh=new&oe=2"));Assert.NotEqual(PayloadParser.SourceKey("https://cdn.test/a.jpg?stp=small"),PayloadParser.SourceKey("https://cdn.test/a.jpg?stp=large"));}
    [Fact]public void MetaFilenameIsTheMediaIdentity()
    {
        // Key version 3: the same asset came back from another CDN edge, path prefix or rendition in 1,216 live groups; the filename is what Meta keeps.
        Assert.Equal("file:1_2_3_n.jpg",PayloadParser.SourceKey("https://scontent-prg1-1.xx.fbcdn.net/v/t39.30808-6/1_2_3_n.jpg?stp=dst-jpg_p526x296&_nc_gid=a&oh=1&oe=2"));
        Assert.Equal(PayloadParser.SourceKey("https://scontent-prg1-1.xx.fbcdn.net/v/t39.30808-6/1_2_3_n.jpg?stp=dst-jpg_p526x296&oh=1"),PayloadParser.SourceKey("https://scontent.fprg2-1.fna.fbcdn.net/v/t1.6435-9/1_2_3_n.jpg?stp=dst-jpg_s1080x1080&oh=2"));
        Assert.Equal("file:token.mp4",PayloadParser.SourceKey("https://scontent-a.cdninstagram.com/o1/v/t16/f2/m86/token.mp4?efg=x&_nc_ht=y"));
        Assert.NotEqual(PayloadParser.SourceKey("https://scontent.xx.fbcdn.net/v/t39/1_2_3_n.jpg"),PayloadParser.SourceKey("https://scontent.xx.fbcdn.net/v/t39/1_2_4_n.jpg"));
        // A proxied preview is its origin, a path without a filename and a third-party host keep the URL key, and a parser key is never a URL.
        Assert.Equal("https://external.xx.fbcdn.net/emg1/v/t13/1?stp=c0&url=https%3A%2F%2Fa.example%2Fsafe_image.jpg",PayloadParser.SourceKey("https://external.xx.fbcdn.net/emg1/v/t13/1?url=https%3A%2F%2Fa.example%2Fsafe_image.jpg&stp=c0&ccb=1"));
        Assert.Equal("https://video.xx.fbcdn.net/o1/v/t2/f2/m69/token",PayloadParser.SourceKey("https://video.xx.fbcdn.net/o1/v/t2/f2/m69/token?efg=x"));
        Assert.Equal("https://cdn.example.test/image.jpg",PayloadParser.SourceKey("https://cdn.example.test/image.jpg?v=2"));
    }
    [Fact]public void OnlyRenditionAndProxiedOriginSurviveInMediaIdentity()
    {
        // Parameters never seen before (here _nc_gid, efg, ig_cache_key) must not create a new identity; a denylist cannot know them.
        Assert.Equal(PayloadParser.SourceKey("https://cdn.test/v/a_n.jpg?stp=dst-jpg_e35&efg=eyJ2IjoxfQ&_nc_gid=first&ig_cache_key=k1&oh=1&oe=2"),PayloadParser.SourceKey("https://cdn.test/v/a_n.jpg?_nc_gid=second&stp=dst-jpg_e35&efg=eyJ2IjoyfQ&ig_cache_key=k2&oh=3&oe=4"));
        Assert.Equal("https://cdn.test/v/a_n.jpg?stp=dst-jpg_e35",PayloadParser.SourceKey("https://cdn.test/v/a_n.jpg?stp=dst-jpg_e35&_nc_gid=first"));
        Assert.NotEqual(PayloadParser.SourceKey("https://external.test/emg1/v/t13/1?url=https%3A%2F%2Fa.example%2F1.jpg&stp=c0"),PayloadParser.SourceKey("https://external.test/emg1/v/t13/1?url=https%3A%2F%2Fa.example%2F2.jpg&stp=c0"));
        Assert.Equal("https://cdn.test/plain.jpg",PayloadParser.SourceKey("https://cdn.test/plain.jpg?_nc_gid=x#frag"));
    }
    [Fact]public async Task RecaptureWithRotatedCdnParametersKeepsRevisionRowsAndFiles()
    {
        await using var i=new TestInstance();await i.Init();var png=Png(640,480);using var handler=new StubHandler(_=>new HttpResponseMessage(System.Net.HttpStatusCode.OK){Content=new ByteArrayContent(png)});using var http=new HttpClient(handler);var ingest=new Ingest(i.Paths,i.Factory,new(i.Paths,http));
        var snapshot=new InstanceSnapshot(new(){Platforms=ImmutableDictionary<string,PlatformConfig>.Empty.Add("instagram",new())},new(),Preferences.Parse(""));
        string Raw(string dir,string gid,string host="scontent-a.cdninstagram.com",string name="a_n.jpg"){var file=i.Paths.Get("raw","instagram",dir,"001.json");Directory.CreateDirectory(Path.GetDirectoryName(file)!);File.WriteAllText(file,$$$"""{"items":[{"pk":"101","code":"synthetic","user":{"pk":"501","username":"synthetic.person"},"caption":{"text":"Synthetic caption"},"image_versions2":{"candidates":[{"url":"https://{{{host}}}/v/{{{name}}}?stp=dst-jpg_e35&_nc_gid={{{gid}}}&oh=sig{{{gid}}}&oe=1","width":640}]}}]}""");return file;}
        var first=Raw("20260101T000000","one");var second=Raw("20260102T000000","two","scontent-b.cdninstagram.com");File.SetLastWriteTimeUtc(second,File.GetLastWriteTimeUtc(first).AddMinutes(1));
        Assert.Equal(1,(await ingest.File("instagram",first,snapshot,false,default)).New);
        var replay=await ingest.File("instagram",second,snapshot,false,default);Assert.Equal(0,replay.Revised);Assert.Equal(1,handler.Calls);
        await using(var db=i.Factory.Open()){var post=await db.Posts.SingleAsync();Assert.Equal(1,post.ContentRevision);var media=await db.Media.SingleAsync();Assert.True(media.IsCurrent);Assert.Equal("file:a_n.jpg",media.SourceKey);Assert.Contains("scontent-b",media.OriginalUrl);}
        // A different filename is a different picture: one revision bump, one new row, the old row retired.
        var third=Raw("20260103T000000","three","scontent-b.cdninstagram.com","b_n.jpg");File.SetLastWriteTimeUtc(third,File.GetLastWriteTimeUtc(first).AddMinutes(2));
        Assert.Equal(1,(await ingest.File("instagram",third,snapshot,false,default)).Revised);Assert.Equal(2,handler.Calls);
        await using(var db=i.Factory.Open()){Assert.Equal(2,(await db.Posts.SingleAsync()).ContentRevision);var rows=await db.Media.OrderBy(m=>m.Id).ToListAsync();Assert.Equal(2,rows.Count);Assert.False(rows[0].IsCurrent);Assert.True(rows[1].IsCurrent);Assert.Equal("file:b_n.jpg",rows[1].SourceKey);}
    }
    [Fact]public async Task KeyVersionThreeNormalizesStoredIdentitiesWithoutAdvancingRevisions()
    {
        await using var i=new TestInstance();await i.Init();var kept="media/instagram/101/01.png";var duplicate="media/instagram/101/01-abcdef123456.png";
        foreach(var rel in new[]{kept,duplicate}){Directory.CreateDirectory(Path.GetDirectoryName(i.Paths.Get(rel))!);await File.WriteAllBytesAsync(i.Paths.Get(rel),Png(128,128));}
        // Version 2 keys of one asset from two CDN edges.
        const string urlA="https://scontent-a.cdninstagram.com/v/a_n.jpg?stp=dst-jpg_e35&_nc_gid=one&oh=1",urlB="https://scontent-b.cdninstagram.com/v/a_n.jpg?stp=dst-jpg_e35&_nc_gid=two&oh=2",oldKeyA="https://scontent-a.cdninstagram.com/v/a_n.jpg?stp=dst-jpg_e35",oldKeyB="https://scontent-b.cdninstagram.com/v/a_n.jpg?stp=dst-jpg_e35";
        await using(var db=i.Factory.Open())
        {
            var p=new Post{Platform="instagram",PlatformPostId="101",Text="Synthetic",ContentRevision=7,ContentHash="stale",MediaManifestJson=System.Text.Json.JsonSerializer.Serialize(new[]{new MediaSource("image",urlB,oldKeyB)})};db.Add(p);await db.SaveChangesAsync();
            db.Media.Add(new(){PostId=p.Id,Kind="image",Position=1,SourceKey=oldKeyA,OriginalUrl=urlA,Path=kept,ContentHash="bytes",IsCurrent=false});
            db.Media.Add(new(){PostId=p.Id,Kind="image",Position=1,SourceKey=oldKeyB,OriginalUrl=urlB,Path=duplicate,ContentHash="bytes",IsCurrent=true});
            await db.SaveChangesAsync();await db.Kv.Where(k=>k.Key==MediaKeys.Marker).ExecuteDeleteAsync();
        }
        await i.Init();
        await using var check=i.Factory.Open();var post=await check.Posts.SingleAsync();var row=await check.Media.SingleAsync();var manifest=System.Text.Json.JsonSerializer.Deserialize<MediaSource[]>(post.MediaManifestJson)!;
        Assert.Equal("file:a_n.jpg",row.SourceKey);Assert.True(row.IsCurrent);Assert.Equal(duplicate,row.Path);Assert.Equal(row.SourceKey,manifest.Single().SourceKey);
        Assert.Equal(7,post.ContentRevision);Assert.Equal(Ingest.ContentHash(post,manifest,[row]),post.ContentHash);
        Assert.True(File.Exists(i.Paths.Get(duplicate)));Assert.False(File.Exists(i.Paths.Get(kept)));Assert.Equal(MediaKeys.Version,await check.Get(MediaKeys.Marker));
    }
    [Fact]public async Task NormalizationKeepsTheWiderRenditionAndPermalinkKeys()
    {
        await using var i=new TestInstance();await i.Init();var wide="media/facebook/102/01.jpg";var thumb="media/facebook/102/02.jpg";
        foreach(var rel in new[]{wide,thumb}){Directory.CreateDirectory(Path.GetDirectoryName(i.Paths.Get(rel))!);await File.WriteAllBytesAsync(i.Paths.Get(rel),Png(128,128));}
        const string urlWide="https://scontent-a.xx.fbcdn.net/v/t39/1_2_3_n.jpg?stp=dst-jpg_s1080x1080&oh=1",urlThumb="https://scontent-b.xx.fbcdn.net/v/t39/1_2_3_n.jpg?stp=dst-jpg_p526x296&oh=2",permalink="https://www.facebook.com/reel/102/";
        await using(var db=i.Factory.Open())
        {
            var p=new Post{Platform="facebook",PlatformPostId="102",Text="Synthetic",ContentRevision=3,ContentHash="stale",Permalink=permalink,MediaManifestJson=System.Text.Json.JsonSerializer.Serialize(new[]{new MediaSource("image",urlWide,PayloadParser.SourceKey(urlWide)),new MediaSource("image",urlThumb,PayloadParser.SourceKey(urlThumb)),new MediaSource("video",permalink,"permalink:102")})};db.Add(p);await db.SaveChangesAsync();
            // The thumbnail holds the lower slot and the newer id, as in most live two-rendition posts; the wide row must still win and take slot 1.
            db.Media.Add(new(){PostId=p.Id,Kind="image",Position=1,SourceKey="https://scontent-b.xx.fbcdn.net/v/t39/1_2_3_n.jpg?stp=dst-jpg_p526x296",OriginalUrl=urlThumb,Path=thumb,ContentHash="thumb",Width=526,Height=296,IsCurrent=true});
            db.Media.Add(new(){PostId=p.Id,Kind="image",Position=2,SourceKey="https://scontent-a.xx.fbcdn.net/v/t39/1_2_3_n.jpg?stp=dst-jpg_s1080x1080",OriginalUrl=urlWide,Path=wide,ContentHash="wide",Width=1080,Height=1080,IsCurrent=true});
            // A permalink video row keeps the parser key; version 2 rekeyed it from the URL and orphaned it.
            db.Media.Add(new(){PostId=p.Id,Kind="video",Position=3,SourceKey="permalink:102",OriginalUrl=permalink,IsCurrent=true});
            await db.SaveChangesAsync();await db.Kv.Where(k=>k.Key==MediaKeys.Marker).ExecuteDeleteAsync();
        }
        await i.Init();
        await using var check=i.Factory.Open();var post=await check.Posts.SingleAsync();var rows=await check.Media.OrderBy(m=>m.Position).ToListAsync();var manifest=System.Text.Json.JsonSerializer.Deserialize<MediaSource[]>(post.MediaManifestJson)!;
        Assert.Equal(2,rows.Count);Assert.Equal(wide,rows[0].Path);Assert.Equal(1,rows[0].Position);Assert.Equal("file:1_2_3_n.jpg",rows[0].SourceKey);Assert.True(File.Exists(i.Paths.Get(wide)));Assert.False(File.Exists(i.Paths.Get(thumb)));
        Assert.Equal("permalink:102",rows[1].SourceKey);Assert.Equal(new[]{"file:1_2_3_n.jpg","permalink:102"},manifest.Select(m=>m.SourceKey));Assert.All(rows,r=>Assert.True(r.IsCurrent));
        Assert.Equal(3,post.ContentRevision);Assert.Equal(Ingest.ContentHash(post,manifest,rows),post.ContentHash);
    }
    [Theory][InlineData(32,32,true)][InlineData(1,200,false)][InlineData(128,128,false)]public async Task GlyphGuardAppliesToExistingPostImages(int w,int h,bool dropped)
    {
        await using var i=new TestInstance();var file=i.Paths.Get("media","image.png");await File.WriteAllBytesAsync(file,Png(w,h));using var http=new HttpClient();var result=await new MediaFiles(i.Paths,http).Image("media/image.png","https://unused.test/image.png",true,false,default);Assert.Equal(dropped,result.Path is null);Assert.Equal(!dropped,File.Exists(file));
    }
    [Fact]public async Task RetentionUsesHiddenDateAndKeepsKnownByteHash()
    {
        await using var i=new TestInstance();await i.Init();var path="media/facebook/fixture/01.png";Directory.CreateDirectory(Path.GetDirectoryName(i.Paths.Get(path))!);await File.WriteAllBytesAsync(i.Paths.Get(path),Png(128,128));long id;
        await using(var db=i.Factory.Open()){var p=new Post{Platform="facebook",PlatformPostId="fixture",CapturedAt=Clock.Now.AddYears(-1)};p.SetHidden("keyword","synthetic");db.Add(p);await db.SaveChangesAsync();id=p.Id;db.Media.Add(new(){PostId=p.Id,Path=path,SourceKey="synthetic",ContentHash="known",Kind="image",Position=1});await db.SaveChangesAsync();}
        using var http=new HttpClient();var maintenance=new Maintenance(i.Paths,i.Factory,http);await maintenance.Prune(new(),false,false,null,default);Assert.True(File.Exists(i.Paths.Get(path)));
        await using(var db=i.Factory.Open()){await db.Posts.Where(p=>p.Id==id).ExecuteUpdateAsync(u=>u.SetProperty(p=>p.HiddenAt,Clock.Now.AddDays(-8)));}
        await maintenance.Prune(new(),false,false,null,default);Assert.False(File.Exists(i.Paths.Get(path)));await using var check=i.Factory.Open();var media=await check.Media.SingleAsync();Assert.Equal("known",media.ContentHash);Assert.Null(media.Path);Assert.NotNull(media.PrunedAt);Assert.Equal(1,(await check.Posts.SingleAsync()).ContentRevision);
    }
    [Fact]public async Task RawRetentionPreservesUnparsedAndRemovesDiagnosticCopy()
    {
        await using var i=new TestInstance();await i.Init();var directory=i.Paths.Get("raw","facebook","old");Directory.CreateDirectory(directory);await File.WriteAllTextAsync(Path.Combine(directory,"001.jsonl"),"{}");var diagnostic=i.Paths.Get("media","diagnostics","facebook","old");Directory.CreateDirectory(diagnostic);await File.WriteAllBytesAsync(Path.Combine(diagnostic,"timeline.png"),Png(128,128));
        await using(var db=i.Factory.Open()){var run=new Run{Kind="collect",Platform="facebook",RawDir="old",Status="ok",FinishedAt=Clock.Now.AddDays(-100)};db.Add(run);await db.SaveChangesAsync();db.RawSnapshots.Add(new(){Platform="facebook",RunId=run.Id,Path="raw/facebook/old/001.jsonl",CapturedAt=Clock.Now.AddDays(-100),Parsed=false});await db.SaveChangesAsync();}
        using var http=new HttpClient();var maintenance=new Maintenance(i.Paths,i.Factory,http);await maintenance.Prune(new(),true,false,null,default);Assert.True(Directory.Exists(directory));await using(var db=i.Factory.Open())await db.RawSnapshots.ExecuteUpdateAsync(u=>u.SetProperty(r=>r.Parsed,true));await maintenance.Prune(new(),true,false,null,default);Assert.False(Directory.Exists(directory));Assert.False(Directory.Exists(diagnostic));
    }

    [Fact]public async Task PublishedVideoRecoversAfterCrashWithoutNetwork()
    {
        await using var i=new TestInstance();await i.Init();var folder=i.Paths.Get("media","facebook","fixture");Directory.CreateDirectory(folder);await File.WriteAllBytesAsync(Path.Combine(folder,"01.webm"),[1,2,3]);
        await using(var db=i.Factory.Open()){var p=new Post{Platform="facebook",PlatformPostId="fixture",IngestReadyAt=Clock.Now};db.Add(p);await db.SaveChangesAsync();db.Media.Add(new(){PostId=p.Id,Kind="video",Position=1,SourceKey="synthetic",OriginalUrl="https://unused.invalid/video.mp4"});await db.SaveChangesAsync();}
        using var http=new HttpClient(NoNetwork());var turn=await new Maintenance(i.Paths,i.Factory,http).Videos(new(new(),new(),Preferences.Parse("")),["facebook"],1,default);Assert.Equal(1,turn.Completed);Assert.Equal(0,turn.Failed);await using var check=i.Factory.Open();var row=await check.Media.SingleAsync();Assert.EndsWith("01.webm",row.Path);Assert.NotNull(row.ContentHash);
    }
    // The probe said yt-dlp is absent: the fallback row is stamped and waits, with no attempt counted and no prune.
    [Fact]public async Task MissingDownloadToolKeepsTheVideoRowWaiting()
    {
        await using var i=new TestInstance();await i.Init();
        await using(var db=i.Factory.Open()){var p=new Post{Platform="facebook",PlatformPostId="fixture",Permalink="https://www.facebook.com/reel/synthetic/",IngestReadyAt=Clock.Now};db.Add(p);await db.SaveChangesAsync();db.Media.Add(new(){PostId=p.Id,Kind="video",Position=1,SourceKey="permalink:synthetic",OriginalUrl=p.Permalink});await db.SaveChangesAsync();
            await db.Put(ToolHealth.Key("yt-dlp"),System.Text.Json.JsonSerializer.Serialize(new ToolHealth.State("missing",null,null,Clock.Now),InstanceValidation.Json));}
        using var http=new HttpClient(NoNetwork());var turn=await new Maintenance(i.Paths,i.Factory,http).Videos(new(new(),new(),Preferences.Parse("")),["facebook"],1,default);Assert.Equal(new MediaTurn(1,0,1),turn);
        await using var check=i.Factory.Open();var row=await check.Media.SingleAsync();Assert.Null(row.PrunedAt);Assert.Null(row.Path);Assert.Equal(0,row.DownloadAttempts);Assert.NotNull(row.AttemptedAt);Assert.Contains("yt-dlp",row.Error);
    }
    static StubHandler NoNetwork()=>new(HttpResponseMessage (_)=>throw new InvalidOperationException("Recovery must not fetch an already published video"));
    sealed class FlakyHandler(byte[] bytes):HttpMessageHandler{public bool Fail=true;public int Calls;protected override Task<HttpResponseMessage>SendAsync(HttpRequestMessage r,CancellationToken ct){Interlocked.Increment(ref Calls);if(Fail)throw new HttpRequestException("proxy tunnel 502");return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK){Content=new ByteArrayContent(bytes)});}}
    static InstanceSnapshot Instagram()=>new(new(){Platforms=ImmutableDictionary<string,PlatformConfig>.Empty.Add("instagram",new())},new(),Preferences.Parse(""));
    static string InstagramRaw(TestInstance i){var file=i.Paths.Get("raw","instagram","20260101T000000","001.json");Directory.CreateDirectory(Path.GetDirectoryName(file)!);File.WriteAllText(file,"""{"items":[{"pk":"101","code":"synthetic","user":{"pk":"501","username":"synthetic.person"},"caption":{"text":"Synthetic caption"},"image_versions2":{"candidates":[{"url":"https://cdn.test/v/a_n.jpg?stp=dst-jpg_e35&_nc_gid=one&oh=1&oe=1","width":640}]}}]}""");return file;}
    [Fact]public async Task FailedImageDownloadKeepsRetryStateAndProcessingCompletesIt()
    {
        await using var i=new TestInstance();await i.Init();using var handler=new FlakyHandler(Png(640,480));using var http=new HttpClient(handler);var ingest=new Ingest(i.Paths,i.Factory,new(i.Paths,http));var maintenance=new Maintenance(i.Paths,i.Factory,http);
        Assert.Equal(1,(await ingest.File("instagram",InstagramRaw(i),Instagram(),false,default)).New);
        string? hash;int revision;
        await using(var db=i.Factory.Open()){var post=await db.Posts.SingleAsync();hash=post.ContentHash;revision=post.ContentRevision;Assert.NotNull(post.IngestReadyAt);var row=await db.Media.SingleAsync();Assert.Null(row.Path);Assert.Null(row.PrunedAt);Assert.Equal(1,row.DownloadAttempts);Assert.Contains("502",row.Error);}
        Assert.Equal(0,(await maintenance.Images(["instagram"],null,default)).Selected);
        await using(var db=i.Factory.Open())await db.Media.ExecuteUpdateAsync(u=>u.SetProperty(m=>m.AttemptedAt,Clock.Now.AddMinutes(-31)));
        handler.Fail=false;Assert.Equal(new MediaTurn(1,1,0),await maintenance.Images(["instagram"],null,default));
        await using(var check=i.Factory.Open()){var post=await check.Posts.SingleAsync();var row=await check.Media.SingleAsync();Assert.EndsWith("/01.jpg",row.Path);Assert.True(File.Exists(i.Paths.Get(row.Path!)));Assert.Equal(640,row.Width);Assert.Null(row.Error);Assert.Equal(2,row.DownloadAttempts);
            Assert.Equal(revision+1,post.ContentRevision);Assert.NotEqual(hash,post.ContentHash);Assert.Null(post.LlmAttemptedAt);Assert.Equal(Ingest.ContentHash(post,System.Text.Json.JsonSerializer.Deserialize<MediaSource[]>(post.MediaManifestJson)!,[row]),post.ContentHash);}
        Assert.Equal(2,handler.Calls);
    }
    [Fact]public async Task ImageRetryStopsAfterThreeFailures()
    {
        await using var i=new TestInstance();await i.Init();using var handler=new FlakyHandler(Png(640,480));using var http=new HttpClient(handler);var ingest=new Ingest(i.Paths,i.Factory,new(i.Paths,http));var maintenance=new Maintenance(i.Paths,i.Factory,http);
        await ingest.File("instagram",InstagramRaw(i),Instagram(),false,default);int revision;await using(var db=i.Factory.Open())revision=(await db.Posts.SingleAsync()).ContentRevision;
        for(var attempt=2;attempt<=3;attempt++){await using(var db=i.Factory.Open())await db.Media.ExecuteUpdateAsync(u=>u.SetProperty(m=>m.AttemptedAt,Clock.Now.AddMinutes(-31)));Assert.Equal(new MediaTurn(1,0,1),await maintenance.Images(["instagram"],null,default));}
        await using var check=i.Factory.Open();var row=await check.Media.SingleAsync();Assert.Equal(3,row.DownloadAttempts);Assert.NotNull(row.PrunedAt);Assert.Contains("502",row.Error);Assert.Equal(revision,(await check.Posts.SingleAsync()).ContentRevision);
        Assert.Equal(new MediaTurn(0,0,0),await maintenance.Images(["instagram"],null,default));
    }
}
