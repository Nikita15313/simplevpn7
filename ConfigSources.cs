using System;
using System.Collections.Generic;

namespace SimpleVPN
{
    // Stored only in the same-user DPAPI library. Body/Url may contain credentials.
    public sealed class ConfigSource
    {
        public string Id = "", Name = "", Url = "", Body = "", SelectedServerId = "";
        public SubscriptionMetadata Metadata = new SubscriptionMetadata();
        public DateTime LastUpdatedUtc = DateTime.MinValue;
        public List<string> HiddenServerIds = new List<string>();
        public override string ToString() { return Name; }
    }

    public sealed class ConfigLibrary
    {
        public List<ConfigSource> Sources = new List<ConfigSource>();
    }
}
