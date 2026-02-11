using System;
using System.Collections.Generic;
using System.Globalization;
using LetterboxdLog.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace LetterboxdLog;

/// <summary>
/// The main plugin.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="xmlSerializer">Instance of the <see cref="IXmlSerializer"/> interface.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <inheritdoc />
    public override string Name => "LetterboxdLog";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("c2fb3d98-3336-4b87-a5c9-8a948bd87234");

    /// <summary>
    /// Gets the current plugin instance.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name = "configLetterboxd",
                EmbeddedResourcePath = $"{GetType().Namespace}.Web.configLetterboxd.html"
            },
            new PluginPageInfo
            {
                Name = "configLetterboxdjs",
                EmbeddedResourcePath = $"{GetType().Namespace}.Web.configLetterboxd.js"
            }
        };
    }

    /// <inheritdoc />
    public Stream GetThumbImage()
    {
        var type = GetType();
        return type.Assembly.GetManifestResourceStream(type.Namespace + ".Web.thumb.png");
    }

    /// <inheritdoc />
    public string ThumbImageFormat => "image/png";
}
