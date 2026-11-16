using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using SimpleVPN;

class IntervalTests
{
    static int checks;
    static void Assert(bool value, string message)
    {
        if (!value) throw new Exception(message);
        checks++;
    }

    static int Main(string[] args)
    {
        try
        {
            if (args.Length != 1) throw new ArgumentException("Supply a workspace scratch directory.");
            string scratch = Path.Combine(Path.GetFullPath(args[0]), "interval-" + Guid.NewGuid().ToString("N"));
            var store = new LocalStore(scratch);
            var server = SubscriptionParser.Parse("vless://00112233-4455-6677-8899-aabbccddeeff@server.invalid:443?security=reality&type=tcp&sni=example.com&pbk=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA#Fixture")[0];

            foreach (int supplied in new[] { 1, 168, 169, 8760 })
            {
                var metadata = Network.ParseSubscriptionMetadata(new Dictionary<string, string> {
                    { "profile-update-interval", supplied.ToString(CultureInfo.InvariantCulture) }
                });
                var settings = new AppSettings { Metadata = metadata };
                settings.ApplyProviderUpdateInterval();
                int expected = supplied > 168 ? 168 : supplied;
                Assert(metadata.UpdateIntervalHours == supplied, "Original provider interval was changed");
                Assert(settings.UpdateIntervalHours == expected, "Effective provider interval is unsupported");
                Assert(ConnectionOptions.BuildConfig(server, 23001, settings).Length > 0, "Provider interval prevents core configuration");
            }

            var manual = new AppSettings {
                UpdateIntervalHours = 24, FollowProviderUpdateInterval = false,
                Metadata = new SubscriptionMetadata { UpdateIntervalHours = 8760 }
            };
            manual.ApplyProviderUpdateInterval();
            Assert(manual.UpdateIntervalHours == 24, "Provider interval replaced an explicit user preference");
            manual.FollowProviderUpdateInterval = true;
            manual.Metadata = new SubscriptionMetadata();
            manual.ApplyProviderUpdateInterval();
            Assert(manual.UpdateIntervalHours == 24, "Missing provider interval replaced a valid preference");

            foreach (int saved in new[] { 0, 1, 24, 168, 169, 8760 })
            {
                store.Save("settings.dpapi", new AppSettings {
                    UpdateIntervalHours = saved,
                    SubscriptionUrl = "https://subscription.invalid/fixture",
                    SelectedServerId = "fixture-id",
                    Metadata = new SubscriptionMetadata { UpdateIntervalHours = saved }
                });
                AppSettings migrated = store.LoadSettings();
                int expected = saved < 1 ? 1 : saved > 168 ? 168 : saved;
                Assert(migrated.UpdateIntervalHours == expected, "Saved interval was not migrated");
                Assert(migrated.SubscriptionUrl == "https://subscription.invalid/fixture" && migrated.SelectedServerId == "fixture-id", "Migration changed subscription or selection");
                Assert(migrated.Metadata.UpdateIntervalHours == saved, "Migration changed provider metadata");
                Assert(store.Load<AppSettings>("settings.dpapi").UpdateIntervalHours == expected, "Migration was not persisted");
                Assert(ConnectionOptions.BuildConfig(server, 23001, migrated).Length > 0, "Migrated interval prevents core configuration");
                Assert(store.LoadSettings().UpdateIntervalHours == expected, "Repeated migration changed effective settings");
            }

            foreach (int invalid in new[] { 0, 169, 8760 })
            {
                bool rejected = false;
                try { ConnectionOptions.Validate(new AppSettings { UpdateIntervalHours = invalid }); }
                catch (ArgumentException) { rejected = true; }
                Assert(rejected, "Explicit unsupported user interval was silently accepted");
            }
            store.Save("settings.dpapi", new AppSettings { UpdateIntervalHours = 8760, SubscriptionUrl = "https://subscription.invalid/fixture" });
            using (var locked = new FileStream(Path.Combine(store.Data, "settings.dpapi"), FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                AppSettings recovered = store.LoadSettings();
                Assert(recovered.UpdateIntervalHours == 168 && recovered.SubscriptionUrl == "https://subscription.invalid/fixture", "Failed migration write discarded usable settings");
                Assert(store.Load<AppSettings>("settings.dpapi").UpdateIntervalHours == 8760, "Read-locked fixture was unexpectedly rewritten");
            }
            Assert(store.LoadSettings().UpdateIntervalHours == 168 && store.Load<AppSettings>("settings.dpapi").UpdateIntervalHours == 168, "Migration did not retry after the write restriction ended");
            Console.WriteLine("PASS intervals: " + checks + " assertions; provider boundaries and persisted DPAPI migration; no network or proxy changes.");
            return 0;
        }
        catch (Exception error)
        {
            Console.WriteLine("FAIL " + error.GetType().Name + ": " + error.Message);
            return 1;
        }
    }
}
