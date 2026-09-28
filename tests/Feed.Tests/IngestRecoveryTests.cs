using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Net;
using Feed.Core.Application;
using Feed.Core.Domain;
using Feed.Core.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;
namespace Feed.Tests;
public sealed class IngestRecoveryTests
{
    sealed class Handler(byte[] bytes):HttpMessageHandler
    {
        public readonly TaskCompletionSource Entered=new(TaskCreationOptions.RunContinuationsAsynchronously);public bool Interrupt=true;public int Calls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken ct){Interlocked.Increment(ref Calls);Entered.TrySetResult();if(Interrupt)await Task.Delay(Timeout.Infinite,ct);return new(HttpStatusCode.OK){Content=new ByteArrayContent(bytes)};}
    }
    [Fact] public async Task InterruptedPartialImagesRecoverWithoutAnotherBrowserAndReplayIsStable()
    {
        await using var i=new TestInstance();await i.Init();var raw=i.Paths.Get("raw","instagram","capture","001.json");Directory.CreateDirectory(Path.GetDirectoryName(raw)!);
        await File.WriteAllTextAsync(raw,"""{"items":[{"pk":"101","code":"synthetic","user":{"username":"synthetic.person"},"caption":{"text":"Synthetic caption"},"carousel_media":[{"pk":"102","image_versions2":{"candidates":[{"url":"https://example.test/a.png","width":640}]}},{"pk":"103","image_versions2":{"candidates":[{"url":"https://example.test/b.png","width":640}]}}]}]}""");
        var bytes=new byte[24];new byte[]{137,80,78,71,13,10,26,10}.CopyTo(bytes,0);BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(16,4),640);BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(20,4),480);
        var first=i.Paths.Get("media","instagram","101","01.png");Directory.CreateDirectory(Path.GetDirectoryName(first)!);await File.WriteAllBytesAsync(first,bytes);
        using var handler=new Handler(bytes);using var http=new HttpClient(handler);var media=new MediaFiles(i.Paths,http);var ingest=new Ingest(i.Paths,i.Factory,media);var snapshot=new InstanceSnapshot(new(){Platforms=ImmutableDictionary<string,PlatformConfig>.Empty.Add("instagram",new())},new(),Preferences.Parse(""));
        using var cancellation=new CancellationTokenSource(TimeSpan.FromSeconds(10));var interrupted=ingest.File("instagram",raw,snapshot,false,cancellation.Token);await handler.Entered.Task.WaitAsync(cancellation.Token);cancellation.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>interrupted);
        await using(var db=i.Factory.Open()){Assert.False((await db.RawSnapshots.SingleAsync()).Parsed);Assert.Null((await db.Posts.SingleAsync()).IngestReadyAt);Assert.Equal(1,await db.Media.CountAsync(m=>m.Path!=null));await db.RawSnapshots.ExecuteUpdateAsync(u=>u.SetProperty(r=>r.AttemptedAt,Clock.Now.AddMinutes(-31)));}
        handler.Interrupt=false;await new Processing(i.Paths,i.Factory,new(http),media,ingest).Run(snapshot,new(),null,default);
        await using(var db=i.Factory.Open()){Assert.True((await db.RawSnapshots.SingleAsync()).Parsed);Assert.NotNull((await db.Posts.SingleAsync()).IngestReadyAt);Assert.Equal(2,await db.Media.CountAsync(m=>m.Path!=null));}
        Assert.Equal(2,handler.Calls);Assert.Equal(0,(await ingest.File("instagram",raw,snapshot,true,default)).Revised);Assert.Equal(2,handler.Calls);
    }
}
