// Three posts to start the journal with, written once when the database is created.
// A flag in the meta table records that it happened, so deleting them all later does not
// bring them back.

using Dapper;
using Microsoft.Data.Sqlite;

public static class StarterPosts
{
    public static async Task SeedAsync(SqliteConnection db)
    {
        if (await db.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM meta WHERE key = 'seeded'") > 0) return;

        var now = DateTimeOffset.UtcNow;
        var posts = new[]
        {
            (Slug: "orion-nebula-in-binoculars", Days: 12, Tags: "deep-sky,binoculars,winter",
             Title: "The Orion Nebula in a pair of binoculars",
             Summary: "A stellar nursery 1,340 light years away, and all it takes is a pair of 10×50s and a clear January night.",
             Body: """
             Below Orion's belt hangs a short line of three "stars" that make up his sword. Look at the middle one
             for a few seconds and it stops behaving like a star: it is **soft**, a little glow with a hint of
             shape. That glow is **M42, the Orion Nebula**, a cloud of gas where stars are being born right now.

             ## What you need

             | | |
             |---|---|
             | **Optics** | Any binoculars. 10×50 is ideal; 8×42 works |
             | **When** | December to March, evenings |
             | **Where** | Anywhere you can see Orion; darker helps, but M42 survives a suburb |
             | **Time** | Twenty minutes, most of it letting your eyes adjust |

             ## How to find it

             1. Find the **belt**: three bright stars in a short, straight line.
             2. Drop down from the belt about the length of the belt itself.
             3. The fuzzy middle "star" of the sword is the nebula.

             > Brace your elbows on a car roof or a fence post. Steady binoculars show twice as much.

             ## What you are seeing

             The light reaching your eye tonight left the nebula around the time of the Roman Empire. The glow
             is hydrogen, lit up by four hot young stars at its heart called **the Trapezium**. You will want a
             small telescope to split those four, but binoculars show the nebula's **wings** spreading from
             the bright core like a bird in flight.

             - [x] Found the belt
             - [x] Found the sword
             - [ ] Saw the wings (keep looking: they appear when your eyes are fully dark-adapted)
             """),

            (Slug: "reading-the-terminator", Days: 6, Tags: "moon,telescope,beginner",
             Title: "Reading the terminator",
             Summary: "Why the best place to look at the Moon is the line between day and night, and why a full Moon is the worst time to look.",
             Body: """
             Everyone's first instinct is to wait for a **full Moon**. It is big, bright, and completely flat:
             with the Sun directly behind you there are no shadows, and without shadows there is no relief.

             The place to look is the **terminator**, the line where lunar day meets lunar night. There, the
             Sun sits low on the lunar horizon, and every crater rim and mountain throws a long shadow.

             ## A week of good nights

             - **Day 4–5**: *Mare Crisium* sits right on the terminator, a lava plain ringed by mountains.
             - **Day 7 (first quarter)**: the *Apennine Mountains* catch the sunrise; their peaks shine while
               the valleys beneath are still dark.
             - **Day 9–10**: *Copernicus* rises out of the dark, with terraced walls and central peaks.

             ## Watch it move

             The terminator moves about **15 km an hour** at the lunar equator. At a telescope that is slow,
             but not *too* slow: a mountain peak that is an isolated point of light at 9pm can be joined to
             the day side by midnight. Sketch a crater at the start of a session and again at the end.

             > The Moon rewards patience more than any other target. It is also the only one you can observe
             > from the middle of a city.
             """),

            (Slug: "twenty-minutes-of-darkness", Days: 1, Tags: "beginner,technique",
             Title: "Twenty minutes of darkness",
             Summary: "Your eyes are the most important instrument you own. Here is how to get the most out of them.",
             Body: """
             Step outside from a lit room and you will see a few dozen stars. Wait **twenty minutes** and there
             will be hundreds. Nothing about the sky changed; your eyes did.

             ## What is happening

             Your pupils open within a minute, but that is the small part. The big change is chemical: the
             rods in your retina rebuild a pigment called **rhodopsin**, and that takes 20–30 minutes. One
             glance at a phone screen undoes most of it.

             ## Keep it

             1. **Red light only.** Rhodopsin is barely affected by deep red. A bike light with red tape works.
             2. **Phone on night mode**, brightness down, or better: in your pocket.
             3. **Avoid the porch light.** If you must go inside, close one eye. It keeps its adaptation.

             ## Averted vision

             The centre of your retina has almost no rods. To see a faint galaxy, look *slightly to the side*
             of it: it will seem to appear out of nothing, then vanish when you look straight at it. It feels
             like a trick. It is simply anatomy.

             > Bring a chair. A comfortable observer is a patient observer, and patience is what shows the faint stuff.
             """),
        };

        foreach (var post in posts)
        {
            var written = now.AddDays(-post.Days).ToString("O");
            await db.ExecuteAsync("""
                INSERT OR IGNORE INTO posts (slug, title, summary, body, tags, published, author, created_utc, updated_utc)
                VALUES (@Slug, @Title, @Summary, @Body, @Tags, 1, 'Observatory Admin', @written, @written)
                """, new { post.Slug, post.Title, post.Summary, post.Body, post.Tags, written });
        }

        await db.ExecuteAsync("INSERT INTO meta (key, value) VALUES ('seeded', @now)", new { now = now.ToString("O") });
    }
}
