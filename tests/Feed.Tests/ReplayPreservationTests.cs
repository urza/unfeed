using System.Buffers.Binary;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Feed.Core.Application;
using Feed.Core.Domain;
using Feed.Core.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Feed.Tests;
public sealed class ReplayPreservationTests
{
    static byte[] Png() { var bytes = new byte[24]; new byte[]{137,80,78,71,13,10,26,10}.CopyTo(bytes,0); BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(16),640); BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(20),480); return bytes; }
    sealed class Handler : HttpMessageHandler
    {
        public bool Interrupt; public int Calls; public TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) { Calls++; Entered.TrySetResult(); if(Interrupt) await Task.Delay(Timeout.Infinite,ct); return new(HttpStatusCode.OK){Content=new ByteArrayContent(Png())}; }
    }
    static async Task<string> Write(TestInstance i, string name, string text, DateTime at, bool partial = false, string author = "501")
    {
        var media = PartialMetadataFixture.Media(); media["caption"]!["text"] = text; media["user"]!["pk"] = author;
        var raw = PartialMetadataFixture.Raw(media);
        if(partial) { media["link"] = null; raw["errors"] = new JsonArray(PartialMetadataFixture.Error([..PartialMetadataFixture.Prefix(false),"link"])); }
        var file = i.Paths.Get("raw","instagram","synthetic",name+".json"); Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllTextAsync(file,raw.ToJsonString()); File.SetLastWriteTimeUtc(file,at); return file;
    }
    static async Task<string> State(TestInstance i)
    {
        await using var db = i.Factory.Open();
        return JsonSerializer.Serialize(new {Posts=await db.Posts.AsNoTracking().OrderBy(p=>p.Id).ToArrayAsync(),Media=await db.Media.AsNoTracking().OrderBy(m=>m.Id).ToArrayAsync(),Authors=await db.Authors.AsNoTracking().OrderBy(a=>a.Id).ToArrayAsync()});
    }
    [Fact] public async Task OlderAndEqualTimeOtherSourcesCannotOverwriteAnyPostMediaVerdictOrIdentity()
    {
        await using var i = new TestInstance(); await i.Init(); using var handler = new Handler(); using var http = new HttpClient(handler);
        var ingest = new Ingest(i.Paths,i.Factory,new(i.Paths,http)); var snapshot = new InstanceFiles(i.Paths).Current;
        var time = Clock.Now.AddDays(-1); var newest = await Write(i,"new","Newest caption",time);
        Assert.Equal(1,(await ingest.File("instagram",newest,snapshot,false,default)).New);
        await using(var db=i.Factory.Open()) {var p=await db.Posts.SingleAsync();p.CategoriesJson="[]";p.VerdictContentRevision=p.ContentRevision;p.Summary="Valid existing summary";p.SummaryContentRevision=p.ContentRevision;await db.SaveChangesAsync();}
        var before = await State(i); var calls = handler.Calls;
        foreach(var at in new[]{time.AddDays(-1),time}) {
            var old = await Write(i,"old"+at.Ticks,"Older caption",at,author:"999");
            var result = await ingest.File("instagram",old,snapshot,false,default);
            Assert.Equal(0,result.Revised); Assert.Equal(0,result.New); Assert.Equal(0,result.Failed); Assert.Equal(before,await State(i)); Assert.Equal(calls,handler.Calls);
            await using var db=i.Factory.Open(); Assert.True((await db.RawSnapshots.SingleAsync(r=>r.Path==Path.GetRelativePath(i.Paths.Root,old))).Parsed);
        }
        var newer=await Write(i,"newer","Newer complete caption",time.AddHours(1));
        Assert.Equal(1,(await ingest.File("instagram",newer,snapshot,false,default)).Revised);
        await using var check=i.Factory.Open();Assert.Equal("Newer complete caption",(await check.Posts.SingleAsync()).Text);
    }
    [Fact] public async Task PartialObservationCannotReplaceExistingCompletePostEvenIfNewer()
    {
        await using var i=new TestInstance();await i.Init();using var http=new HttpClient(new Handler());var ingest=new Ingest(i.Paths,i.Factory,new(i.Paths,http));var s=new InstanceFiles(i.Paths).Current;
        var time=Clock.Now.AddDays(-1);var complete=await Write(i,"complete","Complete",time);await ingest.File("instagram",complete,s,false,default);var before=await State(i);
        var partial=await Write(i,"partial","Partial replacement",time.AddHours(1),true,"999");var result=await ingest.File("instagram",partial,s,false,default);
        Assert.Equal(0,result.Revised);Assert.Equal(0,result.Failed);Assert.Equal(before,await State(i));
        await using var db=i.Factory.Open();var raw=await db.RawSnapshots.SingleAsync(r=>r.Path==Path.GetRelativePath(i.Paths.Root,partial));Assert.True(raw.Parsed);Assert.Contains("partial metadata unavailable",raw.Warning);
    }
    [Fact] public async Task InterruptedNewPartialPostCanResumeAndLaterCompleteCaptureCanReplaceIt()
    {
        await using var i=new TestInstance();await i.Init();using var handler=new Handler{Interrupt=true};using var http=new HttpClient(handler);var ingest=new Ingest(i.Paths,i.Factory,new(i.Paths,http));var s=new InstanceFiles(i.Paths).Current;
        var time=Clock.Now.AddDays(-1);var partial=await Write(i,"partial","Partial caption",time,true);
        using var cancellation=new CancellationTokenSource(TimeSpan.FromSeconds(10));var task=ingest.File("instagram",partial,s,false,cancellation.Token);
        await handler.Entered.Task.WaitAsync(cancellation.Token);cancellation.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>task);
        await using(var db=i.Factory.Open())Assert.Null((await db.Posts.SingleAsync()).IngestReadyAt);
        handler.Interrupt=false;Assert.Equal(0,(await ingest.File("instagram",partial,s,false,default)).Failed);
        await using(var db=i.Factory.Open()){Assert.NotNull((await db.Posts.SingleAsync()).IngestReadyAt);Assert.Single(await db.Media.ToListAsync());}
        var complete=await Write(i,"complete","Complete caption",time.AddHours(1));Assert.Equal(1,(await ingest.File("instagram",complete,s,false,default)).Revised);
        await using var check=i.Factory.Open();Assert.Equal("Complete caption",(await check.Posts.SingleAsync()).Text);
    }
    [Fact] public async Task UnknownCurrentSourceOrderingIsPreservedButSameSourceParserReplayStillWorks()
    {
        await using var i=new TestInstance();await i.Init();using var http=new HttpClient(new Handler());var ingest=new Ingest(i.Paths,i.Factory,new(i.Paths,http));var s=new InstanceFiles(i.Paths).Current;
        var time=Clock.Now.AddDays(-1);var original=await Write(i,"original","Initial caption",time);await ingest.File("instagram",original,s,false,default);
        await using(var db=i.Factory.Open())await db.RawSnapshots.ExecuteDeleteAsync();var before=await State(i);
        var candidate=await Write(i,"candidate","Replacement",time.AddHours(1));Assert.Equal(0,(await ingest.File("instagram",candidate,s,false,default)).Revised);Assert.Equal(before,await State(i));
        await File.WriteAllTextAsync(original,(await File.ReadAllTextAsync(original)).Replace("Initial caption","Improved parser output"));
        Assert.Equal(1,(await ingest.File("instagram",original,s,false,default)).Revised);
    }
}
