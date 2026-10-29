using System.Diagnostics;
using Feed.Core.Application;
using Feed.Core.Domain;
using Feed.Core.Infrastructure;
using Feed.Core.Queries;
using Feed.Web;
using Feed.Web.Components;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.AspNetCore.DataProtection;

var builder = WebApplication.CreateBuilder(args);
var dataArg = args.Select((v,i)=>(v,i)).FirstOrDefault(x=>x.v=="--data");
var data = dataArg.v is null ? args.FirstOrDefault(a=>a.StartsWith("--data="))?[7..] : dataArg.i+1<args.Length?args[dataArg.i+1]:throw new ArgumentException("--data requires a directory");
var paths = new InstancePaths(data); paths.Create(); var files = new InstanceFiles(paths); var initial = files.Current; var factory = new DbFactory(paths);
await using(var db=factory.Open()) await db.Initialize();
if(builder.Configuration["urls"] is null && Environment.GetEnvironmentVariable("ASPNETCORE_URLS") is null)builder.WebHost.UseUrls($"http://{initial.Config.Web.Host}:{initial.Config.Web.Port}");
var minimum=initial.Config.Ui.LogLevel switch{"trace"=>LogLevel.Trace,"debug"=>LogLevel.Debug,"warn"=>LogLevel.Warning,"error"=>LogLevel.Error,_=>LogLevel.Information};
builder.Logging.ClearProviders();builder.Logging.SetMinimumLevel(minimum);builder.Logging.AddProvider(new LogSink(paths,"web",minimum));builder.Logging.AddFilter("Microsoft",LogLevel.Warning);
builder.Services.AddSingleton(paths);builder.Services.AddSingleton(files);builder.Services.AddSingleton(factory);builder.Services.AddSingleton<Actions>();builder.Services.AddSingleton<FeedQuery>();builder.Services.AddRazorComponents();builder.Services.AddResponseCompression();
builder.Services.AddSingleton<Scheduler>();builder.Services.AddHostedService(sp=>sp.GetRequiredService<Scheduler>());builder.Services.AddSingleton<Backgrounds>();builder.Services.AddHostedService(sp=>sp.GetRequiredService<Backgrounds>());
builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(paths.Get("keys"))).SetApplicationName("Feed:" + Prompts.Sha(paths.Root));
builder.Services.AddAntiforgery();builder.Services.AddSingleton<ManagementFiles>();builder.Services.AddSingleton<Management>();builder.Services.AddSingleton<People>();
var app=builder.Build();app.UseResponseCompression();
var assets=Path.Combine(app.Environment.ContentRootPath,"wwwroot"); if(!Directory.Exists(assets))assets=Path.Combine(AppContext.BaseDirectory,"wwwroot");
var stamp=Directory.Exists(assets)?Prompts.Sha(string.Join("",Directory.EnumerateFiles(assets,"*",SearchOption.AllDirectories).Order().Select(File.ReadAllText)))[..12]:"dev";
app.Use(async(context,next)=>{context.Items["instance"]=files.Refresh();var watch=Stopwatch.StartNew();context.Response.OnStarting(()=>{context.Response.Headers["X-Render-Ms"]=watch.Elapsed.TotalMilliseconds.ToString("0.00",System.Globalization.CultureInfo.InvariantCulture);return Task.CompletedTask;});await next();});
InstanceSnapshot Snapshot(HttpContext c)=>(InstanceSnapshot)c.Items["instance"]!;
IResult Back(HttpContext c){var referer=c.Request.Headers.Referer.ToString();return Results.Redirect(Uri.TryCreate(referer,UriKind.Absolute,out var u)&&u.Authority==c.Request.Host.Value?u.PathAndQuery:"/");}
string Type(string path){new FileExtensionContentTypeProvider().TryGetContentType(path,out var type);return type??"application/octet-stream";}
string Logs(string? which,int count){var names=which is "cli" or "web"?new[]{which}:new[]{"cli","web"};return string.Join('\n',names.SelectMany(n=>File.Exists(paths.Get("logs",$"feed-{n}.log"))?File.ReadLines(paths.Get("logs",$"feed-{n}.log")).TakeLast(count):[]).Order(StringComparer.Ordinal).TakeLast(count));}
app.MapGet("/",async(HttpContext ctx,FeedQuery query,Backgrounds backgrounds)=>{
 var s=Snapshot(ctx);var view=ctx.Request.Query["view"].ToString();if(view.Length==0)view=s.Taxonomy.Views.Any(v=>v.Key==s.Config.Ui.DefaultView)?s.Config.Ui.DefaultView:"all";
 var platform=ctx.Request.Query["platform"].ToString();if(platform is "" or "all")platform=null;var scope=ctx.Request.Query["hidden"]=="1"?"hidden":ctx.Request.Query["unsorted"]=="1"?"unsorted":"live";
 try{var model=await query.Read(s,view,platform,scope,ctx.RequestAborted);return (IResult)new RazorComponentResult<FeedView>(new{Model=model,Stamp=stamp,Background=backgrounds.Current.Selected});}catch(ArgumentException){return Results.Redirect("/");}
});
app.MapGet("/healthz",async(CancellationToken ct)=>{await using var db=factory.Open();await db.Kv.AsNoTracking().Select(k=>k.Key).FirstOrDefaultAsync(ct);return Results.Text("ok\n");});
app.MapGet("/manage/log",(HttpContext ctx)=>Results.Text(Logs(ctx.Request.Query["file"],5000)));
app.MapGet("/manage/backgrounds",(HttpContext ctx,Backgrounds backgrounds)=>new RazorComponentResult<Gallery>(new{Model=backgrounds.Current,Stamp=stamp,Blur=Snapshot(ctx).Config.Backgrounds.BlurPx,UploadMessage=int.TryParse(ctx.Request.Query["uploaded"],out var uploaded)&&uploaded is >0 and <=10?$"Uploaded {uploaded} picture(s). Choose Pin to keep one selected.":null}));
app.MapPost("/manage/backgrounds/upload",async(HttpContext ctx,Backgrounds backgrounds)=>{
 string? error;
 var status=400;
 var requestLimit=ctx.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
 if(requestLimit is {IsReadOnly:false}) requestLimit.MaxRequestBodySize=22*1024*1024;
 try {
  if (!ctx.Request.HasFormContentType) error="Choose pictures using the upload form.";
  else {
   var form=await ctx.Request.ReadFormAsync(new Microsoft.AspNetCore.Http.Features.FormOptions { MultipartBodyLengthLimit=20*1024*1024 },ctx.RequestAborted);
   error=await backgrounds.Upload(form.Files,ctx.RequestAborted);
   if(error is null) return (IResult)Results.Redirect($"/manage/backgrounds?uploaded={form.Files.Count}");
  }
 } catch(InvalidDataException) { error="The upload is too large or malformed. Use up to 10 MB per file and 20 MB total."; }
 catch(BadHttpRequestException) { error="The upload is too large or malformed. Use up to 10 MB per file and 20 MB total."; }
 catch(Exception e) when(e is IOException or UnauthorizedAccessException) { app.Logger.LogError(e,"Background upload could not be saved"); error="Could not finish saving the upload. Check the gallery before retrying; some files may have been saved."; status=500; }
 return new RazorComponentResult<Gallery>(new{Model=backgrounds.Current,Stamp=stamp,Blur=Snapshot(ctx).Config.Backgrounds.BlurPx,UploadError=error}){StatusCode=status};
});
app.MapPost("/manage/backgrounds/{action}",async(string action,HttpContext ctx,Backgrounds backgrounds)=>{var form=await ctx.Request.ReadFormAsync();return backgrounds.Action(action,form["name"].FirstOrDefault())?Results.Redirect("/manage/backgrounds"):(IResult)Results.NotFound();});
app.MapGet("/backgrounds/{name}",(string name,HttpContext ctx,Backgrounds backgrounds)=>{var image=backgrounds.Current.Images.FirstOrDefault(i=>i.Name==name);if(image is null||paths.SafeFile(image.Source=="local"?"backgrounds/local":"backgrounds",Path.GetFileName(image.Path)) is null)return Results.NotFound();if(ctx.Request.Headers.IfNoneMatch==image.Etag)return Results.StatusCode(304);ctx.Response.Headers.ETag=image.Etag;ctx.Response.Headers.CacheControl="public, max-age=0, must-revalidate";return (IResult)Results.File(image.Path,Type(image.Path),enableRangeProcessing:true);});
app.MapGet("/media/{**path}",(string path,HttpContext ctx)=>{var file=paths.SafeFile("media",path);if(file is null)return Results.NotFound();ctx.Response.Headers.CacheControl="public, max-age=31536000, immutable";return (IResult)Results.File(file,Type(file),enableRangeProcessing:true);});
app.MapGet("/assets/{version}/{**path}",(string version,string path,HttpContext ctx)=>{if(version!=stamp)return Results.NotFound();var file=Path.GetFullPath(Path.Combine(assets,path));if(!file.StartsWith(assets+Path.DirectorySeparatorChar,StringComparison.Ordinal)||!File.Exists(file))return Results.NotFound();ctx.Response.Headers.CacheControl="public, max-age=86400";return (IResult)Results.File(file,Type(file));});
app.MapPost("/read",async(HttpContext ctx,Actions actions)=>{await actions.MarkRead(ctx.RequestAborted);return Back(ctx);});
app.MapPost("/posts/{id:long}/thumbs/{signal}",async(long id,string signal,HttpContext ctx,Actions actions)=>{if(signal is not("up" or "down"))return Results.NotFound();var ok=await actions.Thumb(id,signal=="up"?1:-1,ctx.Request.Query["view"],ctx.Request.Query["scope"].FirstOrDefault()??"live",ctx.RequestAborted);return ok?Back(ctx):(IResult)Results.NotFound();});
app.MapPost("/posts/{id:long}/unhide",async(long id,HttpContext ctx,Actions actions)=>await actions.Unhide(id,ctx.RequestAborted)?Back(ctx):(IResult)Results.NotFound());
app.MapPost("/posts/{id:long}/like",async(long id,HttpContext ctx,Actions actions)=>{var error=await actions.QueueLike(id,Snapshot(ctx),ct:ctx.RequestAborted);return error is null?Back(ctx):Results.Text(error,statusCode:409);});
app.MapGet("/posts/{id:long}/like",async(long id)=>{await using var db=factory.Open();var row=await db.Likes.AsNoTracking().Where(l=>l.PostId==id).OrderByDescending(l=>l.Id).FirstOrDefaultAsync();return Results.Json(new{state=row?.State??"none",error=row?.Error});});
app.MapPost("/collect",async(HttpContext ctx,Actions actions)=>{foreach(var platform in Platforms.All.Where(Snapshot(ctx).Config.Enabled))await actions.Collect(platform,"home",ctx.RequestAborted);return Back(ctx);});
app.MapPost("/collect/{platform}/{mode}",async(string platform,string mode,HttpContext ctx,Actions actions)=>{if(!Platforms.All.Contains(platform)||!Platforms.Modes.Contains(Platforms.Mode(mode)))return Results.Text($"collect refused: {platform} has no mode {mode}",statusCode:404);await actions.Collect(platform,mode,ctx.RequestAborted);return Back(ctx);});
app.MapManagement(stamp);
app.MapPeople(stamp);
app.Run();
public partial class Program;
