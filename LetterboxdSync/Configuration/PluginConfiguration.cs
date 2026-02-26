using System.Collections.Generic;
using System.Collections.ObjectModel;
using MediaBrowser.Model.Plugins;

namespace LetterboxdLog.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2227:Collection properties should be read only", Justification = "Needed for Jellyfin configuration persistence")]
    public Collection<Account> Accounts { get; set; } = new Collection<Account>();
}
