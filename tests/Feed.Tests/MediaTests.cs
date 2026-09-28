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
