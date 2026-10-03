using Feed.Core.Infrastructure;
using Feed.Core.Domain;
using Feed.Web.Components;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Feed.Web;

public static class PersonRoutes
{
    public static void MapPeople(this WebApplication app, string stamp)
    {
        app.MapGet("/people/{id:long}", async (long id, HttpContext ctx, People people, Backgrounds backgrounds, IAntiforgery antiforgery) => {
            var s = (InstanceSnapshot)ctx.Items["instance"]!;
            var category = ctx.Request.Query["category"].FirstOrDefault(); if (string.IsNullOrEmpty(category)) category = null;
            var scope = ctx.Request.Query["scope"].ToString(); if (scope is not ("hidden" or "unsorted")) scope = "live";
            if (scope != "live") category = null;
            try {
                var model = await people.Read(id, s, category, scope, ctx.RequestAborted);
                if (model is null) return Results.NotFound();
                ctx.Response.Headers.CacheControl = "no-store";
                return (IResult)new RazorComponentResult<PersonView>(new { Model = model, Stamp = stamp, Background = backgrounds.Current.Selected, RequestToken = antiforgery.GetAndStoreTokens(ctx).RequestToken!, Message = ctx.Request.Query["message"].FirstOrDefault() });
            } catch (ArgumentException) { return Results.Redirect($"/people/{id}"); }
        });
        app.MapPost("/people/{id:long}/collect", async (long id, HttpContext ctx, People people, IAntiforgery antiforgery) => {
            try {
                await antiforgery.ValidateRequestAsync(ctx);
                var error = await people.Collect(id, (InstanceSnapshot)ctx.Items["instance"]!, ctx.RequestAborted);
                return Results.Redirect($"/people/{id}" + QueryString.Create("message", error ?? "Collection queued for this person. Refresh this page to see progress and new posts."));
            } catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (AntiforgeryValidationException) { return Results.Text("This form expired. Reload the person page before collecting.", statusCode: 400); }
        });
    }
}
