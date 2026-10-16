using Feed.Core.Application;
using Feed.Web.Components;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http.HttpResults;
using Feed.Core.Infrastructure;

namespace Feed.Web;

public static class ManagementRoutes
{
    static readonly string[] Sections = ["overview", "recovery", "people", "schedule", "rules", "categories", "processing", "storage"];
    public static void MapManagement(this WebApplication app, string stamp)
    {
        app.MapGet("/manage", async (HttpContext ctx, Management management, Backgrounds backgrounds, IAntiforgery antiforgery) => {
            var section = ctx.Request.Query["section"].ToString(); if (!Sections.Contains(section)) section = "overview";
            try {
                var model = await management.Read(section, ctx.Request.Query["q"].ToString(), ctx.Request.Query["platform"].ToString(), ctx.Request.Query["filter"].ToString(), int.TryParse(ctx.Request.Query["page"], out var page) ? page : 1, ctx.RequestAborted, long.TryParse(ctx.Request.Query["collection"], out var collection) ? collection : null, ctx.Request.Query["signature"].FirstOrDefault());
                ctx.Response.Headers.CacheControl = "no-store";
                return (IResult)new RazorComponentResult<Manage>(new { Model = model, Stamp = stamp, Background = backgrounds.Current.Selected, RequestToken = antiforgery.GetAndStoreTokens(ctx).RequestToken!, Message = ctx.Request.Query["message"].FirstOrDefault() });
            } catch (Exception e) when (e is FormatException or System.Text.Json.JsonException or IOException) { return Problem("Instance files could not be read: " + e.Message + " Correct the files and reload. Diagnostics keeps the last valid settings.", 409, section); }
        });
        app.MapGet("/debug/capture-recovery/{id:long}", async (long id, Feed.Core.Infrastructure.DbFactory factory, Backgrounds backgrounds) => {
            await using var db = factory.Open(); var attempt = await db.CaptureRecoveryAttempts.FindAsync(id);
            return attempt is null ? (IResult)Results.NotFound() : new RazorComponentResult<RecoveryAudit>(new { Attempt = attempt, Stamp = stamp, Background = backgrounds.Current.Selected });
        });
        app.MapPost("/manage/storage/reset", (HttpContext ctx, DbFactory factory, InstancePaths paths, IAntiforgery antiforgery) => Submit(ctx, antiforgery, async f => {
            if (f["confirm"] != "yes") throw new ArgumentException("Confirm permanent deletion before resetting downloads.");
            return await new StorageCleanup(paths, factory).ResetDownloads(f["delete_posts"] == "yes", ctx.RequestAborted);
        }));
        app.MapPost("/manage/storage/hidden", (HttpContext ctx, DbFactory factory, InstancePaths paths, IAntiforgery antiforgery) => Submit(ctx, antiforgery, async f => {
            var preview = f["action"] == "preview";
            if (!preview && (f["action"] != "delete" || f["confirm"] != "yes")) throw new ArgumentException("Confirm permanent deletion before continuing.");
            return (await new StorageCleanup(paths, factory).HiddenPosts(preview, ctx.RequestAborted)).ToString();
        }));
        app.MapPost("/manage/work/process", (HttpContext ctx, Management management, IAntiforgery antiforgery) => Submit(ctx, antiforgery, _ => management.QueueProcessing(ctx.RequestAborted)));
        app.MapPost("/manage/settings/{action}", (string action, HttpContext ctx, Management management, IAntiforgery antiforgery) => Submit(ctx, antiforgery, f => management.SaveSettings(action, f, ctx.RequestAborted)));
        app.MapPost("/manage/people/{id:long}/{action}", (long id, string action, HttpContext ctx, Management management, IAntiforgery antiforgery) => Submit(ctx, antiforgery, f => management.Person(id, action, f["enabled"] == "true", f["version"].ToString(), ctx.RequestAborted)));
        app.MapPost("/manage/platform/{platform}/{action}", (string platform, string action, HttpContext ctx, Management management, IAntiforgery antiforgery) => Submit(ctx, antiforgery, f => management.Queue(platform, action, f["mode"].FirstOrDefault(), ctx.RequestAborted)));
    }
    static async Task<IResult> Submit(HttpContext ctx, IAntiforgery antiforgery, Func<IFormCollection, Task<string>> action)
    {
        string section = "overview";
        try {
            await antiforgery.ValidateRequestAsync(ctx);
            var form = await ctx.Request.ReadFormAsync(ctx.RequestAborted); section = form["section"].ToString(); if (!Sections.Contains(section)) section = "overview";
            var message = await action(form);
            var query = new Dictionary<string, string?> { ["section"] = section, ["message"] = message };
            if (section == "people") { query["q"] = form["q"]; query["platform"] = form["return_platform"]; query["filter"] = form["filter"]; query["page"] = form["page"]; }
            return Results.Redirect("/manage" + QueryString.Create(query));
        }
        catch (AntiforgeryValidationException) { return Problem("This form expired or did not originate from the management page. Reload it before saving.", 400, section); }
        catch (SettingsConflictException e) { return Problem(e.Message, 409, section); }
        catch (ResourceBusyException e) { return Problem(e.Message, 409, section); }
        catch (Exception e) when (e is FormatException or ArgumentException or System.Text.Json.JsonException) { return Problem(e.Message, 400, section); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return Problem("The save could not finish. Check the current settings before retrying. " + e.Message, 500, section); }
    }
    static IResult Problem(string message, int status, string section) => new RazorComponentResult<ManageProblem>(new { Message = message, Section = section }) { StatusCode = status };
}
