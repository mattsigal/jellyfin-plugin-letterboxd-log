using System.Collections.Generic;
using System.Collections.ObjectModel;
using MediaBrowser.Model.Plugins;

namespace LetterboxdLog.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    public Collection<Account> Accounts { get; } = new Collection<Account>();
}
