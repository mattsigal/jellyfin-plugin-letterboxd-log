using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using LetterboxdSync.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;

namespace LetterboxdSync;

/// <summary>
/// The main plugin.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    private readonly IApplicationPaths _applicationPaths;

    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="xmlSerializer">Instance of the <see cref="IXmlSerializer"/> interface.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
        _applicationPaths = applicationPaths;
        InjectClientScript();
    }

    /// <inheritdoc />
    public override string Name => "LetterboxdSync";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("b1fb3d98-3336-4b87-a5c9-8a948bd87233");

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
            },
            new PluginPageInfo
            {
                Name = "userConfigLetterboxd",
                EmbeddedResourcePath = $"{GetType().Namespace}.Web.userConfigLetterboxd.html",
                EnableInMainMenu = true,
                DisplayName = "Letterboxd Sync",
            },
            new PluginPageInfo
            {
                Name = "userConfigLetterboxdjs",
                EmbeddedResourcePath = $"{GetType().Namespace}.Web.userConfigLetterboxd.js"
            }
        };
    }

    private void InjectClientScript()
    {
        try
        {
            RegisterWithFileTransformation();
            Console.WriteLine("[LetterboxdSync] Registered with FileTransformation");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[LetterboxdSync] FileTransformation not available: {ex.Message}");
            // FileTransformation not available, fall back to direct HTML modification
            try
            {
                Console.WriteLine($"[LetterboxdSync] Attempting direct injection into {_applicationPaths.WebPath}");
                IndexHtmlTransformer.InjectIntoFile(_applicationPaths.WebPath);
                Console.WriteLine("[LetterboxdSync] Direct HTML injection succeeded");
            }
            catch (Exception ex2)
            {
                Console.WriteLine($"[LetterboxdSync] Direct HTML injection failed: {ex2.Message}");
            }
        }
    }

    private void RegisterWithFileTransformation()
    {
        var payload = new
        {
            id = Id,
            fileNamePattern = "index.html",
            callbackAssembly = "LetterboxdSync",
            callbackClass = "LetterboxdSync.IndexHtmlTransformer",
            callbackMethod = "TransformIndexHtml"
        };

        var json = JsonSerializer.Serialize(payload);

        using var client = new HttpClient();
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = client.PostAsync(
            "http://localhost:8096/FileTransformation/RegisterTransformation",
            content).Result;
        response.EnsureSuccessStatusCode();
    }
}
