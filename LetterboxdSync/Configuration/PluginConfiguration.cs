using System.Collections.Generic;
using MediaBrowser.Model.Plugins;

namespace LetterboxdLog.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    public List<Account> Accounts { get; set; } = new List<Account>();
}
