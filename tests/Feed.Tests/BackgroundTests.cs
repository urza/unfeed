using Feed.Web;
using Feed.Core.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
namespace Feed.Tests;
public sealed class BackgroundTests
{
    [Fact]public async Task PoolCombinesSourcesAndPinPersistsWithoutDownloads()
    {
        await using var i=new TestInstance();Directory.CreateDirectory(i.Paths.Get("backgrounds","local"));await File.WriteAllBytesAsync(i.Paths.Get("backgrounds","bing-synthetic.jpg"),[1,2,3]);await File.WriteAllBytesAsync(i.Paths.Get("backgrounds","local","synthetic.jpg"),[4,5,6]);var files=new InstanceFiles(i.Paths);var gallery=new Backgrounds(i.Paths,files,NullLogger<Backgrounds>.Instance);gallery.Scan();Assert.Equal(2,gallery.Current.Images.Length);Assert.True(gallery.Action("pin","local-synthetic.jpg"));Assert.Equal("local-synthetic.jpg",gallery.Current.Selected);
        var restarted=new Backgrounds(i.Paths,files,NullLogger<Backgrounds>.Instance);restarted.Scan();Assert.Equal("local-synthetic.jpg",restarted.Current.Pinned);Assert.True(restarted.Action("next",null));Assert.Equal("bing-synthetic.jpg",restarted.Current.Selected);Assert.Null(restarted.Current.Pinned);Assert.False(restarted.Action("remove","../config.json"));Assert.True(restarted.Action("remove","bing-synthetic.jpg"));Assert.Contains("bing-synthetic.jpg",await File.ReadAllTextAsync(i.Paths.Get("backgrounds","removed.txt")));Assert.Single(restarted.Current.Images);
    }
    [Fact]public async Task LocalReplacementChangesEtagAndSymlinkIsExcluded()
    {
        await using var i=new TestInstance();Directory.CreateDirectory(i.Paths.Get("backgrounds","local"));var local=i.Paths.Get("backgrounds","local","synthetic.png");await File.WriteAllBytesAsync(local,[1]);var gallery=new Backgrounds(i.Paths,new(i.Paths),NullLogger<Backgrounds>.Instance);gallery.Scan();var before=Assert.Single(gallery.Current.Images).Etag;await File.WriteAllBytesAsync(local,[2]);gallery.Scan();Assert.NotEqual(before,Assert.Single(gallery.Current.Images).Etag);await File.WriteAllTextAsync(i.Paths.Get("config.json"),"{}");File.CreateSymbolicLink(i.Paths.Get("backgrounds","local","escape.jpg"),i.Paths.Get("config.json"));gallery.Scan();Assert.Single(gallery.Current.Images);
    }

    sealed class Handler(Func<HttpRequestMessage,HttpResponseMessage> response):HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)=>Task.FromResult(response(request)); }
    [Fact]public async Task EightMarketWindowsDeduplicateAndPersistRemovalsAndRefreshDelay()
    {
        await using var i=new TestInstance();int archives=0,downloads=0;
        using var handler=new Handler(r=>{
            if(r.RequestUri!.AbsolutePath.Contains("HPImageArchive")){archives++;return new(System.Net.HttpStatusCode.OK){Content=new StringContent(System.Text.Json.JsonSerializer.Serialize(new{images=Enumerable.Range(0,8).Select(n=>new{urlbase="/th?id=OHR.Synthetic"+n}).ToArray()}))};}
            downloads++;Assert.EndsWith("_1920x1080.jpg",r.RequestUri.ToString());var bytes=new byte[24];new byte[]{137,80,78,71,13,10,26,10}.CopyTo(bytes,0);System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(16,4),1920);System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(20,4),1080);var content=new ByteArrayContent(bytes);content.Headers.ContentType=new("image/png");return new(System.Net.HttpStatusCode.OK){Content=content,RequestMessage=r};
        });using var http=new HttpClient(handler);var files=new InstanceFiles(i.Paths);var gallery=new Backgrounds(i.Paths,files,NullLogger<Backgrounds>.Instance);await gallery.Refresh(http,default);Assert.Equal(8,archives);Assert.Equal(8,downloads);Assert.Equal(8,gallery.Current.Images.Length);
        gallery.Action("remove",gallery.Current.Images[0].Name);File.Delete(i.Paths.Get("backgrounds","refresh.json"));await gallery.Refresh(http,default);Assert.Equal(8,downloads);Assert.Equal(7,gallery.Current.Images.Length);
        var restarted=new Backgrounds(i.Paths,files,NullLogger<Backgrounds>.Instance);restarted.Scan();await restarted.Refresh(http,default);Assert.Equal(16,archives);Assert.NotNull(restarted.Current.Outcome);
        File.Delete(i.Paths.Get("backgrounds","refresh.json"));using var failure=new HttpClient(new Handler(_=>new(System.Net.HttpStatusCode.ServiceUnavailable)));await restarted.Refresh(failure,default);Assert.Equal(7,restarted.Current.Images.Length);await restarted.Refresh(http,default);Assert.Equal(16,archives);
    }
}
