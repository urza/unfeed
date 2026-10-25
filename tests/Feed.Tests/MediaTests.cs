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
    [Fact]public void SigningTokensDoNotChangeMediaIdentityButRenditionsDo(){Assert.Equal(PayloadParser.SourceKey("https://cdn.test/a.jpg?oh=old&oe=1"),PayloadParser.SourceKey("https://cdn.test/a.jpg?oh=new&oe=2"));Assert.NotEqual(PayloadParser.SourceKey("https://cdn.test/a.jpg?stp=small"),PayloadParser.SourceKey("https://cdn.test/a.jpg?stp=large"));}
    [Fact]public void OnlyRenditionAndProxiedOriginSurviveInMediaIdentity()
    {
        // Parameters never seen before (here _nc_gid, efg, ig_cache_key) must not create a new identity; a denylist cannot know them.
        Assert.Equal(PayloadParser.SourceKey("https://cdn.test/v/a_n.jpg?stp=dst-jpg_e35&efg=eyJ2IjoxfQ&_nc_gid=first&ig_cache_key=k1&oh=1&oe=2"),PayloadParser.SourceKey("https://cdn.test/v/a_n.jpg?_nc_gid=second&stp=dst-jpg_e35&efg=eyJ2IjoyfQ&ig_cache_key=k2&oh=3&oe=4"));
        Assert.Equal("https://cdn.test/v/a_n.jpg?stp=dst-jpg_e35",PayloadParser.SourceKey("https://cdn.test/v/a_n.jpg?stp=dst-jpg_e35&_nc_gid=first"));
        Assert.NotEqual(PayloadParser.SourceKey("https://external.test/emg1/v/t13/1?url=https%3A%2F%2Fa.example%2F1.jpg&stp=c0"),PayloadParser.SourceKey("https://external.test/emg1/v/t13/1?url=https%3A%2F%2Fa.example%2F2.jpg&stp=c0"));
        Assert.Equal("https://cdn.test/plain.jpg",PayloadParser.SourceKey("https://cdn.test/plain.jpg?_nc_gid=x#frag"));
    }
    sealed class PngHandler(byte[] bytes):HttpMessageHandler{public int Calls;protected override Task<HttpResponseMessage>SendAsync(HttpRequestMessage r,CancellationToken ct){Interlocked.Increment(ref Calls);return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK){Content=new ByteArrayContent(bytes)});}}
    [Fact]public async Task RecaptureWithRotatedCdnParametersKeepsRevisionRowsAndFiles()
    {
        await using var i=new TestInstance();await i.Init();using var handler=new PngHandler(Png(640,480));using var http=new HttpClient(handler);var ingest=new Ingest(i.Paths,i.Factory,new(i.Paths,http));
        var snapshot=new InstanceSnapshot(new(){Platforms=ImmutableDictionary<string,PlatformConfig>.Empty.Add("instagram",new())},new(),Preferences.Parse(""));
        string Raw(string dir,string gid){var file=i.Paths.Get("raw","instagram",dir,"001.json");Directory.CreateDirectory(Path.GetDirectoryName(file)!);File.WriteAllText(file,$$$"""{"items":[{"pk":"101","code":"synthetic","user":{"pk":"501","username":"synthetic.person"},"caption":{"text":"Synthetic caption"},"image_versions2":{"candidates":[{"url":"https://cdn.test/v/a_n.jpg?stp=dst-jpg_e35&_nc_gid={{{gid}}}&oh=sig{{{gid}}}&oe=1","width":640}]}}]}""");return file;}
        var first=Raw("20260101T000000","one");var second=Raw("20260102T000000","two");File.SetLastWriteTimeUtc(second,File.GetLastWriteTimeUtc(first).AddMinutes(1));
        Assert.Equal(1,(await ingest.File("instagram",first,snapshot,false,default)).New);
        var replay=await ingest.File("instagram",second,snapshot,false,default);Assert.Equal(0,replay.Revised);Assert.Equal(1,handler.Calls);
        await using var db=i.Factory.Open();var post=await db.Posts.SingleAsync();Assert.Equal(1,post.ContentRevision);var media=await db.Media.SingleAsync();Assert.True(media.IsCurrent);Assert.Equal("https://cdn.test/v/a_n.jpg?stp=dst-jpg_e35",media.SourceKey);Assert.Contains("_nc_gid=two",media.OriginalUrl);
    }
    [Fact]public async Task KeyVersionTwoNormalizesStoredIdentitiesWithoutAdvancingRevisions()
    {
        await using var i=new TestInstance();await i.Init();var kept="media/instagram/101/01.png";var duplicate="media/instagram/101/01-abcdef123456.png";
        foreach(var rel in new[]{kept,duplicate}){Directory.CreateDirectory(Path.GetDirectoryName(i.Paths.Get(rel))!);await File.WriteAllBytesAsync(i.Paths.Get(rel),Png(128,128));}
        const string urlA="https://cdn.test/v/a_n.jpg?stp=dst-jpg_e35&_nc_gid=one&oh=1",urlB="https://cdn.test/v/a_n.jpg?stp=dst-jpg_e35&_nc_gid=two&oh=2",oldKeyA="https://cdn.test/v/a_n.jpg?stp=dst-jpg_e35&_nc_gid=one",oldKeyB="https://cdn.test/v/a_n.jpg?stp=dst-jpg_e35&_nc_gid=two";
        await using(var db=i.Factory.Open())
        {
            var p=new Post{Platform="instagram",PlatformPostId="101",Text="Synthetic",ContentRevision=7,ContentHash="stale",MediaManifestJson=System.Text.Json.JsonSerializer.Serialize(new[]{new MediaSource("image",urlB,oldKeyB)})};db.Add(p);await db.SaveChangesAsync();
            db.Media.Add(new(){PostId=p.Id,Kind="image",Position=1,SourceKey=oldKeyA,OriginalUrl=urlA,Path=kept,ContentHash="bytes",IsCurrent=false});
            db.Media.Add(new(){PostId=p.Id,Kind="image",Position=1,SourceKey=oldKeyB,OriginalUrl=urlB,Path=duplicate,ContentHash="bytes",IsCurrent=true});
            await db.SaveChangesAsync();await db.Kv.Where(k=>k.Key==MediaKeys.Marker).ExecuteDeleteAsync();
        }
        await i.Init();
        await using var check=i.Factory.Open();var post=await check.Posts.SingleAsync();var row=await check.Media.SingleAsync();var manifest=System.Text.Json.JsonSerializer.Deserialize<MediaSource[]>(post.MediaManifestJson)!;
        Assert.Equal("https://cdn.test/v/a_n.jpg?stp=dst-jpg_e35",row.SourceKey);Assert.True(row.IsCurrent);Assert.Equal(duplicate,row.Path);Assert.Equal(row.SourceKey,manifest.Single().SourceKey);
        Assert.Equal(7,post.ContentRevision);Assert.Equal(Ingest.ContentHash(post,manifest,[row]),post.ContentHash);
        Assert.True(File.Exists(i.Paths.Get(duplicate)));Assert.False(File.Exists(i.Paths.Get(kept)));Assert.Equal(MediaKeys.Version,await check.Get(MediaKeys.Marker));
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
        using var http=new HttpClient(new NoNetwork());var turn=await new Maintenance(i.Paths,i.Factory,http).Videos(new(new(),new(),Preferences.Parse("")),["facebook"],1,default);Assert.Equal(1,turn.Completed);Assert.Equal(0,turn.Failed);await using var check=i.Factory.Open();var row=await check.Media.SingleAsync();Assert.EndsWith("01.webm",row.Path);Assert.NotNull(row.ContentHash);
    }
    sealed class NoNetwork:HttpMessageHandler{protected override Task<HttpResponseMessage>SendAsync(HttpRequestMessage r,CancellationToken ct)=>throw new InvalidOperationException("Recovery must not fetch an already published video");}
}
