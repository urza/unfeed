using Microsoft.Playwright;

namespace Feed.Cli;

// A person visit explores history. Known database posts still count as progress
// when the page moves or a different post appears in this visit.
public sealed record ScrollPosition(double Top, double Height)
{
    public static async Task<ScrollPosition> Read(IPage page)
    {
        var values = await page.EvaluateAsync<double[]>("""
            () => [document.scrollingElement?.scrollTop ?? window.scrollY,
                   document.scrollingElement?.scrollHeight ?? document.body.scrollHeight]
            """);
        return new(values[0], values[1]);
    }
    public bool AdvancedFrom(ScrollPosition before) => Math.Abs(Top - before.Top) > 1 || Height > before.Height + 1;
}

public sealed class CollectionProgress(bool personVisit)
{
    int idle;
    public string? Observe(bool newToDatabase, bool newToVisit, bool pageAdvanced)
    {
        var progress = personVisit ? newToVisit || pageAdvanced : newToDatabase;
        idle = progress ? 0 : idle + 1;
        return idle < 3 ? null : personVisit ? "stalled" : "no_new";
    }
}
