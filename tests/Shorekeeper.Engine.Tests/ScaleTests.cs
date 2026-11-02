using System.Diagnostics;
using System.Globalization;
using System.Text;
using Shorekeeper.Core.Identity;
using Shorekeeper.Engine.Chat;
using Shorekeeper.Engine.Groups;
using Shorekeeper.Engine.Transfers;
using Shorekeeper.Engine.Trust;

namespace Shorekeeper.Engine.Tests;

/// <summary>Scale runs take minutes: only with SHOREKEEPER_SCALE=1 (docs/22-scale.md §2).</summary>
public static class ScaleRun
{
    public static bool IsOn => Environment.GetEnvironmentVariable("SHOREKEEPER_SCALE") == "1";

    public static bool SeedsFolder => Directory.Exists(Environment.GetEnvironmentVariable("SHOREKEEPER_SCALE_SEED_DIR"));
}

/// <summary>
/// What a device used for years does with its data (docs/22-scale.md): a conversation of 50 000 messages, a group of
/// 100 people, thousands of offers. Prints how long each step takes and fails past a budget a person would notice.
/// </summary>
public sealed class ScaleTests
{
    private const string Skip = "Set SHOREKEEPER_SCALE=1 to measure at scale.";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact(Skip = Skip, SkipType = typeof(ScaleRun), SkipUnless = nameof(ScaleRun.IsOn))]
    public async Task Everyday_steps_stay_quick_with_years_of_data()
    {
        await using TestNode node = await TestNode.StartAsync("Me");
        var seeding = Stopwatch.StartNew();
        ScaleSeed seed = ScaleData.Seed(node.Get<AppPaths>().DatabaseFile, node.Id, new ScaleSize());
        TimeSpan seeded = seeding.Elapsed;
        var report = new StringBuilder($"Seeded in {seeded.TotalSeconds:0.0} s\n");
        var budgets = new List<string>();

        async Task Measure(string what, Func<Task<object?>> step, double budgetMs)
        {
            var times = new List<double>();
            object? result = null;
            for (int i = 0; i < 3; i++)
            {
                var watch = Stopwatch.StartNew();
                result = await step();
                times.Add(watch.Elapsed.TotalMilliseconds);
            }

            double median = times.Order().ElementAt(1);
            string count = result is System.Collections.ICollection items ? $" ({items.Count} items)" : "";
            report.AppendLine(CultureInfo.InvariantCulture, $"{what,-48} first {times[0],8:0.0} ms   median {median,8:0.0} ms{count}");
            if (median > budgetMs)
            {
                budgets.Add($"{what}: {median:0} ms > {budgetMs:0} ms");
            }
        }

        // What starting the app reads.
        await Measure("Load contacts", async () => { await node.Trust.LoadAsync(Ct); return null; }, 500);
        await Measure("Load groups", async () => { await node.GroupStore.LoadAsync(Ct); return null; }, 500);
        await Measure("Load sent offers", async () => { await node.Offers.LoadAsync(Ct); return node.Offers.GetOffers(); }, 1500);
        await Measure("Load received offers", async () => { await node.Inbox.LoadAsync(Ct); return node.Inbox.GetOffers(); }, 1500);

        // Tin nhắn: the list, then the big conversations.
        await Measure("Conversations (1-1)", async () => await node.Chat.GetConversationsAsync(Ct), 300);
        await Measure("Conversations (groups)", async () => await node.GroupChat.GetConversationsAsync(Ct), 300);
        await Measure("Messages of a 50 000 conversation", async () => await node.Chat.GetMessagesAsync(seed.BigPeer, Ct), 1000);
        await Measure("Reactions of that conversation", async () => await node.Chat.GetReactionsAsync(seed.BigPeer, Ct), 300);
        await Measure("Messages of a 30 000 group (100 people)", async () => await node.GroupChat.GetMessagesAsync(seed.BigGroup, Ct), 1500);
        await Measure("Reactions of that group", async () => await node.GroupChat.GetReactionsAsync(seed.BigGroup, Ct), 300);
        await Measure("Sent and received offers in memory", () => Task.FromResult<object?>(node.Offers.GetOffers().Count + node.Inbox.GetOffers().Count), 100);

        // Ctrl+K: words found soon, and words nowhere (every message read).
        await Measure("Search, found soon (1-1)", async () => await node.Chat.FindAsync("báo cáo", 50, Ct), 300);
        await Measure("Search, found nowhere (1-1)", async () => await node.Chat.FindAsync("khongcotunay", 50, Ct), 1500);
        await Measure("Search, found nowhere (groups)", async () => await node.GroupChat.FindAsync("khongcotunay", 50, Ct), 1500);

        // Writing into the big ones (the peers are away: it waits to be sent).
        ChatMessage last = (await node.Chat.GetMessagesAsync(seed.BigPeer, Ct))[^1];
        await Measure("Send a message (50 000 conversation)", async () => await node.Chat.SendTextAsync(seed.BigPeer, "thử", Ct), 200);
        await Measure("React (50 000 conversation)", async () => await node.Chat.ReactAsync(seed.BigPeer, last.Id, "👍", Ct), 200);
        await Measure("Send a message (100-person group)", async () => await node.GroupChat.SendTextAsync(seed.BigGroup, "thử", Ct), 300);

        TestContext.Current.TestOutputHelper?.WriteLine(report.ToString());
        Console.WriteLine(report);
        Assert.True(budgets.Count == 0, "Over budget:\n" + string.Join('\n', budgets) + "\n\n" + report);
    }

    /// <summary>
    /// Fills the app folder SHOREKEEPER_SCALE_SEED_DIR (made by the Debug app started with SHOREKEEPER_DATA_DIR, then
    /// closed) for trying the app on it (docs/22-scale.md §3). Its own device id is read from its log.
    /// </summary>
    [Fact(Skip = "Set SHOREKEEPER_SCALE_SEED_DIR to an app folder to fill.", SkipType = typeof(ScaleRun), SkipUnless = nameof(ScaleRun.SeedsFolder))]
    public void Fill_an_app_folder()
    {
        string folder = Environment.GetEnvironmentVariable("SHOREKEEPER_SCALE_SEED_DIR")!;
        string log = Directory.GetFiles(Path.Combine(folder, "logs"), "*.log").Order().Last();
        string me = System.Text.RegularExpressions.Regex.Match(
            File.ReadAllText(log), "(?:Created new|Loaded) device identity ([a-z2-7]{52})").Groups[1].Value;

        ScaleSeed seed = ScaleData.Seed(Path.Combine(folder, "shorekeeper.db"), DeviceId.Parse(me), new ScaleSize());

        Console.WriteLine($"Filled {folder} for {me}: big conversation with {seed.BigPeer}, big group {seed.BigGroup}");
    }

    /// <summary>
    /// Ours in a group are looked up in one query with every id as a parameter: SQLite takes at most 32 766, so a group
    /// where we wrote more than that could not be opened.
    /// </summary>
    [Fact(Skip = Skip, SkipType = typeof(ScaleRun), SkipUnless = nameof(ScaleRun.IsOn))]
    public async Task A_group_where_we_wrote_more_than_sqlite_takes_in_one_query_still_opens()
    {
        await using TestNode node = await TestNode.StartAsync("Me");
        ScaleSeed seed = ScaleData.Seed(node.Get<AppPaths>().DatabaseFile, node.Id, new ScaleSize
        {
            Contacts = 3, BigConversation = 10, OtherConversations = 10, BigGroupMembers = 3, BigGroupMessages = 40_000, OursInGroup = 0.9,
            SmallGroups = 0, ReceivedOffers = 0, SentOffers = 0,
        });
        await node.GroupStore.LoadAsync(Ct);

        IReadOnlyList<GroupChatMessage> messages = await node.GroupChat.GetMessagesAsync(seed.BigGroup, Ct);
        IReadOnlyList<GroupChatConversation> conversations = await node.GroupChat.GetConversationsAsync(Ct);

        Assert.Equal(40_000, messages.Count);
        Assert.All(messages.Where(m => m.Outgoing), m => Assert.Equal(ChatMessageState.Read, m.State));
        Assert.Single(conversations);
    }
}
