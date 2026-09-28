using System.Net;
using System.Text;
using System.Text.Json;
using System.Collections.Immutable;
using Feed.Core.Application;
using Feed.Core.Domain;
using Feed.Core.Infrastructure;
using Feed.Core.Queries;
using Microsoft.EntityFrameworkCore;
using Xunit;
namespace Feed.Tests;
public sealed class ProcessingTests
{
    sealed class GatedHandler : HttpMessageHandler
    {
        public readonly TaskCompletionSource Release=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Later=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls; public int Active; public int Peak;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            int call=Interlocked.Increment(ref Calls);int active=Interlocked.Increment(ref Active);int current;do{current=Peak;}while(active>current&&Interlocked.CompareExchange(ref Peak,active,current)!=current);
            try{if(call==1)await Release.Task.WaitAsync(ct);if(call>=7)Later.TrySetResult();return new(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(new{choices=new[]{new{message=new{content="{\"score\":9,\"reason\":\"synthetic accepted\",\"categories\":[]}"}}}}),Encoding.UTF8,"application/json")};}finally{Interlocked.Decrement(ref Active);}
        }
    }
    [Theory] [InlineData(1)] [InlineData(4)] public async Task RollingPoolRefillsAcrossBatchesBeforeSlowRequestCompletes(int batch)
    {
        await using var i=new TestInstance();await i.Init();await using(var db=i.Factory.Open()){for(int n=0;n<12;n++)db.Posts.Add(new(){Platform="facebook",PlatformPostId=n.ToString(),Text="synthetic",IngestReadyAt=Clock.Now,PostedAt=Clock.Now.AddMinutes(-n)});await db.SaveChangesAsync();}
        var s=new InstanceSnapshot(new(){Platforms=ImmutableDictionary<string,PlatformConfig>.Empty.Add("facebook",new()),Llm=new(){Enabled=true,BaseUrl="http://synthetic.test/v1",Batch=batch,Parallel=4,Vision=false}},new(),Preferences.Parse(""));
        using var handler=new GatedHandler();using var http=new HttpClient(handler);var media=new MediaFiles(i.Paths,http);using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var work=new Processing(i.Paths,i.Factory,new(http),media,new(i.Paths,i.Factory,media)).Run(s,new("rescore"),null,timeout.Token);
        try{await handler.Later.Task.WaitAsync(timeout.Token);Assert.False(work.IsCompleted);Assert.InRange(handler.Peak,2,4);}finally{handler.Release.TrySetResult();}
        var counts=await work;Assert.Equal(12,counts.Completed);Assert.Equal(12,handler.Calls);await using var check=i.Factory.Open();Assert.Equal(12,await check.Posts.CountAsync(p=>p.VerdictContentRevision==p.ContentRevision));
    }
    [Fact] public async Task HigherRenderCapAndNullLastAreHonored()
    {
        await using var i=new TestInstance();await i.Init();await using(var db=i.Factory.Open()){for(int n=0;n<350;n++)db.Posts.Add(new(){Platform="facebook",PlatformPostId=n.ToString(),PostedAt=n==349?null:Clock.Now.AddMinutes(-n)});await db.SaveChangesAsync();}
        var s=new InstanceSnapshot(new(){Platforms=ImmutableDictionary<string,PlatformConfig>.Empty.Add("facebook",new()),Ui=new(){RenderCap=1000}},new(),Preferences.Parse(""));
        var page=await new FeedQuery(i.Factory).Read(s,"all",null,"live",default);Assert.Equal(350,page.Items.Length);Assert.Null(page.Items[^1].Lead.Post.PostedAt);Assert.Equal(350,page.VisibleCount);
    }
    [Fact] public async Task ThumbsDoNotChangeVisibilityAndUnhideAdvancesRevision()
    {
        await using var i=new TestInstance();await i.Init();long id;await using(var db=i.Factory.Open()){var p=new Post{Platform="instagram",PlatformPostId="fixture"};p.SetHidden("keyword","synthetic");db.Add(p);await db.SaveChangesAsync();id=p.Id;}
        var actions=new Actions(i.Factory);Assert.True(await actions.Thumb(id,-1,"all","hidden"));await using var check=i.Factory.Open();var post=await check.Posts.FindAsync(id);Assert.True(post!.Hidden);Assert.True(await actions.Unhide(id));await check.Entry(post).ReloadAsync();Assert.False(post.Hidden);Assert.Equal(2,post.VisibilityRevision);
    }
}
